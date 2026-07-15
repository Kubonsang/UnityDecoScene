import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

export const approvalAction = "approve_apply";
export const defaultAuthorityId = "unity-decoscene-review-bridge";

export function defaultAuthorityRoot(_environment = process.env, platform = process.platform, home = os.userInfo().homedir) {
  let cacheRoot;
  if (platform === "win32") cacheRoot = path.join(home, "AppData", "Local");
  else if (platform === "darwin") cacheRoot = path.join(home, "Library", "Caches");
  else cacheRoot = path.join(home, ".cache");
  return path.join(path.resolve(cacheRoot), "unity-ctx", "review-authorities");
}

export function ensureApprovalAuthority(options = {}) {
  const authority = String(options.authority || `${defaultAuthorityId}-${crypto.randomBytes(8).toString("hex")}`).trim();
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$/.test(authority)) throw new Error("Review authority id is invalid.");
  const root = path.resolve(options.root || defaultAuthorityRoot());
  const publicPath = path.join(root, `${authority}.pub`);
  fs.mkdirSync(root, { recursive: true, mode: 0o700 });
  if (fs.existsSync(publicPath)) throw new Error("Review authority id is already registered; refusing to reuse a key identity.");

  // The signing key is session-ephemeral and never touches disk. Only the
  // public key is registered outside the Unity project for unity-ctx.
  const pair = crypto.generateKeyPairSync("ed25519");
  const publicKey = pair.publicKey.export({ format: "pem", type: "spki" });
  fs.writeFileSync(publicPath, publicKey, { flag: "wx", mode: 0o600 });
  return { authority, privateKey: pair.privateKey, publicPath, root };
}

export function createApprovalGrant(binding, authorityKey, options = {}) {
  const now = Number.isFinite(options.nowUnix) ? Math.trunc(options.nowUnix) : Math.trunc(Date.now() / 1000);
  const lifetime = Number.isFinite(options.lifetimeSeconds) ? Math.trunc(options.lifetimeSeconds) : 300;
  if (lifetime < 1 || lifetime > 900) throw new Error("Approval grant lifetime must be between 1 and 900 seconds.");
  const verification = normalizeBinding(binding, {
    authority: authorityKey.authority,
    nonce: options.nonce || crypto.randomBytes(24).toString("base64url"),
    expires_unix: now + lifetime,
    proof: "",
  });
  verification.evidence.proof = crypto.sign(null, signingPayload(verification), authorityKey.privateKey).toString("base64url");
  return verification.evidence;
}

export function signingPayload(verification) {
  const normalized = normalizeBinding(verification, verification.evidence);
  const fields = [
    "unity-ctx-review-grant-v2",
    normalized.action,
    normalized.evidence.authority,
    normalized.evidence.nonce,
    String(normalized.evidence.expires_unix),
    normalized.contract_hash,
    normalized.current_hash,
    normalized.capture_set_hash,
    normalized.reviewer,
    normalized.destination,
    normalized.subject_geometry_hash,
    normalized.target_geometry_hash,
  ];
  return Buffer.from(fields.map(value => `${Buffer.byteLength(value, "utf8")}:${value}`).join(""), "utf8");
}

export function canonicalContractPath(projectRoot, contract) {
  const root = path.resolve(projectRoot);
  if (contract?.contract_type === "asset" && /^[0-9a-fA-F]{32}$/.test(String(contract.asset?.asset_guid || ""))) {
    return path.join(root, "Assets", "SpatialContracts", "Assets", `${String(contract.asset.asset_guid).toLowerCase()}.spatial.json`);
  }
  if (contract?.contract_type === "interaction" && /^[0-9a-fA-F]{32}$/.test(String(contract.interaction?.subject_guid || ""))) {
    const item = contract.interaction;
    if (!String(item.target_key || "") || !String(item.relation || "")) throw new Error("Interaction target_key and relation are required.");
    const target = Buffer.from(String(item.target_key), "utf8").toString("hex");
    const relation = Buffer.from(String(item.relation), "utf8").toString("hex");
    return path.join(root, "Assets", "SpatialContracts", "Interactions", `${String(item.subject_guid).toLowerCase()}__${target}__${relation}.interaction.json`);
  }
  throw new Error("Draft does not contain a supported Spatial Contract identity.");
}

function normalizeBinding(binding, evidence) {
  const action = String(binding.action || approvalAction);
  const contractHash = String(binding.contract_hash || binding.contractHash || "").toLowerCase();
  const currentHash = String(binding.current_hash || binding.currentHash || "").toLowerCase();
  const captureSetHash = String(binding.capture_set_hash || binding.captureSetHash || "");
  const reviewer = String(binding.reviewer || "").trim();
  const rawDestination = String(binding.destination || "").trim();
  const destination = rawDestination ? path.resolve(rawDestination).replaceAll("\\", "/") : "";
  const subjectGeometryHash = String(binding.subject_geometry_hash || binding.subjectGeometryHash || "").toLowerCase();
  const targetGeometryHash = String(binding.target_geometry_hash || binding.targetGeometryHash || "").toLowerCase();
  const authority = String(evidence?.authority || "").trim();
  const nonce = String(evidence?.nonce || "").trim();
  const expiresUnix = Number(evidence?.expires_unix || evidence?.expiresUnix || 0);
  if (action !== approvalAction || !/^[0-9a-f]{64}$/.test(contractHash) ||
      !(currentHash === "absent" || /^[0-9a-f]{64}$/.test(currentHash)) ||
      !captureSetHash || !reviewer || !rawDestination || !path.isAbsolute(destination) ||
      !((!subjectGeometryHash && !targetGeometryHash) ||
        (/^[0-9a-f]{64}$/.test(subjectGeometryHash) && /^[0-9a-f]{64}$/.test(targetGeometryHash)))) {
    throw new Error("Approval grant binding is incomplete.");
  }
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$/.test(authority) || !/^[a-zA-Z0-9_-]{24,192}$/.test(nonce) || !Number.isSafeInteger(expiresUnix)) {
    throw new Error("Approval grant evidence is invalid.");
  }
  return {
    action,
    contract_hash: contractHash,
    current_hash: currentHash,
    capture_set_hash: captureSetHash,
    reviewer,
    destination,
    subject_geometry_hash: subjectGeometryHash,
    target_geometry_hash: targetGeometryHash,
    evidence: { authority, nonce, expires_unix: expiresUnix, proof: String(evidence?.proof || "") },
  };
}
