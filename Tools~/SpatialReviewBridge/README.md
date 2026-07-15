# Spatial Review Bridge

This loopback-only bridge is the human-authority path for Spatial Contract review. It is intentionally separate from MCP.

In Unity, select an installed executable with **Tools › Concept Room Decorator ›
Fast Calibration › Configure unity-ctx**. The executable must be outside the
Unity project. For a manual bridge launch the same trusted path is passed
explicitly:

```powershell
$env:UNITY_CTX_BIN = "C:\path\to\unity-ctx.exe"
node Tools~/SpatialReviewBridge/server.mjs --project C:\path\to\UnityProject
```

The browser obtains a one-request nonce from `GET /api/health`. Mutating requests are accepted only from the bridge's own `http://127.0.0.1:<port>` or `http://localhost:<port>` review pages; file/null origins, missing origins, and the read-only Preference Studio development origin are rejected. Before serving evidence or recording any decision, the bridge recomputes the Unity capture-set SHA-256 from the four raw views, four overlay views, and technical report. An approval then validates the active draft, diffs it, asks Unity for an explicit local confirmation, and invokes the signed one-request `unity-ctx review-bridge` protocol. The Ed25519 private key is generated per bridge session and remains only in memory; only its public key is registered outside the project. `unity-ctx` persists the consumed signed grant and re-verifies it whenever Unity imports an Approved contract.

No approval or tracked-file write endpoint is registered with MCP.

## Trusted review runtime

The Unity-launched review bridge requires explicitly selected, absolute `Node.js` and `unity-ctx` executables. Both must be real, non-reparse files outside the Unity project; the privileged launch never resolves them through the project or `PATH`. Unity passes a private, in-memory review nonce to the child process. That nonce is separate from the public MCP session nonce stored in `session.json`, so public MCP clients cannot invoke native confirmation tools.

The live `/workflow` page is always rendered from the installed package template with a restrictive CSP. Project-generated HTML is an offline artifact only, and the public workflow API removes filesystem paths.

Spatial evidence consists of four raw views, four overlay views, `technical-report.json`, and `capture-manifest.json`. The bridge rehashes all ten files and requires the manifest to bind the same session, zero-error technical report, and exact ordinal proposal set. Every draft proposal is revalidated immediately before the native Unity modal and again immediately before review/apply. Interaction approval grants bind authority-derived subject and target geometry hashes.

Room and workflow state are reloaded after modal waits and only the reviewed target is merged into the latest state. The signed `unity-ctx review-bridge` result is the durable commit boundary; if later validation or workflow persistence fails, the bridge checks the external approval receipt and reports `applied` with `reconciliationRequired` instead of misreporting a committed approval as a failure.

## Room review

Unity publishes the active room review through
`Library/DungeonDecorator/RoomReviews/current.json`. The pointer contains a safe
`roomKey` and the absolute or RoomReviews-relative path to that room's
`state.json`.

- `GET /room-review` serves the solo-user Korean review UI.
- `GET /api/room-review/current` returns the current run without filesystem paths.
- `GET /api/room-review/image/:view?hash=<capture-set-hash>` serves one hash-bound PNG.
- `POST /api/room-review/decision` records `Approved`, `RevisionRequested`, or
  `UnableToJudge` after checking the nonce, run id, input hash, technical-report
  hash, capture-set hash, zero-error technical gate, and every capture's content
  hash.

The decision endpoint writes only the current `state.json` under `Library`. It
does not generate a preview, apply scene objects, or write tracked assets. Final
Apply remains a Unity Editor action that re-verifies the stored human decision.
