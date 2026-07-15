using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [InitializeOnLoad]
    public static class RoomPreviewManager
    {
        private const string PreviewRootName = "__ConceptRoomDecoratorPreview__";

        public static PreviewSession Current { get; private set; }
        public static event Action Changed;

        static RoomPreviewManager()
        {
            EditorApplication.delayCall += CleanupOrphanedPreviewRoots;
            AssemblyReloadEvents.beforeAssemblyReload += DiscardPreview;
        }

        public static PreviewSession GeneratePreview(RoomCompositionPlan plan, bool preserveLocks = true)
        {
            return GeneratePreview(new LayoutRequest(plan), preserveLocks);
        }

        public static PreviewSession GeneratePreview(LayoutRequest request, bool preserveLocks = true)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var plan = request.Plan;
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            IReadOnlyList<PlacedDecorItem> lockSource;
            if (request.LockedPlacements != null) lockSource = request.LockedPlacements;
            else if (preserveLocks && Current != null) lockSource = Current.Placements;
            else lockSource = Array.Empty<PlacedDecorItem>();
            var locked = BuildCompatibleRegenerationLocks(plan, lockSource);
            var effectiveRequest = new LayoutRequest(plan, locked, request.AuthoringContext, request.SupportContracts);

            DiscardPreview(false, plan);
            var layout = DeterministicLayoutEngine.Generate(effectiveRequest);
            var root = new GameObject(PreviewRootName)
            {
                hideFlags = HideFlags.DontSaveInEditor
            };
            var roomScene = plan.Room.gameObject.scene;
            if (roomScene.IsValid() && roomScene.isLoaded && root.scene != roomScene)
                SceneManager.MoveGameObjectToScene(root, roomScene);
            root.transform.SetParent(plan.Room.transform, true);

            var session = new PreviewSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Plan = plan,
                Request = effectiveRequest,
                AuthoringContext = effectiveRequest.AuthoringContext,
                Root = root,
                AssetGaps = layout.AssetGaps,
                ManifestHash = ComputeManifestHash(plan.Room),
                GeometryProfileHash = ComputeGeometryHash(plan, effectiveRequest.SupportContracts),
                AuthoringSourceHash = effectiveRequest.AuthoringContext?.sourceHash ?? string.Empty,
                ObstacleGeometryHash = effectiveRequest.AuthoringContext?.ComputeObstacleHash() ?? string.Empty
            };
            session.ApprovalSnapshot.sourceHash = session.AuthoringSourceHash;
            session.ApprovalSnapshot.obstacleHash = session.ObstacleGeometryHash;

            foreach (var placement in layout.Placements)
            {
                var instance = PrefabUtility.InstantiatePrefab(placement.descriptor.Prefab, plan.Room.gameObject.scene) as GameObject;
                if (instance == null) continue;
                instance.name = $"[Preview] {placement.descriptor.Prefab.name}";
                instance.transform.SetParent(root.transform, true);
                instance.transform.SetPositionAndRotation(placement.position, placement.rotation);
                instance.transform.localScale = placement.scale;
                var marker = instance.AddComponent<PreviewPlacementMarker>();
                if (marker != null)
                {
                    marker.placementId = placement.placementId;
                    marker.elementId = placement.elementId;
                    marker.locked = placement.locked;
                }
                SetPreviewFlags(instance);
                placement.previewObject = instance;
                session.Placements.Add(placement);
            }

            Current = session;
            Physics.SyncTransforms();
            Current.LastValidation = PreviewValidationService.Validate(Current);
            Current.ApprovalSnapshot.technicalReportHash = Current.LastValidation.reportHash;
            Selection.activeGameObject = root;
            SceneView.RepaintAll();
            Changed?.Invoke();
            return Current;
        }

        /// <summary>
        /// Rebuilds one Surface Arrangement while freezing every placement outside that arrangement.
        /// The temporary freeze is removed after generation so the user's existing lock choices are
        /// preserved rather than turning the whole room into permanently locked content.
        /// </summary>
        public static PreviewSession RegenerateArrangement(string arrangementId)
        {
            if (Current == null) throw new InvalidOperationException("Generate a room preview before regenerating one Surface Arrangement.");
            if (string.IsNullOrWhiteSpace(arrangementId)) throw new ArgumentException("arrangementId is required.", nameof(arrangementId));
            arrangementId = arrangementId.Trim();
            if (!Current.Plan.SurfaceArrangements.Any(value => value != null &&
                                                               string.Equals(value.arrangement_id, arrangementId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Surface Arrangement '{arrangementId}' is not part of the active plan.");

            SynchronizePreviewTransforms();
            var active = Current;
            var originalLockStates = active.Placements
                .Where(value => value != null &&
                                !string.Equals(value.arrangementId, arrangementId, StringComparison.Ordinal) &&
                                !string.IsNullOrWhiteSpace(value.placementId))
                .GroupBy(value => value.placementId, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.First().locked, StringComparer.Ordinal);
            var frozen = BuildArrangementRegenerationLocks(active.Placements, arrangementId);
            var request = new LayoutRequest(
                active.Plan,
                frozen,
                active.AuthoringContext,
                active.Request?.SupportContracts);
            var regenerated = GeneratePreview(request, false);

            foreach (var placement in regenerated.Placements)
            {
                if (placement == null || !originalLockStates.TryGetValue(placement.placementId ?? string.Empty, out var wasLocked)) continue;
                placement.locked = wasLocked;
                var marker = placement.previewObject != null ? placement.previewObject.GetComponent<PreviewPlacementMarker>() : null;
                if (marker != null) marker.locked = wasLocked;
            }
            Changed?.Invoke();
            return regenerated;
        }

        internal static IReadOnlyList<PlacedDecorItem> BuildArrangementRegenerationLocks(
            IReadOnlyList<PlacedDecorItem> placements,
            string arrangementId)
        {
            if (string.IsNullOrWhiteSpace(arrangementId)) throw new ArgumentException("arrangementId is required.", nameof(arrangementId));
            return (placements ?? Array.Empty<PlacedDecorItem>())
                .Where(value => value != null && !string.Equals(value.arrangementId, arrangementId.Trim(), StringComparison.Ordinal))
                .Select(CloneForRegeneration)
                .ToArray();
        }

        internal static IReadOnlyList<PlacedDecorItem> BuildCompatibleRegenerationLocks(
            RoomCompositionPlan plan,
            IReadOnlyList<PlacedDecorItem> placements)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var source = (placements ?? Array.Empty<PlacedDecorItem>()).Where(value => value != null).ToArray();
            var analysis = SurfaceArrangementPlacementRules.Analyze(plan, source);
            var byId = source.Where(value => !string.IsNullOrWhiteSpace(value.placementId))
                .GroupBy(value => value.placementId, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.ToArray(), StringComparer.Ordinal);
            return source
                .Where(value => value.locked &&
                                (string.IsNullOrWhiteSpace(value.arrangementId) ||
                                 analysis.CompatiblePlacements.Contains(value) &&
                                 HasFullyLockedSupportChain(value, byId, analysis.CompatiblePlacements)))
                .Select(CloneForRegeneration)
                .ToArray();
        }

        private static bool HasFullyLockedSupportChain(
            PlacedDecorItem placement,
            IReadOnlyDictionary<string, PlacedDecorItem[]> byId,
            ISet<PlacedDecorItem> compatiblePlacements)
        {
            var visited = new HashSet<PlacedDecorItem>();
            var current = placement;
            while (!string.IsNullOrWhiteSpace(current?.arrangementId))
            {
                if (!current.locked || !compatiblePlacements.Contains(current) || !visited.Add(current) ||
                    string.IsNullOrWhiteSpace(current.supportPlacementId) ||
                    !byId.TryGetValue(current.supportPlacementId, out var supports) || supports.Length != 1)
                    return false;
                current = supports[0];
            }
            return current?.locked == true;
        }

        public static bool SetLocked(IEnumerable<string> placementIds, bool locked)
        {
            if (Current == null) return false;
            var ids = new HashSet<string>(placementIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            var changed = false;
            foreach (var placement in Current.Placements)
            {
                if (!ids.Contains(placement.placementId) && !ids.Contains(placement.elementId)) continue;
                placement.locked = locked;
                if (placement.previewObject != null)
                {
                    var marker = placement.previewObject.GetComponent<PreviewPlacementMarker>();
                    if (marker != null) marker.locked = locked;
                }
                changed = true;
            }
            if (changed) Changed?.Invoke();
            return changed;
        }

        public static bool LockSelection(bool locked)
        {
            if (Selection.activeGameObject == null) return false;
            var marker = Selection.activeGameObject.GetComponentInParent<PreviewPlacementMarker>();
            return marker != null && SetLocked(new[] { marker.placementId }, locked);
        }

        public static ValidationReport ValidateCurrent()
        {
            if (Current == null) return null;
            SynchronizePreviewTransforms();
            Current.LastValidation = PreviewValidationService.Validate(Current);
            Current.ApprovalSnapshot.technicalReportHash = Current.LastValidation.reportHash;
            Changed?.Invoke();
            return Current.LastValidation;
        }

        public static bool SynchronizePreviewTransforms()
        {
            if (Current == null) return false;
            var changed = false;
            foreach (var placement in Current.Placements)
            {
                if (placement?.previewObject == null || placement.descriptor == null) continue;
                var transform = placement.previewObject.transform;
                var position = transform.position;
                var rotation = transform.rotation;
                var scale = transform.localScale;
                if (Approximately(placement.position, position) && Approximately(placement.rotation, rotation) && Approximately(placement.scale, scale)) continue;
                placement.position = position;
                placement.rotation = rotation;
                placement.scale = scale;
                placement.worldBounds = PlacementBoundsUtility.TransformBounds(
                    new Bounds(placement.descriptor.LocalBoundsCenter, placement.descriptor.LocalBoundsSize),
                    position,
                    rotation,
                    scale);
                changed = true;
            }
            if (changed) Physics.SyncTransforms();
            return changed;
        }

        public static bool SetVisualReview(VisualQualityScores scores)
        {
            if (Current == null || scores == null) return false;
            if (Current.LastValidation == null) Current.LastValidation = PreviewValidationService.Validate(Current);
            scores.reviewed = true;
            scores.mood = Mathf.Clamp(scores.mood, 0, 100);
            scores.style = Mathf.Clamp(scores.style, 0, 100);
            scores.story = Mathf.Clamp(scores.story, 0, 100);
            scores.composition = Mathf.Clamp(scores.composition, 0, 100);
            Current.LastValidation.visualScores = scores;
            Changed?.Invoke();
            return true;
        }

        public static GameObject ApplyCurrent()
        {
            if (Current == null) throw new InvalidOperationException("There is no active preview.");
            SynchronizePreviewTransforms();
            if (HasAuthoringSourceChanged(Current, out var changeReason))
                throw new InvalidOperationException($"{RoomAuthoringErrorCodes.ApplySourceChanged}: {changeReason}");
            var report = PreviewValidationService.Validate(Current);
            if (report.HasErrors) throw new InvalidOperationException($"The preview has {report.ErrorCount} technical errors. Resolve them before Apply.");
            if (!RoomReviewWorkflow.HasCurrentHumanApproval(Current, report, out var approvalReason))
                throw new InvalidOperationException($"Human room approval is required before Apply. {approvalReason}");

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Concept Room Decoration");

            var title = Current.Plan.ConceptBrief != null ? Current.Plan.ConceptBrief.ConceptTitle : "Concept Room";
            var container = new GameObject($"Decor - {title}");
            Undo.RegisterCreatedObjectUndo(container, "Create decoration container");
            container.transform.SetParent(Current.Plan.Room.transform, true);

            foreach (var placement in Current.Placements)
            {
                var instance = PrefabUtility.InstantiatePrefab(placement.descriptor.Prefab, Current.Plan.Room.gameObject.scene) as GameObject;
                if (instance == null) continue;
                Undo.RegisterCreatedObjectUndo(instance, "Place room decoration");
                instance.transform.SetParent(container.transform, true);
                instance.transform.SetPositionAndRotation(placement.position, placement.rotation);
                instance.transform.localScale = placement.scale;
            }

            Undo.CollapseUndoOperations(undoGroup);
            DiscardPreview();
            Selection.activeGameObject = container;
            return container;
        }

        private static bool HasAuthoringSourceChanged(PreviewSession session, out string reason)
        {
            reason = string.Empty;
            var context = session?.AuthoringContext;
            if (context == null) return false;
            if (!string.Equals(session.AuthoringSourceHash ?? string.Empty, context.sourceHash ?? string.Empty, StringComparison.Ordinal))
            {
                reason = "The authoring context source hash changed after preview generation. Generate a new preview.";
                return true;
            }
            if (!context.IsSourceCurrent(out var currentHash))
            {
                reason = $"The source hash changed from '{session.AuthoringSourceHash}' to '{currentHash}'. Generate a new preview.";
                return true;
            }
            var currentObstacleHash = context.ComputeObstacleHash();
            if (!string.Equals(session.ObstacleGeometryHash ?? string.Empty, currentObstacleHash, StringComparison.Ordinal))
            {
                reason = "The room obstacle geometry changed after preview generation. Generate a new preview.";
                return true;
            }
            return false;
        }

        public static void DiscardPreview() => DiscardPreview(true);

        private static void DiscardPreview(bool notify, RoomCompositionPlan planToKeep = null)
        {
            var transientPlan = Current?.Plan != null && Current.Plan != planToKeep && !AssetDatabase.Contains(Current.Plan) ? Current.Plan : null;
            if (Current?.Root != null) UnityEngine.Object.DestroyImmediate(Current.Root);
            Current = null;
            if (transientPlan != null) UnityEngine.Object.DestroyImmediate(transientPlan);
            CleanupOrphanedPreviewRoots();
            SceneView.RepaintAll();
            if (notify) Changed?.Invoke();
        }

        private static void CleanupOrphanedPreviewRoots()
        {
            var roots = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(item => item != null && item.name == PreviewRootName && (Current == null || item != Current.Root))
                .ToArray();
            foreach (var root in roots) UnityEngine.Object.DestroyImmediate(root);
        }

        private static void SetPreviewFlags(GameObject root)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags |= HideFlags.DontSaveInEditor;
        }

        private static PlacedDecorItem CloneForRegeneration(PlacedDecorItem source) => new()
        {
            placementId = source.placementId,
            elementId = source.elementId,
            instanceIndex = source.instanceIndex,
            role = source.role,
            relation = source.relation,
            descriptor = source.descriptor,
            position = source.position,
            rotation = source.rotation,
            scale = source.scale,
            worldBounds = source.worldBounds,
            surfaceId = source.surfaceId,
            contactEvidence = source.contactEvidence,
            surfaceIds = source.surfaceIds != null ? new List<string>(source.surfaceIds) : new List<string>(),
            contactEvidenceSet = source.contactEvidenceSet != null ? new List<ContactEvidence>(source.contactEvidenceSet) : new List<ContactEvidence>(),
            arrangementId = source.arrangementId,
            affinityGroup = source.affinityGroup,
            supportPlacementId = source.supportPlacementId,
            supportContactFrameId = source.supportContactFrameId,
            stackLevel = source.stackLevel,
            locked = true
        };

        private static string ComputeManifestHash(ConceptRoom room)
        {
            var text = new StringBuilder(room.RoomId ?? string.Empty);
            foreach (var surface in room.Surfaces.Where(value => value != null).OrderBy(value => value.SurfaceId, StringComparer.Ordinal))
                text.Append('|').Append(surface.SurfaceId).Append(':').Append(surface.SurfaceType).Append(':').Append(Vector(surface.Origin)).Append(':').Append(Vector(surface.Normal)).Append(':').Append(surface.Reviewed ? '1' : '0');
            return Hash128.Compute(text.ToString()).ToString();
        }

        internal static string ComputeGeometryHash(RoomCompositionPlan plan, SupportContractCatalog supportContracts)
        {
            var text = new StringBuilder("arrangement-support-resolver@")
                .Append(ArrangementSupportSurfaceResolver.ResolverVersion)
                .Append(':').Append(ArrangementSupportSurfaceResolver.ResolverHash)
                .Append("|derived-contact-frame@")
                .Append(SpatialDerivedContactFrameResolver.ResolverVersion)
                .Append(':').Append(SpatialDerivedContactFrameResolver.ResolverHash).Append('|');
            foreach (var descriptor in plan.Catalog.Assets.Where(value => value?.Geometry != null).OrderBy(value => value.AssetId, StringComparer.Ordinal))
            {
                var geometry = descriptor.Geometry;
                text.Append("asset:").Append(descriptor.AssetId ?? string.Empty)
                    .Append("/version:").Append(geometry.version)
                    .Append("/source:").Append((int)geometry.source)
                    .Append("/dependency:").Append(geometry.dependencyHash ?? string.Empty)
                    .Append("/reviewed:").Append(geometry.reviewed ? '1' : '0')
                    .Append("/confidence:").Append(Number(geometry.inferenceConfidence))
                    .Append("/forward:").Append(Vector(geometry.forwardAxis))
                    .Append("/up:").Append(Vector(geometry.upAxis))
                    .Append("/pivot:").Append(Vector(geometry.pivotOffset));
                foreach (var proxy in (geometry.collisionProxies ?? new List<OrientedBoxProxy>())
                             .Where(value => value != null)
                             .OrderBy(ProxyCanonical, StringComparer.Ordinal))
                    text.Append("/obb:").Append(ProxyCanonical(proxy));
                AppendFrame(text, "bottom", geometry.bottomContact);
                AppendFrame(text, "back", geometry.backContact);
                AppendFrame(text, "top", geometry.topContact);
                foreach (var rules in geometry.EffectiveContacts.Where(value => value != null)
                             .OrderBy(ContactCanonical, StringComparer.Ordinal))
                    text.Append("/contact:").Append(ContactCanonical(rules));
                text.Append('|');
            }
            foreach (var arrangement in plan.SurfaceArrangements.Where(value => value != null)
                         .OrderBy(value => value.arrangement_id ?? string.Empty, StringComparer.Ordinal))
                text.Append("arrangement:").Append(SurfaceArrangementSpecUtility.ComputeSpecHash(arrangement)).Append('|');
            if (supportContracts != null) text.Append("support-contracts:").Append(supportContracts.ComputeHash()).Append('|');
            return Hash128.Compute(text.ToString()).ToString();
        }

        private static void AppendFrame(StringBuilder text, string slot, ContactFrame frame)
        {
            text.Append('/').Append(slot).Append(':');
            if (frame == null)
            {
                text.Append("null");
                return;
            }
            text.Append(frame.frameId ?? string.Empty).Append('@')
                .Append(Vector(frame.localPoint)).Append('@')
                .Append(Vector(frame.localNormal)).Append('@')
                .Append(Vector(frame.localTangent)).Append('@')
                .Append(Vector(frame.size));
        }

        private static string ProxyCanonical(OrientedBoxProxy proxy) =>
            (proxy.proxyId ?? string.Empty) + "@" + Vector(proxy.localCenter) + "@" +
            Vector(proxy.size) + "@" + QuaternionValue(proxy.localRotation);

        private static string ContactCanonical(ContactRules rules) =>
            (rules.ruleId ?? string.Empty) + "@" + (int)rules.requirement + "@" +
            (rules.frameId ?? string.Empty) + "@" + Number(rules.minimumGap) + "@" +
            Number(rules.maximumGap) + "@" + Number(rules.maximumPenetration) + "@" +
            Number(rules.minimumSupportCoverage);

        private static string Vector(Vector3 value) =>
            Number(value.x) + "," + Number(value.y) + "," + Number(value.z);

        private static string Vector(Vector2 value) => Number(value.x) + "," + Number(value.y);

        private static string QuaternionValue(Quaternion value)
        {
            if (value == default) value = Quaternion.identity;
            value.Normalize();
            if (value.w < 0f || value.w == 0f &&
                (value.z < 0f || value.z == 0f && (value.y < 0f || value.y == 0f && value.x < 0f)))
                value = new Quaternion(-value.x, -value.y, -value.z, -value.w);
            return Number(value.x) + "," + Number(value.y) + "," + Number(value.z) + "," + Number(value.w);
        }

        private static string Number(float value)
        {
            if (value == 0f) return "0";
            if (float.IsNaN(value)) return "NaN";
            if (float.IsPositiveInfinity(value)) return "+Infinity";
            if (float.IsNegativeInfinity(value)) return "-Infinity";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool Approximately(Vector3 left, Vector3 right) => (left - right).sqrMagnitude <= 0.00000001f;

        private static bool Approximately(Quaternion left, Quaternion right) => Mathf.Abs(Quaternion.Dot(left, right)) >= 0.999999f;
    }
}
