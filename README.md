# Unity Concept Room Decorator

Unity 6 editor package for dressing one existing room at a time from a concept brief and a curated set of prefabs. It deliberately does not generate mazes, rooms, navigation, or meshes.

> **Project status:** `0.4.0` preview for Unity 6 on Windows. The repository root is a Unity Package Manager package, not a standalone Unity project.

## Install

In Unity, open **Window > Package Management > Package Manager**, choose **Add package from git URL**, and enter:

```text
https://github.com/Kubonsang/UnityDecoScene.git
```

After Unity imports the package, open **Window > Concept Room Decorator > Spatial Calibration** to review asset geometry, or **Window > Concept Room Decorator** for the room workflow. Source prefabs and materials are never modified by calibration or preview operations.

## Core workflow

1. Add `ConceptRoom` to an existing room root and assign a `BoxCollider` as its authoring bounds.
2. Add flat floor colliders, observation points, and optional `KeepClearZone` volumes.
3. Create a `RoomConceptBrief`, `DecorCatalog`, and `RoomCompositionPlan` from **Assets > Create > Concept Room Decorator**.
4. Open **Window > Concept Room Decorator**.
5. Scan room surfaces and prefab geometry. Approve inward surface normals, compound OBB proxies, semantic axes, and bottom/back contact faces.
6. Compose a plan and generate a non-persistent preview. Unreviewed or incomplete geometry becomes an Asset Gap instead of an AABB guess.
7. Validate and capture the preview. Deterministic technical errors must be cleared before a person can approve the latest evidence set.
8. Apply only when the source and geometry are still current and the latest capture hash has explicit human approval; the complete apply operation is one Unity Undo group.

Host projects can provide an Editor-only `IRoomAuthoringAdapter` to build a `RoomAuthoringContext` from their own room source of truth. The context supplies reviewed surfaces, fixed wall and corner geometry, stable source hashes, and optional cell/side metadata. Floor surfaces remain support targets rather than solid obstacles, while adjacent walls and pillars participate in compound OBB collision checks. Existing integrations can continue to use the original preview API.

## MCP

The package includes a dependency-free Node MCP bridge under `Tools~/McpBridge/server.mjs`. In Unity, open **Tools > Concept Room Decorator > MCP Bridge Setup** to generate project-scoped Codex and Claude Code configuration snippets.

Unity must be open for room MCP tools to operate. If `unity-ctx` is installed or `UNITY_CTX_BIN` points to its executable, the bridge also exposes its read-only context, Spatial Manifest v2 check, and wall-suggestion tools. The room workflow remains available when that optional child process is disconnected. MCP can create and refine preview state, but it cannot apply changes to the scene.

Spatial Calibration adds proposal-only tools for inspection, four-view capture, draft reading, deterministic validation, and temporary AI proposals. MCP has no pass, approval, tracked-file write, or Apply tool.

## Development verification

Package-scoped EditMode regression tests use `testplay-runner v0.11.0` through the open Unity Editor. The compact wrapper and expected agent-safe output are documented in [Tools~/TestPlay/README.md](Tools~/TestPlay/README.md). The 0.4.0 verification environment is Unity `6000.3.8f1` for Windows.

## Human-reviewed Spatial Contracts

Open **Window > Concept Room Decorator > Spatial Calibration** to create a reusable geometry or interaction contract without modifying the active scene or source prefab.

1. Select a subject descriptor, optional target prefab, and a relationship template.
2. Review or edit the generated compound OBBs and bottom/back/top contact frames in the temporary calibration scene.
3. Place the subject in the intended reference pose and run deterministic validation.
4. Capture front, side, top, and contact-close-up views with raw/evidence variants.
5. Review the capture set in the Korean Preference Studio. Technical errors disable approval; a human can approve, request revision, or mark the capture unable to judge.

Contracts move through `Draft → TechnicalPassed → AwaitingHumanReview → Approved`. Geometry, dependency, contract, or capture hash changes invalidate approval. Approved asset contracts are stored under `Assets/SpatialContracts/Assets`; `SupportedBy` interaction contracts use `Assets/SpatialContracts/Interactions`.

The optional loopback human-authority service lives at `Tools~/SpatialReviewBridge/server.mjs`. It is separate from MCP, accepts only the local Preference Studio origin, uses a per-process nonce, and runs `validate → review → diff → apply --write → validate` after a human click.

## Geometry contract

Collider geometry is preferred. Prefabs without colliders receive one local OBB proxy per Renderer bound. A dependency hash prevents unchanged reviewed assets from being reanalysed; changed source assets return to an unreviewed state. Final collision decisions use compound OBB SAT, while AABB is only a broad-phase filter.

Default physical contracts allow a 0.5–1 cm gap for wall-mounted props, a 1–5 cm gap for wall-backed furniture, and a 0–1 cm floor gap with at least 60% support for floor-supported props. Penetration is not allowed. Curved, sloped, or non-planar room surfaces are reported as `UNSUPPORTED_SURFACE`.

## Supported scope

- Unity 6.0+
- URP-first; decal prefabs are detected without a hard URP assembly dependency
- Windows is the verified platform for the first release
- Editor-time, flat, single-room dressing
- Existing props, lights, and decal prefabs only

See [Documentation~/index.md](Documentation~/index.md) for setup and tool details.
