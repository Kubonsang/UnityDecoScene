# Changelog

## 0.2.0

- Added reviewed compound OBB geometry profiles and bottom/back contact frames.
- Added reviewed floor, wall, and ceiling surfaces, including planar MeshCollider floors.
- Added deterministic surface alignment, OBB SAT collision checks, contact gap, penetration, direction, and support validation.
- Added five-step Room Setup, Asset Catalog, Composition, Preview, and Validate & Apply workflow.
- Added Scene View contact evidence and strict Asset Gap handling for incomplete geometry.
- Added incremental dependency-hash rescanning and a geometry review inspector.
- Added optional unity-ctx v0.9 tool aggregation through the project MCP bridge.
- Added shared Go/C# spatial verdict fixtures and response hashes for reproducibility.

## 0.1.0

- Initial room, concept, catalog, and composition data model.
- Deterministic room placement and technical validation.
- Non-persistent previews, multi-view capture, and Undo-safe apply.
- Prefab scanning and catalog sheet rendering.
- Local MCP bridge for Codex and Claude Code.
