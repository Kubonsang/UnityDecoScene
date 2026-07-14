using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public sealed class SpatialCalibrationWorkflowWindow : EditorWindow
    {
        private Vector2 scroll;
        private SpatialCalibrationWorkflowState state;

        [MenuItem("Window/Concept Room Decorator/Fast Calibration")]
        public static void Open()
        {
            var window = GetWindow<SpatialCalibrationWorkflowWindow>("빠른 에셋 캘리브레이션");
            window.minSize = new Vector2(620f, 520f);
        }

        private void OnEnable()
        {
            SpatialCalibrationWorkflow.Changed += Reload;
            Reload();
        }

        private void OnDisable() => SpatialCalibrationWorkflow.Changed -= Reload;

        private void Reload()
        {
            state = SpatialCalibrationWorkflow.LoadState();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("빠른 공간 캘리브레이션", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Unity가 미검수 에셋을 찾고 기술 검사와 4방향 캡처를 만듭니다. " +
                "AI는 실패한 항목만 받으며, 최종 판정은 검수 페이지에서 사용자가 내립니다.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("1. 에셋 찾기", GUILayout.Height(32f))) state = SpatialCalibrationWorkflow.ScanOrResume();
                using (new EditorGUI.DisabledScope(state == null))
                {
                    if (GUILayout.Button("2. 다음 묶음 검사", GUILayout.Height(32f))) SpatialCalibrationWorkflow.RequestNextBatch();
                    if (GUILayout.Button("3. 사용자 검수 열기", GUILayout.Height(32f))) SpatialCalibrationWorkflow.OpenReview();
                }
            }

            if (state == null)
            {
                EditorGUILayout.HelpBox("‘에셋 찾기’를 누르면 프로젝트의 DecorAssetDescriptor를 안전하게 스캔합니다.", MessageType.None);
                return;
            }

            DrawProgress();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var item in state.items.OrderBy(StatusOrder).ThenBy(value => value.displayName, StringComparer.OrdinalIgnoreCase))
                DrawItem(item);
            EditorGUILayout.EndScrollView();
        }

        private void DrawProgress()
        {
            var total = Mathf.Max(1, state.items.Count);
            var approved = state.items.Count(item => item.status == SpatialCalibrationWorkflowStates.Approved);
            var awaiting = state.items.Count(item => item.status == SpatialCalibrationWorkflowStates.AwaitingHumanReview);
            var blocked = state.items.Count(item => item.status is SpatialCalibrationWorkflowStates.TechnicalFailed
                or SpatialCalibrationWorkflowStates.NeedsRelationReview);
            EditorGUILayout.Space(8f);
            var area = GUILayoutUtility.GetRect(10f, 20f, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(area, approved / (float)total, $"승인 {approved}/{state.items.Count} · 검수 대기 {awaiting} · 확인 필요 {blocked}");
            EditorGUILayout.Space(4f);
        }

        private void DrawItem(SpatialCalibrationWorkflowItem item)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(item.displayName, EditorStyles.boldLabel, GUILayout.MinWidth(190f));
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(StatusLabel(item.status), GUILayout.Width(105f));
                }
                EditorGUILayout.LabelField($"관계: {(string.IsNullOrWhiteSpace(item.template) ? "미확정" : item.template)} · 근거: {item.relationSource}", EditorStyles.miniLabel);

                if (item.status == SpatialCalibrationWorkflowStates.NeedsRelationReview)
                {
                    EditorGUILayout.HelpBox("관계를 추측하지 않았습니다. 실제 사용 방식을 선택하면 다음 묶음부터 자동 처리됩니다.", MessageType.Warning);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        foreach (var template in new[]
                                 {
                                     SpatialCalibrationTemplate.FloorSupported,
                                     SpatialCalibrationTemplate.WallBackedFloorSupported,
                                     SpatialCalibrationTemplate.WallMounted
                                 })
                        {
                            if (GUILayout.Button(RelationLabel(template)))
                            {
                                SpatialCalibrationWorkflow.SetRelation(item.id, template);
                                GUIUtility.ExitGUI();
                            }
                        }
                    }
                }
                else if (item.errors is { Length: > 0 })
                {
                    EditorGUILayout.LabelField(string.Join(" · ", item.errors.Select(ShortError)), EditorStyles.wordWrappedMiniLabel);
                }
            }
        }

        private static int StatusOrder(SpatialCalibrationWorkflowItem item) => item.status switch
        {
            SpatialCalibrationWorkflowStates.NeedsRelationReview => 0,
            SpatialCalibrationWorkflowStates.TechnicalFailed => 1,
            SpatialCalibrationWorkflowStates.AwaitingHumanReview => 2,
            SpatialCalibrationWorkflowStates.Pending => 3,
            SpatialCalibrationWorkflowStates.Approved => 5,
            _ => 4
        };

        private static string StatusLabel(string status) => status switch
        {
            SpatialCalibrationWorkflowStates.Approved => "✓ 승인",
            SpatialCalibrationWorkflowStates.AwaitingHumanReview => "사용자 검수 대기",
            SpatialCalibrationWorkflowStates.TechnicalFailed => "기술 검사 실패",
            SpatialCalibrationWorkflowStates.NeedsRelationReview => "관계 선택 필요",
            SpatialCalibrationWorkflowStates.Running => "검사 중",
            SpatialCalibrationWorkflowStates.Stale => "재검사 필요",
            _ => "대기"
        };

        private static string RelationLabel(SpatialCalibrationTemplate template) => template switch
        {
            SpatialCalibrationTemplate.FloorSupported => "바닥에 놓기",
            SpatialCalibrationTemplate.WallBackedFloorSupported => "바닥 + 벽에 기대기",
            SpatialCalibrationTemplate.WallMounted => "벽에 걸기",
            _ => template.ToString()
        };

        private static string ShortError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return "UNKNOWN";
            var newline = error.IndexOf('\n');
            return newline < 0 ? error : error.Substring(0, newline);
        }
    }
}
