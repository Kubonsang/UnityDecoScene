# Migrating from 0.1 to 0.2

Existing briefs, catalogs, plans, and `ConceptRoom` components remain readable. Version 0.2 deliberately blocks placement until new physical evidence is reviewed.

1. Open **Window > Concept Room Decorator** and run **Scan Room Surfaces**.
2. Check that wall normals point into the room, then approve supported surfaces.
3. Rescan the prefab catalog. Existing descriptor metadata is preserved, while a geometry draft is added.
4. Select each descriptor, correct forward/back/bottom assumptions in its Inspector, select a contact requirement, and approve geometry.
5. Regenerate the preview. Treat geometry blockers as catalog work, not as permission to fall back to AABB placement.

The old manifest v1 remains valid for legacy bounds queries. Contact or rotated-geometry requests against v1 must return `UNKNOWN NEED_GEOMETRY_V2`.
