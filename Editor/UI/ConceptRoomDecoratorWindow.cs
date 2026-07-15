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
        [SerializeField] private string approvedContractRoot = "Assets/SpatialContracts/Assets";
        private Vector2 scroll;
        private string status;
        private bool unityCtxAvailable;
        private ApprovedContractSyncPlan contractSyncPlan;

        [MenuItem("Window/Concept Room Decorator")]
        public static void Open() => GetWindow<ConceptRoomDecoratorWindow>("Concept Room Decorator");

        private void OnEnable()
        {
            RoomPreviewManager.Changed += Repaint;
            RoomReviewWorkflow.Changed += Repaint;
            unityCtxAvailable = DetectUnityCtx();
        }
        private void OnDisable()
        {
            RoomPreviewManager.Changed -= Repaint;
            RoomReviewWorkflow.Changed -= Repaint;
        }

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
            approvedContractRoot = EditorGUILayout.TextField("Approved Contracts", approvedContractRoot);

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
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(catalog == null))
                {
                    if (GUILayout.Button("Preview Contract Sync")) RunSafe(() =>
                    {
                        contractSyncPlan = ApprovedContractSyncService.Preview(catalog, approvedContractRoot);
                        status = $"Contract sync preview: {contractSyncPlan.ReadyCount} ready, {contractSyncPlan.BlockerCount} blocked.";
                    });
                }
                using (new EditorGUI.DisabledScope(contractSyncPlan == null || contractSyncPlan.ReadyCount == 0))
                {
                    if (GUILayout.Button("Sync Approved Geometry")) RunSafe(() =>
                    {
                        var applied = ApprovedContractSyncService.Apply(contractSyncPlan);
                        status = $"Imported {applied} approved geometry profiles in one Undo operation.";
                    });
                }
            }
            if (contractSyncPlan != null)
            {
                foreach (var item in contractSyncPlan.items.Where(value => value.status != ApprovedContractSyncStatus.Unchanged))
                {
                    var messageType = item.status is ApprovedContractSyncStatus.Ready or ApprovedContractSyncStatus.Applied
                        ? MessageType.Info
                        : item.status == ApprovedContractSyncStatus.Missing ? MessageType.Warning : MessageType.Error;
                    EditorGUILayout.HelpBox($"{item.descriptor?.name ?? item.assetGuid}: {item.status} — {item.message}", messageType);
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

            EditorGUILayout.Space(5f);
            SurfaceArrangementCompositionEditor.Draw(plan, value => status = value);
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

            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("사용자 룸 검수", EditorStyles.boldLabel);
            var review = current != null ? RoomReviewWorkflow.GetDisplayState(current) : null;
            DrawReviewProgress(review, report);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(current == null))
                {
                    if (GUILayout.Button(review == null ? "현재 방 검사 시작" : "현재 방 검사 / 계속", GUILayout.Height(30f))) RunSafe(() =>
                    {
                        var prepared = RoomReviewWorkflow.PrepareCurrent(true);
                        status = prepared.status == RoomReviewStates.TechnicalFailed
                            ? $"기술 오류 {prepared.technicalErrorCount}개가 있어 캡처와 사용자 검수를 중단했습니다."
                            : prepared.status == RoomReviewStates.Approved
                                ? "변경 사항이 없어 기존 캡처와 사용자 승인을 그대로 재사용했습니다."
                                : "기술 검사를 통과했습니다. 브라우저에서 네 시점을 확인하고 판정해 주세요.";
                    });
                }
                using (new EditorGUI.DisabledScope(review == null || review.technicalErrorCount > 0 || review.capture?.views == null || review.capture.views.Count == 0))
                {
                    if (GUILayout.Button("검수 화면 열기", GUILayout.Height(30f))) RunSafe(RoomReviewWorkflow.OpenReview);
                }
            }

            var approved = current != null && report != null && !report.HasErrors && review != null && RoomReviewHashUtility.IsCurrentApproval(review);
            using (new EditorGUI.DisabledScope(!approved))
            {
                if (GUILayout.Button("승인된 배치 적용", GUILayout.Height(34f))) RunSafe(() =>
                {
                    var container = RoomPreviewManager.ApplyCurrent();
                    status = $"{container.name} 배치를 적용했습니다. 전체 작업은 Undo 한 번으로 되돌릴 수 있습니다.";
                });
            }
        }

        private static void DrawReviewProgress(RoomReviewRun review, ValidationReport report)
        {
            var technicalPassed = report != null && !report.HasErrors;
            var humanApproved = review != null && RoomReviewHashUtility.IsCurrentApproval(review);
            var waiting = review != null && review.status is RoomReviewStates.AwaitingHumanReview or RoomReviewStates.Stale or RoomReviewStates.RevisionRequested or RoomReviewStates.UnableToJudge;
            EditorGUILayout.LabelField($"준비 {(review != null ? "✓" : "○")}   →   기술 {(technicalPassed ? "✓" : report == null ? "○" : "✕")}   →   사용자 {(humanApproved ? "✓" : waiting ? "대기" : "○")}   →   적용 {(humanApproved ? "가능" : "잠김")}", EditorStyles.wordWrappedLabel);
            if (review == null)
            {
                EditorGUILayout.HelpBox("검사를 시작하면 충돌을 먼저 확인하고, 통과한 경우에만 사람용 4뷰를 준비합니다.", MessageType.Info);
                return;
            }
            if (review.status == RoomReviewStates.TechnicalFailed)
            {
                EditorGUILayout.HelpBox("기술 오류가 있어 사용자 승인을 요청하지 않았습니다. 위 오류를 고친 뒤 다시 검사하세요.", MessageType.Error);
                return;
            }
            if (review.status == RoomReviewStates.Stale)
                EditorGUILayout.HelpBox("승인 이후 방 또는 배치가 변경되어 이전 승인이 만료되었습니다. 새 캡처를 검수해야 합니다.", MessageType.Warning);
            else if (review.status == RoomReviewStates.RevisionRequested)
                EditorGUILayout.HelpBox("사용자가 수정을 요청했습니다. 배치를 고친 뒤 다시 검사하세요.", MessageType.Warning);
            else if (review.status == RoomReviewStates.UnableToJudge)
                EditorGUILayout.HelpBox("현재 캡처만으로 판단하기 어렵습니다. 카메라나 장면을 조정한 뒤 다시 검사하세요.", MessageType.Warning);
            else if (humanApproved)
                EditorGUILayout.HelpBox("현재 입력·기술 보고서·캡처 해시에 대한 사용자 승인이 저장되었습니다.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("브라우저에서 상단·입구·코너 2개 시점을 확인한 뒤 승인 여부를 정하세요.", MessageType.Info);

            if (review.changes?.HasChanges == true)
            {
                var scopes = Enum.GetValues(typeof(RoomReviewChangeScope)).Cast<RoomReviewChangeScope>()
                    .Where(value => value != RoomReviewChangeScope.None && value != RoomReviewChangeScope.All && (review.changes.scope & value) != 0)
                    .Select(value => value.ToString());
                EditorGUILayout.LabelField($"변경 범위: {string.Join(", ", scopes)}", EditorStyles.wordWrappedLabel);
                if ((review.changes.affectedIds?.Length ?? 0) > 0)
                    EditorGUILayout.LabelField($"영향 대상: {string.Join(", ", review.changes.affectedIds.Take(8))}", EditorStyles.wordWrappedMiniLabel);
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
