#!/usr/bin/env node

import fs from "node:fs";
import net from "node:net";
import path from "node:path";
import readline from "node:readline";
import { spawn } from "node:child_process";

const projectArgIndex = process.argv.indexOf("--project");
const projectRoot = path.resolve(projectArgIndex >= 0 && process.argv[projectArgIndex + 1] ? process.argv[projectArgIndex + 1] : process.cwd());
const sessionFile = path.join(projectRoot, "Library", "DungeonDecorator", "session.json");

const tools = [
  tool("inspect_room", "Inspect a loaded ConceptRoom, including bounds, observation points, floors, and keep-clear zones.", {
    roomId: { type: "string", description: "Room id or GameObject name. Omit to use the first loaded room." }
  }),
  tool("get_concept_brief", "Read a RoomConceptBrief and return its references as image content.", {
    assetPath: { type: "string", description: "Unity asset path, for example Assets/Concept/Brief.asset. Omit to use the active preview." }
  }),
  tool("render_asset_catalog", "Render the current decor catalog as a visual contact sheet.", {
    assetPath: { type: "string", description: "Unity asset path to a DecorCatalog. Omit to use the active preview." }
  }),
  tool("search_decor_assets", "Search reviewed decor descriptors by words, role, and style set.", {
    assetPath: { type: "string" },
    query: { type: "string" },
    role: { type: "string", enum: ["Hero", "Support", "StoryEvidence", "Clutter", "LightingCue", "DecalCue"] },
    styleSet: { type: "string" }
  }),
  compositionTool("create_composition_preview", "Create a non-persistent deterministic room preview. This cannot apply scene changes."),
  compositionTool("refine_composition", "Replace the current preview while preserving explicitly locked placements."),
  tool("lock_preview_group", "Lock or unlock preview placements by placement id or composition element id.", {
    ids: { type: "array", items: { type: "string" } },
    locked: { type: "boolean", default: true }
  }, ["ids"]),
  tool("validate_preview", "Run deterministic technical validation on the active preview.", {}),
  tool("capture_preview_views", "Explicit diagnostic only: capture top, observation, and corner views and return image content. Prefer prepare_room_review for the token-light human review workflow.", {}),
  tool("submit_visual_review", "Store advisory AI mood, style, story, and composition notes. These scores never approve a room and never unlock Apply.", {
    mood: { type: "integer", minimum: 0, maximum: 100 },
    style: { type: "integer", minimum: 0, maximum: 100 },
    story: { type: "integer", minimum: 0, maximum: 100 },
    composition: { type: "integer", minimum: 0, maximum: 100 },
    feedback: { type: "string" }
  }, ["mood", "style", "story", "composition", "feedback"]),
  tool("prepare_room_review", "Run deterministic room validation and prepare or reuse the fixed human-review capture set. Returns only a compact brief and never returns images, approves, or applies.", {}),
  tool("get_room_review_agent_brief", "Read the compact room-review status and next action without scene hierarchy, transforms, absolute paths, or images.", {}),
  tool("discard_preview", "Discard the active non-persistent preview.", {}),
  tool("begin_spatial_calibration", "Open a non-persistent Unity PreviewSceneStage for a reviewed descriptor. This does not approve, apply, or write a contract.", {
    descriptorAssetPath: { type: "string", description: "Unity asset path to a DecorAssetDescriptor." },
    template: { type: "string", enum: ["WallMounted", "WallBackedFloorSupported", "FloorSupported", "SupportedBy"] },
    targetPrefabPath: { type: "string", description: "Required only for SupportedBy." },
    subjectFrameId: { type: "string", enum: ["bottom", "back", "top"], description: "Optional SupportedBy contact frame. Defaults to bottom; use back to lay an upright book flat." },
    targetFrameId: { type: "string", enum: ["top"], description: "Optional SupportedBy target frame. Surface Arrangement 0.1 supports top only." }
  }, ["descriptorAssetPath", "template"]),
  tool("inspect_spatial_calibration", "Inspect the active temporary Spatial Calibration session. This is read-only and cannot approve or apply a contract.", {}),
  tool("capture_spatial_calibration", "Run deterministic validation, create the four human-review captures, and write temporary drafts under Library. This cannot approve or apply a contract.", {}),
  tool("get_spatial_contract_draft", "Read temporary Spatial Contract drafts for analysis. Drafts are not approved contracts.", {}),
  tool("submit_spatial_contract_proposal", "Submit a temporary AI suggestion for relation, frames, or tolerances. A proposal cannot pass, approve, apply, or save a tracked contract.", {
    proposalJson: { type: "string", description: "A JSON proposal describing suggested relation, frames, tolerances, and rationale." }
  }, ["proposalJson"]),
  tool("get_deterministic_validation_report", "Run and return the deterministic collision, penetration, gap, support, and direction report. The report is not a human approval.", {}),
  tool("inspect_surface_arrangement", "Read temporary Surface Arrangement proposals and human-review evidence. This is read-only and cannot approve or apply anything.", {}),
  tool("submit_surface_arrangement_proposal", "Store an AI-authored Surface Arrangement suggestion in Unity SessionState only. This cannot validate, approve, apply, or write tracked files.", {
    arrangementId: { type: "string" },
    targetElementId: { type: "string" },
    targetFrameId: { type: "string", enum: ["top"], default: "top" },
    preset: { type: "string", enum: ["Neat", "InUse", "Scattered"] },
    amount: { type: "number", minimum: 0, maximum: 1 },
    orderliness: { type: "number", minimum: 0, maximum: 1 },
    grouping: { type: "number", minimum: 0, maximum: 1 },
    stacking: { type: "number", minimum: 0, maximum: 1 },
    edgeMargin: { type: "number", minimum: 0.04, default: 0.08 },
    maxStackHeight: { type: "integer", minimum: 1, maximum: 3, default: 3 },
    seedOffset: { type: "integer", minimum: 0, default: 0 },
    membersJson: { type: "string", description: "JSON array of descriptor/count suggestions. Kept behind a JSON boundary until the core schema is available." },
    rationale: { type: "string" }
  }, ["arrangementId", "targetElementId", "preset", "amount", "orderliness", "grouping", "stacking"])
];

const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity, terminal: false });
rl.on("line", line => {
  if (!line.trim()) return;
  let request;
  try { request = JSON.parse(line); }
  catch (error) { return writeError(null, -32700, `Invalid JSON: ${error.message}`); }
  handle(request).catch(error => writeError(request.id ?? null, -32603, error.message));
});

async function handle(request) {
  const { id, method, params = {} } = request;
  if (method === "notifications/initialized" || method === "notifications/cancelled") return;
  if (method === "initialize") {
    const genericTools = await unityCtx.tools();
    return writeResult(id, {
      protocolVersion: params.protocolVersion || "2024-11-05",
      capabilities: { tools: { listChanged: false } },
      serverInfo: { name: "unity-concept-room", version: "0.2.0" },
      instructions: `Inspect the room and concept, view the catalog, choose only matching reviewed assets, then create a preview. Use prepare_room_review after refinement; it returns a compact status without image tokens. When it says OPEN_REVIEW, stop and wait for the human decision in the local review UI. AI scores are advisory only. AI cannot pass, approve, apply, or write tracked contracts; never claim the preview was applied. Optional unity-ctx integration: ${genericTools.length ? "connected" : "disconnected"}.`
    });
  }
  if (method === "ping") return writeResult(id, {});
  if (method === "tools/list") return writeResult(id, { tools: [...tools, ...(await unityCtx.tools())] });
  if (method === "tools/call") {
    const name = params.name;
    if (!tools.some(item => item.name === name)) {
      const generic = await unityCtx.tools();
      if (!generic.some(item => item.name === name)) return writeError(id, -32602, `Unknown tool '${name}'.`);
      try { return writeResult(id, await unityCtx.request("tools/call", { name, arguments: params.arguments || {} })); }
      catch (error) { return writeResult(id, { content: [{ type: "text", text: `unity-ctx unavailable: ${error.message}` }], isError: true }); }
    }
    const response = await callUnity(name, params.arguments || {});
    const content = [{ type: "text", text: prettyResult(response.resultJson) }];
    for (const imagePath of response.imagePaths || []) {
      if (!fs.existsSync(imagePath)) continue;
      content.push({ type: "image", mimeType: mimeType(imagePath), data: fs.readFileSync(imagePath).toString("base64") });
    }
    return writeResult(id, { content, isError: !response.ok });
  }
  return writeError(id ?? null, -32601, `Unsupported method '${method}'.`);
}

class UnityCtxProxy {
  constructor(command) { this.command = command; this.child = null; this.pending = new Map(); this.nextId = 1; this.cachedTools = null; this.failed = false; }
  async tools() {
    if (this.cachedTools) return this.cachedTools;
    if (this.failed) return [];
    try {
      await this.start();
      const listed = await this.request("tools/list", {});
      this.cachedTools = (listed.tools || []).filter(item => !tools.some(local => local.name === item.name));
      return this.cachedTools;
    } catch { this.failed = true; this.stop(); return []; }
  }
  async start() {
    if (this.child) return;
    this.child = spawn(this.command, ["mcp"], { cwd: projectRoot, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    const output = readline.createInterface({ input: this.child.stdout, crlfDelay: Infinity });
    output.on("line", line => { let message; try { message = JSON.parse(line); } catch { return; } const pending = this.pending.get(message.id); if (!pending) return; this.pending.delete(message.id); message.error ? pending.reject(new Error(message.error.message)) : pending.resolve(message.result); });
    this.child.on("error", error => this.rejectAll(error));
    this.child.on("exit", code => { this.rejectAll(new Error(`unity-ctx exited with code ${code}`)); this.child = null; });
    await this.request("initialize", { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "unity-concept-room", version: "0.2.0" } });
  }
  request(method, params) {
    if (!this.child) return Promise.reject(new Error("unity-ctx is not running"));
    const id = this.nextId++;
    return new Promise((resolve, reject) => { const timer = setTimeout(() => { this.pending.delete(id); reject(new Error("unity-ctx request timed out")); }, 15000); this.pending.set(id, { resolve: value => { clearTimeout(timer); resolve(value); }, reject: error => { clearTimeout(timer); reject(error); } }); this.child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n"); });
  }
  rejectAll(error) { for (const pending of this.pending.values()) pending.reject(error); this.pending.clear(); }
  stop() { try { this.child?.kill(); } catch {} this.child = null; }
}

const unityCtx = new UnityCtxProxy(process.env.UNITY_CTX_BIN || "unity-ctx");

function callUnity(name, args) {
  return new Promise((resolve, reject) => {
    if (!fs.existsSync(sessionFile)) return reject(new Error(`Unity session file not found at ${sessionFile}. Open the Unity project and wait for compilation to finish.`));
    let session;
    try { session = JSON.parse(fs.readFileSync(sessionFile, "utf8")); }
    catch (error) { return reject(new Error(`Could not read Unity session: ${error.message}`)); }

    const socket = net.createConnection({ host: "127.0.0.1", port: session.port });
    let received = "";
    const timeout = setTimeout(() => socket.destroy(new Error("Unity request timed out.")), 32000);
    socket.setEncoding("utf8");
    socket.on("connect", () => socket.write(JSON.stringify({ nonce: session.nonce, tool: name, argumentsJson: JSON.stringify(args) }) + "\n"));
    socket.on("data", chunk => {
      received += chunk;
      const newline = received.indexOf("\n");
      if (newline < 0) return;
      clearTimeout(timeout);
      socket.end();
      try {
        const response = JSON.parse(received.slice(0, newline));
        if (!response.ok) return resolve({ ...response, resultJson: JSON.stringify({ error: response.error }) });
        resolve(response);
      } catch (error) { reject(new Error(`Invalid response from Unity: ${error.message}`)); }
    });
    socket.on("error", error => { clearTimeout(timeout); reject(error); });
  });
}

function tool(name, description, properties, required = []) {
  return { name, description, inputSchema: { type: "object", properties, required, additionalProperties: false } };
}

function compositionTool(name, description) {
  return tool(name, description, {
    roomId: { type: "string", description: "ConceptRoom id or GameObject name." },
    briefAssetPath: { type: "string", description: "Unity asset path to RoomConceptBrief." },
    catalogAssetPath: { type: "string", description: "Unity asset path to DecorCatalog." },
    seed: { type: "integer", default: 12345 },
    density: { type: "number", minimum: 0, maximum: 1, default: 0.5 },
    elements: {
      type: "array",
      items: {
        type: "object",
        properties: {
          elementId: { type: "string" },
          descriptorId: { type: "string" },
          role: { type: "string", enum: ["Hero", "Support", "StoryEvidence", "Clutter", "LightingCue", "DecalCue"] },
          relation: { type: "string", enum: ["Independent", "Surrounds", "Faces", "Supports", "ScatteredNear", "AttachedTo", "Avoids"] },
          anchorElementId: { type: "string" },
          count: { type: "integer", minimum: 1, default: 1 },
          preferredZone: { type: "string", enum: ["Any", "Focal", "Center", "Perimeter", "Corner"] },
          spacing: { type: "number", minimum: 0, default: 1 },
          locked: { type: "boolean", default: false }
        },
        required: ["descriptorId", "role"]
      }
    }
  }, ["roomId", "briefAssetPath", "catalogAssetPath", "elements"]);
}

function prettyResult(value) {
  try { return JSON.stringify(JSON.parse(value || "{}"), null, 2); }
  catch { return value || "{}"; }
}

function mimeType(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  return ext === ".jpg" || ext === ".jpeg" ? "image/jpeg" : "image/png";
}

function writeResult(id, result) { process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, result }) + "\n"); }
function writeError(id, code, message) { process.stdout.write(JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } }) + "\n"); }
