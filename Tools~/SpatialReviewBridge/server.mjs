#!/usr/bin/env node

import { execFile, spawn } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

import {
  approvalAction,
  canonicalContractPath,
  createApprovalGrant,
  ensureApprovalAuthority,
} from "./approval-grant.mjs";

const execute = promisify(execFile);
const projectArg = process.argv.indexOf("--project");
const projectRoot = path.resolve(projectArg >= 0 ? process.argv[projectArg + 1] : process.cwd());
const workflowRoot = path.join(projectRoot, "Library", "DungeonDecorator", "CalibrationWorkflow");
const workflowStatePath = path.join(workflowRoot, "state.json");
const roomReviewsRoot = path.join(projectRoot, "Library", "DungeonDecorator", "RoomReviews");
const roomReviewPointerPath = path.join(roomReviewsRoot, "current.json");
const serverDirectory = path.dirname(fileURLToPath(import.meta.url));
const workflowTemplatePath = path.resolve(serverDirectory, "..", "..", "Editor", "Spatial", "Templates", "CalibrationReviewTemplate.html");
const roomReviewPagePath = path.resolve(serverDirectory, "..", "..", "Editor", "Review", "Templates", "RoomReviewTemplate.html");
// Approval and import authority must be explicitly selected by the user in
// Unity and live outside the writable project. Never discover it from PATH or
// a project-local Library executable.
const unityCtx = String(process.env.UNITY_CTX_BIN || "").trim();
const unityCtxPrefixArgs = testPrefixArgs();
const testAuthorityRoot = process.env.NODE_ENV === "test" ? String(process.env.SPATIAL_REVIEW_TEST_AUTHORITY_ROOT || "") : "";
const reviewNonces = new Map();
let approvalAuthority = null;
const port = Number(process.env.SPATIAL_REVIEW_PORT || 4174);
const pinnedUnityPort = Number(process.env.SPATIAL_REVIEW_UNITY_PORT || 0);
const pinnedUnityNonce = String(process.env.SPATIAL_REVIEW_UNITY_NONCE || "");
const hasPinnedUnitySession = Number.isSafeInteger(pinnedUnityPort) && pinnedUnityPort >= 1 && pinnedUnityPort <= 65535 &&
  /^[a-zA-Z0-9_-]{16,192}$/.test(pinnedUnityNonce);
const allowedOrigins = new Set([
  "http://localhost:4173",
  "http://127.0.0.1:4173",
  `http://localhost:${port}`,
  `http://127.0.0.1:${port}`,
]);
const allowedMutationOrigins = new Set([`http://localhost:${port}`, `http://127.0.0.1:${port}`]);
const allowedHosts = new Set([`127.0.0.1:${port}`, `localhost:${port}`]);

const server = http.createServer(async (request, response) => {
  if (!allowedHosts.has(String(request.headers.host || "").toLowerCase())) {
    return send(response, 403, { error: "Host is not allowed." });
  }
  const origin = request.headers.origin || "";
  const requestUrl = new URL(request.url || "/", `http://127.0.0.1:${port}`);
  if (request.method === "POST" && (!origin || !allowedMutationOrigins.has(origin))) {
    return send(response, 403, { error: "Mutating review requests require an allowed browser origin." });
  }
  if (origin && !allowedOrigins.has(origin)) return send(response, 403, { error: "Origin is not allowed." });
  if (origin) {
    response.setHeader("Access-Control-Allow-Origin", origin);
    response.setHeader("Vary", "Origin");
  }
  response.setHeader("Access-Control-Allow-Headers", "Content-Type, X-Spatial-Review-Nonce");
  response.setHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  if (request.method === "OPTIONS") return send(response, 204, null);

  try {
    if (request.method === "GET" && requestUrl.pathname === "/api/health") {
      return send(response, 200, {
        connected: hasPinnedUnitySession,
        unityPort: hasPinnedUnitySession ? pinnedUnityPort : 0,
        workflowAvailable: fs.existsSync(workflowStatePath),
        projectRoot,
        nonce: issueReviewNonce(),
      });
    }
    if (request.method === "GET" && requestUrl.pathname === "/workflow") {
      if (!fs.existsSync(workflowTemplatePath)) throw new Error("Trusted calibration workflow review template is not available.");
      response.statusCode = 200;
      response.setHeader("Content-Type", "text/html; charset=utf-8");
      response.setHeader("Cache-Control", "no-store");
      response.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self' http://127.0.0.1:4174 http://localhost:4174; object-src 'none'; base-uri 'none'; frame-ancestors 'none'");
      response.setHeader("Referrer-Policy", "no-referrer");
      response.setHeader("X-Content-Type-Options", "nosniff");
      const html = fs.readFileSync(workflowTemplatePath, "utf8")
        .replace("__WORKFLOW_JSON__", "null")
        .replace("__GENERATED_UTC__", "");
      return response.end(html);
    }
    if (request.method === "GET" && requestUrl.pathname === "/room-review") {
      if (!fs.existsSync(roomReviewPagePath)) throw new Error("Room review page is not available.");
      response.statusCode = 200;
      response.setHeader("Content-Type", "text/html; charset=utf-8");
      response.setHeader("Cache-Control", "no-store");
      response.setHeader("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'");
      response.setHeader("Referrer-Policy", "no-referrer");
      response.setHeader("X-Content-Type-Options", "nosniff");
      return response.end(fs.readFileSync(roomReviewPagePath));
    }
    if (request.method === "GET" && requestUrl.pathname === "/api/spatial/workflow") {
      return send(response, 200, publicWorkflowState(loadWorkflow()));
    }
    if (request.method === "GET" && requestUrl.pathname === "/api/room-review/current") {
      const { pointer, state } = loadCurrentRoomReview();
      return send(response, 200, publicRoomReviewState(state, pointer.roomKey));
    }
    if (request.method === "GET" && requestUrl.pathname.startsWith("/api/room-review/image/")) {
      const view = decodeURIComponent(requestUrl.pathname.slice("/api/room-review/image/".length));
      if (!/^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$/.test(view)) throw new Error("Unsupported room review view.");
      const { state, statePath } = loadCurrentRoomReview();
      const captureHash = roomCaptureHash(state);
      if (!captureHash || requestUrl.searchParams.get("hash") !== captureHash) throw new Error("Room capture hash is stale.");
      const capture = (state.capture?.views || []).find(value => value && value.id === view);
      if (!capture || !capture.imagePath) throw new Error("Room review capture was not found.");
      const filePath = verifyRoomReviewCapture(capture, statePath);
      response.statusCode = 200;
      response.setHeader("Content-Type", "image/png");
      response.setHeader("Cache-Control", "no-store");
      response.setHeader("X-Content-Type-Options", "nosniff");
      return response.end(fs.readFileSync(filePath));
    }
    if (request.method === "GET" && requestUrl.pathname.startsWith("/api/spatial/workflow/image/")) {
      const parts = requestUrl.pathname.slice("/api/spatial/workflow/image/".length).split("/");
      const itemId = decodeURIComponent(parts[0] || "");
      const view = parts[1] || "";
      if (!itemId || !["front", "side", "top", "contact"].includes(view)) throw new Error("Unsupported workflow capture view.");
      const state = loadWorkflow();
      const item = state.items?.find(value => value.id === itemId);
      if (!item) throw new Error("Calibration workflow item was not found.");
      if (requestUrl.searchParams.get("hash") !== item.captureHash) throw new Error("Capture hash is stale.");
      const captures = verifyWorkflowCaptureSet(item);
      const suffix = requestUrl.searchParams.get("evidence") === "1" ? "-evidence" : "";
      const filePath = captures.files.get(`${view}${suffix}.png`);
      if (!filePath) throw new Error("Workflow capture image is not available.");
      response.statusCode = 200;
      response.setHeader("Content-Type", "image/png");
      response.setHeader("Cache-Control", "no-store");
      return response.end(fs.readFileSync(filePath));
    }
    if (request.method === "GET" && requestUrl.pathname === "/api/spatial/session") {
      const inspection = resultOf(await callUnity("inspect_spatial_calibration", {}));
      const validation = resultOf(await callUnity("get_deterministic_validation_report", {}));
      return send(response, 200, { session: inspection, validation });
    }
    if (request.method === "GET" && requestUrl.pathname.startsWith("/api/spatial/image/")) {
      const view = requestUrl.pathname.slice("/api/spatial/image/".length);
      if (!["front", "side", "top", "contact"].includes(view)) throw new Error("Unsupported capture view.");
      const inspection = resultOf(await callUnity("inspect_spatial_calibration", {}));
      if (!/^[0-9a-f]{32}$/.test(inspection.sessionId || "")) throw new Error("Active calibration session is invalid.");
      const expectedCaptureHash = String(inspection.captureSetHash || "");
      if (!expectedCaptureHash || requestUrl.searchParams.get("hash") !== expectedCaptureHash) throw new Error("Capture hash is stale.");
      const suffix = requestUrl.searchParams.get("evidence") === "1" ? "-evidence" : "";
      const captures = verifyCaptureDirectory(
        path.join(projectRoot, "Library", "DungeonDecorator", "SpatialCaptures", inspection.sessionId),
        expectedCaptureHash,
        "Active calibration capture",
      );
      const filePath = captures.files.get(`${view}${suffix}.png`);
      if (!filePath) throw new Error("Capture image is not available.");
      response.statusCode = 200;
      response.setHeader("Content-Type", "image/png");
      response.setHeader("Cache-Control", "no-store");
      return response.end(fs.readFileSync(filePath));
    }
    if (request.method === "POST" && requestUrl.pathname === "/api/spatial/capture") {
      requireNonce(request);
      return send(response, 200, await callUnity("capture_spatial_calibration", {}));
    }
    if (request.method === "POST" && requestUrl.pathname === "/api/spatial/review") {
      requireNonce(request);
      const body = await readJson(request);
      return send(response, 200, await reviewActiveDraft(body));
    }
    if (request.method === "POST" && requestUrl.pathname === "/api/spatial/workflow/review") {
      requireNonce(request);
      const body = await readJson(request);
      return send(response, 200, await reviewWorkflowItem(body));
    }
    if (request.method === "POST" && requestUrl.pathname === "/api/spatial/workflow/review-batch") {
      requireNonce(request);
      const body = await readJson(request);
      return send(response, 200, await reviewWorkflowBatch(body));
    }
    if (request.method === "POST" && requestUrl.pathname === "/api/room-review/decision") {
      requireNonce(request);
      const body = await readJson(request);
      return send(response, 200, await reviewCurrentRoom(body));
    }
    return send(response, 404, { error: "Not found." });
  } catch (error) {
    return send(response, 400, { error: error.message });
  }
});

server.listen(port, "127.0.0.1", () => {
  process.stderr.write(`Spatial Review Bridge listening on http://127.0.0.1:${port}\n`);
});

async function reviewActiveDraft(body) {
  const expectedCaptureHash = String(body.expectedCaptureHash || "");
  if (!expectedCaptureHash) throw new Error("expectedCaptureHash is required for a human review decision.");
  const inspection = resultOf(await callUnity("inspect_spatial_calibration", {}));
  if (!/^[0-9a-f]{32}$/.test(String(inspection.sessionId || ""))) throw new Error("Active calibration session is invalid.");
  if (String(inspection.captureSetHash || "") !== expectedCaptureHash) {
    throw new Error("Active calibration capture hash is stale.");
  }
  verifyCaptureDirectory(
    path.join(projectRoot, "Library", "DungeonDecorator", "SpatialCaptures", inspection.sessionId),
    expectedCaptureHash,
    "Active calibration capture",
  );
  return reviewDraft({ ...body, expectedCaptureHash }, {
    verifyEvidence: async draftPath => {
      const captures = verifyCaptureDirectory(
        path.join(projectRoot, "Library", "DungeonDecorator", "SpatialCaptures", inspection.sessionId),
        expectedCaptureHash,
        "Active calibration capture",
      );
      assertDraftTechnicalBinding(draftPath, expectedCaptureHash, captures.technicalReport, "Active calibration");
      await verifyDraftManifestBinding(draftPath, captures, "Active calibration");
    },
  });
}

async function reviewDraft(body, options = {}) {
  const decision = body.decision;
  if (!["Approved", "RevisionRequested", "UnableToJudge"].includes(decision)) throw new Error("Unsupported human decision.");
  const reviewer = cleanReviewer(body.reviewer);
  const issues = normalizeIssues(body.issues);
  const comment = cleanText(body.comment, 4000, "comment");
  if (decision === "RevisionRequested" && issues.length === 0 && !comment) {
    throw new Error("RevisionRequested requires at least one issue or a comment.");
  }

  let draftPath = body.draftPath;
  if (!draftPath) {
    const draftResponse = await callUnity("get_spatial_contract_draft", {});
    const payload = JSON.parse(draftResponse.resultJson || "{}");
    draftPath = payload.drafts?.[0]?.path;
  }
  if (!draftPath || !path.isAbsolute(draftPath) || !inside(path.join(projectRoot, "Library"), draftPath)) {
    throw new Error("Draft must be an active Unity draft under this project's Library folder.");
  }
  ensureRealPathInside(path.join(projectRoot, "Library"), draftPath);
  if (options.verifyEvidence) await options.verifyEvidence(draftPath);

  const contract = JSON.parse(fs.readFileSync(draftPath, "utf8"));
  const currentPath = canonicalContractPath(projectRoot, contract);
  const expectedCaptureHash = String(body.expectedCaptureHash || "");
  if (!expectedCaptureHash) throw new Error("expectedCaptureHash is required for a human review decision.");
  const captureSetHash = contract.contract_type === "asset"
    ? String(contract.asset?.capture_set_hash || "")
    : String(contract.interaction?.capture_set_hash || "");
  if (!captureSetHash) throw new Error("Spatial Contract draft is missing capture_set_hash.");
  if (captureSetHash !== expectedCaptureHash) {
    throw new Error("Spatial Contract draft capture_set_hash is stale relative to the reviewed evidence.");
  }
  const validated = await runCtx(["spatial", "validate", "--json", draftPath]);

  if (decision !== "Approved") {
    const confirmation = resultOf(await callUnity("confirm_spatial_contract_review_decision", {
      contractHash: validated.contract_hash,
      captureSetHash,
      decision,
      reviewer,
      draftPath,
      displayName: String(body.displayName || path.basename(draftPath)),
      issueCount: issues.length,
      comment,
    }));
    if (!confirmation.confirmed) throw new Error("Unity에서 공간 계약 검수 판정이 취소되었습니다.");
    if (options.verifyEvidence) await options.verifyEvidence(draftPath);
    const revalidated = await runCtx(["spatial", "validate", "--json", draftPath]);
    if (String(revalidated.contract_hash || "") !== String(validated.contract_hash || "")) {
      throw new Error("Spatial Contract draft changed after human confirmation.");
    }
    const review = await runCtx([
      "spatial", "review", "--draft", draftPath, "--decision", decision,
      "--reviewer", reviewer, "--issues", issues.join(","),
      "--comment", comment, "--write", "--json",
    ]);
    return { status: decision, draftPath, currentPath: null, review };
  }

  const diff = await runCtx(["spatial", "diff", "--current", currentPath, "--draft", draftPath, "--json"]);
  const geometryBinding = await verifyInteractionGeometryBinding(contract, diff);
  const binding = {
    contractHash: String(validated.contract_hash || ""),
    currentHash: String(diff.current_hash || ""),
    captureSetHash,
    reviewer,
    destination: currentPath,
    ...geometryBinding,
  };
  if (options.expectedBinding && !sameApprovalBinding(binding, options.expectedBinding)) {
    throw new Error("BATCH_SOURCE_CHANGED contract or apply baseline changed after batch confirmation.");
  }
  if (!options.nativeConfirmed) {
    const confirmation = resultOf(await callUnity("confirm_spatial_contract_approval", {
      ...binding,
      displayName: String(body.displayName || path.basename(currentPath)),
    }));
    if (!confirmation.confirmed) throw new Error("Unity에서 최종 승인이 취소되었습니다.");
  }
  if (options.verifyEvidence) await options.verifyEvidence(draftPath);
  approvalAuthority ||= ensureApprovalAuthority(testAuthorityRoot ? { root: testAuthorityRoot, authority: "test-unity-decoscene-review-bridge" } : {});
  const grant = createApprovalGrant({
    action: approvalAction,
    contract_hash: validated.contract_hash,
    current_hash: diff.current_hash,
    capture_set_hash: captureSetHash,
    reviewer,
    destination: currentPath,
    subject_geometry_hash: geometryBinding.subjectGeometryHash,
    target_geometry_hash: geometryBinding.targetGeometryHash,
  }, approvalAuthority);
  const bridgeRequest = {
    protocol_version: 1,
    action: approvalAction,
    project_root: projectRoot,
    current_path: currentPath,
    current_hash: diff.current_hash,
    draft_path: draftPath,
    reviewer,
    grant,
  };
  let applied;
  let postCommitError = "";
  try {
    applied = await runReviewBridge(bridgeRequest);
  } catch (error) {
    const receipt = await tryVerifyCommittedApproval(currentPath, validated.contract_hash);
    if (!receipt) throw error;
    applied = { ok: true, recoveredFromAuthorityReceipt: true, receipt };
    postCommitError = error.message;
  }

  let verified = null;
  try {
    verified = await runCtx(["spatial", "validate", "--json", currentPath]);
  } catch (error) {
    const receipt = await tryVerifyCommittedApproval(currentPath, validated.contract_hash);
    if (!receipt) throw error;
    postCommitError = postCommitError || error.message;
    verified = { status: "AUTHORIZED_COMMIT", contract_hash: validated.contract_hash, receipt };
  }
  return {
    status: "Approved",
    draftPath,
    currentPath,
    review: { status: "ATOMIC_APPROVE_APPLY" },
    diff,
    applied,
    verified,
    committed: true,
    reconciliationRequired: Boolean(postCommitError),
    postCommitError,
  };
}

async function tryVerifyCommittedApproval(currentPath, expectedContractHash) {
  try {
    const receipt = await runCtx(["spatial", "verify-approved", currentPath, "--json"]);
    return receipt?.authorized === true &&
      String(receipt.contract_hash || "").toLowerCase() === String(expectedContractHash || "").toLowerCase()
      ? receipt
      : null;
  } catch {
    return null;
  }
}

async function reviewWorkflowItem(body, options = {}) {
  const state = loadWorkflow();
  const item = state.items?.find(value => value.id === String(body.id || ""));
  validateWorkflowReviewItem(item, body);
  const sourceSnapshot = workflowItemSnapshot(item);
  await verifyWorkflowItemEvidence(item);
  const issues = normalizeIssues(body.issues);
  const comment = cleanText(body.comment, 4000, "comment");
  const result = await reviewDraft({
    draftPath: item.draftPath,
    decision: body.decision,
    reviewer: body.reviewer,
    issues,
    comment,
    expectedCaptureHash: item.captureHash,
  }, {
    ...options,
    verifyEvidence: async draftPath => {
      const latest = requireUnchangedWorkflowItem(item.id, sourceSnapshot);
      if (!samePath(latest.draftPath, draftPath)) throw new Error("WORKFLOW_SOURCE_CHANGED draft path changed during review.");
      await verifyWorkflowItemEvidence(latest);
    },
  });

  const latestState = loadWorkflow();
  const latestItem = latestState.items?.find(value => value.id === item.id);
  if (!latestItem || workflowItemSnapshot(latestItem) !== sourceSnapshot) {
    return {
      status: result.status,
      id: item.id,
      captureHash: item.captureHash,
      applied: result.status === "Approved",
      workflowReconciled: false,
      reconciliationRequired: true,
      warning: result.postCommitError || "The contract decision completed, but the workflow item changed concurrently and was not overwritten.",
    };
  }

  latestItem.status = result.status;
  latestItem.reviewer = String(body.reviewer || "");
  latestItem.reviewedUtc = new Date().toISOString();
  latestItem.comment = comment;
  if (result.status === "RevisionRequested") {
    latestItem.revisionIssueCodes = issues;
    latestItem.revisionComment = comment;
  }
  latestState.updatedUtc = new Date().toISOString();
  latestState.status = deriveWorkflowStatus(latestState);
  try {
    atomicJson(workflowStatePath, latestState);
  } catch (error) {
    if (result.status !== "Approved") throw error;
    return {
      status: "Approved",
      id: latestItem.id,
      captureHash: latestItem.captureHash,
      applied: true,
      workflowReconciled: false,
      reconciliationRequired: true,
      warning: `The approval was committed, but workflow state could not be saved: ${error.message}`,
    };
  }
  return {
    status: result.status,
    id: latestItem.id,
    captureHash: latestItem.captureHash,
    applied: result.status === "Approved",
    workflowReconciled: true,
    reconciliationRequired: result.reconciliationRequired === true,
    warning: result.postCommitError || "",
  };
}

async function reviewWorkflowBatch(body) {
  if (body.decision !== "Approved") throw new Error("Batch review currently supports approval only.");
  const reviewer = cleanReviewer(body.reviewer);
  const requested = Array.isArray(body.items) ? body.items : [];
  if (!requested.length) throw new Error("At least one workflow item is required.");
  const state = loadWorkflow();
  const awaiting = (state.items || []).filter(item => item.status === "AwaitingHumanReview");
  if (requested.length !== awaiting.length) throw new Error("Every awaiting item must be checked before batch approval.");
  const ids = new Set(requested.map(item => String(item.id || "")));
  if (ids.size !== requested.length || awaiting.some(item => !ids.has(item.id))) {
    throw new Error("Batch approval selection does not match the current review queue.");
  }
  for (const requestItem of requested) {
    const item = awaiting.find(value => value.id === String(requestItem.id || ""));
    validateWorkflowReviewItem(item, requestItem);
    await verifyWorkflowItemEvidence(item);
  }
  // Finish every read-only CLI preflight before the first tracked contract is
  // approved. Runtime failures may still produce a partial batch, but expected
  // validation/diff failures cannot mutate an earlier item.
  const bindings = [];
  for (const requestItem of requested) {
    const item = awaiting.find(value => value.id === String(requestItem.id || ""));
    bindings.push({ id: item.id, ...(await preflightApprovedDraft(item.draftPath, item.captureHash, reviewer)) });
  }
  const destinations = new Set(bindings.map(value => normalizePathKey(value.destination)));
  if (destinations.size !== bindings.length) throw new Error("Batch approval contains duplicate contract destinations.");
  const batchHash = hashApprovalBatch(bindings);
  const batchConfirmation = resultOf(await callUnity("confirm_spatial_contract_batch_approval", {
    batchHash,
    itemCount: bindings.length,
    reviewer,
    displayNames: bindings.map(value => value.displayName),
  }));
  if (!batchConfirmation.confirmed) throw new Error("Unity에서 일괄 최종 승인이 취소되었습니다.");

  const results = [];
  for (const requestItem of requested) {
    try {
      const expectedBinding = bindings.find(value => value.id === String(requestItem.id || ""));
      const reviewed = await reviewWorkflowItem({
        id: requestItem.id,
        captureHash: requestItem.captureHash,
        decision: "Approved",
        reviewer,
        issues: [],
        comment: body.comment || "Batch-approved after individual visual checks.",
      }, { nativeConfirmed: true, expectedBinding });
      results.push(reviewed);
      if (reviewed.reconciliationRequired) {
        const latest = loadWorkflow();
        return {
          status: "Partial",
          count: results.filter(value => value.applied).length,
          results,
          applied: true,
          reconciliationRequired: true,
          error: "An approved contract was committed, but its workflow item changed concurrently and requires reconciliation.",
          remaining: (latest.items || []).filter(item => item.status === "AwaitingHumanReview").map(item => item.id),
        };
      }
    } catch (error) {
      const latest = loadWorkflow();
      return {
        status: "Partial",
        count: results.filter(value => value.applied).length,
        results,
        error: error.message,
        remaining: (latest.items || []).filter(item => item.status === "AwaitingHumanReview").map(item => item.id),
      };
    }
  }
  return { status: "Approved", count: results.length, results };
}

function workflowItemSnapshot(item) {
  return crypto.createHash("sha256").update(stableJson(item), "utf8").digest("hex");
}

function stableJson(value) {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(",")}]`;
  if (value && typeof value === "object") {
    return `{${Object.keys(value).sort(ordinalCompare).map(key => `${JSON.stringify(key)}:${stableJson(value[key])}`).join(",")}}`;
  }
  return JSON.stringify(value);
}

function requireUnchangedWorkflowItem(id, expectedSnapshot) {
  const latest = loadWorkflow();
  const item = latest.items?.find(value => value.id === id);
  if (!item || workflowItemSnapshot(item) !== expectedSnapshot) {
    throw new Error("WORKFLOW_SOURCE_CHANGED calibration item changed during review.");
  }
  if (item.status !== "AwaitingHumanReview") {
    throw new Error(`WORKFLOW_SOURCE_CHANGED item is no longer awaiting review: ${item.status}`);
  }
  return item;
}

function validateWorkflowReviewItem(item, body) {
  if (!item) throw new Error("Calibration workflow item was not found.");
  if (item.status !== "AwaitingHumanReview") throw new Error(`Item is not awaiting review: ${item.status}`);
  if (!item.captureHash || item.captureHash !== String(body.captureHash || "")) throw new Error("Capture hash is stale.");
  if (!item.draftPath || !path.isAbsolute(item.draftPath) || !inside(path.join(projectRoot, "Library"), item.draftPath)) {
    throw new Error("Workflow draft is outside the project Library folder.");
  }
  ensureRealPathInside(path.join(projectRoot, "Library"), item.draftPath);
}

function assertDraftCaptureBinding(draftPath, expectedCaptureHash) {
  const contract = JSON.parse(fs.readFileSync(draftPath, "utf8"));
  canonicalContractPath(projectRoot, contract);
  const actual = contract.contract_type === "asset"
    ? String(contract.asset?.capture_set_hash || "")
    : String(contract.interaction?.capture_set_hash || "");
  if (!actual || actual !== String(expectedCaptureHash || "")) {
    throw new Error("Spatial Contract draft capture_set_hash is stale relative to the reviewed evidence.");
  }
  return contract;
}

async function preflightApprovedDraft(draftPath, expectedCaptureHash, reviewer) {
  const contract = assertDraftCaptureBinding(draftPath, expectedCaptureHash);
  const currentPath = canonicalContractPath(projectRoot, contract);
  const validated = await runCtx(["spatial", "validate", "--json", draftPath]);
  const diff = await runCtx(["spatial", "diff", "--current", currentPath, "--draft", draftPath, "--json"]);
  const geometryBinding = await verifyInteractionGeometryBinding(contract, diff);
  return {
    contractHash: String(validated.contract_hash || ""),
    currentHash: String(diff.current_hash || ""),
    captureSetHash: String(expectedCaptureHash || ""),
    reviewer,
    destination: currentPath,
    displayName: approvalDisplayName(contract, currentPath),
    ...geometryBinding,
  };
}

function approvalDisplayName(contract, currentPath) {
  const relative = path.relative(projectRoot, currentPath).replaceAll("\\", "/");
  if (contract?.contract_type === "asset") {
    return `asset:${String(contract.asset?.asset_guid || "unknown")} → ${relative}`;
  }
  return `${String(contract?.interaction?.relation || "interaction")} ${String(contract?.interaction?.subject_guid || "unknown")} → ${String(contract?.interaction?.target_key || "unknown")} → ${relative}`;
}

function sameApprovalBinding(left, right) {
  return left.contractHash === right.contractHash && left.currentHash === right.currentHash &&
    left.captureSetHash === right.captureSetHash && left.reviewer === right.reviewer &&
    left.subjectGeometryHash === right.subjectGeometryHash && left.targetGeometryHash === right.targetGeometryHash &&
    samePath(left.destination, right.destination);
}

function hashApprovalBatch(bindings) {
  const normalized = [...bindings].sort((left, right) => ordinalCompare(left.id, right.id)).map(value => ({
    id: value.id,
    contract_hash: value.contractHash,
    current_hash: value.currentHash,
    capture_set_hash: value.captureSetHash,
    reviewer: value.reviewer,
    destination: path.resolve(value.destination).replaceAll("\\", "/"),
    subject_geometry_hash: value.subjectGeometryHash,
    target_geometry_hash: value.targetGeometryHash,
  }));
  return crypto.createHash("sha256").update(JSON.stringify(normalized), "utf8").digest("hex");
}

async function verifyInteractionGeometryBinding(contract, diff) {
  if (contract?.contract_type !== "interaction") {
    return { subjectGeometryHash: "", targetGeometryHash: "" };
  }

  const subjectGuid = String(contract.interaction?.subject_guid || "").toLowerCase();
  const targetMatch = /^asset:([0-9a-fA-F]{32})$/.exec(String(contract.interaction?.target_key || ""));
  if (!/^[0-9a-f]{32}$/.test(subjectGuid) || !targetMatch ||
      String(contract.interaction?.relation || "") !== "SupportedBy") {
    throw new Error("SUPPORT_CONTRACT_MISSING SupportedBy requires canonical subject and asset target identities.");
  }
  const targetGuid = targetMatch[1].toLowerCase();
  const verifiedByGuid = new Map();
  const verifyAsset = async guid => {
    if (verifiedByGuid.has(guid)) return verifiedByGuid.get(guid);
    const contractPath = path.join(projectRoot, "Assets", "SpatialContracts", "Assets", `${guid}.spatial.json`);
    const verified = await runCtx(["spatial", "verify-approved", contractPath, "--json"]);
    const geometryHash = String(verified.geometry_hash || "").toLowerCase();
    if (!verified.authorized || verified.contract_type !== "asset" ||
        String(verified.asset_guid || "").toLowerCase() !== guid || !/^[0-9a-f]{64}$/.test(geometryHash)) {
      throw new Error(`SUPPORT_CONTRACT_STALE approved geometry receipt is invalid for asset ${guid}.`);
    }
    verifiedByGuid.set(guid, geometryHash);
    return geometryHash;
  };
  const subjectGeometryHash = await verifyAsset(subjectGuid);
  const targetGeometryHash = await verifyAsset(targetGuid);
  return { subjectGeometryHash, targetGeometryHash };
}

function normalizePathKey(value) {
  const normalized = path.resolve(value).replaceAll("\\", "/");
  return process.platform === "win32" ? normalized.toLowerCase() : normalized;
}

async function reviewCurrentRoom(body) {
  const decision = String(body.decision || "");
  if (!["Approved", "RevisionRequested", "UnableToJudge"].includes(decision)) {
    throw new Error("Unsupported human decision.");
  }

  const { state, statePath } = loadCurrentRoomReview();
  const sourceSnapshot = stableJson(state);
  if (!["AwaitingHumanReview", "Stale"].includes(state.status)) throw new Error(`Room review is not awaiting a decision: ${state.status}`);
  if (technicalErrorCount(state) !== 0) throw new Error("A room review with technical errors cannot be approved or decided.");
  const expectedHashes = {
    runId: state.runId,
    inputHash: state.inputHash,
    validationHash: state.technicalReportHash,
    captureHash: roomCaptureHash(state),
  };
  for (const [field, expectedValue] of Object.entries(expectedHashes)) {
    const expected = String(expectedValue || "");
    const actual = String(body[field] || "");
    if (!expected || actual !== expected) throw new Error(`Room review ${field} is stale.`);
  }
  if (!Array.isArray(state.capture?.views) || state.capture.views.length === 0) throw new Error("Room review captures are missing.");
  for (const capture of state.capture.views) verifyRoomReviewCapture(capture, statePath);

  const issues = normalizeIssues(body.issues);
  const comment = cleanText(body.comment, 4000, "comment");
  if (decision === "RevisionRequested" && issues.length === 0 && !comment) {
    throw new Error("RevisionRequested requires at least one issue or a comment.");
  }

  const response = await callUnity("verify_room_review", {
    runId: state.runId,
    inputHash: state.inputHash,
    technicalReportHash: state.technicalReportHash,
    captureSetHash: roomCaptureHash(state),
  });
  const verification = resultOf(response);
  if (!verification.valid) throw new Error(`Unity rejected stale room evidence: ${verification.reason || "verification failed"}`);
  const confirmation = resultOf(await callUnity("confirm_room_review_decision", {
    runId: state.runId,
    inputHash: state.inputHash,
    technicalReportHash: state.technicalReportHash,
    captureSetHash: roomCaptureHash(state),
    decision,
    issueCount: issues.length,
    comment,
    targetName: String(state.targetName || state.roomKey || "현재 방"),
  }));
  if (!confirmation.confirmed) throw new Error("Unity에서 방 검수 최종 판정이 취소되었습니다.");

  const latest = loadCurrentRoomReview();
  if (!samePath(latest.statePath, statePath) || stableJson(latest.state) !== sourceSnapshot) {
    throw new Error("ROOM_REVIEW_SOURCE_CHANGED room review state changed during human confirmation.");
  }
  if (!["AwaitingHumanReview", "Stale"].includes(latest.state.status) || technicalErrorCount(latest.state) !== 0) {
    throw new Error("ROOM_REVIEW_SOURCE_CHANGED room review is no longer eligible for this decision.");
  }
  for (const capture of latest.state.capture?.views || []) verifyRoomReviewCapture(capture, latest.statePath);

  const reviewedUtc = new Date().toISOString();
  latest.state.decision = {
    value: decision,
    inputHash: latest.state.inputHash,
    technicalReportHash: latest.state.technicalReportHash,
    captureSetHash: roomCaptureHash(latest.state),
    reviewer: "local-user",
    issueCodes: issues,
    comment,
    reviewedUtc,
  };
  latest.state.status = decision;
  latest.state.updatedUtc = reviewedUtc;
  const reviewPath = path.join(path.dirname(latest.statePath), "runs", latest.state.runId, "human-review.json");
  if (!inside(roomReviewsRoot, reviewPath)) throw new Error("Room review record path is invalid.");
  atomicJson(reviewPath, latest.state.decision);
  atomicJson(latest.statePath, latest.state);
  return {
    runId: latest.state.runId,
    status: latest.state.status,
    inputHash: latest.state.inputHash,
    validationHash: latest.state.technicalReportHash,
    captureHash: roomCaptureHash(latest.state),
    reviewedUtc,
  };
}

function loadCurrentRoomReview() {
  if (!fs.existsSync(roomReviewPointerPath)) throw new Error("No current room review has been prepared in Unity.");
  const pointer = JSON.parse(fs.readFileSync(roomReviewPointerPath, "utf8"));
  if (pointer.schemaVersion !== 1) throw new Error("Unsupported current room review pointer.");
  const roomKey = String(pointer.roomKey || "");
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$/.test(roomKey)) throw new Error("Current room review key is invalid.");
  if (!String(pointer.statePath || "")) throw new Error("Current room review pointer has no statePath.");

  const expectedPath = path.join(roomReviewsRoot, roomKey, "state.json");
  const suppliedPath = String(pointer.statePath);
  const candidates = path.isAbsolute(suppliedPath)
    ? [path.resolve(suppliedPath)]
    : [path.resolve(roomReviewsRoot, suppliedPath), path.resolve(projectRoot, suppliedPath)];
  const candidate = candidates.find(value => samePath(value, expectedPath)) || candidates[0];
  if (!inside(roomReviewsRoot, candidate) || !samePath(candidate, expectedPath)) {
    throw new Error("Current room review state must be the selected room's state.json under RoomReviews.");
  }
  if (!fs.existsSync(candidate)) throw new Error("Current room review state is missing.");
  ensureRealPathInside(roomReviewsRoot, candidate);

  const state = JSON.parse(fs.readFileSync(candidate, "utf8"));
  if (state.schemaVersion !== 1) throw new Error("Unsupported room review state.");
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$/.test(String(state.runId || ""))) throw new Error("Room review runId is invalid.");
  if (state.roomKey && state.roomKey !== roomKey) throw new Error("Room review state does not match the current room pointer.");
  if (!String(state.inputHash || "").trim()) throw new Error("Room review inputHash is missing.");
  if (!String(state.technicalReportHash || "").trim()) throw new Error("Room review technicalReportHash is missing.");
  if (!Number.isInteger(Number(state.technicalErrorCount)) || Number(state.technicalErrorCount) < 0) {
    throw new Error("Room review technicalErrorCount is invalid.");
  }
  const evidenceRequired = ["AwaitingHumanReview", "Approved", "RevisionRequested", "UnableToJudge", "Stale"].includes(state.status);
  if (evidenceRequired && !String(roomCaptureHash(state) || "").trim()) throw new Error("Room review captureSetHash is missing.");
  if (evidenceRequired && !Array.isArray(state.capture?.views)) throw new Error("Room review captures are invalid.");
  return { pointer, state, statePath: candidate };
}

function publicRoomReviewState(state, roomKey) {
  const arrangements = Array.isArray(state.arrangementEvidence) ? state.arrangementEvidence.map(value => ({
    arrangementId: String(value?.arrangementId || ""),
    targetElementId: String(value?.targetElementId || ""),
    targetFrameId: String(value?.targetFrameId || "top"),
    preset: String(value?.preset || ""),
    memberCount: Number(value?.memberCount || 0),
    stackCount: Number(value?.stackCount || 0),
    maximumStackLevel: Number(value?.maximumStackLevel || 0),
    minimumSupport: Number(value?.minimumSupport || 0),
    minimumEdgeDistance: Number(value?.minimumEdgeDistance || 0),
    specHash: String(value?.specHash || ""),
    placementHash: String(value?.placementHash || ""),
    stackStructure: Array.isArray(value?.stackStructure) ? value.stackStructure.map(String) : [],
    errorCodes: Array.isArray(value?.errorCodes) ? value.errorCodes.map(String) : [],
    captureViewIds: Array.isArray(value?.captureViewIds) ? value.captureViewIds.map(String) : [],
  })) : [];
  return {
    schemaVersion: state.schemaVersion,
    roomKey,
    runId: state.runId,
    targetKind: state.targetKind || "Room",
    targetId: state.targetId || "",
    targetName: state.targetName || roomKey || "Room",
    profileId: "기본 방 검수 · 4뷰",
    status: state.status || "Pending",
    createdUtc: state.createdUtc || "",
    updatedUtc: state.updatedUtc || "",
    inputHash: state.inputHash,
    validationHash: state.technicalReportHash,
    captureHash: roomCaptureHash(state),
    technicalErrorCount: technicalErrorCount(state),
    technicalIssues: Array.isArray(state.technicalErrorCodes) ? state.technicalErrorCodes : [],
    changeSummary: roomChangeSummary(state.changes),
    changeScope: state.changes?.scope ?? 0,
    arrangements,
    captures: (state.capture?.views || []).map(capture => ({
      view: String(capture?.id || ""),
      label: roomCaptureLabel(capture?.id),
      required: capture?.required !== false,
      arrangementId: arrangements.find(value => value.captureViewIds.includes(String(capture?.id || "")))?.arrangementId || "",
    })),
    review: state.decision && state.decision.value && state.decision.value !== "Pending" ? {
      decision: state.decision.value,
      reviewer: state.decision.reviewer || "local-user",
      reviewedUtc: state.decision.reviewedUtc || "",
      issues: Array.isArray(state.decision.issueCodes) ? state.decision.issueCodes : [],
      comment: state.decision.comment || "",
    } : null,
  };
}

const spatialCaptureNames = [
  "front.png", "side.png", "top.png", "contact.png",
  "front-evidence.png", "side-evidence.png", "top-evidence.png", "contact-evidence.png",
  "technical-report.json", "capture-manifest.json",
];

function verifyWorkflowCaptureSet(item) {
  const directory = String(item?.captureDirectory || "");
  const rawPaths = Array.isArray(item?.rawPaths) ? item.rawPaths.map(String) : [];
  const evidencePaths = Array.isArray(item?.evidencePaths) ? item.evidencePaths.map(String) : [];
  if (!path.isAbsolute(directory) || rawPaths.length !== 4 || evidencePaths.length !== 4) {
    throw new Error("Workflow capture evidence is incomplete.");
  }
  const declared = [...rawPaths, ...evidencePaths];
  const expectedNames = spatialCaptureNames.filter(value => value.endsWith(".png")).sort(ordinalCompare);
  const actualNames = declared.map(value => {
    if (!path.isAbsolute(value)) throw new Error("Workflow capture paths must be absolute.");
    const resolved = path.resolve(value);
    if (!samePath(path.dirname(resolved), directory)) throw new Error("Workflow capture paths do not match captureDirectory.");
    return path.basename(resolved);
  }).sort(ordinalCompare);
  if (actualNames.length !== expectedNames.length || actualNames.some((value, index) => value !== expectedNames[index])) {
    throw new Error("Workflow capture evidence does not contain the required four raw and four overlay views.");
  }
  return verifyCaptureDirectory(directory, String(item.captureHash || ""), "Workflow capture");
}

async function verifyWorkflowItemEvidence(item) {
  const captures = verifyWorkflowCaptureSet(item);
  const reportHash = String(item?.technicalReportHash || "").toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(reportHash) || reportHash !== captures.technicalReport.report_hash) {
    throw new Error("Workflow technical report hash is stale relative to the captured evidence.");
  }
  assertDraftTechnicalBinding(
    item.draftPath,
    String(item.captureHash || ""),
    captures.technicalReport,
    "Workflow",
  );
  return verifyDraftManifestBinding(item.draftPath, captures, "Workflow");
}

function assertDraftTechnicalBinding(draftPath, expectedCaptureHash, technicalReport, label) {
  const contract = assertDraftCaptureBinding(draftPath, expectedCaptureHash);
  const technical = contract?.technical;
  if (contract.state !== "AwaitingHumanReview" || !technical || technical.passed !== true ||
      Number(technical.error_count) !== 0 || String(technical.report_hash || "").toLowerCase() !== technicalReport.report_hash) {
    throw new Error(`${label} draft technical evidence does not match the captured zero-error report.`);
  }
  return contract;
}

function verifyCaptureDirectory(directory, expectedHash, label) {
  const captureRoot = path.join(projectRoot, "Library", "DungeonDecorator", "SpatialCaptures");
  const resolvedDirectory = path.resolve(directory);
  if (!path.isAbsolute(directory) || !inside(captureRoot, resolvedDirectory) || !fs.existsSync(resolvedDirectory)) {
    throw new Error(`${label} directory is outside the project capture root or is missing.`);
  }
  ensureRealPathInside(captureRoot, resolvedDirectory);
  if (!/^[0-9a-f]{64}$/.test(String(expectedHash || "").toLowerCase())) throw new Error(`${label} hash is invalid.`);

  const files = new Map();
  const orderedPaths = spatialCaptureNames.map(name => {
    const filePath = path.join(resolvedDirectory, name);
    if (!fs.existsSync(filePath)) throw new Error(`${label} evidence file is missing: ${name}`);
    ensureRealPathInside(captureRoot, filePath);
    files.set(name, filePath);
    return filePath;
  }).sort(ordinalCompare);
  const digest = crypto.createHash("sha256");
  for (const filePath of orderedPaths) digest.update(fs.readFileSync(filePath));
  const actualHash = digest.digest("hex");
  if (actualHash !== String(expectedHash).toLowerCase()) {
    throw new Error(`${label} content has changed and must be recaptured.`);
  }
  const technicalReport = parseTechnicalReport(files.get("technical-report.json"), label);
  const manifest = parseCaptureManifest(files.get("capture-manifest.json"), technicalReport, label);
  return { directory: resolvedDirectory, files, captureHash: actualHash, technicalReport, manifest };
}

function parseTechnicalReport(reportPath, label) {
  let report;
  try { report = JSON.parse(fs.readFileSync(reportPath, "utf8")); }
  catch { throw new Error(`${label} technical-report.json is not valid JSON.`); }
  const hash = String(report?.report_hash || "").toLowerCase();
  if (report?.status !== "AwaitingHumanReview" || Number(report?.error_count) !== 0 ||
      !Array.isArray(report?.errors) || report.errors.length !== 0 || !/^[0-9a-f]{64}$/.test(hash)) {
    throw new Error(`${label} technical report must be AwaitingHumanReview with zero errors and a valid report hash.`);
  }
  return { ...report, report_hash: hash };
}

function parseCaptureManifest(manifestPath, technicalReport, label) {
  let manifest;
  try { manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8")); }
  catch { throw new Error(`${label} capture-manifest.json is not valid JSON.`); }
  if (manifest?.schema_version !== 1 || manifest?.manifest_version !== 1 ||
      !/^[0-9a-f]{32}$/i.test(String(manifest?.session_id || "")) ||
      manifest?.technical_passed !== true || Number(manifest?.technical_error_count) !== 0 ||
      String(manifest?.technical_report_hash || "").toLowerCase() !== technicalReport.report_hash ||
      !Array.isArray(manifest?.proposals) || manifest.proposals.length < 1 || manifest.proposals.length > 2) {
    throw new Error(`${label} capture manifest does not match its zero-error technical report.`);
  }
  const proposals = manifest.proposals.map(value => ({
    contract_type: String(value?.contract_type || ""),
    canonical_identity: String(value?.canonical_identity || ""),
    proposal_hash: String(value?.proposal_hash || "").toLowerCase(),
  }));
  if (proposals.some(value => !["asset", "interaction"].includes(value.contract_type) ||
      !/^[a-zA-Z0-9][a-zA-Z0-9_:-]{0,511}$/.test(value.canonical_identity) ||
      !/^[0-9a-f]{64}$/.test(value.proposal_hash))) {
    throw new Error(`${label} capture manifest contains an invalid proposal binding.`);
  }
  const keys = proposals.map(value => `${value.contract_type}\0${value.canonical_identity}`);
  const sorted = [...keys].sort(ordinalCompare);
  if (new Set(keys).size !== keys.length || keys.some((value, index) => value !== sorted[index])) {
    throw new Error(`${label} capture manifest proposals must be unique and ordinally sorted.`);
  }
  return { ...manifest, proposals, technical_report_hash: technicalReport.report_hash };
}

async function verifyDraftManifestBinding(draftPath, captures, label) {
  const manifest = captures.manifest;
  const draftDirectory = path.dirname(path.resolve(draftPath));
  const expectedByType = new Map([
    ["asset", path.join(draftDirectory, `${manifest.session_id}.spatial.json`)],
    ["interaction", path.join(draftDirectory, `${manifest.session_id}.interaction.json`)],
  ]);
  const existing = [...expectedByType.entries()].filter(([, candidate]) => fs.existsSync(candidate));
  if (existing.length !== manifest.proposals.length ||
      !existing.some(([, candidate]) => samePath(candidate, draftPath))) {
    throw new Error(`${label} capture manifest has a missing or extra draft proposal.`);
  }

  const seenTypes = new Set();
  for (const proposal of manifest.proposals) {
    if (seenTypes.has(proposal.contract_type)) throw new Error(`${label} capture manifest has duplicate proposal types.`);
    seenTypes.add(proposal.contract_type);
    const candidate = expectedByType.get(proposal.contract_type);
    if (!candidate || !fs.existsSync(candidate)) throw new Error(`${label} capture manifest proposal draft is missing.`);
    ensureRealPathInside(path.join(projectRoot, "Library"), candidate);
    const document = JSON.parse(fs.readFileSync(candidate, "utf8"));
    const identity = proposalIdentity(document);
    assertDraftTechnicalBinding(candidate, captures.captureHash, captures.technicalReport, label);
    const validated = await runCtx(["spatial", "validate", "--json", candidate]);
    if (document.contract_type !== proposal.contract_type || identity !== proposal.canonical_identity ||
        String(validated.proposal_hash || "").toLowerCase() !== proposal.proposal_hash) {
      throw new Error(`${label} draft proposal does not match capture-manifest.json.`);
    }
  }
  return manifest;
}

function proposalIdentity(document) {
  if (document?.contract_type === "asset" && /^[0-9a-f]{32}$/i.test(String(document.asset?.asset_guid || ""))) {
    return String(document.asset.asset_guid).toLowerCase();
  }
  if (document?.contract_type === "interaction" && /^[0-9a-f]{32}$/i.test(String(document.interaction?.subject_guid || ""))) {
    const hex = value => Buffer.from(String(value || ""), "utf8").toString("hex");
    return `${String(document.interaction.subject_guid).toLowerCase()}__${hex(document.interaction.target_key)}__${hex(document.interaction.relation)}`;
  }
  throw new Error("Spatial draft has no canonical proposal identity.");
}

function ordinalCompare(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

function resolveRoomReviewCapture(capturePath, statePath) {
  if (!String(capturePath || "")) throw new Error("Room review capture path is missing.");
  const candidate = path.isAbsolute(capturePath)
    ? path.resolve(capturePath)
    : path.resolve(path.dirname(statePath), capturePath);
  const dungeonDataRoot = path.join(projectRoot, "Library", "DungeonDecorator");
  if (!inside(dungeonDataRoot, candidate) || path.extname(candidate).toLowerCase() !== ".png" || !fs.existsSync(candidate)) {
    throw new Error("Room review capture is outside the project review data or is missing.");
  }
  ensureRealPathInside(dungeonDataRoot, candidate);
  return candidate;
}

function verifyRoomReviewCapture(capture, statePath) {
  const filePath = resolveRoomReviewCapture(capture?.imagePath, statePath);
  const expected = String(capture?.contentHash || "").toLowerCase();
  if (!/^[0-9a-f]{64}$/.test(expected)) throw new Error("Room review capture content hash is invalid.");
  const actual = crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
  if (actual !== expected) throw new Error("Room review capture content has changed and must be recaptured.");
  return filePath;
}

function ensureRealPathInside(root, candidate) {
  const realRoot = fs.realpathSync(root);
  const realCandidate = fs.realpathSync(candidate);
  if (!inside(realRoot, realCandidate)) throw new Error("Review path escapes the local review data root.");
}

function technicalErrorCount(state) {
  return Number(state.technicalErrorCount || 0);
}

function roomCaptureHash(state) {
  return String(state.capture?.captureSetHash || "");
}

function roomCaptureLabel(id) {
  const fixed = ({ top: "방 전체 · 상단", entrance: "방 전체 · 입구 시점", "primary-observation": "방 전체 · 입구 시점", "corner-a": "방 전체 · 첫 번째 모서리", "corner-b": "방 전체 · 두 번째 모서리" })[id];
  if (fixed) return fixed;
  const value = String(id || "");
  if (value.startsWith("surface-arrangement-")) {
    if (value.endsWith("-overview")) return "탁자 연출 · 사선 전경";
    if (value.endsWith("-top")) return "탁자 연출 · 상단";
    if (value.endsWith("-side")) return "탁자 연출 · 측면";
    if (value.endsWith("-contact")) return "탁자 연출 · 접촉 확대";
  }
  if (value.startsWith("wall-contact-side-")) return "벽 접촉 · 측면 확대";
  return value || "캡처";
}

function roomChangeSummary(changes) {
  const scope = Number(changes?.scope || 0);
  const labels = [
    [1, "방 구조"], [2, "구성 계획"], [4, "오브젝트 배치"], [8, "에셋·공간 계약"],
    [16, "콘셉트"], [32, "기술 검사 규칙"], [64, "캡처 방식"], [128, "조명·표현"],
  ].filter(([flag]) => (scope & flag) !== 0).map(([, label]) => `${label} 변경`);
  const affected = Array.isArray(changes?.affectedIds) ? changes.affectedIds : [];
  const visible = affected.slice(0, 12).map(value => `영향 항목 · ${value}`);
  if (affected.length > visible.length) visible.push(`그 외 ${affected.length - visible.length}개 항목`);
  return [...labels, ...visible];
}

function normalizeIssues(value) {
  if (value == null) return [];
  if (!Array.isArray(value) || value.length > 16) throw new Error("issues must be an array with at most 16 entries.");
  return [...new Set(value.map(item => cleanText(item, 120, "issue")).filter(Boolean))];
}

function cleanText(value, limit, field) {
  const result = String(value || "").trim();
  if (result.length > limit) throw new Error(`${field} is too long.`);
  return result;
}

function samePath(left, right) {
  const a = path.resolve(left);
  const b = path.resolve(right);
  return process.platform === "win32" ? a.toLowerCase() === b.toLowerCase() : a === b;
}

function loadWorkflow() {
  if (!fs.existsSync(workflowStatePath)) throw new Error("Calibration workflow has not been created in Unity.");
  const state = JSON.parse(fs.readFileSync(workflowStatePath, "utf8"));
  if (state.schemaVersion !== 1 || !Array.isArray(state.items) || state.items.length > 4096) {
    throw new Error("Unsupported calibration workflow state.");
  }
  const ids = new Set();
  const allowedStatuses = new Set([
    "Pending", "NeedsRelationReview", "Running", "TechnicalFailed", "AwaitingHumanReview",
    "Approved", "RevisionRequested", "UnableToJudge", "Stale",
  ]);
  for (const item of state.items) {
    if (!item || typeof item !== "object" || !/^[a-zA-Z0-9][a-zA-Z0-9._:-]{0,127}$/.test(String(item.id || "")) || ids.has(item.id)) {
      throw new Error("Calibration workflow contains an invalid or duplicate item id.");
    }
    ids.add(item.id);
    if (!allowedStatuses.has(String(item.status || ""))) throw new Error(`Calibration workflow item ${item.id} has an invalid status.`);
    if (!item.reviewKind) item.reviewKind = "asset";
    if (!["asset", "interaction", "arrangement"].includes(item.reviewKind)) {
      throw new Error(`Calibration workflow item ${item.id} has an invalid review kind.`);
    }
    if (!Array.isArray(item.revisionIssueCodes)) item.revisionIssueCodes = [];
    if (item.status === "RevisionRequested" && !item.revisionComment) item.revisionComment = item.comment || "";
    if (item.status === "AwaitingHumanReview") validateAwaitingWorkflowSource(item);
  }
  return state;
}

function validateAwaitingWorkflowSource(item) {
  if (!/^[0-9a-f]{64}$/i.test(String(item.captureHash || "")) ||
      !/^[0-9a-f]{64}$/i.test(String(item.technicalReportHash || "")) ||
      !/^[0-9a-f]{32}$/i.test(String(item.sessionId || ""))) {
    throw new Error(`Calibration workflow item ${item.id} has invalid evidence identifiers.`);
  }
  const captureRoot = path.join(projectRoot, "Library", "DungeonDecorator", "SpatialCaptures");
  const libraryRoot = path.join(projectRoot, "Library");
  if (!path.isAbsolute(String(item.captureDirectory || "")) || !inside(captureRoot, item.captureDirectory) ||
      !path.isAbsolute(String(item.draftPath || "")) || !inside(libraryRoot, item.draftPath) ||
      !String(item.draftPath).endsWith(".json") ||
      !Array.isArray(item.rawPaths) || item.rawPaths.length !== 4 ||
      !Array.isArray(item.evidencePaths) || item.evidencePaths.length !== 4) {
    throw new Error(`Calibration workflow item ${item.id} has invalid evidence paths.`);
  }
}

function publicWorkflowState(state) {
  return {
    schemaVersion: state.schemaVersion,
    workflowId: cleanText(state.workflowId, 128, "workflowId"),
    status: String(state.status || "Pending"),
    createdUtc: String(state.createdUtc || ""),
    updatedUtc: String(state.updatedUtc || ""),
    batchSize: Number(state.batchSize || 0),
    autoContinue: state.autoContinue === true,
    // Treat Library workflow JSON as untrusted project data. Return an explicit,
    // bounded view model instead of forwarding unknown fields or filesystem
    // paths into the trusted review template.
    items: state.items.map(publicWorkflowItem),
  };
}

function publicWorkflowItem(item) {
  const safeHash = value => /^[0-9a-f]{64}$/i.test(String(value || "")) ? String(value).toLowerCase() : "";
  const safeSession = value => /^[0-9a-f]{32}$/i.test(String(value || "")) ? String(value).toLowerCase() : "";
  const safeText = (value, limit = 256) => String(value || "").replace(/[\u0000-\u001f\u007f]/g, "").slice(0, limit);
  const safeList = (value, limit = 16, itemLimit = 256) => Array.isArray(value)
    ? value.slice(0, limit).map(entry => safeText(entry, itemLimit))
    : [];
  const finite = value => Number.isFinite(Number(value)) ? Number(value) : 0;
  const contacts = Array.isArray(item.contacts) ? item.contacts.slice(0, 16).map(contact => ({
    ruleId: safeText(contact?.ruleId, 128),
    target: safeText(contact?.target, 256),
    gap: finite(contact?.gap),
    penetration: finite(contact?.penetration),
    support: finite(contact?.support),
    direction: finite(contact?.direction),
  })) : [];
  const sourceArrangement = item.arrangementEvidence;
  const arrangementEvidence = sourceArrangement && typeof sourceArrangement === "object" ? {
    arrangementId: safeText(sourceArrangement.arrangementId, 128),
    targetElementId: safeText(sourceArrangement.targetElementId, 128),
    targetFrameId: safeText(sourceArrangement.targetFrameId, 128),
    preset: safeText(sourceArrangement.preset, 64),
    memberCount: finite(sourceArrangement.memberCount),
    stackCount: finite(sourceArrangement.stackCount),
    maximumStackLevel: finite(sourceArrangement.maximumStackLevel),
    minimumSupport: finite(sourceArrangement.minimumSupport),
    minimumEdgeDistance: finite(sourceArrangement.minimumEdgeDistance),
    specHash: safeHash(sourceArrangement.specHash),
    placementHash: safeHash(sourceArrangement.placementHash),
    stackStructure: safeList(sourceArrangement.stackStructure, 12, 128),
    errorCodes: safeList(sourceArrangement.errorCodes, 16, 128),
    captureViewIds: safeList(sourceArrangement.captureViewIds, 8, 128),
  } : null;
  return {
    id: safeText(item.id, 128),
    assetId: safeText(item.assetId, 256),
    displayName: safeText(item.displayName, 256),
    geometryFamilyHash: safeText(item.geometryFamilyHash, 128),
    geometryFamilyCanonicalId: safeText(item.geometryFamilyCanonicalId, 128),
    reviewGroupKey: safeText(item.reviewGroupKey, 256),
    template: safeText(item.template, 128),
    reviewKind: safeText(item.reviewKind, 32),
    status: safeText(item.status, 64),
    sessionId: safeSession(item.sessionId),
    technicalReportHash: safeHash(item.technicalReportHash),
    captureHash: safeHash(item.captureHash),
    reviewer: safeText(item.reviewer, 128),
    reviewedUtc: safeText(item.reviewedUtc, 64),
    comment: safeText(item.comment, 4000),
    revisionComment: safeText(item.revisionComment, 4000),
    revisionIssueCodes: safeList(item.revisionIssueCodes, 16, 128),
    errors: safeList(item.errors, 64, 512),
    contacts,
    arrangementEvidence,
  };
}

function deriveWorkflowStatus(state) {
  if (state.items.some(item => item.status === "AwaitingHumanReview")) return "AwaitingHumanReview";
  if (state.items.some(item => item.status === "TechnicalFailed")) return "TechnicalFailed";
  if (state.items.some(item => item.status === "NeedsRelationReview")) return "NeedsRelationReview";
  if (state.items.some(item => item.status === "RevisionRequested")) return "RevisionRequested";
  if (state.items.some(item => item.status === "UnableToJudge")) return "UnableToJudge";
  if (state.items.length && state.items.every(item => item.status === "Approved")) return "Approved";
  return "Pending";
}

function atomicJson(filePath, value) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const temporary = `${filePath}.${process.pid}.tmp`;
  fs.writeFileSync(temporary, `${JSON.stringify(value, null, 2)}\n`, "utf8");
  fs.renameSync(temporary, filePath);
}

async function runCtx(args) {
  if (!unityCtx) throw new Error("Trusted unity-ctx is not configured. Select it in Unity before saving review decisions.");
  try {
    const result = await execute(unityCtx, [...unityCtxPrefixArgs, ...args], { cwd: projectRoot, windowsHide: true, timeout: 30000, maxBuffer: 1024 * 1024 });
    return JSON.parse(result.stdout || "{}");
  } catch (error) {
    const detail = String(error.stderr || error.message || error).trim();
    throw new Error(`unity-ctx failed: ${detail}`);
  }
}

function cleanReviewer(value) {
  const reviewer = cleanText(value, 128, "reviewer");
  if (!reviewer || /[\u0000-\u001f\u007f]/.test(reviewer)) throw new Error("reviewer must be a printable one-line identifier.");
  return reviewer;
}

function runReviewBridge(request) {
  return new Promise((resolve, reject) => {
    if (!unityCtx) return reject(new Error("Trusted unity-ctx is not configured. Select it in Unity before approval."));
    const child = spawn(unityCtx, [...unityCtxPrefixArgs, "review-bridge"], {
      cwd: projectRoot,
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });
    let stdout = "";
    let stderr = "";
    let settled = false;
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      error ? reject(error) : resolve(value);
    };
    const timer = setTimeout(() => {
      child.kill();
      finish(new Error("unity-ctx review bridge timed out."));
    }, 30000);
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", chunk => {
      stdout += chunk;
      if (stdout.length > 1024 * 1024) child.kill();
    });
    child.stderr.on("data", chunk => {
      stderr += chunk;
      if (stderr.length > 1024 * 1024) child.kill();
    });
    child.stdin.on("error", error => finish(new Error(`unity-ctx review bridge stdin failed: ${error.message}`)));
    child.on("error", error => finish(new Error(`unity-ctx review bridge failed: ${error.message}`)));
    child.on("close", code => {
      let response;
      try { response = JSON.parse(stdout || "{}"); }
      catch { return finish(new Error(`unity-ctx review bridge returned invalid JSON: ${stderr.trim() || stdout.trim()}`)); }
      if (code !== 0 || !response.ok) {
        return finish(new Error(`unity-ctx review bridge failed: ${response.error || stderr.trim() || `exit ${code}`}`));
      }
      return finish(null, response);
    });
    child.stdin.end(`${JSON.stringify(request)}\n`, "utf8");
  });
}

function testPrefixArgs() {
  const raw = String(process.env.SPATIAL_REVIEW_TEST_UNITY_CTX_PREFIX_ARGS || "");
  if (!raw) return [];
  if (process.env.NODE_ENV !== "test") throw new Error("Test unity-ctx prefix arguments require NODE_ENV=test.");
  const value = JSON.parse(raw);
  if (!Array.isArray(value) || value.some(item => typeof item !== "string" || !item)) {
    throw new Error("SPATIAL_REVIEW_TEST_UNITY_CTX_PREFIX_ARGS must be a JSON string array.");
  }
  return value;
}

function callUnity(tool, args) {
  return new Promise((resolve, reject) => {
    if (!hasPinnedUnitySession) return reject(new Error("Secure Unity session is unavailable. Open this review from the Unity Editor."));
    const socket = net.createConnection({ host: "127.0.0.1", port: pinnedUnityPort });
    let data = "";
    const timeoutMs = tool.startsWith("confirm_") ? 5 * 60 * 1000 + 5000 : 32000;
    const timer = setTimeout(() => socket.destroy(new Error("Unity request timed out.")), timeoutMs);
    socket.setEncoding("utf8");
    socket.on("connect", () => socket.write(JSON.stringify({ nonce: pinnedUnityNonce, tool, argumentsJson: JSON.stringify(args) }) + "\n"));
    socket.on("data", chunk => {
      data += chunk;
      const newline = data.indexOf("\n");
      if (newline < 0) return;
      clearTimeout(timer);
      socket.end();
      const response = JSON.parse(data.slice(0, newline));
      response.ok ? resolve(response) : reject(new Error(response.error || "Unity rejected the request."));
    });
    socket.on("error", error => { clearTimeout(timer); reject(error); });
  });
}

function requireNonce(request) {
  const supplied = String(request.headers["x-spatial-review-nonce"] || "");
  const expiresAt = reviewNonces.get(supplied);
  reviewNonces.delete(supplied);
  if (!expiresAt || expiresAt < Date.now()) throw new Error("Invalid or expired review nonce.");
}

function issueReviewNonce() {
  const now = Date.now();
  for (const [value, expiresAt] of reviewNonces) if (expiresAt < now) reviewNonces.delete(value);
  const value = crypto.randomBytes(24).toString("base64url");
  reviewNonces.set(value, now + 2 * 60 * 1000);
  return value;
}

function resultOf(response) {
  return JSON.parse(response.resultJson || "{}");
}

function inside(root, candidate) {
  const relative = path.relative(path.resolve(root), path.resolve(candidate));
  return relative && !relative.startsWith("..") && !path.isAbsolute(relative);
}

function readJson(request) {
  return new Promise((resolve, reject) => {
    let data = "";
    request.on("data", chunk => {
      data += chunk;
      if (data.length > 1024 * 1024) request.destroy(new Error("Request body is too large."));
    });
    request.on("end", () => {
      try { resolve(data ? JSON.parse(data) : {}); }
      catch (error) { reject(new Error(`Invalid JSON: ${error.message}`)); }
    });
    request.on("error", reject);
  });
}

function send(response, status, body) {
  response.statusCode = status;
  if (body == null) return response.end();
  response.setHeader("Content-Type", "application/json; charset=utf-8");
  response.setHeader("Cache-Control", "no-store");
  response.setHeader("X-Content-Type-Options", "nosniff");
  response.end(JSON.stringify(body));
}
