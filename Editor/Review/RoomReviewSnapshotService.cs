using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    /// <summary>
    /// Builds the review fingerprint from the state that can change either a deterministic verdict
    /// or what the human reviewer sees. Display names and review comments are intentionally excluded.
    /// </summary>
    public static class RoomReviewSnapshotService
    {
        public const string CaptureProfileVersion = "room-capture-4view-wall-contact-v2-768";

        public static RoomReviewInputs Capture(PreviewSession session)
        {
            if (session?.Plan?.Room == null) throw new ArgumentException("An active room preview is required.", nameof(session));
            if (ReferenceEquals(RoomPreviewManager.Current, session)) RoomPreviewManager.SynchronizePreviewTransforms();

            var inputs = new RoomReviewInputs
            {
                targetId = StableTargetId(session.Plan.Room),
                validationRulesHash = RoomReviewHashUtility.Sha256(PreviewValidationService.ValidationVersion),
                captureProfileHash = RoomReviewHashUtility.Sha256(CaptureProfileVersion)
            };

            var roomItems = CaptureRoomShell(session);
            var compositionItems = CaptureComposition(session);
            var assetItems = CaptureAssets(session);
            var conceptItems = CaptureConcept(session);
            var placementItems = CapturePlacements(session);
            var presentationItems = CapturePresentation(session);

            inputs.items.AddRange(roomItems);
            inputs.items.AddRange(compositionItems);
            inputs.items.AddRange(assetItems);
            inputs.items.AddRange(conceptItems);
            inputs.items.AddRange(placementItems);
            inputs.items.AddRange(presentationItems);
            inputs.items.Add(Item("validation:rules", RoomReviewChangeScope.ValidationRules, inputs.validationRulesHash));
            inputs.items.Add(Item("capture:profile", RoomReviewChangeScope.CaptureProfile, inputs.captureProfileHash));

            inputs.roomShellHash = Aggregate(roomItems);
            inputs.compositionHash = Aggregate(compositionItems);
            inputs.assetDependencyHash = Aggregate(assetItems);
            inputs.conceptHash = Aggregate(conceptItems);
            inputs.placementHash = Aggregate(placementItems);
            inputs.presentationHash = Aggregate(presentationItems);
            session.ApprovalSnapshot ??= new PreviewApprovalSnapshot();
            session.ApprovalSnapshot.sourceHash = session.AuthoringSourceHash ?? string.Empty;
            session.ApprovalSnapshot.obstacleHash = session.ObstacleGeometryHash ?? string.Empty;
            session.ApprovalSnapshot.placementHash = inputs.placementHash;
            return inputs;
        }

        private static List<RoomReviewInputItem> CaptureRoomShell(PreviewSession session)
        {
            var room = session.Plan.Room;
            var result = new List<RoomReviewInputItem>();
            var bounds = room.AuthoringBounds;
            result.Add(Item("room:authoring-bounds", RoomReviewChangeScope.RoomShell, RoomReviewHashUtility.HashParts(
                room.RoomId ?? string.Empty,
                bounds != null ? Collider(bounds) : "missing",
                F(room.BoundaryPadding))));

            if (session.AuthoringContext != null)
            {
                var sourceHash = session.AuthoringSourceHash ?? session.AuthoringContext.sourceHash ?? string.Empty;
                var obstacleHash = session.ObstacleGeometryHash ?? session.AuthoringContext.ComputeObstacleHash();
                result.Add(Item("authoring:source", RoomReviewChangeScope.RoomShell, RoomReviewHashUtility.HashParts(
                    session.AuthoringContext.adapterId ?? string.Empty,
                    session.AuthoringContext.adapterVersion ?? string.Empty,
                    session.AuthoringContext.sourceId ?? string.Empty,
                    sourceHash)));
                result.Add(Item("authoring:obstacles", RoomReviewChangeScope.RoomShell, RoomReviewHashUtility.Sha256(obstacleHash)));
            }

            var floorFingerprints = room.FloorColliders.Where(value => value != null).Select(Collider).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < floorFingerprints.Length; index++)
                result.Add(Item($"floor:{index}", RoomReviewChangeScope.RoomShell, RoomReviewHashUtility.Sha256(floorFingerprints[index])));

            var reviewSurfaces = session.AuthoringContext?.surfaces != null && session.AuthoringContext.surfaces.Count > 0
                ? session.AuthoringContext.surfaces
                : room.Surfaces;
            foreach (var surface in reviewSurfaces.Where(value => value != null).OrderBy(value => value.SurfaceId, StringComparer.Ordinal))
            {
                result.Add(Item($"surface:{surface.SurfaceId}", RoomReviewChangeScope.RoomShell, RoomReviewHashUtility.HashParts(
                    surface.SurfaceId ?? string.Empty,
                    surface.SurfaceType.ToString(),
                    V(surface.Origin),
                    V(surface.Normal),
                    V(surface.Tangent),
                    V2(surface.Size),
                    surface.Reviewed ? "1" : "0",
                    surface.Supported ? "1" : "0",
                    surface.UnsupportedReason ?? string.Empty,
                    surface.SourceCollider != null ? Collider(surface.SourceCollider) : "none")));
            }

            var clearFingerprints = room.KeepClearZones.Where(value => value?.Volume != null)
                .Select(value => Collider(value.Volume)).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < clearFingerprints.Length; index++)
                result.Add(Item($"keep-clear:{index}", RoomReviewChangeScope.RoomShell, RoomReviewHashUtility.Sha256(clearFingerprints[index])));

            var observations = room.ObservationPoints.Where(value => value != null)
                .Select(value => string.Join("|", V(value.transform.position), Q(value.transform.rotation), F(value.FieldOfView), value.Primary ? "1" : "0"))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < observations.Length; index++)
                result.Add(Item($"observation:{index}", RoomReviewChangeScope.RoomShell, RoomReviewHashUtility.Sha256(observations[index])));

            var shellRenderers = room.GetComponentsInChildren<Renderer>(true)
                .Where(value => value != null && (session.Root == null || !value.transform.IsChildOf(session.Root.transform)))
                .Select(Renderer)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < shellRenderers.Length; index++)
                result.Add(Item($"shell-renderer:{index}", RoomReviewChangeScope.RoomShell, RoomReviewHashUtility.Sha256(shellRenderers[index])));

            return result;
        }

        private static List<RoomReviewInputItem> CaptureComposition(PreviewSession session)
        {
            var plan = session.Plan;
            var result = new List<RoomReviewInputItem>
            {
                Item("composition:settings", RoomReviewChangeScope.Composition,
                    RoomReviewHashUtility.HashParts(plan.Seed.ToString(CultureInfo.InvariantCulture), F(plan.Density)))
            };
            foreach (var element in plan.Elements.Where(value => value != null).OrderBy(value => value.elementId, StringComparer.Ordinal))
            {
                result.Add(Item($"element:{element.elementId}", RoomReviewChangeScope.Composition, RoomReviewHashUtility.HashParts(
                    element.elementId ?? string.Empty,
                    element.descriptorId ?? string.Empty,
                    element.role.ToString(),
                    element.relation.ToString(),
                    element.anchorElementId ?? string.Empty,
                    element.count.ToString(CultureInfo.InvariantCulture),
                    element.preferredZone.ToString(),
                    element.preferredSurfaceId ?? string.Empty,
                    F(element.spacing),
                    element.locked ? "1" : "0")));
            }
            foreach (var arrangement in plan.SurfaceArrangements.Where(value => value != null)
                         .OrderBy(value => value.arrangement_id, StringComparer.Ordinal))
            {
                result.Add(Item($"arrangement:{arrangement.arrangement_id}", RoomReviewChangeScope.Composition,
                    RoomReviewHashUtility.Sha256(SurfaceArrangementSpecUtility.CanonicalJson(arrangement))));
            }
            if (session.Request?.SupportContracts != null)
            {
                result.Add(Item("arrangement:support-contracts", RoomReviewChangeScope.Composition,
                    RoomReviewHashUtility.Sha256(session.Request.SupportContracts.ComputeHash())));
            }
            return result;
        }

        private static List<RoomReviewInputItem> CaptureAssets(PreviewSession session)
        {
            var requested = new HashSet<string>(session.Plan.Elements.Where(value => value != null)
                .Select(value => value.descriptorId).Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.Ordinal);
            foreach (var descriptorId in session.Plan.SurfaceArrangements.Where(value => value?.members != null)
                         .SelectMany(value => value.members)
                         .Where(value => value != null && !string.IsNullOrWhiteSpace(value.descriptor_id))
                         .Select(value => value.descriptor_id)) requested.Add(descriptorId);
            foreach (var placement in session.Placements.Where(value => value?.descriptor != null)) requested.Add(placement.descriptor.AssetId);
            var result = new List<RoomReviewInputItem>();
            foreach (var id in requested.OrderBy(value => value, StringComparer.Ordinal))
            {
                var descriptor = session.Plan.Catalog != null ? session.Plan.Catalog.Find(id) : null;
                result.Add(descriptor == null
                    ? Item($"asset:missing:{id}", RoomReviewChangeScope.Asset, RoomReviewHashUtility.Sha256($"missing:{id}"))
                    : Item($"asset:{descriptor.AssetId}", RoomReviewChangeScope.Asset, Descriptor(descriptor)));
            }
            return result;
        }

        private static List<RoomReviewInputItem> CaptureConcept(PreviewSession session)
        {
            var brief = session.Plan.ConceptBrief;
            if (brief == null) return new List<RoomReviewInputItem> { Item("concept:none", RoomReviewChangeScope.Concept, RoomReviewHashUtility.Sha256("none")) };
            var references = brief.ReferenceImages.Where(value => value != null).Select(Asset).OrderBy(value => value, StringComparer.Ordinal);
            var parts = new List<string>
            {
                "purpose:" + (brief.RoomPurpose ?? string.Empty),
                "faction:" + (brief.OccupantsAndFaction ?? string.Empty),
                "story:" + (brief.StoryOrEvidence ?? string.Empty),
                "hero:" + (brief.HeroSubject ?? string.Empty),
                "minimum-mood:" + brief.MinimumMoodScore.ToString(CultureInfo.InvariantCulture),
                "minimum-style:" + brief.MinimumStyleScore.ToString(CultureInfo.InvariantCulture),
                "minimum-story:" + brief.MinimumStoryScore.ToString(CultureInfo.InvariantCulture),
                "minimum-composition:" + brief.MinimumCompositionScore.ToString(CultureInfo.InvariantCulture),
                "moods:" + string.Join("\u001f", Sorted(brief.MoodKeywords)),
                "materials:" + string.Join("\u001f", Sorted(brief.MaterialKeywords)),
                "colors:" + string.Join("\u001f", Sorted(brief.ColorKeywords)),
                "required:" + string.Join("\u001f", Sorted(brief.RequiredMotifs)),
                "forbidden:" + string.Join("\u001f", Sorted(brief.ForbiddenMotifs)),
                "references:" + string.Join("\u001f", references)
            };
            return new List<RoomReviewInputItem>
            {
                Item("concept:brief", RoomReviewChangeScope.Concept, RoomReviewHashUtility.HashParts(parts))
            };
        }

        private static List<RoomReviewInputItem> CapturePlacements(PreviewSession session) => session.Placements
            .Where(value => value != null)
            .OrderBy(value => value.placementId, StringComparer.Ordinal)
            .Select(value => Item($"placement:{value.placementId}", RoomReviewChangeScope.Placement, RoomReviewHashUtility.HashParts(
                value.placementId ?? string.Empty,
                value.elementId ?? string.Empty,
                value.instanceIndex.ToString(CultureInfo.InvariantCulture),
                value.descriptor?.AssetId ?? string.Empty,
                value.role.ToString(),
                value.relation.ToString(),
                V(value.position),
                Q(value.rotation),
                V(value.scale),
                string.Join(",", (value.surfaceIds ?? new List<string>()).OrderBy(id => id, StringComparer.Ordinal)),
                value.surfaceId ?? string.Empty,
                value.arrangementId ?? string.Empty,
                value.affinityGroup ?? string.Empty,
                value.supportPlacementId ?? string.Empty,
                value.stackLevel.ToString(CultureInfo.InvariantCulture),
                value.locked ? "1" : "0")))
            .ToList();

        private static List<RoomReviewInputItem> CapturePresentation(PreviewSession session)
        {
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(value => value != null && !EditorUtility.IsPersistent(value))
                .Select(value => string.Join("|",
                    value.type,
                    value.enabled ? "1" : "0",
                    value.gameObject.activeInHierarchy ? "1" : "0",
                    V(value.transform.position),
                    Q(value.transform.rotation),
                    C(value.color),
                    F(value.intensity),
                    F(value.range),
                    F(value.spotAngle),
                    value.shadows,
                    value.cullingMask.ToString(CultureInfo.InvariantCulture)))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            var render = new[]
            {
                "ambient-mode:" + RenderSettings.ambientMode,
                "ambient-sky:" + C(RenderSettings.ambientSkyColor),
                "ambient-equator:" + C(RenderSettings.ambientEquatorColor),
                "ambient-ground:" + C(RenderSettings.ambientGroundColor),
                "ambient-light:" + C(RenderSettings.ambientLight),
                "ambient-intensity:" + F(RenderSettings.ambientIntensity),
                "fog-enabled:" + (RenderSettings.fog ? "1" : "0"),
                "fog-color:" + C(RenderSettings.fogColor),
                "fog-density:" + F(RenderSettings.fogDensity),
                "skybox:" + Asset(RenderSettings.skybox),
                "lights:" + RoomReviewHashUtility.HashOrdinal(lights)
            };
            return new List<RoomReviewInputItem>
            {
                Item("presentation:lighting", RoomReviewChangeScope.Presentation, RoomReviewHashUtility.HashOrdinal(render))
            };
        }

        private static string Descriptor(DecorAssetDescriptor descriptor)
        {
            var parts = new List<string>
            {
                "id:" + (descriptor.AssetId ?? string.Empty),
                "descriptor-asset:" + Asset(descriptor),
                "prefab-asset:" + Asset(descriptor.Prefab),
                "type:" + descriptor.AssetType,
                "surface:" + descriptor.Surface,
                "style:" + (descriptor.StyleSet ?? string.Empty),
                "visual-weight:" + F(descriptor.VisualWeight),
                "clearance:" + F(descriptor.Clearance),
                "minimum-scale:" + F(descriptor.MinimumScale),
                "maximum-scale:" + F(descriptor.MaximumScale),
                "rotation:" + descriptor.Rotation,
                "bounds-center:" + V(descriptor.LocalBoundsCenter),
                "bounds-size:" + V(descriptor.LocalBoundsSize),
                "maximum-instances:" + descriptor.MaximumInstancesPerRoom.ToString(CultureInfo.InvariantCulture),
                "minimum-light:" + F(descriptor.MinimumLightIntensity),
                "maximum-light:" + F(descriptor.MaximumLightIntensity),
                "maximum-range:" + F(descriptor.MaximumLightRange),
                "reviewed:" + (descriptor.Reviewed ? "1" : "0"),
                "roles:" + string.Join(",", descriptor.Roles.OrderBy(value => value)),
                "themes:" + string.Join(",", Sorted(descriptor.Themes)),
                "factions:" + string.Join(",", Sorted(descriptor.Factions)),
                "eras:" + string.Join(",", Sorted(descriptor.Eras)),
                "materials:" + string.Join(",", Sorted(descriptor.Materials)),
                "motifs:" + string.Join(",", Sorted(descriptor.Motifs)),
                "forbidden-assets:" + string.Join(",", Sorted(descriptor.ForbiddenAssetIds))
            };
            var geometry = descriptor.Geometry;
            if (geometry == null) return RoomReviewHashUtility.HashParts(parts.Append("geometry:none"));
            parts.AddRange(new[]
            {
                "geometry-version:" + geometry.version.ToString(CultureInfo.InvariantCulture), "geometry-source:" + geometry.source,
                "forward:" + V(geometry.forwardAxis), "up:" + V(geometry.upAxis), "pivot:" + V(geometry.pivotOffset),
                "confidence:" + F(geometry.inferenceConfidence), "geometry-reviewed:" + (geometry.reviewed ? "1" : "0"),
                "geometry-dependency:" + (geometry.dependencyHash ?? string.Empty),
                "bottom-frame:" + Frame(geometry.bottomContact), "back-frame:" + Frame(geometry.backContact),
                "top-frame:" + Frame(geometry.topContact)
            });
            parts.AddRange((geometry.collisionProxies ?? new List<OrientedBoxProxy>()).Where(value => value != null)
                .Select(value => "proxy:" + string.Join("|", value.proxyId ?? string.Empty, V(value.localCenter), V(value.size), Q(value.localRotation)))
                .OrderBy(value => value, StringComparer.Ordinal));
            parts.AddRange(geometry.EffectiveContacts.Where(value => value != null)
                .Select(value => "contact:" + string.Join("|", value.ruleId ?? string.Empty, value.frameId ?? string.Empty, value.requirement,
                    F(value.minimumGap), F(value.maximumGap), F(value.maximumPenetration), F(value.minimumSupportCoverage)))
                .OrderBy(value => value, StringComparer.Ordinal));
            return RoomReviewHashUtility.HashParts(parts);
        }

        private static string Renderer(Renderer renderer)
        {
            var mesh = renderer switch
            {
                SkinnedMeshRenderer skinned => skinned.sharedMesh,
                _ => renderer.GetComponent<MeshFilter>()?.sharedMesh
            };
            return string.Join("|",
                renderer.GetType().Name,
                renderer.enabled ? "1" : "0",
                renderer.gameObject.activeInHierarchy ? "1" : "0",
                V(renderer.transform.position),
                Q(renderer.transform.rotation),
                V(renderer.transform.lossyScale),
                renderer.shadowCastingMode,
                renderer.receiveShadows ? "1" : "0",
                Asset(mesh),
                string.Join(",", renderer.sharedMaterials.Where(value => value != null).Select(Asset).OrderBy(value => value, StringComparer.Ordinal)),
                Asset(PrefabUtility.GetCorrespondingObjectFromSource(renderer.gameObject)));
        }

        private static string Collider(Collider collider)
        {
            var shape = collider switch
            {
                BoxCollider box => string.Join("|", "box", V(box.center), V(box.size)),
                SphereCollider sphere => string.Join("|", "sphere", V(sphere.center), F(sphere.radius)),
                CapsuleCollider capsule => string.Join("|", "capsule", V(capsule.center), F(capsule.radius), F(capsule.height), capsule.direction),
                MeshCollider mesh => string.Join("|", "mesh", Asset(mesh.sharedMesh), mesh.convex ? "1" : "0"),
                _ => collider.GetType().Name
            };
            return string.Join("|", shape, collider.enabled ? "1" : "0", collider.isTrigger ? "1" : "0",
                V(collider.transform.position), Q(collider.transform.rotation), V(collider.transform.lossyScale));
        }

        private static string Frame(ContactFrame frame) => frame == null ? "frame:none" :
            string.Join("|", frame.frameId ?? string.Empty, V(frame.localPoint), V(frame.localNormal), V(frame.localTangent), V2(frame.size));

        private static string Asset(UnityEngine.Object value)
        {
            if (value == null) return "none";
            var path = AssetDatabase.GetAssetPath(value);
            if (string.IsNullOrWhiteSpace(path)) return $"transient:{value.GetType().Name}";
            return string.Join("|", AssetDatabase.AssetPathToGUID(path), AssetDatabase.GetAssetDependencyHash(path));
        }

        private static string StableTargetId(ConceptRoom room)
        {
            if (!string.IsNullOrWhiteSpace(room.RoomId)) return room.RoomId;
            var global = GlobalObjectId.GetGlobalObjectIdSlow(room);
            return global.identifierType != 0 ? global.ToString() : "unsaved-room";
        }

        private static RoomReviewInputItem Item(string id, RoomReviewChangeScope scope, string hash) => new() { id = id, scope = scope, hash = hash };
        private static string Aggregate(IEnumerable<RoomReviewInputItem> items) => RoomReviewHashUtility.HashOrdinal(items.Select(value => $"{value.id}|{value.hash}"));
        private static IEnumerable<string> Sorted(IEnumerable<string> values) => (values ?? Array.Empty<string>()).Where(value => value != null).OrderBy(value => value, StringComparer.Ordinal);
        private static string F(float value) => RoomReviewHashUtility.Quantize(value).ToString(CultureInfo.InvariantCulture);
        private static string V(Vector3 value) => string.Join(",", F(value.x), F(value.y), F(value.z));
        private static string V2(Vector2 value) => string.Join(",", F(value.x), F(value.y));
        private static string C(Color value) => string.Join(",", F(value.r), F(value.g), F(value.b), F(value.a));

        private static string Q(Quaternion value)
        {
            value.Normalize();
            if (value.w < 0f || (Mathf.Approximately(value.w, 0f) && (value.z < 0f || (Mathf.Approximately(value.z, 0f) && (value.y < 0f || (Mathf.Approximately(value.y, 0f) && value.x < 0f))))))
                value = new Quaternion(-value.x, -value.y, -value.z, -value.w);
            return string.Join(",", F(value.x), F(value.y), F(value.z), F(value.w));
        }
    }
}
