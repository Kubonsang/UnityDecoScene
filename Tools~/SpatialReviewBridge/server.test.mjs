import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const serverPath = fileURLToPath(new URL("./server.mjs", import.meta.url));

test("room review serves sanitized evidence and atomically records a current approval", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());

  const pageResponse = await fetch(`${fixture.baseUrl}/room-review`);
  assert.equal(pageResponse.status, 200);
  assert.match(await pageResponse.text(), /기술 검사는 도구가, 최종 성공 판정은 내가 합니다/);

  const currentResponse = await fetch(`${fixture.baseUrl}/api/room-review/current`);
  assert.equal(currentResponse.status, 200);
  const current = await currentResponse.json();
  assert.equal(current.runId, "run-1");
  assert.equal(current.validationHash, "validation-1");
  assert.equal(current.captureHash, "capture-1");
  assert.equal(current.captures[0].view, "top");
  assert.equal(JSON.stringify(current).includes("imagePath"), false);

  const imageResponse = await fetch(`${fixture.baseUrl}/api/room-review/image/top?hash=capture-1`);
  assert.equal(imageResponse.status, 200);
  assert.equal(Buffer.from(await imageResponse.arrayBuffer()).toString("utf8"), "fake-png");

  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const decisionResponse = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Spatial-Review-Nonce": health.nonce },
    body: JSON.stringify({
      runId: "run-1",
      inputHash: "input-1",
      validationHash: "validation-1",
      captureHash: "capture-1",
      decision: "Approved",
      issues: [],
      comment: "네 시점을 직접 확인함",
    }),
  });
  assert.equal(decisionResponse.status, 200);
  const stored = JSON.parse(fs.readFileSync(fixture.statePath, "utf8"));
  assert.equal(stored.status, "Approved");
  assert.equal(stored.decision.value, "Approved");
  assert.equal(stored.decision.inputHash, "input-1");
  assert.equal(stored.decision.technicalReportHash, "validation-1");
  assert.equal(stored.decision.captureSetHash, "capture-1");
  assert.equal(fs.existsSync(fixture.reviewPath), true);
});

test("room review rejects stale hashes and unexplained revision requests", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const post = body => fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Spatial-Review-Nonce": health.nonce },
    body: JSON.stringify(body),
  });
  const common = { runId: "run-1", inputHash: "input-1", validationHash: "validation-1", captureHash: "capture-1" };

  const stale = await post({ ...common, captureHash: "old-capture", decision: "Approved", issues: [], comment: "" });
  assert.equal(stale.status, 400);
  assert.match((await stale.json()).error, /captureHash is stale/);

  const unexplained = await post({ ...common, decision: "RevisionRequested", issues: [], comment: "" });
  assert.equal(unexplained.status, 400);
  assert.match((await unexplained.json()).error, /requires at least one issue or a comment/);

  const stored = JSON.parse(fs.readFileSync(fixture.statePath, "utf8"));
  assert.equal(stored.status, "AwaitingHumanReview");
});

test("room review current pointer cannot leave its selected room directory", async t => {
  const fixture = await startFixture({ pointerStatePath: "../outside/state.json" });
  t.after(() => fixture.close());
  const response = await fetch(`${fixture.baseUrl}/api/room-review/current`);
  assert.equal(response.status, 400);
  assert.match((await response.json()).error, /must be the selected room's state\.json/);
});

test("room review decision requires the nonce and a zero-error technical gate", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const body = {
    runId: "run-1", inputHash: "input-1", validationHash: "validation-1", captureHash: "capture-1",
    decision: "Approved", issues: [], comment: "",
  };
  const withoutNonce = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
  });
  assert.equal(withoutNonce.status, 400);
  assert.match((await withoutNonce.json()).error, /Invalid review nonce/);

  const state = JSON.parse(fs.readFileSync(fixture.statePath, "utf8"));
  state.technicalErrorCount = 1;
  state.technicalErrorCodes = ["OBB_OVERLAP"];
  fs.writeFileSync(fixture.statePath, JSON.stringify(state, null, 2));
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const blocked = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Spatial-Review-Nonce": health.nonce },
    body: JSON.stringify(body),
  });
  assert.equal(blocked.status, 400);
  assert.match((await blocked.json()).error, /technical errors cannot be approved or decided/);
});

test("technical-failed room state remains readable before captures exist", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const state = JSON.parse(fs.readFileSync(fixture.statePath, "utf8"));
  state.status = "TechnicalFailed";
  state.technicalErrorCount = 1;
  state.technicalErrorCodes = ["OBB_OVERLAP"];
  state.capture = { captureSetHash: "", captureProfileHash: "", views: [] };
  fs.writeFileSync(fixture.statePath, JSON.stringify(state, null, 2));

  const response = await fetch(`${fixture.baseUrl}/api/room-review/current`);
  assert.equal(response.status, 200);
  const current = await response.json();
  assert.equal(current.status, "TechnicalFailed");
  assert.equal(current.technicalErrorCount, 1);
  assert.deepEqual(current.captures, []);
});

async function startFixture(options = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "room-review-bridge-"));
  const roomRoot = path.join(root, "Library", "DungeonDecorator", "RoomReviews", "room-a");
  fs.mkdirSync(roomRoot, { recursive: true });
  const imagePath = path.join(roomRoot, "top.png");
  fs.writeFileSync(imagePath, "fake-png");
  const imageHash = crypto.createHash("sha256").update(fs.readFileSync(imagePath)).digest("hex");
  const statePath = path.join(roomRoot, "state.json");
  fs.writeFileSync(statePath, JSON.stringify({
    schemaVersion: 1,
    runId: "run-1",
    targetId: "room-id",
    targetName: "잊힌 감시실",
    status: "AwaitingHumanReview",
    createdUtc: "2026-07-14T00:00:00.000Z",
    updatedUtc: "2026-07-14T00:00:00.000Z",
    inputHash: "input-1",
    changes: { scope: 4, affectedIds: ["corner-pillars"] },
    technicalReportHash: "validation-1",
    technicalErrorCount: 0,
    technicalErrorCodes: [],
    capture: {
      captureSetHash: "capture-1",
      captureProfileHash: "profile-1",
      createdUtc: "2026-07-14T00:00:00.000Z",
      views: [{ id: "top", contentHash: imageHash, imagePath, required: true }],
    },
    decision: { value: "Pending", issueCodes: [] },
  }, null, 2));
  const pointerRoot = path.dirname(roomRoot);
  fs.writeFileSync(path.join(pointerRoot, "current.json"), JSON.stringify({
    schemaVersion: 1,
    roomKey: "room-a",
    statePath: options.pointerStatePath || "room-a/state.json",
  }));

  const unityPort = await freePort();
  const unityNonce = "test-unity-nonce";
  const unityServer = net.createServer(socket => {
    let data = "";
    socket.setEncoding("utf8");
    socket.on("data", chunk => {
      data += chunk;
      const newline = data.indexOf("\n");
      if (newline < 0) return;
      const request = JSON.parse(data.slice(0, newline));
      const valid = request.nonce === unityNonce && request.tool === "verify_room_review";
      socket.end(JSON.stringify({ ok: true, resultJson: JSON.stringify({ valid, reason: valid ? "current" : "invalid test request" }) }) + "\n");
    });
  });
  await new Promise((resolve, reject) => {
    unityServer.once("error", reject);
    unityServer.listen(unityPort, "127.0.0.1", resolve);
  });
  fs.mkdirSync(path.join(root, "Library", "DungeonDecorator"), { recursive: true });
  fs.writeFileSync(path.join(root, "Library", "DungeonDecorator", "session.json"), JSON.stringify({ port: unityPort, nonce: unityNonce }));

  const port = await freePort();
  const child = spawn(process.execPath, [serverPath, "--project", root], {
    env: { ...process.env, SPATIAL_REVIEW_PORT: String(port) },
    stdio: ["ignore", "ignore", "pipe"],
    windowsHide: true,
  });
  const baseUrl = `http://127.0.0.1:${port}`;
  await waitUntilHealthy(baseUrl, child);
  return {
    baseUrl,
    statePath,
    reviewPath: path.join(roomRoot, "runs", "run-1", "human-review.json"),
    close() {
      child.kill();
      unityServer.close();
      fs.rmSync(root, { recursive: true, force: true });
    },
  };
}

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      server.close(error => error ? reject(error) : resolve(port));
    });
  });
}

async function waitUntilHealthy(baseUrl, child) {
  let detail = "";
  child.stderr.on("data", chunk => { detail += chunk.toString(); });
  for (let attempt = 0; attempt < 50; attempt++) {
    if (child.exitCode != null) throw new Error(`Review bridge exited early: ${detail}`);
    try {
      const response = await fetch(`${baseUrl}/api/health`);
      if (response.ok) return;
    } catch { }
    await new Promise(resolve => setTimeout(resolve, 40));
  }
  throw new Error(`Review bridge did not start: ${detail}`);
}
