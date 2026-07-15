import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import {
  approvalAction,
  canonicalContractPath,
  createApprovalGrant,
  defaultAuthorityRoot,
  ensureApprovalAuthority,
  signingPayload,
} from "./approval-grant.mjs";

test("default authority root ignores caller-controlled cache environment", () => {
  const poisoned = path.resolve("caller-controlled-cache");
  const environment = { LOCALAPPDATA: poisoned, XDG_CACHE_HOME: poisoned };
  assert.equal(defaultAuthorityRoot(environment, "win32", "C:\\Users\\reviewer"), path.join("C:\\Users\\reviewer", "AppData", "Local", "unity-ctx", "review-authorities"));
  assert.equal(defaultAuthorityRoot(environment, "linux", "/home/reviewer"), path.join(path.resolve("/home/reviewer", ".cache"), "unity-ctx", "review-authorities"));
});

test("approval grants are short-lived, action-bound Ed25519 signatures", t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "unity-review-authority-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const authority = ensureApprovalAuthority({ root, authority: "test-authority" });
  assert.equal(fs.existsSync(path.join(root, "test-authority.key")), false, "private review keys must remain memory-only");
  const destination = path.resolve(root, "Project", "Assets", "SpatialContracts", "Assets", "a.spatial.json");
  const binding = {
    action: approvalAction,
    contract_hash: "a".repeat(64),
    current_hash: "absent",
    capture_set_hash: "capture-1",
    reviewer: "local-user",
    destination,
    subject_geometry_hash: "c".repeat(64),
    target_geometry_hash: "d".repeat(64),
  };
  const grant = createApprovalGrant(binding, authority, {
    nowUnix: 1_800_000_000,
    lifetimeSeconds: 300,
    nonce: "fixed_nonce_value_1234567890",
  });
  const verification = { ...binding, evidence: grant };
  const publicKey = crypto.createPublicKey(fs.readFileSync(authority.publicPath));

  assert.equal(grant.expires_unix, 1_800_000_300);
  assert.equal(crypto.verify(null, signingPayload(verification), publicKey, Buffer.from(grant.proof, "base64url")), true);
  verification.current_hash = "b".repeat(64);
  assert.equal(crypto.verify(null, signingPayload(verification), publicKey, Buffer.from(grant.proof, "base64url")), false);
  verification.current_hash = "absent";
  verification.contract_hash = "b".repeat(64);
  assert.equal(crypto.verify(null, signingPayload(verification), publicKey, Buffer.from(grant.proof, "base64url")), false);
  verification.contract_hash = "a".repeat(64);
  verification.subject_geometry_hash = "e".repeat(64);
  assert.equal(crypto.verify(null, signingPayload(verification), publicKey, Buffer.from(grant.proof, "base64url")), false);
});

test("canonical interaction path losslessly binds target and relation", () => {
  const root = path.resolve("Project");
  const result = canonicalContractPath(root, {
    contract_type: "interaction",
    interaction: {
      subject_guid: "A".repeat(32),
      target_key: "table/상단",
      relation: "SupportedBy",
    },
  });
  const target = Buffer.from("table/상단", "utf8").toString("hex");
  const relation = Buffer.from("SupportedBy", "utf8").toString("hex");
  assert.equal(result, path.join(root, "Assets", "SpatialContracts", "Interactions", `${"a".repeat(32)}__${target}__${relation}.interaction.json`));
});
