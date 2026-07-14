# Spatial Review Bridge

This loopback-only bridge is the human-authority path for Spatial Contract review. It is intentionally separate from MCP.

```powershell
$env:UNITY_CTX_BIN = "C:\path\to\unity-ctx.exe"
node Tools~/SpatialReviewBridge/server.mjs --project C:\path\to\UnityProject
```

The browser obtains a per-process nonce from `GET /api/health`. A human review POST then validates the active draft, records the review, diffs it, applies only an `Approved` draft, and validates the tracked result. Requests are accepted only from the local Preference Studio origin and drafts must remain under the current project's `Library` directory.

No approval or tracked-file write endpoint is registered with MCP.

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
