# Concept Room Decorator

## Install

Install the package from a local folder or Git URL through Unity Package Manager. The minimum supported Editor version is Unity 6.0.

## Prepare a room

`ConceptRoom` requires an authoring `BoxCollider`. The box is an authoring boundary, not generated room geometry. In **Room Setup**, scan its inward wall faces and ceiling plus one or more flat floor colliders. BoxCollider floors and coplanar MeshCollider floors are supported; curved, sloped, and non-planar meshes remain visible as unsupported evidence and cannot receive props.

Add `RoomObservationPoint` children for entrance or art-direction views. Add `KeepClearZone` children for doors, combat areas, interaction spots, or deliberate negative space. The package performs no pathfinding.

## Prepare a catalog

Create a `DecorCatalog`, select a prefab folder in the Editor window, and scan it. The scanner creates one `DecorAssetDescriptor` asset per prefab. Collider children become compound local OBB proxies; if no collider exists, Renderer local bounds become the proxies. Unchanged dependency hashes skip geometry reanalysis.

Review every descriptor before relying on it:

- Set one consistent `styleSet` for assets that belong together.
- Assign Hero, Support, StoryEvidence, Clutter, LightingCue, or DecalCue roles.
- Assign Floor, Wall, or Ceiling placement.
- Add motifs used by concept required/forbidden checks.
- Check compound OBB proxies, pivot offset, semantic forward/up axes, and bottom/back contact frames.
- Choose FloorSupported, WallBacked, WallMounted, CeilingMounted, or FreeStanding and review its gap, penetration, and support contract.
- Approve geometry only after this check. Source dependency changes automatically invalidate the approval.

## Compose and preview

A `RoomCompositionPlan` references the room, concept brief, catalog, seed, density, and semantic elements. Relationships are resolved near an anchor element; Unity, not the external agent, selects final transforms.

Preview objects use `DontSaveInEditor` and are recreated on every generation. Locked placements retain their transforms during refinement. Apply is disabled whenever technical validation has errors.

The Scene View overlay renders OBB/contact evidence in red for overlap, orange for contact failure, and green for valid contact. The technical gate uses stable codes including `GEOMETRY_UNREVIEWED`, `SURFACE_UNREVIEWED`, `NEED_GEOMETRY_V2`, `OBB_OVERLAP`, `SURFACE_PENETRATION`, `CONTACT_GAP`, `INSUFFICIENT_SUPPORT`, `CONTACT_DIRECTION`, and `UNSUPPORTED_SURFACE`.

After capture, compare the room views with the references and submit mood, style, story, and composition scores. Every score must meet the threshold stored in the concept brief; a high average cannot hide a weak axis.

## Calibrate Spatial Contracts

Spatial Calibration runs in an additive unsaved scene and never edits the active authoring scene or source prefab. Choose `WallMounted`, `WallBacked + FloorSupported`, `FloorSupported`, or `SupportedBy`, correct compound OBB handles and contact frames, then position the subject as the intended example.

One example defines the reference pose; tolerances still come from the conservative template. The deterministic report exposes gap, penetration, support, and direction alignment for every simultaneous contact. A wall-backed bookshelf therefore must pass both floor support and wall contact.

`Capture Example` produces front, side, top, and contact-close-up PNGs plus raw/evidence variants under `Library/DungeonDecorator/SpatialCaptures`. Draft contracts remain under `Library/DungeonDecorator/SpatialDrafts` until a human approves the latest capture hash.

The Preference Studio's **에셋 공간 규칙 / Asset Geometry** workspace is the human gate. `승인` is disabled when technical errors exist. `수정 필요` records issue types and a comment; `판단 불가` requests a better camera or overlay instead of forcing failure. AI suggestions are displayed separately and never count as approval.

## MCP safety model

The MCP bridge binds only to `127.0.0.1`, generates an Editor-session nonce, and writes connection details under `Library/DungeonDecorator/session.json`. Agent tools can read project context and manipulate preview state only. They cannot call Apply.

Open **Tools > Concept Room Decorator > MCP Bridge Setup** to copy the Node bridge to a stable project-local path and generate project-scoped configuration. The MCP workflow ends with `submit_visual_review`; final Apply remains available only in Unity.

The Node bridge optionally starts `unity-ctx mcp` and merges both read-only tool lists. Set `UNITY_CTX_BIN` when the executable is not on `PATH`. Every preview and validation response includes the manifest hash, geometry-profile hash, and seed. The bridge never exposes Unity Apply or a unity-ctx mutation tool.

Spatial tools available to agents are `inspect_spatial_calibration`, `capture_spatial_calibration`, `get_spatial_contract_draft`, `submit_spatial_contract_proposal`, and `get_deterministic_validation_report`. They are intentionally proposal-only. The separate loopback Spatial Review Bridge is the only web path that can invoke the human-reviewed `unity-ctx spatial apply --write` flow.
