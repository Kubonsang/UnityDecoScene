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
const unityCtx = process.env.UNITY_CTX_BIN || "unity-ctx";
const reviewNonce = crypto.randomBytes(24).toString("hex");
const allowedOrigins = new Set(["http://localhost:4173", "http://127.0.0.1:4173"]);
const port = Number(process.env.SPATIAL_REVIEW_PORT || 4174);

const server = http.createServer(async (request, response) => {
  const origin = request.headers.origin || "";
  if (origin && !allowedOrigins.has(origin)) return send(response, 403, { error: "Origin is not allowed." });
  if (origin) {
    response.setHeader("Access-Control-Allow-Origin", origin);
    response.setHeader("Vary", "Origin");
  }
  response.setHeader("Access-Control-Allow-Headers", "Content-Type, X-Spatial-Review-Nonce");
  response.setHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
  if (request.method === "OPTIONS") return send(response, 204, null);

  try {
    if (request.method === "GET" && request.url === "/api/health") {
      return send(response, 200, { connected: fs.existsSync(sessionPath), nonce: reviewNonce, projectRoot });
    }
    if (request.method === "GET" && request.url === "/api/spatial/session") {
      return send(response, 200, await callUnity("inspect_spatial_calibration", {}));
    }
    if (request.method === "POST" && request.url === "/api/spatial/capture") {
      requireNonce(request);
      return send(response, 200, await callUnity("capture_spatial_calibration", {}));
    }
    if (request.method === "POST" && request.url === "/api/spatial/review") {
      requireNonce(request);
      const body = await readJson(request);
      return send(response, 200, await reviewDraft(body));
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
