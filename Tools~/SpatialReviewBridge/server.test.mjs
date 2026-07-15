import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { approvalAction, signingPayload } from "./approval-grant.mjs";

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
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
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
  assert.equal(stored.decision.comment, "네 시점을 직접 확인함");
  assert.equal(fs.existsSync(fixture.reviewPath), true);
  assert.deepEqual(fixture.confirmationRequests.map(value => value.tool), ["confirm_room_review_decision"]);
  assert.equal(fixture.confirmationRequests[0].decision, "Approved");

  const replay = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      runId: "run-1", inputHash: "input-1", validationHash: "validation-1", captureHash: "capture-1",
      decision: "Approved", issues: [], comment: "replay",
    }),
  });
  assert.equal(replay.status, 400);
  assert.match((await replay.json()).error, /Invalid or expired review nonce/);
});

test("room review preserves structured Korean revision feedback as UTF-8", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      runId: "run-1", inputHash: "input-1", validationHash: "validation-1", captureHash: "capture-1",
      decision: "RevisionRequested",
      issues: ["ARRANGEMENT_STACK_UNNATURAL", "물건 관계가 어색함"],
      comment: "책 더미가 너무 가지런하고 촛불과 붙어 있어요.",
    }),
  });
  assert.equal(response.status, 200);
  const storedText = fs.readFileSync(fixture.statePath, "utf8");
  const stored = JSON.parse(storedText);
  assert.equal(stored.decision.comment, "책 더미가 너무 가지런하고 촛불과 붙어 있어요.");
  assert.deepEqual(stored.decision.issueCodes, ["ARRANGEMENT_STACK_UNNATURAL", "물건 관계가 어색함"]);
  assert.match(storedText, /책 더미가 너무 가지런하고 촛불과 붙어 있어요/);
  assert.deepEqual(fixture.confirmationRequests.map(value => value.tool), ["confirm_room_review_decision"]);
  assert.equal(fixture.confirmationRequests[0].decision, "RevisionRequested");
});

test("room review cancellation leaves state and human record untouched", async t => {
  const fixture = await startFixture({ confirmationResult: false });
  t.after(() => fixture.close());
  const before = fs.readFileSync(fixture.statePath, "utf8");
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      runId: "run-1", inputHash: "input-1", validationHash: "validation-1", captureHash: "capture-1",
      decision: "Approved", issues: [], comment: "must not persist",
    }),
  });
  assert.equal(response.status, 400);
  assert.match((await response.json()).error, /취소/);
  assert.equal(fs.readFileSync(fixture.statePath, "utf8"), before);
  assert.equal(fs.existsSync(fixture.reviewPath), false);
  assert.deepEqual(fixture.confirmationRequests.map(value => value.tool), ["confirm_room_review_decision"]);
});

test("room review never overwrites state changed while the native confirmation is open", async t => {
  const fixture = await startFixture({
    onConfirmation({ tool, statePath }) {
      if (tool !== "confirm_room_review_decision") return;
      const state = JSON.parse(fs.readFileSync(statePath, "utf8"));
      state.targetName = "concurrently-regenerated-room";
      fs.writeFileSync(statePath, JSON.stringify(state, null, 2));
    },
  });
  t.after(() => fixture.close());
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      runId: "run-1", inputHash: "input-1", validationHash: "validation-1", captureHash: "capture-1",
      decision: "Approved", issues: [], comment: "concurrent change",
    }),
  });
  assert.equal(response.status, 400);
  assert.match((await response.json()).error, /ROOM_REVIEW_SOURCE_CHANGED/);
  const stored = JSON.parse(fs.readFileSync(fixture.statePath, "utf8"));
  assert.equal(stored.targetName, "concurrently-regenerated-room");
  assert.equal(stored.status, "AwaitingHumanReview");
  assert.equal(fs.existsSync(fixture.reviewPath), false);
});

test("room review rejects stale hashes and unexplained revision requests", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const post = async body => {
    const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
    return fetch(`${fixture.baseUrl}/api/room-review/decision`, {
      method: "POST",
      headers: reviewHeaders(fixture.baseUrl, health.nonce),
      body: JSON.stringify(body),
    });
  };
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
    method: "POST", headers: reviewHeaders(fixture.baseUrl, ""), body: JSON.stringify(body),
  });
  assert.equal(withoutNonce.status, 400);
  assert.match((await withoutNonce.json()).error, /Invalid or expired review nonce/);

  const state = JSON.parse(fs.readFileSync(fixture.statePath, "utf8"));
  state.technicalErrorCount = 1;
  state.technicalErrorCodes = ["OBB_OVERLAP"];
  fs.writeFileSync(fixture.statePath, JSON.stringify(state, null, 2));
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const blocked = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
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

test("mutating review requests reject null or missing browser origins", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const body = {
    runId: "run-1", inputHash: "input-1", validationHash: "validation-1", captureHash: "capture-1",
    decision: "Approved", issues: [], comment: "",
  };
  for (const origin of [undefined, "null", "http://127.0.0.1:4173"]) {
    const headers = { "Content-Type": "application/json", "X-Spatial-Review-Nonce": health.nonce };
    if (origin !== undefined) headers.Origin = origin;
    const response = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
      method: "POST", headers, body: JSON.stringify(body),
    });
    assert.equal(response.status, 403);
    assert.match((await response.json()).error, /allowed browser origin/);
  }
});

test("Unity confirmation uses the spawn-pinned session after session.json is tampered", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const maliciousPort = await freePort();
  let maliciousRequests = 0;
  const malicious = net.createServer(socket => {
    maliciousRequests++;
    socket.end(JSON.stringify({ ok: true, resultJson: JSON.stringify({ confirmed: true, valid: true }) }) + "\n");
  });
  await new Promise((resolve, reject) => {
    malicious.once("error", reject);
    malicious.listen(maliciousPort, "127.0.0.1", resolve);
  });
  t.after(() => malicious.close());
  fs.writeFileSync(fixture.sessionPath, JSON.stringify({ port: maliciousPort, nonce: "attacker-session-nonce" }));

  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/room-review/decision`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      runId: "run-1", inputHash: "input-1", validationHash: "validation-1", captureHash: "capture-1",
      decision: "Approved", issues: [], comment: "pinned session",
    }),
  });
  assert.equal(response.status, 200, JSON.stringify(await response.clone().json().catch(() => ({}))));
  assert.equal(maliciousRequests, 0);
  assert.deepEqual(fixture.confirmationRequests.map(value => value.tool), ["confirm_room_review_decision"]);
});

test("workflow capture bytes are rehashed before serving and review", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const generatedPage = path.join(fixture.root, "Library", "DungeonDecorator", "CalibrationWorkflow", "review.html");
  fs.writeFileSync(generatedPage, "<script>window.__UNTRUSTED_WORKFLOW_PAGE__=true</script>");
  const workflowPage = await fetch(`${fixture.baseUrl}/workflow`);
  assert.equal(workflowPage.status, 200);
  assert.match(workflowPage.headers.get("content-security-policy") || "", /default-src 'self'/);
  assert.doesNotMatch(await workflowPage.text(), /__UNTRUSTED_WORKFLOW_PAGE__/);
  const publicState = await (await fetch(`${fixture.baseUrl}/api/spatial/workflow`)).json();
  assert.equal(JSON.stringify(publicState).includes("draftPath"), false);
  assert.equal(JSON.stringify(publicState).includes("captureDirectory"), false);

  const imageUrl = `${fixture.baseUrl}/api/spatial/workflow/image/workflow-1/front?hash=${fixture.workflowCaptureHash}`;
  const initial = await fetch(imageUrl);
  assert.equal(initial.status, 200);
  assert.equal(Buffer.from(await initial.arrayBuffer()).toString("utf8"), "evidence:front.png");

  fs.writeFileSync(path.join(fixture.workflowCaptureDirectory, "front.png"), "tampered");
  const staleImage = await fetch(imageUrl);
  assert.equal(staleImage.status, 400);
  assert.match((await staleImage.json()).error, /content has changed and must be recaptured/);

  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const staleReview = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      id: "workflow-1", captureHash: fixture.workflowCaptureHash,
      decision: "RevisionRequested", reviewer: "local-user", issues: ["CONTACT_GAP"], comment: "stale capture",
    }),
  });
  assert.equal(staleReview.status, 400);
  assert.match((await staleReview.json()).error, /content has changed and must be recaptured/);
});

test("public workflow state bounds non-reviewable project data before trusted template rendering", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const state = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  state.items[0].status = "TechnicalFailed";
  state.items[0].displayName = `<img src=x onerror="window.__PROJECT_XSS__=1">`;
  state.items[0].captureHash = `\" onerror=\"window.__PROJECT_XSS__=1`;
  state.items[0].unknownFilesystemPath = "C:/secret/project/path";
  fs.writeFileSync(fixture.workflowStatePath, JSON.stringify(state, null, 2));

  const page = await fetch(`${fixture.baseUrl}/workflow`);
  const html = await page.text();
  assert.equal(page.status, 200);
  assert.doesNotMatch(html, /__PROJECT_XSS__/);

  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow`);
  const publicState = await response.json();
  assert.equal(response.status, 200);
  assert.equal(publicState.items[0].captureHash, "");
  assert.equal(publicState.items[0].unknownFilesystemPath, undefined);
  assert.equal(publicState.items[0].displayName, state.items[0].displayName);
});

test("every workflow decision is bound to the draft capture hash", async t => {
  const fixture = await startFixture();
  t.after(() => fixture.close());
  const draft = JSON.parse(fs.readFileSync(fixture.workflowDraftPath, "utf8"));
  draft.asset.capture_set_hash = "b".repeat(64);
  fs.writeFileSync(fixture.workflowDraftPath, JSON.stringify(draft));
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      id: "workflow-1", captureHash: fixture.workflowCaptureHash,
      decision: "UnableToJudge", reviewer: "local-user", issues: [], comment: "need another angle",
    }),
  });
  assert.equal(response.status, 400);
  assert.match((await response.json()).error, /draft capture_set_hash is stale relative to the reviewed evidence/);
});

test("workflow review rejects a rehashed capture whose technical report differs from the draft", async t => {
  const fixture = await startFixture({ fakeUnityCtx: true });
  t.after(() => fixture.close());
  const reportPath = path.join(fixture.workflowCaptureDirectory, "technical-report.json");
  const report = JSON.parse(fs.readFileSync(reportPath, "utf8"));
  report.report_hash = "d".repeat(64);
  fs.writeFileSync(reportPath, JSON.stringify(report));
  const manifestPath = path.join(fixture.workflowCaptureDirectory, "capture-manifest.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.technical_report_hash = report.report_hash;
  fs.writeFileSync(manifestPath, JSON.stringify(manifest));
  const captureHash = workflowEvidenceHash(fixture.workflowCaptureDirectory);
  const state = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  state.items[0].captureHash = captureHash;
  state.items[0].technicalReportHash = report.report_hash;
  fs.writeFileSync(fixture.workflowStatePath, JSON.stringify(state, null, 2));
  const draft = JSON.parse(fs.readFileSync(fixture.workflowDraftPath, "utf8"));
  draft.asset.capture_set_hash = captureHash;
  fs.writeFileSync(fixture.workflowDraftPath, JSON.stringify(draft));

  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      id: "workflow-1", captureHash, decision: "Approved", reviewer: "local-user", issues: [], comment: "mismatch",
    }),
  });
  assert.equal(response.status, 400);
  assert.match((await response.json()).error, /draft technical evidence does not match/);
  assert.equal(fixture.confirmationRequests.length, 0);
});

test("workflow review rejects a capture manifest proposal that does not match its draft", async t => {
  const fixture = await startFixture({ fakeUnityCtx: true });
  t.after(() => fixture.close());
  const manifestPath = path.join(fixture.workflowCaptureDirectory, "capture-manifest.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.proposals[0].proposal_hash = "c".repeat(64);
  fs.writeFileSync(manifestPath, JSON.stringify(manifest));
  const captureHash = workflowEvidenceHash(fixture.workflowCaptureDirectory);
  const state = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  state.items[0].captureHash = captureHash;
  fs.writeFileSync(fixture.workflowStatePath, JSON.stringify(state, null, 2));
  const draft = JSON.parse(fs.readFileSync(fixture.workflowDraftPath, "utf8"));
  draft.asset.capture_set_hash = captureHash;
  fs.writeFileSync(fixture.workflowDraftPath, JSON.stringify(draft));

  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      id: "workflow-1", captureHash, decision: "Approved", reviewer: "local-user", issues: [], comment: "mismatch",
    }),
  });
  assert.equal(response.status, 400);
  assert.match((await response.json()).error, /draft proposal does not match capture-manifest/);
  assert.equal(fixture.confirmationRequests.length, 0);
});

test("batch approval preflights every capture before approving the first item", async t => {
  const fixture = await startFixture({ fakeUnityCtx: true });
  t.after(() => fixture.close());
  const state = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  const second = addWorkflowAssetFixture(fixture, state, "workflow-2", "2".repeat(32), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
  fs.writeFileSync(path.join(second.captureDirectory, "side.png"), "tampered-before-batch");
  state.items.push(second);
  fs.writeFileSync(fixture.workflowStatePath, JSON.stringify(state, null, 2));

  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review-batch`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      decision: "Approved", reviewer: "local-user",
      items: state.items.map(item => ({ id: item.id, captureHash: item.captureHash })),
    }),
  });
  assert.equal(response.status, 400);
  assert.match((await response.json()).error, /content has changed and must be recaptured/);
  const stored = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  assert.deepEqual(stored.items.map(item => item.status), ["AwaitingHumanReview", "AwaitingHumanReview"]);
});

test("spatial revision decision requires one bound Unity confirmation", async t => {
  const fixture = await startFixture({ fakeUnityCtx: true });
  t.after(() => fixture.close());
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      id: "workflow-1", captureHash: fixture.workflowCaptureHash,
      decision: "RevisionRequested", reviewer: "local-user",
      issues: ["CONTACT_GAP"], comment: "벽과 더 가깝게 붙여 주세요.",
    }),
  });
  const result = await response.json();
  assert.equal(response.status, 200, result.error);
  assert.equal(result.status, "RevisionRequested");
  assert.deepEqual(fixture.confirmationRequests.map(value => value.tool), ["confirm_spatial_contract_review_decision"]);
  assert.equal(fixture.confirmationRequests[0].decision, "RevisionRequested");
  assert.equal(fixture.confirmationRequests[0].captureSetHash, fixture.workflowCaptureHash);
  const events = fs.readFileSync(fixture.fakeUnityCtx.logPath, "utf8").trim().split(/\r?\n/).map(JSON.parse);
  assert.deepEqual(events.map(value => value.kind), ["validate", "validate", "validate", "validate", "validate", "review"]);
});

test("workflow decision merges into the latest queue without clobbering unrelated concurrent work", async t => {
  const fixture = await startFixture({
    fakeUnityCtx: true,
    onConfirmation({ tool, workflow }) {
      if (tool !== "confirm_spatial_contract_review_decision") return;
      const state = JSON.parse(fs.readFileSync(workflow.workflowStatePath, "utf8"));
      state.concurrentMarker = "preserve-me";
      state.items.push({ id: "concurrent-item", status: "Pending", reviewKind: "asset", revisionIssueCodes: [] });
      fs.writeFileSync(workflow.workflowStatePath, JSON.stringify(state, null, 2));
    },
  });
  t.after(() => fixture.close());
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      id: "workflow-1", captureHash: fixture.workflowCaptureHash,
      decision: "RevisionRequested", reviewer: "local-user", issues: ["CONTACT_GAP"], comment: "merge latest",
    }),
  });
  const result = await response.json();
  assert.equal(response.status, 200, result.error);
  assert.equal(result.workflowReconciled, true);
  const stored = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  assert.equal(stored.concurrentMarker, "preserve-me");
  assert.deepEqual(stored.items.map(value => value.id), ["workflow-1", "concurrent-item"]);
  assert.equal(stored.items[0].status, "RevisionRequested");
  assert.equal(stored.items[1].status, "Pending");
});

test("cancelled spatial review decision does not mutate draft or workflow state", async t => {
  const fixture = await startFixture({ fakeUnityCtx: true, confirmationResult: false });
  t.after(() => fixture.close());
  const stateBefore = fs.readFileSync(fixture.workflowStatePath, "utf8");
  const draftBefore = fs.readFileSync(fixture.workflowDraftPath, "utf8");
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      id: "workflow-1", captureHash: fixture.workflowCaptureHash,
      decision: "UnableToJudge", reviewer: "local-user", issues: [], comment: "측면 확대가 필요함",
    }),
  });
  assert.equal(response.status, 400);
  assert.match((await response.json()).error, /취소/);
  assert.equal(fs.readFileSync(fixture.workflowStatePath, "utf8"), stateBefore);
  assert.equal(fs.readFileSync(fixture.workflowDraftPath, "utf8"), draftBefore);
  const events = fs.readFileSync(fixture.fakeUnityCtx.logPath, "utf8").trim().split(/\r?\n/).map(JSON.parse);
  assert.deepEqual(events.map(value => value.kind), ["validate", "validate", "validate"]);
});

test("batch approval uses one Unity confirmation and applies every exact binding", async t => {
  const fixture = await startFixture({ fakeUnityCtx: true });
  t.after(() => fixture.close());
  const state = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  state.items.push(addWorkflowAssetFixture(
    fixture, state, "workflow-2", "2".repeat(32), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
  fs.writeFileSync(fixture.workflowStatePath, JSON.stringify(state, null, 2));

  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review-batch`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      decision: "Approved",
      reviewer: "local-user",
      items: state.items.map(item => ({ id: item.id, captureHash: item.captureHash })),
    }),
  });
  const result = await response.json();
  assert.equal(response.status, 200, result.error);
  assert.equal(result.status, "Approved");
  assert.equal(result.count, 2);
  assert.deepEqual(fixture.confirmationRequests.map(value => value.tool), ["confirm_spatial_contract_batch_approval"]);
  assert.equal(fixture.confirmationRequests[0].itemCount, 2);
  assert.ok(fixture.confirmationRequests[0].displayNames.every(value => value.startsWith("asset:") && value.includes("Assets/SpatialContracts/Assets/")));
  assert.equal(fixture.confirmationRequests[0].displayNames.some(value => value.includes("workflow-")), false);

  const stored = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  assert.deepEqual(stored.items.map(item => item.status), ["Approved", "Approved"]);
  const events = fs.readFileSync(fixture.fakeUnityCtx.logPath, "utf8").trim().split(/\r?\n/).map(JSON.parse);
  assert.equal(events.filter(value => value.kind === "review-bridge").length, 2);
});

test("approved workflow reaches Unity confirmation, signed review bridge, and final revalidation", async t => {
  const fixture = await startFixture({ fakeUnityCtx: true });
  t.after(() => fixture.close());
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      id: "workflow-1", captureHash: fixture.workflowCaptureHash,
      decision: "Approved", reviewer: "local-user", issues: [], comment: "reviewed all views",
    }),
  });
  const result = await response.json();
  assert.equal(response.status, 200, result.error);
  assert.equal(result.status, "Approved");
  assert.equal(fixture.confirmationRequests.length, 1);
  assert.equal(fixture.confirmationRequests[0].tool, "confirm_spatial_contract_approval");
  assert.equal(fixture.confirmationRequests[0].contractHash, "a".repeat(64));
  assert.equal(fixture.confirmationRequests[0].currentHash, "absent");
  assert.equal(fixture.confirmationRequests[0].captureSetHash, fixture.workflowCaptureHash);

  const events = fs.readFileSync(fixture.fakeUnityCtx.logPath, "utf8").trim().split(/\r?\n/).map(JSON.parse);
  assert.deepEqual(events.map(value => value.kind), ["validate", "validate", "validate", "diff", "validate", "review-bridge", "validate"]);
  const bridgeRequest = events.find(value => value.kind === "review-bridge").request;
  assert.equal(bridgeRequest.current_hash, "absent");
  assert.equal(bridgeRequest.grant.authority, "test-unity-decoscene-review-bridge");
  const verification = {
    action: approvalAction,
    contract_hash: "a".repeat(64),
    current_hash: "absent",
    capture_set_hash: fixture.workflowCaptureHash,
    reviewer: "local-user",
    destination: bridgeRequest.current_path,
    evidence: bridgeRequest.grant,
  };
  const publicKey = crypto.createPublicKey(fs.readFileSync(path.join(
    fixture.fakeUnityCtx.authorityRoot,
    "test-unity-decoscene-review-bridge.pub",
  )));
  assert.equal(crypto.verify(
    null,
    signingPayload(verification),
    publicKey,
    Buffer.from(bridgeRequest.grant.proof, "base64url"),
  ), true);
  assert.equal(fs.existsSync(bridgeRequest.current_path), true);
  const stored = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  assert.equal(stored.items[0].status, "Approved");
});

test("a durable approval is reported as applied when post-commit validation and workflow reconciliation fail", async t => {
  const fixture = await startFixture({
    fakeUnityCtx: true,
    failCurrentValidation: true,
    mutateWorkflowAfterCommit: true,
  });
  t.after(() => fixture.close());
  const health = await (await fetch(`${fixture.baseUrl}/api/health`)).json();
  const response = await fetch(`${fixture.baseUrl}/api/spatial/workflow/review`, {
    method: "POST",
    headers: reviewHeaders(fixture.baseUrl, health.nonce),
    body: JSON.stringify({
      id: "workflow-1", captureHash: fixture.workflowCaptureHash,
      decision: "Approved", reviewer: "local-user", issues: [], comment: "commit boundary",
    }),
  });
  const result = await response.json();
  assert.equal(response.status, 200, result.error);
  assert.equal(result.status, "Approved");
  assert.equal(result.applied, true);
  assert.equal(result.reconciliationRequired, true);
  assert.equal(result.workflowReconciled, false);
  const stored = JSON.parse(fs.readFileSync(fixture.workflowStatePath, "utf8"));
  assert.equal(stored.concurrentMarker, "after-commit");
  assert.equal(stored.items[0].status, "AwaitingHumanReview");
  assert.equal(stored.items[0].comment, "concurrent-after-commit");
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
  const workflow = createWorkflowFixture(root);

  const unityPort = await freePort();
  const unityNonce = "test-unity-nonce";
  const confirmationRequests = [];
  const unityServer = net.createServer(socket => {
    let data = "";
    socket.setEncoding("utf8");
    socket.on("data", chunk => {
      data += chunk;
      const newline = data.indexOf("\n");
      if (newline < 0) return;
      const request = JSON.parse(data.slice(0, newline));
      let result;
      if (request.nonce !== unityNonce) result = { valid: false, reason: "invalid test nonce" };
      else if (request.tool === "verify_room_review") result = { valid: true, reason: "current" };
      else if ([
        "confirm_room_review_decision",
        "confirm_spatial_contract_review_decision",
        "confirm_spatial_contract_approval",
        "confirm_spatial_contract_batch_approval",
      ].includes(request.tool)) {
        const argumentsValue = JSON.parse(request.argumentsJson || "{}");
        confirmationRequests.push({ tool: request.tool, ...argumentsValue });
        if (typeof options.onConfirmation === "function") {
          options.onConfirmation({ tool: request.tool, arguments: argumentsValue, root, statePath, workflow });
        }
        result = { confirmed: options.confirmationResult !== false };
      } else result = { valid: false, reason: "invalid test request" };
      socket.end(JSON.stringify({ ok: true, resultJson: JSON.stringify(result) }) + "\n");
    });
  });
  await new Promise((resolve, reject) => {
    unityServer.once("error", reject);
    unityServer.listen(unityPort, "127.0.0.1", resolve);
  });
  fs.mkdirSync(path.join(root, "Library", "DungeonDecorator"), { recursive: true });
  fs.writeFileSync(path.join(root, "Library", "DungeonDecorator", "session.json"), JSON.stringify({ port: unityPort, nonce: unityNonce }));

  const port = await freePort();
  const fakeUnityCtx = options.fakeUnityCtx ? createFakeUnityCtx(root) : null;
  const child = spawn(process.execPath, [serverPath, "--project", root], {
    env: {
      ...process.env,
      SPATIAL_REVIEW_PORT: String(port),
      SPATIAL_REVIEW_UNITY_PORT: String(unityPort),
      SPATIAL_REVIEW_UNITY_NONCE: unityNonce,
      ...(fakeUnityCtx ? {
        NODE_ENV: "test",
        UNITY_CTX_BIN: process.execPath,
        SPATIAL_REVIEW_TEST_UNITY_CTX_PREFIX_ARGS: JSON.stringify([fakeUnityCtx.scriptPath]),
        SPATIAL_REVIEW_TEST_AUTHORITY_ROOT: fakeUnityCtx.authorityRoot,
        FAKE_UNITY_CTX_LOG: fakeUnityCtx.logPath,
        ...(options.failCurrentValidation ? { FAKE_UNITY_CTX_FAIL_CURRENT_VALIDATE: "1" } : {}),
        ...(options.mutateWorkflowAfterCommit ? { FAKE_UNITY_CTX_MUTATE_WORKFLOW: workflow.workflowStatePath } : {}),
      } : {}),
    },
    stdio: ["ignore", "ignore", "pipe"],
    windowsHide: true,
  });
  const baseUrl = `http://127.0.0.1:${port}`;
  await waitUntilHealthy(baseUrl, child);
  return {
    baseUrl,
    statePath,
    root,
    sessionPath: path.join(root, "Library", "DungeonDecorator", "session.json"),
    confirmationRequests,
    fakeUnityCtx,
    ...workflow,
    reviewPath: path.join(roomRoot, "runs", "run-1", "human-review.json"),
    close() {
      child.kill();
      unityServer.close();
      fs.rmSync(root, { recursive: true, force: true });
    },
  };
}

function createWorkflowFixture(root) {
  const captureDirectory = path.join(root, "Library", "DungeonDecorator", "SpatialCaptures", "session-1");
  fs.mkdirSync(captureDirectory, { recursive: true });
  const sessionId = "1".repeat(32);
  const assetGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
  const rawNames = ["front.png", "side.png", "top.png", "contact.png"];
  const evidenceNames = ["front-evidence.png", "side-evidence.png", "top-evidence.png", "contact-evidence.png"];
  const technicalReportHash = "e".repeat(64);
  const allNames = [...rawNames, ...evidenceNames, "technical-report.json", "capture-manifest.json"];
  for (const name of [...rawNames, ...evidenceNames]) fs.writeFileSync(path.join(captureDirectory, name), `evidence:${name}`);
  fs.writeFileSync(path.join(captureDirectory, "technical-report.json"), JSON.stringify({
    session_id: sessionId,
    status: "AwaitingHumanReview",
    error_count: 0,
    errors: [],
    contacts: [],
    report_hash: technicalReportHash,
  }));
  fs.writeFileSync(path.join(captureDirectory, "capture-manifest.json"), JSON.stringify({
    schema_version: 1,
    manifest_version: 1,
    session_id: sessionId,
    proposals: [{ contract_type: "asset", canonical_identity: assetGuid, proposal_hash: fakeProposalHash("asset", assetGuid) }],
    technical_report_hash: technicalReportHash,
    technical_passed: true,
    technical_error_count: 0,
  }));
  const captureHash = hashCaptureDirectory(captureDirectory, allNames);
  const draftPath = path.join(root, "Library", "DungeonDecorator", "Drafts", `${sessionId}.spatial.json`);
  fs.mkdirSync(path.dirname(draftPath), { recursive: true });
  fs.writeFileSync(draftPath, JSON.stringify({
    contract_version: 1,
    contract_type: "asset",
    state: "AwaitingHumanReview",
    asset: { asset_guid: assetGuid, capture_set_hash: captureHash },
    technical: { passed: true, error_count: 0, report_hash: technicalReportHash },
  }));
  const workflowStatePath = path.join(root, "Library", "DungeonDecorator", "CalibrationWorkflow", "state.json");
  fs.mkdirSync(path.dirname(workflowStatePath), { recursive: true });
  const item = {
    id: "workflow-1",
    status: "AwaitingHumanReview",
    sessionId,
    technicalReportHash,
    captureHash,
    captureDirectory,
    rawPaths: rawNames.map(name => path.join(captureDirectory, name)),
    evidencePaths: evidenceNames.map(name => path.join(captureDirectory, name)),
    draftPath,
  };
  fs.writeFileSync(workflowStatePath, JSON.stringify({ schemaVersion: 1, status: "AwaitingHumanReview", items: [item] }, null, 2));
  return { workflowStatePath, workflowCaptureDirectory: captureDirectory, workflowDraftPath: draftPath, workflowCaptureHash: captureHash };
}

function addWorkflowAssetFixture(fixture, state, id, sessionId, assetGuid) {
  const captureDirectory = path.join(fixture.root, "Library", "DungeonDecorator", "SpatialCaptures", `session-${id}`);
  fs.cpSync(fixture.workflowCaptureDirectory, captureDirectory, { recursive: true });
  const reportPath = path.join(captureDirectory, "technical-report.json");
  const report = JSON.parse(fs.readFileSync(reportPath, "utf8"));
  report.session_id = sessionId;
  fs.writeFileSync(reportPath, JSON.stringify(report));
  const manifestPath = path.join(captureDirectory, "capture-manifest.json");
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  manifest.session_id = sessionId;
  manifest.proposals = [{
    contract_type: "asset",
    canonical_identity: assetGuid,
    proposal_hash: fakeProposalHash("asset", assetGuid),
  }];
  fs.writeFileSync(manifestPath, JSON.stringify(manifest));
  const names = [
    "front.png", "side.png", "top.png", "contact.png",
    "front-evidence.png", "side-evidence.png", "top-evidence.png", "contact-evidence.png",
    "technical-report.json", "capture-manifest.json",
  ];
  const captureHash = hashCaptureDirectory(captureDirectory, names);
  const draftPath = path.join(fixture.root, "Library", "DungeonDecorator", "Drafts", `${sessionId}.spatial.json`);
  const draft = JSON.parse(fs.readFileSync(fixture.workflowDraftPath, "utf8"));
  draft.asset.asset_guid = assetGuid;
  draft.asset.capture_set_hash = captureHash;
  fs.writeFileSync(draftPath, JSON.stringify(draft));
  return {
    ...state.items[0],
    id,
    sessionId,
    captureHash,
    captureDirectory,
    draftPath,
    rawPaths: state.items[0].rawPaths.map(value => path.join(captureDirectory, path.basename(value))),
    evidencePaths: state.items[0].evidencePaths.map(value => path.join(captureDirectory, path.basename(value))),
  };
}

function hashCaptureDirectory(directory, names) {
  const hash = crypto.createHash("sha256");
  for (const filePath of names.map(name => path.join(directory, name)).sort()) hash.update(fs.readFileSync(filePath));
  return hash.digest("hex");
}

function workflowEvidenceHash(directory) {
  return hashCaptureDirectory(directory, [
    "front.png", "side.png", "top.png", "contact.png",
    "front-evidence.png", "side-evidence.png", "top-evidence.png", "contact-evidence.png",
    "technical-report.json", "capture-manifest.json",
  ]);
}

function fakeProposalHash(contractType, canonicalIdentity) {
  return crypto.createHash("sha256").update(`proposal:${contractType}:${canonicalIdentity}`, "utf8").digest("hex");
}

function createFakeUnityCtx(root) {
  const scriptPath = path.join(root, "fake-unity-ctx.mjs");
  const logPath = path.join(root, "fake-unity-ctx.jsonl");
  const authorityRoot = path.join(root, "review-authorities");
  fs.writeFileSync(scriptPath, `
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";

const args = process.argv.slice(2);
const logPath = process.env.FAKE_UNITY_CTX_LOG;
const contractHash = "a".repeat(64);
const log = value => fs.appendFileSync(logPath, JSON.stringify(value) + "\\n");
const flag = name => args[args.indexOf(name) + 1];

if (args[0] === "review-bridge") {
  let input = "";
  for await (const chunk of process.stdin) input += chunk;
  const request = JSON.parse(input);
  log({ kind: "review-bridge", request });
  if (request.action !== "approve_apply" || request.current_hash !== "absent" || !request.grant?.proof) {
    process.stdout.write(JSON.stringify({ ok: false, error: "invalid fake bridge request" }));
    process.exitCode = 1;
  } else {
    fs.mkdirSync(path.dirname(request.current_path), { recursive: true });
    fs.copyFileSync(request.draft_path, request.current_path);
    if (process.env.FAKE_UNITY_CTX_MUTATE_WORKFLOW) {
      const state = JSON.parse(fs.readFileSync(process.env.FAKE_UNITY_CTX_MUTATE_WORKFLOW, "utf8"));
      state.concurrentMarker = "after-commit";
      state.items[0].comment = "concurrent-after-commit";
      fs.writeFileSync(process.env.FAKE_UNITY_CTX_MUTATE_WORKFLOW, JSON.stringify(state, null, 2));
    }
    process.stdout.write(JSON.stringify({ ok: true, status: "WRITE", current_path: request.current_path, contract_hash: contractHash, written: true }));
  }
} else if (args[0] === "spatial" && args[1] === "validate") {
  const file = args.find(value => value.endsWith(".json"));
  if (process.env.FAKE_UNITY_CTX_FAIL_CURRENT_VALIDATE === "1" && file.includes(path.join("Assets", "SpatialContracts")) && fs.existsSync(file)) {
    log({ kind: "validate-failed-after-commit", file });
    process.stderr.write("simulated post-commit validate failure");
    process.exitCode = 3;
  } else {
  const document = JSON.parse(fs.readFileSync(file, "utf8"));
  const identity = document.contract_type === "asset"
    ? String(document.asset?.asset_guid || "").toLowerCase()
    : String(document.interaction?.subject_guid || "").toLowerCase() + "__" +
      Buffer.from(String(document.interaction?.target_key || ""), "utf8").toString("hex") + "__" +
      Buffer.from(String(document.interaction?.relation || ""), "utf8").toString("hex");
  const proposalHash = crypto.createHash("sha256").update("proposal:" + document.contract_type + ":" + identity, "utf8").digest("hex");
  log({ kind: "validate", file });
  process.stdout.write(JSON.stringify({ status: "OK", file, contract_hash: contractHash, proposal_hash: proposalHash }));
  }
} else if (args[0] === "spatial" && args[1] === "diff") {
  const current = flag("--current");
  const draft = flag("--draft");
  const currentHash = fs.existsSync(current)
    ? crypto.createHash("sha256").update(fs.readFileSync(current)).digest("hex")
    : "absent";
  log({ kind: "diff", current, draft, current_hash: currentHash });
  process.stdout.write(JSON.stringify({ status: currentHash === "absent" ? "NEW" : "OK", current, draft, changed: true, contract_hash: contractHash, current_hash: currentHash }));
} else if (args[0] === "spatial" && args[1] === "review") {
  const draft = flag("--draft");
  const decision = flag("--decision");
  log({ kind: "review", draft, decision });
  process.stdout.write(JSON.stringify({ status: decision, draft, written: true }));
} else if (args[0] === "spatial" && args[1] === "verify-approved") {
  const file = args.find(value => value.endsWith(".json"));
  log({ kind: "verify-approved", file });
  process.stdout.write(JSON.stringify({ authorized: fs.existsSync(file), contract_hash: contractHash, contract_type: "asset" }));
} else {
  log({ kind: "unsupported", args });
  process.stderr.write("unsupported fake unity-ctx command");
  process.exitCode = 2;
}
`, "utf8");
  return { scriptPath, logPath, authorityRoot };
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

function reviewHeaders(baseUrl, nonce) {
  const headers = { "Content-Type": "application/json", Origin: baseUrl };
  if (nonce) headers["X-Spatial-Review-Nonce"] = nonce;
  return headers;
}
