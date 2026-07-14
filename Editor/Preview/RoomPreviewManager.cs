using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

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
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var locked = preserveLocks && Current != null
                ? Current.Placements.Where(item => item.locked).Select(CloneForRegeneration).ToArray()
                : Array.Empty<PlacedDecorItem>();

            DiscardPreview(false, plan);
            var layout = DeterministicLayoutEngine.Generate(plan, locked);
            var root = new GameObject(PreviewRootName)
            {
                hideFlags = HideFlags.DontSaveInEditor
            };
            root.transform.SetParent(plan.Room.transform, true);

            var session = new PreviewSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Plan = plan,
                Root = root,
                AssetGaps = layout.AssetGaps,
                ManifestHash = ComputeManifestHash(plan.Room),
                GeometryProfileHash = ComputeGeometryHash(plan)
            };

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
            Selection.activeGameObject = root;
            SceneView.RepaintAll();
            Changed?.Invoke();
            return Current;
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
            Current.LastValidation = PreviewValidationService.Validate(Current);
            Changed?.Invoke();
            return Current.LastValidation;
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
            var report = PreviewValidationService.Validate(Current);
            if (report.HasErrors) throw new InvalidOperationException($"The preview has {report.ErrorCount} technical errors. Resolve them before Apply.");
            if (!report.MeetsVisualThresholds(Current.Plan.ConceptBrief))
                throw new InvalidOperationException("Mood, style, story, and composition must each pass the concept brief threshold before Apply.");

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
            locked = true
        };

        private static string ComputeManifestHash(ConceptRoom room)
        {
            var text = new StringBuilder(room.RoomId ?? string.Empty);
            foreach (var surface in room.Surfaces.Where(value => value != null).OrderBy(value => value.SurfaceId, StringComparer.Ordinal))
                text.Append('|').Append(surface.SurfaceId).Append(':').Append(surface.SurfaceType).Append(':').Append(Vector(surface.Origin)).Append(':').Append(Vector(surface.Normal)).Append(':').Append(surface.Reviewed ? '1' : '0');
            return Hash128.Compute(text.ToString()).ToString();
        }

        private static string ComputeGeometryHash(RoomCompositionPlan plan)
        {
            var text = new StringBuilder();
            foreach (var descriptor in plan.Catalog.Assets.Where(value => value?.Geometry != null).OrderBy(value => value.AssetId, StringComparer.Ordinal))
            {
                text.Append(descriptor.AssetId).Append(':').Append(descriptor.Geometry.dependencyHash).Append(':').Append(descriptor.Geometry.reviewed ? '1' : '0');
                foreach (var rules in descriptor.Geometry.EffectiveContacts)
                    text.Append(':').Append(rules.ruleId).Append('/').Append(rules.requirement).Append('/').Append(rules.frameId)
                        .Append('/').Append(rules.minimumGap.ToString("R", CultureInfo.InvariantCulture))
                        .Append('/').Append(rules.maximumGap.ToString("R", CultureInfo.InvariantCulture));
                text.Append('|');
            }
            return Hash128.Compute(text.ToString()).ToString();
        }

        private static string Vector(Vector3 value) => string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", value.x, value.y, value.z);
    }
}
