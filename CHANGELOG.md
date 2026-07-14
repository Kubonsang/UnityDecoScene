# Changelog

## 0.4.0

- Added host-project room-authoring adapters and source-bound authoring contexts without taking a dependency on host project code.
- Added reviewed fixed-room geometry and compound OBB obstacle checks for walls, adjacent wall segments, and corner structures.
- Preserved supporting-prop relation anchors while projecting candidates onto their intended contact surfaces.
- Isolated room captures to the preview scene so unrelated objects from other open scenes cannot appear in review evidence.
- Added hash-verified synchronization of approved Spatial Contracts into Decor Asset geometry without accepting stale dependency or contract data.

## 0.3.0

- Added human-reviewed asset and interaction Spatial Contracts with hash-bound approval states.
- Added a temporary Spatial Calibration stage with editable compound OBBs and contact frames.
- Added deterministic multi-contact validation for floor-supported, wall-backed, wall-mounted, and supported-by relationships.
- Added four-view calibration evidence, revision feedback, and a nonce-protected local review bridge.
- Added Korean Preference Studio review status, texture-variant grouping, and explicit wall selection.
- Added deterministic room-review evidence caching and approved-contract dungeon preview validation.
- Added a compact TestPlay v0.11.0 bridge workflow for package-scoped EditMode regression tests.

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
