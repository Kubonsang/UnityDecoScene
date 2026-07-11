# Spatial Review Bridge

This loopback-only bridge is the human-authority path for Spatial Contract review. It is intentionally separate from MCP.

```powershell
$env:UNITY_CTX_BIN = "C:\path\to\unity-ctx.exe"
node Tools~/SpatialReviewBridge/server.mjs --project C:\path\to\UnityProject
```

The browser obtains a per-process nonce from `GET /api/health`. A human review POST then validates the active draft, records the review, diffs it, applies only an `Approved` draft, and validates the tracked result. Requests are accepted only from the local Preference Studio origin and drafts must remain under the current project's `Library` directory.

No approval or tracked-file write endpoint is registered with MCP.
