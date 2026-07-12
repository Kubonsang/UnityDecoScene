#!/usr/bin/env node

import { execFile } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import path from "node:path";
import { promisify } from "node:util";

const execute = promisify(execFile);
const projectArg = process.argv.indexOf("--project");
const projectRoot = path.resolve(projectArg >= 0 ? process.argv[projectArg + 1] : process.cwd());
const sessionPath = path.join(projectRoot, "Library", "DungeonDecorator", "session.json");
const workflowRoot = path.join(projectRoot, "Library", "DungeonDecorator", "CalibrationWorkflow");
const workflowStatePath = path.join(workflowRoot, "state.json");
const workflowPagePath = path.join(workflowRoot, "review.html");
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

const server = http.createServer(async (request, response) => {
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
        projectRoot,
      });
    }
    if (request.method === "GET" && requestUrl.pathname === "/workflow") {
      if (!fs.existsSync(workflowPagePath)) throw new Error("Calibration workflow review page is not available.");
      response.statusCode = 200;
      response.setHeader("Content-Type", "text/html; charset=utf-8");
      response.setHeader("Cache-Control", "no-store");
      return response.end(fs.readFileSync(workflowPagePath));
    }
    if (request.method === "GET" && requestUrl.pathname === "/api/spatial/workflow") {
      return send(response, 200, loadWorkflow());
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
  const result = await reviewDraft({
    draftPath: item.draftPath,
    decision: body.decision,
    reviewer: body.reviewer,
    issues: body.issues || [],
    comment: body.comment || "",
  });
  item.status = result.status;
  item.reviewer = String(body.reviewer || "");
  item.reviewedUtc = new Date().toISOString();
  item.comment = String(body.comment || "");
  state.updatedUtc = new Date().toISOString();
  state.status = deriveWorkflowStatus(state);
  atomicJson(workflowStatePath, state);
  return { status: result.status, id: item.id, captureHash: item.captureHash };
}

function loadWorkflow() {
  if (!fs.existsSync(workflowStatePath)) throw new Error("Calibration workflow has not been created in Unity.");
  const state = JSON.parse(fs.readFileSync(workflowStatePath, "utf8"));
  if (state.schemaVersion !== 1 || !Array.isArray(state.items)) throw new Error("Unsupported calibration workflow state.");
  return state;
}

function deriveWorkflowStatus(state) {
  if (state.items.some(item => item.status === "AwaitingHumanReview")) return "AwaitingHumanReview";
  if (state.items.some(item => item.status === "TechnicalFailed")) return "TechnicalFailed";
  if (state.items.some(item => item.status === "NeedsRelationReview")) return "NeedsRelationReview";
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
  if (request.headers["x-spatial-review-nonce"] !== reviewNonce) throw new Error("Invalid review nonce.");
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
  response.end(JSON.stringify(body));
}
