using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public sealed class ConceptRoomDecoratorWindow : EditorWindow
    {
        [SerializeField] private RoomCompositionPlan plan;
        [SerializeField] private DecorCatalog catalog;
        [SerializeField] private DefaultAsset prefabFolder;
        [SerializeField] private string descriptorFolder = "Assets/ConceptRoomDecorator/Descriptors";
        private Vector2 scroll;
        private string status;
        private bool unityCtxAvailable;

        [MenuItem("Window/Concept Room Decorator")]
        public static void Open() => GetWindow<ConceptRoomDecoratorWindow>("Concept Room Decorator");

        private void OnEnable()
        {
            RoomPreviewManager.Changed += Repaint;
            unityCtxAvailable = DetectUnityCtx();
        }
        private void OnDisable() => RoomPreviewManager.Changed -= Repaint;

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawHeader();
            DrawPlanSection();
            EditorGUILayout.Space(8f);
            DrawRoomSetupSection();
            EditorGUILayout.Space(8f);
            DrawCatalogSection();
            EditorGUILayout.Space(8f);
            DrawCompositionSection();
            EditorGUILayout.Space(8f);
            DrawPreviewSection();
            EditorGUILayout.Space(8f);
            DrawValidationSection();
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Concept Room Decorator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Dress one existing room from a reviewed concept and curated assets. Preview generation never applies scene changes; Apply is always a human action.", MessageType.Info);
            EditorGUILayout.HelpBox(unityCtxAvailable ? "Optional unity-ctx integration connected." : "Optional unity-ctx integration disconnected. Room tools continue to work independently.", unityCtxAvailable ? MessageType.Info : MessageType.Warning);
            if (!string.IsNullOrWhiteSpace(status)) EditorGUILayout.HelpBox(status, MessageType.None);
        }

        private void DrawPlanSection()
        {
            EditorGUILayout.LabelField("Project Context", EditorStyles.boldLabel);
            plan = (RoomCompositionPlan)EditorGUILayout.ObjectField("Composition Plan", plan, typeof(RoomCompositionPlan), false);
            if (plan == null) return;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Room", plan.Room, typeof(ConceptRoom), true);
                EditorGUILayout.ObjectField("Concept Brief", plan.ConceptBrief, typeof(RoomConceptBrief), false);
                EditorGUILayout.ObjectField("Catalog", plan.Catalog, typeof(DecorCatalog), false);
                EditorGUILayout.IntField("Seed", plan.Seed);
                EditorGUILayout.Slider("Density", plan.Density, 0f, 1f);
                EditorGUILayout.IntField("Elements", plan.Elements.Count);
            }

            if (plan.Room == null || plan.Room.AuthoringBounds == null)
                EditorGUILayout.HelpBox("Assign a ConceptRoom with authoring bounds in the plan.", MessageType.Error);
            if (plan.Catalog == null)
                EditorGUILayout.HelpBox("Assign a DecorCatalog in the plan.", MessageType.Error);
        }

        private void DrawRoomSetupSection()
        {
            EditorGUILayout.LabelField("1. Room Setup", EditorStyles.boldLabel);
            var room = plan != null ? plan.Room : null;
            if (room == null)
            {
                EditorGUILayout.HelpBox("Choose a plan with a ConceptRoom before scanning room surfaces.", MessageType.Warning);
                return;
            }
            var reviewed = room.Surfaces.Count(item => item != null && item.Reviewed && item.Supported);
            var unsupported = room.Surfaces.Count(item => item != null && !item.Supported);
            EditorGUILayout.LabelField($"Surfaces: {room.Surfaces.Count}    Reviewed: {reviewed}    Unsupported: {unsupported}");
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan Floor / Walls")) RunSafe(() =>
                {
                    var surfaces = RoomSurfaceScanner.Scan(room);
                    status = $"Generated {surfaces.Count} surface candidates. Review their inward normals before approval.";
                });
                using (new EditorGUI.DisabledScope(room.Surfaces.Count == 0))
                {
                    if (GUILayout.Button("Approve Supported")) RunSafe(() => RoomSurfaceScanner.SetReviewed(room, true));
                    if (GUILayout.Button("Unapprove All")) RunSafe(() => RoomSurfaceScanner.SetReviewed(room, false));
                }
            }
            if (room.Surfaces.Count == 0) EditorGUILayout.HelpBox("No room surfaces are registered. Contact-aware placement is blocked.", MessageType.Error);
            else if (room.Surfaces.Any(item => item != null && item.Supported && !item.Reviewed)) EditorGUILayout.HelpBox("Review surface normals in Scene View, then approve supported surfaces.", MessageType.Warning);
        }

        private void DrawCatalogSection()
        {
            EditorGUILayout.LabelField("2. Asset Catalog", EditorStyles.boldLabel);
            catalog = (DecorCatalog)EditorGUILayout.ObjectField("Catalog", catalog != null ? catalog : plan != null ? plan.Catalog : null, typeof(DecorCatalog), false);
            prefabFolder = (DefaultAsset)EditorGUILayout.ObjectField("Prefab Folder", prefabFolder, typeof(DefaultAsset), false);
            descriptorFolder = EditorGUILayout.TextField("Descriptor Folder", descriptorFolder);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(catalog == null || prefabFolder == null))
                {
                    if (GUILayout.Button("Scan Prefabs")) RunSafe(ScanPrefabs);
                }

                using (new EditorGUI.DisabledScope(catalog == null))
                {
                    if (GUILayout.Button("Render Catalog Sheet")) RunSafe(() =>
                    {
                        var path = CatalogSheetRenderer.Render(catalog);
                        status = $"Catalog sheet written to {path}";
                        EditorUtility.RevealInFinder(path);
                    });
                }
            }
            if (catalog != null)
            {
                var geometryReady = catalog.Assets.Count(item => item?.Geometry != null && item.Geometry.IsUsable);
                var blockers = catalog.Assets.Count - geometryReady;
                EditorGUILayout.LabelField($"Geometry ready: {geometryReady}    Review blockers: {blockers}");
                if (blockers > 0) EditorGUILayout.HelpBox("Unreviewed geometry is never placed. Select descriptor assets to confirm forward/up axes and contact faces.", MessageType.Warning);
            }
        }

        private void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("4. Preview", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!CanGenerate()))
                {
                    if (GUILayout.Button("Generate / Reroll")) RunSafe(() =>
                    {
                        var session = RoomPreviewManager.GeneratePreview(plan, true);
                        status = $"Preview {session.SessionId}: {session.Placements.Count} placements, {session.AssetGaps.gaps.Count} asset gaps.";
                    });
                }

                using (new EditorGUI.DisabledScope(RoomPreviewManager.Current == null))
                {
                    if (GUILayout.Button("Discard")) RoomPreviewManager.DiscardPreview();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(RoomPreviewManager.Current == null))
                {
                    if (GUILayout.Button("Lock Selected")) status = RoomPreviewManager.LockSelection(true) ? "Selected preview item locked." : "Select a preview item first.";
                    if (GUILayout.Button("Unlock Selected")) status = RoomPreviewManager.LockSelection(false) ? "Selected preview item unlocked." : "Select a preview item first.";
                    if (GUILayout.Button("Capture Views")) RunSafe(() =>
                    {
                        var paths = RoomCaptureService.CaptureAll(RoomPreviewManager.Current);
                        status = $"Captured {paths.Count} views to {Path.GetDirectoryName(paths[0])}.";
                        if (paths.Count > 0) EditorUtility.RevealInFinder(paths[0]);
                    });
                }
            }

            var current = RoomPreviewManager.Current;
            if (current == null) return;
            EditorGUILayout.LabelField($"Session: {current.SessionId}");
            foreach (var placement in current.Placements)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(placement.role.ToString(), GUILayout.Width(95f));
                    EditorGUILayout.LabelField(placement.descriptor != null ? placement.descriptor.name : "Missing");
                    EditorGUILayout.LabelField(placement.locked ? "Locked" : string.Empty, GUILayout.Width(50f));
                    if (GUILayout.Button("Select", GUILayout.Width(55f)) && placement.previewObject != null) Selection.activeGameObject = placement.previewObject;
                }
            }
        }

        private void DrawCompositionSection()
        {
            EditorGUILayout.LabelField("3. Composition", EditorStyles.boldLabel);
            if (plan == null)
            {
                EditorGUILayout.HelpBox("Choose a RoomCompositionPlan.", MessageType.Warning);
                return;
            }
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Concept Brief", plan.ConceptBrief, typeof(RoomConceptBrief), false);
                EditorGUILayout.IntField("Seed", plan.Seed);
                EditorGUILayout.Slider("Density", plan.Density, 0f, 1f);
                EditorGUILayout.IntField("Semantic Elements", plan.Elements.Count);
            }
            if (RoomPreviewManager.Current?.AssetGaps?.gaps.Count > 0)
                EditorGUILayout.HelpBox($"Active preview has {RoomPreviewManager.Current.AssetGaps.gaps.Count} asset gaps. Missing or unreviewed geometry is not substituted.", MessageType.Warning);
        }

        private void DrawValidationSection()
        {
            EditorGUILayout.LabelField("5. Validate and Apply", EditorStyles.boldLabel);
            var current = RoomPreviewManager.Current;
            using (new EditorGUI.DisabledScope(current == null))
            {
                if (GUILayout.Button("Run Technical Validation")) RunSafe(() => RoomPreviewManager.ValidateCurrent());
            }

            var report = current?.LastValidation;
            if (report != null)
            {
                EditorGUILayout.LabelField($"Errors: {report.ErrorCount}    Warnings: {report.WarningCount}");
                foreach (var issue in report.issues)
                {
                    var type = issue.severity switch
                    {
                        ValidationSeverity.Error => MessageType.Error,
                        ValidationSeverity.Warning => MessageType.Warning,
                        _ => MessageType.Info
                    };
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.HelpBox($"[{issue.code}] {issue.message}", type);
                        using (new EditorGUI.DisabledScope(issue.elementIds == null || issue.elementIds.Count == 0))
                        {
                            if (GUILayout.Button("Focus", GUILayout.Width(55f), GUILayout.Height(38f))) FocusIssue(issue);
                        }
                    }
                }

                EditorGUILayout.Space(6f);
                EditorGUILayout.LabelField("Visual Review", EditorStyles.boldLabel);
                var scores = report.visualScores;
                scores.mood = EditorGUILayout.IntSlider("Mood", scores.mood, 0, 100);
                scores.style = EditorGUILayout.IntSlider("Style", scores.style, 0, 100);
                scores.story = EditorGUILayout.IntSlider("Story", scores.story, 0, 100);
                scores.composition = EditorGUILayout.IntSlider("Composition", scores.composition, 0, 100);
                EditorGUILayout.LabelField("Feedback");
                scores.feedback = EditorGUILayout.TextArea(scores.feedback ?? string.Empty, GUILayout.MinHeight(45f));
                if (GUILayout.Button(scores.reviewed ? "Update Visual Review" : "Submit Visual Review"))
                {
                    RoomPreviewManager.SetVisualReview(scores);
                    status = report.MeetsVisualThresholds(current.Plan.ConceptBrief)
                        ? "All visual quality thresholds passed."
                        : "Visual review saved; one or more quality thresholds are still below the brief minimum.";
                }
            }

            using (new EditorGUI.DisabledScope(current == null || report == null || report.HasErrors || !report.MeetsVisualThresholds(current.Plan.ConceptBrief)))
            {
                if (GUILayout.Button("Apply Reviewed Preview", GUILayout.Height(32f))) RunSafe(() =>
                {
                    var container = RoomPreviewManager.ApplyCurrent();
                    status = $"Applied decoration to {container.name}. Use Undo to revert the entire operation.";
                });
            }
        }

        private bool CanGenerate() => plan != null && plan.Room != null && plan.Room.AuthoringBounds != null && plan.Catalog != null;

        private static void FocusIssue(ValidationIssue issue)
        {
            var current = RoomPreviewManager.Current;
            if (current == null || issue?.elementIds == null) return;
            var placement = current.Placements.FirstOrDefault(item => item != null && issue.elementIds.Contains(item.placementId));
            if (placement?.previewObject == null) return;
            Selection.activeGameObject = placement.previewObject;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        private void ScanPrefabs()
        {
            var path = AssetDatabase.GetAssetPath(prefabFolder);
            var assets = DecorAssetScanner.ScanFolder(path, descriptorFolder, catalog);
            status = $"Scanned {assets.Count} prefabs. Review descriptor tags and roles before generation.";
        }

        private void RunSafe(Action action)
        {
            try
            {
                action();
                Repaint();
            }
            catch (Exception exception)
            {
                status = exception.Message;
                Debug.LogException(exception);
            }
        }

        private static bool DetectUnityCtx()
        {
            try
            {
                var configured = Environment.GetEnvironmentVariable("UNITY_CTX_BIN");
                if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return true;
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "unity-ctx",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                return process != null && process.WaitForExit(1500) && process.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
