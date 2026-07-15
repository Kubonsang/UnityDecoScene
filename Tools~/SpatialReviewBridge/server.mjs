#!/usr/bin/env node

import { execFile } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const execute = promisify(execFile);
const projectArg = process.argv.indexOf("--project");
const projectRoot = path.resolve(projectArg >= 0 ? process.argv[projectArg + 1] : process.cwd());
const sessionPath = path.join(projectRoot, "Library", "DungeonDecorator", "session.json");
const workflowRoot = path.join(projectRoot, "Library", "DungeonDecorator", "CalibrationWorkflow");
const workflowStatePath = path.join(workflowRoot, "state.json");
const workflowPagePath = path.join(workflowRoot, "review.html");
const roomReviewsRoot = path.join(projectRoot, "Library", "DungeonDecorator", "RoomReviews");
const roomReviewPointerPath = path.join(roomReviewsRoot, "current.json");
const serverDirectory = path.dirname(fileURLToPath(import.meta.url));
const roomReviewPagePath = path.resolve(serverDirectory, "..", "..", "Editor", "Review", "Templates", "RoomReviewTemplate.html");
const unityCtx = process.env.UNITY_CTX_BIN || "unity-ctx";
const reviewNonce = crypto.randomBytes(24).toString("hex");
const port = Number(process.env.SPATIAL_REVIEW_PORT || 4174);
const allowedOrigins = new Set([
  "null",
  "http://localhost:4173",
  "http://127.0.0.1:4173",
  `http://localhost:${port}`,
  `http://127.0.0.1:${port}`,
]);
const allowedHosts = new Set([`127.0.0.1:${port}`, `localhost:${port}`]);

const server = http.createServer(async (request, response) => {
  if (!allowedHosts.has(String(request.headers.host || "").toLowerCase())) {
    return send(response, 403, { error: "Host is not allowed." });
  }
  const origin = request.headers.origin || "";
  const requestUrl = new URL(request.url || "/", `http://127.0.0.1:${port}`);
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
        connected: fs.existsSync(sessionPath),
        workflowAvailable: fs.existsSync(workflowStatePath),
        nonce: reviewNonce,
      });
    }
    if (request.method === "GET" && requestUrl.pathname === "/workflow") {
      if (!fs.existsSync(workflowPagePath)) throw new Error("Calibration workflow review page is not available.");
      response.statusCode = 200;
      response.setHeader("Content-Type", "text/html; charset=utf-8");
      response.setHeader("Cache-Control", "no-store");
      return response.end(fs.readFileSync(workflowPagePath));
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
      return send(response, 200, loadWorkflow());
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
      const suffix = requestUrl.searchParams.get("evidence") === "1" ? "-evidence" : "";
      const filePath = path.join(item.captureDirectory, `${view}${suffix}.png`);
      const captureRoot = path.join(projectRoot, "Library", "DungeonDecorator", "SpatialCaptures");
      if (!inside(captureRoot, filePath) || !fs.existsSync(filePath)) throw new Error("Workflow capture image is not available.");
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
      const suffix = requestUrl.searchParams.get("evidence") === "1" ? "-evidence" : "";
      const filePath = path.join(projectRoot, "Library", "DungeonDecorator", "SpatialCaptures", inspection.sessionId, `${view}${suffix}.png`);
      const captureRoot = path.join(projectRoot, "Library", "DungeonDecorator", "SpatialCaptures");
      if (!inside(captureRoot, filePath) || !fs.existsSync(filePath)) throw new Error("Capture image is not available.");
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
      return send(response, 200, await reviewDraft(body));
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

async function reviewDraft(body) {
  const decision = body.decision;
  if (!["Approved", "RevisionRequested", "UnableToJudge"].includes(decision)) throw new Error("Unsupported human decision.");
  if (!String(body.reviewer || "").trim()) throw new Error("reviewer is required.");

  let draftPath = body.draftPath;
  if (!draftPath) {
    const draftResponse = await callUnity("get_spatial_contract_draft", {});
    const payload = JSON.parse(draftResponse.resultJson || "{}");
    draftPath = payload.drafts?.[0]?.path;
  }
  if (!draftPath || !path.isAbsolute(draftPath) || !inside(path.join(projectRoot, "Library"), draftPath)) {
    throw new Error("Draft must be an active Unity draft under this project's Library folder.");
  }

  const contract = JSON.parse(fs.readFileSync(draftPath, "utf8"));
  const currentPath = contractPath(contract);
  await runCtx(["spatial", "validate", "--json", draftPath]);
  const review = await runCtx([
    "spatial", "review", "--draft", draftPath, "--decision", decision,
    "--reviewer", String(body.reviewer), "--issues", (body.issues || []).join(","),
    "--comment", String(body.comment || ""), "--write", "--json",
  ]);

  if (decision !== "Approved") return { status: decision, draftPath, currentPath: null, review };
  const diff = await runCtx(["spatial", "diff", "--current", currentPath, "--draft", draftPath, "--json"]);
  const applied = await runCtx(["spatial", "apply", "--current", currentPath, "--draft", draftPath, "--write", "--json"]);
  const verified = await runCtx(["spatial", "validate", "--json", currentPath]);
  return { status: "Approved", draftPath, currentPath, review, diff, applied, verified };
}

async function reviewWorkflowItem(body) {
  const state = loadWorkflow();
  const item = state.items?.find(value => value.id === String(body.id || ""));
  if (!item) throw new Error("Calibration workflow item was not found.");
  if (item.status !== "AwaitingHumanReview") throw new Error(`Item is not awaiting review: ${item.status}`);
  if (!item.captureHash || item.captureHash !== String(body.captureHash || "")) throw new Error("Capture hash is stale.");
  if (!item.draftPath || !path.isAbsolute(item.draftPath) || !inside(path.join(projectRoot, "Library"), item.draftPath)) {
    throw new Error("Workflow draft is outside the project Library folder.");
  }
  const issues = normalizeIssues(body.issues);
  const comment = cleanText(body.comment, 4000, "comment");
  const result = await reviewDraft({
    draftPath: item.draftPath,
    decision: body.decision,
    reviewer: body.reviewer,
    issues,
    comment,
  });
  item.status = result.status;
  item.reviewer = String(body.reviewer || "");
  item.reviewedUtc = new Date().toISOString();
  item.comment = comment;
  if (result.status === "RevisionRequested") {
    item.revisionIssueCodes = issues;
    item.revisionComment = comment;
  }
  state.updatedUtc = new Date().toISOString();
  state.status = deriveWorkflowStatus(state);
  atomicJson(workflowStatePath, state);
  return { status: result.status, id: item.id, captureHash: item.captureHash };
}

async function reviewWorkflowBatch(body) {
  if (body.decision !== "Approved") throw new Error("Batch review currently supports approval only.");
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
    if (!item || item.captureHash !== String(requestItem.captureHash || "")) throw new Error("Capture hash is stale.");
  }

  const results = [];
  for (const requestItem of requested) {
    results.push(await reviewWorkflowItem({
      id: requestItem.id,
      captureHash: requestItem.captureHash,
      decision: "Approved",
      reviewer: body.reviewer,
      issues: [],
      comment: body.comment || "Batch-approved after individual visual checks.",
    }));
  }
  return { status: "Approved", count: results.length, results };
}

async function reviewCurrentRoom(body) {
  const decision = String(body.decision || "");
  if (!["Approved", "RevisionRequested", "UnableToJudge"].includes(decision)) {
    throw new Error("Unsupported human decision.");
  }

  const { state, statePath } = loadCurrentRoomReview();
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

  const response = await callUnity("verify_room_review", {
    runId: state.runId,
    inputHash: state.inputHash,
    technicalReportHash: state.technicalReportHash,
    captureSetHash: roomCaptureHash(state),
  });
  const verification = resultOf(response);
  if (!verification.valid) throw new Error(`Unity rejected stale room evidence: ${verification.reason || "verification failed"}`);

  const issues = normalizeIssues(body.issues);
  const comment = cleanText(body.comment, 4000, "comment");
  if (decision === "RevisionRequested" && issues.length === 0 && !comment) {
    throw new Error("RevisionRequested requires at least one issue or a comment.");
  }

  const reviewedUtc = new Date().toISOString();
  state.decision = {
    value: decision,
    inputHash: state.inputHash,
    technicalReportHash: state.technicalReportHash,
    captureSetHash: roomCaptureHash(state),
    reviewer: "local-user",
    issueCodes: issues,
    comment,
    reviewedUtc,
  };
  state.status = decision;
  state.updatedUtc = reviewedUtc;
  const reviewPath = path.join(path.dirname(statePath), "runs", state.runId, "human-review.json");
  if (!inside(roomReviewsRoot, reviewPath)) throw new Error("Room review record path is invalid.");
  atomicJson(reviewPath, state.decision);
  atomicJson(statePath, state);
  return {
    runId: state.runId,
    status: state.status,
    inputHash: state.inputHash,
    validationHash: state.technicalReportHash,
    captureHash: roomCaptureHash(state),
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
    captures: (state.capture?.views || []).map(capture => ({
      view: String(capture?.id || ""),
      label: roomCaptureLabel(capture?.id),
      required: capture?.required !== false,
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
  if (!inside(realRoot, realCandidate)) throw new Error("Room review path escapes the local review data root.");
}

function technicalErrorCount(state) {
  return Number(state.technicalErrorCount || 0);
}

function roomCaptureHash(state) {
  return String(state.capture?.captureSetHash || "");
}

function roomCaptureLabel(id) {
  return ({ top: "상단", entrance: "입구 시점", "primary-observation": "입구 시점", "corner-a": "첫 번째 모서리", "corner-b": "두 번째 모서리" })[id]
    || String(id || "캡처");
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
  if (state.schemaVersion !== 1 || !Array.isArray(state.items)) throw new Error("Unsupported calibration workflow state.");
  for (const item of state.items) {
    if (!item.reviewKind) item.reviewKind = "asset";
    if (!Array.isArray(item.revisionIssueCodes)) item.revisionIssueCodes = [];
    if (item.status === "RevisionRequested" && !item.revisionComment) item.revisionComment = item.comment || "";
  }
  return state;
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

function contractPath(contract) {
  if (contract.contract_type === "asset" && contract.asset?.asset_guid) {
    return path.join(projectRoot, "Assets", "SpatialContracts", "Assets", `${contract.asset.asset_guid}.spatial.json`);
  }
  if (contract.contract_type === "interaction" && contract.interaction) {
    const item = contract.interaction;
    const target = String(item.target_key || "target").replace(/[^a-zA-Z0-9_-]/g, "-");
    const name = `${item.subject_guid}__${target}__${item.relation}.interaction.json`;
    return path.join(projectRoot, "Assets", "SpatialContracts", "Interactions", name);
  }
  throw new Error("Draft does not contain a supported asset or interaction contract.");
}

async function runCtx(args) {
  try {
    const result = await execute(unityCtx, args, { cwd: projectRoot, windowsHide: true, timeout: 30000, maxBuffer: 1024 * 1024 });
    return JSON.parse(result.stdout || "{}");
  } catch (error) {
    const detail = String(error.stderr || error.message || error).trim();
    throw new Error(`unity-ctx failed: ${detail}`);
  }
}

function callUnity(tool, args) {
  return new Promise((resolve, reject) => {
    if (!fs.existsSync(sessionPath)) return reject(new Error("Unity session is not available."));
    const session = JSON.parse(fs.readFileSync(sessionPath, "utf8"));
    const socket = net.createConnection({ host: "127.0.0.1", port: session.port });
    let data = "";
    const timer = setTimeout(() => socket.destroy(new Error("Unity request timed out.")), 32000);
    socket.setEncoding("utf8");
    socket.on("connect", () => socket.write(JSON.stringify({ nonce: session.nonce, tool, argumentsJson: JSON.stringify(args) }) + "\n"));
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
  const supplied = Buffer.from(String(request.headers["x-spatial-review-nonce"] || ""));
  const expected = Buffer.from(reviewNonce);
  if (supplied.length !== expected.length || !crypto.timingSafeEqual(supplied, expected)) throw new Error("Invalid review nonce.");
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
