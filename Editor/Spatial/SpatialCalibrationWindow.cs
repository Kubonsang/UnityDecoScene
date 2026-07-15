using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public sealed class SpatialCalibrationWindow : EditorWindow
    {
        private const string LayoutPath = "Packages/com.unitydecoscene.dungeon-decorator/Editor/Spatial/SpatialCalibrationWindow.uxml";
        private const string StylePath = "Packages/com.unitydecoscene.dungeon-decorator/Editor/Spatial/SpatialCalibrationWindow.uss";

        private static readonly SpatialCalibrationTemplate[] RelationshipValues =
        {
            SpatialCalibrationTemplate.WallMounted,
            SpatialCalibrationTemplate.WallBackedFloorSupported,
            SpatialCalibrationTemplate.FloorSupported,
            SpatialCalibrationTemplate.SupportedBy
        };

        private static readonly List<string> RelationshipLabels = new()
        {
            "벽에 걸기 · 배너 / 벽 장식",
            "바닥에 놓고 벽에 기대기 · 책장",
            "바닥에 놓기 · 가구 / 항아리",
            "다른 오브젝트 위에 놓기 · 테이블 소품"
        };

        private static readonly SpatialWallNormalAxis[] AxisValues =
        {
            SpatialWallNormalAxis.LocalForward,
            SpatialWallNormalAxis.LocalRight,
            SpatialWallNormalAxis.LocalUp
        };

        private static readonly List<string> AxisLabels = new()
        {
            "앞/뒤 면 · Local Forward",
            "좌/우 면 · Local Right",
            "위/아래 면 · Local Up"
        };

        private static readonly string[] SupportedBySubjectFrames = { "bottom", "back", "top" };
        private static readonly List<string> SupportedBySubjectFrameLabels = new()
        {
            "바닥면 · bottom (기본)",
            "뒷면 · back (책을 눕힐 때)",
            "윗면 · top (뒤집어 놓을 때)"
        };

        [SerializeField] private DecorAssetDescriptor descriptor;
        [SerializeField] private DecorAssetDescriptor targetDescriptor;
        [SerializeField] private GameObject wallSource;
        [SerializeField] private SpatialWallNormalAxis wallNormalAxis = SpatialWallNormalAxis.LocalForward;
        [SerializeField] private bool flipWallNormal;
        [SerializeField] private SpatialCalibrationTemplate template = SpatialCalibrationTemplate.WallMounted;
        [SerializeField] private string supportedBySubjectFrameId = "bottom";
        [SerializeField] private string selectedContactFrameId = "top";

        private readonly BoxBoundsHandle boxHandle = new();
        private int selectedProxy;
        private string statusTitle = "준비 중";
        private string statusMessage = "검수할 오브젝트와 관계를 선택하세요.";
        private StatusTone statusTone = StatusTone.Neutral;

        private VisualElement setupFields;
        private VisualElement wallFields;
        private VisualElement targetFields;
        private VisualElement relationshipHelp;
        private VisualElement sessionContent;
        private VisualElement sessionSummary;
        private VisualElement stageActions;
        private VisualElement geometryControls;
        private VisualElement rulesControls;
        private VisualElement validationSummary;
        private VisualElement validationResults;
        private VisualElement captureResults;
        private VisualElement statusBanner;
        private Label statusIcon;
        private Label statusTitleLabel;
        private Label statusMessageLabel;
        private ObjectField descriptorField;
        private ObjectField wallField;
        private ObjectField targetField;
        private DropdownField supportedBySubjectFrameField;
        private DropdownField relationshipField;
        private DropdownField axisField;
        private Toggle flipField;
        private Button useSelectedWallButton;
        private Button startButton;
        private Button discardButton;
        private Button addProxyButton;
        private Button deleteProxyButton;
        private Button validateButton;
        private Button captureButton;

        [MenuItem("Window/Concept Room Decorator/Spatial Calibration")]
        public static void Open()
        {
            var window = GetWindow<SpatialCalibrationWindow>("공간 관계 설정");
            window.minSize = new Vector2(460f, 620f);
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += DuringSceneGUI;
            SpatialCalibrationSession.Changed += OnSessionChanged;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DuringSceneGUI;
            SpatialCalibrationSession.Changed -= OnSessionChanged;
            SpatialCalibrationSession.Current?.Dispose();
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            if (layout == null)
            {
                rootVisualElement.Add(new HelpBox(
                    $"UI 레이아웃을 찾을 수 없습니다: {LayoutPath}", HelpBoxMessageType.Error));
                return;
            }

            layout.CloneTree(rootVisualElement);
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(StylePath);
            if (style != null) rootVisualElement.styleSheets.Add(style);
            CacheElements();
            BuildSetupFields();
            BindButtons();
            RefreshAll();
        }

        private void CacheElements()
        {
            setupFields = rootVisualElement.Q<VisualElement>("setup-fields");
            wallFields = rootVisualElement.Q<VisualElement>("setup-wall-fields");
            targetFields = rootVisualElement.Q<VisualElement>("setup-target-fields");
            relationshipHelp = rootVisualElement.Q<VisualElement>("relationship-help");
            sessionContent = rootVisualElement.Q<VisualElement>("session-content");
            sessionSummary = rootVisualElement.Q<VisualElement>("session-summary");
            stageActions = rootVisualElement.Q<VisualElement>("stage-actions");
            geometryControls = rootVisualElement.Q<VisualElement>("geometry-controls");
            rulesControls = rootVisualElement.Q<VisualElement>("rules-controls");
            validationSummary = rootVisualElement.Q<VisualElement>("validation-summary");
            validationResults = rootVisualElement.Q<VisualElement>("validation-results");
            captureResults = rootVisualElement.Q<VisualElement>("capture-results");
            statusBanner = rootVisualElement.Q<VisualElement>("status-banner");
            statusIcon = rootVisualElement.Q<Label>("status-icon");
            statusTitleLabel = rootVisualElement.Q<Label>("status-title");
            statusMessageLabel = rootVisualElement.Q<Label>("status-message");
            startButton = rootVisualElement.Q<Button>("start-button");
            discardButton = rootVisualElement.Q<Button>("discard-button");
            addProxyButton = rootVisualElement.Q<Button>("add-proxy-button");
            deleteProxyButton = rootVisualElement.Q<Button>("delete-proxy-button");
            validateButton = rootVisualElement.Q<Button>("validate-button");
            captureButton = rootVisualElement.Q<Button>("capture-button");
        }

        private void BuildSetupFields()
        {
            descriptorField = new ObjectField("검수할 오브젝트")
            {
                name = "subject-descriptor-field",
                objectType = typeof(DecorAssetDescriptor),
                allowSceneObjects = false,
                tooltip = "프리팹 스캔으로 만든 DecorAssetDescriptor를 선택합니다."
            };
            descriptorField.SetValueWithoutNotify(descriptor);
            descriptorField.RegisterValueChangedCallback(evt =>
            {
                descriptor = evt.newValue as DecorAssetDescriptor;
                RefreshSetupState();
            });
            setupFields.Add(descriptorField);

            var relationshipIndex = Mathf.Max(0, Array.IndexOf(RelationshipValues, template));
            relationshipField = new DropdownField("어떻게 놓을까요?", RelationshipLabels, relationshipIndex)
            {
                name = "relationship-field",
                tooltip = "오브젝트가 어떤 표면과 어떤 관계를 맺는지 선택합니다."
            };
            relationshipField.RegisterValueChangedCallback(evt =>
            {
                var index = RelationshipLabels.IndexOf(evt.newValue);
                if (index >= 0) template = RelationshipValues[index];
                RefreshSetupState();
            });
            setupFields.Add(relationshipField);

            wallField = new ObjectField("기준 벽")
            {
                name = "wall-object-field",
                objectType = typeof(GameObject),
                allowSceneObjects = true,
                tooltip = "Hierarchy에서 오브젝트가 실제로 맞닿을 직선 벽 하나를 선택합니다."
            };
            wallField.SetValueWithoutNotify(wallSource);
            wallField.RegisterValueChangedCallback(evt => SetWallSource(evt.newValue as GameObject));
            wallFields.Add(wallField);

            useSelectedWallButton = new Button(UseSelectedWall)
            {
                text = "Hierarchy에서 선택한 오브젝트를 벽으로 사용",
                tooltip = "방 전체가 아니라 실제 접촉할 벽 세그먼트 하나를 먼저 선택하세요."
            };
            useSelectedWallButton.AddToClassList("secondary-button");
            wallFields.Add(useSelectedWallButton);

            var axisIndex = Mathf.Max(0, Array.IndexOf(AxisValues, wallNormalAxis));
            axisField = new DropdownField("사용할 벽면", AxisLabels, axisIndex)
            {
                name = "wall-axis-field",
                tooltip = "보라색 사각형이 벽의 넓은 면을 감싸는 축을 선택합니다."
            };
            axisField.RegisterValueChangedCallback(evt =>
            {
                var index = AxisLabels.IndexOf(evt.newValue);
                if (index >= 0) wallNormalAxis = AxisValues[index];
            });
            wallFields.Add(axisField);

            flipField = new Toggle("방 안쪽 방향 뒤집기")
            {
                name = "flip-wall-normal-field",
                value = flipWallNormal,
                tooltip = "보라색 사각형은 맞지만 화살표가 방 밖을 향할 때 켭니다."
            };
            flipField.RegisterValueChangedCallback(evt => flipWallNormal = evt.newValue);
            wallFields.Add(flipField);

            targetField = new ObjectField("검수된 받침 Descriptor")
            {
                name = "target-descriptor-field",
                objectType = typeof(DecorAssetDescriptor),
                allowSceneObjects = false,
                tooltip = "테이블 위 소품처럼 다른 에셋 위에 놓는 경우, 승인된 top 배치 영역이 들어 있는 Descriptor를 선택합니다."
            };
            targetField.SetValueWithoutNotify(targetDescriptor);
            targetField.RegisterValueChangedCallback(evt =>
            {
                targetDescriptor = evt.newValue as DecorAssetDescriptor;
                RefreshSetupState();
            });
            targetFields.Add(targetField);

            targetFields.Add(new HelpBox(
                "받침의 top 배치 가능 영역은 여기서 임시 수정하지 않습니다. 영역을 바꾸려면 받침 Descriptor를 '검수할 오브젝트'로 열어 top 프레임을 수정하고 사용자 승인을 다시 받은 뒤 선택하세요.",
                HelpBoxMessageType.Info));

            var subjectFrameIndex = Mathf.Max(0, Array.IndexOf(SupportedBySubjectFrames, supportedBySubjectFrameId));
            supportedBySubjectFrameField = new DropdownField("오브젝트의 접촉면", SupportedBySubjectFrameLabels, subjectFrameIndex)
            {
                name = "supported-by-subject-frame-field",
                tooltip = "받침 상단에 닿을 오브젝트의 면입니다. 책을 평평하게 눕히려면 뒷면(back)을 선택하세요."
            };
            supportedBySubjectFrameField.RegisterValueChangedCallback(evt =>
            {
                var index = SupportedBySubjectFrameLabels.IndexOf(evt.newValue);
                if (index >= 0) supportedBySubjectFrameId = SupportedBySubjectFrames[index];
            });
            targetFields.Add(supportedBySubjectFrameField);

            var targetFrame = new TextField("받침의 접촉면")
            {
                value = "top",
                isReadOnly = true,
                tooltip = "Surface Arrangement 0.1에서는 평평한 받침의 상단만 지원합니다."
            };
            targetFields.Add(targetFrame);
        }

        private void BindButtons()
        {
            startButton.clicked += StartCalibration;
            discardButton.clicked += DiscardCalibration;
            addProxyButton.clicked += AddProxy;
            deleteProxyButton.clicked += DeleteProxy;
            validateButton.clicked += Validate;
            captureButton.clicked += Capture;
        }

        private void StartCalibration() => RunSafe(() =>
        {
            if (!CanStart(out var reason)) throw new InvalidOperationException(reason);
            var session = template == SpatialCalibrationTemplate.SupportedBy
                ? SpatialCalibrationSession.Begin(
                    descriptor, targetDescriptor, template, supportedBySubjectFrameId, "top")
                : SpatialCalibrationSession.Begin(
                    descriptor, null, template, wallSource, wallNormalAxis, flipWallNormal);
            selectedProxy = 0;
            SetStatus("정답 자세를 만들어 주세요", "Scene View에서 오브젝트를 이동·회전한 뒤 기술 검사를 실행하세요.", StatusTone.Neutral);
        });

        private void DiscardCalibration()
        {
            SpatialCalibrationSession.Current?.Dispose();
            SetStatus("작업을 안전하게 폐기했습니다", "원본 씬과 프리팹에는 변경이 없습니다.", StatusTone.Neutral);
            RefreshAll();
        }

        private void UseSelectedWall()
        {
            var selected = Selection.activeGameObject;
            if (selected == null || EditorUtility.IsPersistent(selected))
            {
                SetStatus("씬의 벽을 먼저 선택하세요", "Hierarchy에서 오브젝트가 맞닿을 직선 벽 세그먼트 하나를 선택해 주세요.", StatusTone.Warning);
                return;
            }
            SetWallSource(selected);
            SetStatus("기준 벽을 선택했습니다", $"{selected.name} · 스테이지에서 보라색 사각형과 방 안쪽 화살표를 확인하세요.", StatusTone.Success);
        }

        private void SetWallSource(GameObject value)
        {
            if (value != null && EditorUtility.IsPersistent(value))
            {
                wallSource = null;
                wallField?.SetValueWithoutNotify(null);
                SetStatus("프리팹 에셋이 아니라 씬의 벽이 필요합니다", "Hierarchy에서 실제 방에 배치된 벽을 선택해 주세요.", StatusTone.Warning);
            }
            else
            {
                wallSource = value;
                wallField?.SetValueWithoutNotify(value);
            }
            RefreshSetupState();
        }

        private void AddProxy()
        {
            var session = SpatialCalibrationSession.Current;
            if (session == null) return;
            var bounds = new Bounds(session.Descriptor.LocalBoundsCenter, session.Descriptor.LocalBoundsSize);
            session.Geometry.collisionProxies.Add(new OrientedBoxProxy(
                $"manual-{session.Geometry.collisionProxies.Count}", bounds.center, bounds.size, Quaternion.identity));
            selectedProxy = session.Geometry.collisionProxies.Count - 1;
            session.NotifyChanged();
        }

        private void DeleteProxy()
        {
            var session = SpatialCalibrationSession.Current;
            if (session == null || session.Geometry.collisionProxies.Count <= 1) return;
            session.Geometry.collisionProxies.RemoveAt(selectedProxy);
            selectedProxy = Mathf.Max(0, selectedProxy - 1);
            session.NotifyChanged();
        }

        private void Validate() => RunSafe(() =>
        {
            var report = SpatialCalibrationValidator.Validate(SpatialCalibrationSession.Current);
            SetStatus(
                report.Passed ? "기술 검사를 통과했습니다" : "수정이 필요한 항목이 있습니다",
                report.Passed ? "오류 0개 · 이제 4뷰 캡처를 만들 수 있습니다." : $"오류 {report.error_count}개 · 아래 항목을 수정한 뒤 다시 검사하세요.",
                report.Passed ? StatusTone.Success : StatusTone.Error);
            RefreshAll();
        });

        private void Capture() => RunSafe(() =>
        {
            var session = SpatialCalibrationSession.Current;
            var report = SpatialCalibrationValidator.Validate(session);
            if (!report.Passed) throw new InvalidOperationException("기술 오류를 모두 수정한 뒤 캡처할 수 있습니다.");
            var captures = SpatialCalibrationCaptureService.Capture(session, report);
            var paths = SpatialContractIO.WriteDrafts(session, report, captures);
            SetStatus("사용자 검수 자료를 만들었습니다", $"4개 시점과 증거 이미지가 생성되었습니다. Draft {paths.Count}개 · 최종 판정은 실제 사용자가 합니다.", StatusTone.Success);
            EditorUtility.RevealInFinder(captures.raw_paths[0]);
            RefreshAll();
        });

        private bool CanStart(out string reason)
        {
            if (descriptor == null || descriptor.Prefab == null)
            {
                reason = "검수할 오브젝트 Descriptor를 선택해 주세요.";
                return false;
            }
            if (UsesWall && wallSource == null)
            {
                reason = "Hierarchy에서 실제 기준 벽을 선택해 주세요.";
                return false;
            }
            if (template == SpatialCalibrationTemplate.SupportedBy &&
                (targetDescriptor == null || targetDescriptor.Prefab == null))
            {
                reason = "승인된 받침 Descriptor를 선택해 주세요.";
                return false;
            }
            if (template == SpatialCalibrationTemplate.SupportedBy && targetDescriptor.Geometry?.IsUsable != true)
            {
                reason = "받침 Descriptor의 Geometry와 top 배치 영역을 먼저 사용자 승인해 주세요.";
                return false;
            }
            reason = null;
            return true;
        }

        private bool UsesWall => template is SpatialCalibrationTemplate.WallMounted
            or SpatialCalibrationTemplate.WallBackedFloorSupported;

        private void RefreshAll()
        {
            if (rootVisualElement.childCount == 0 || startButton == null) return;
            RefreshSetupState();
            RefreshStatus();
            RefreshSession();
            RefreshStepper();
        }

        private void RefreshSetupState()
        {
            if (relationshipHelp == null) return;
            relationshipHelp.Clear();
            relationshipHelp.Add(new Label(RelationshipDescription()));
            wallFields.style.display = UsesWall ? DisplayStyle.Flex : DisplayStyle.None;
            targetFields.style.display = template == SpatialCalibrationTemplate.SupportedBy
                ? DisplayStyle.Flex : DisplayStyle.None;

            var hasSession = SpatialCalibrationSession.Current != null;
            setupFields.SetEnabled(!hasSession);
            wallFields.SetEnabled(!hasSession);
            targetFields.SetEnabled(!hasSession);
            startButton.SetEnabled(!hasSession && CanStart(out _));
            discardButton.SetEnabled(hasSession);

            if (!hasSession && UsesWall && wallSource == null)
            {
                relationshipHelp.Add(new Label("필수 단계 · Hierarchy에서 실제 벽 하나를 선택해야 시작할 수 있습니다."));
            }
        }

        private string RelationshipDescription() => template switch
        {
            SpatialCalibrationTemplate.WallMounted => "배너처럼 벽에 거는 오브젝트입니다. 벽과 0.5~1cm 간격, 관통 0m를 검사합니다.",
            SpatialCalibrationTemplate.WallBackedFloorSupported => "책장처럼 바닥에 서면서 벽에도 기대는 오브젝트입니다. 두 접촉을 동시에 검사합니다.",
            SpatialCalibrationTemplate.FloorSupported => "가구나 항아리처럼 바닥에 놓는 오브젝트입니다. 바닥 간격과 지지율을 검사합니다.",
            SpatialCalibrationTemplate.SupportedBy => "책이나 촛대처럼 다른 오브젝트 위에 놓습니다. 받침 상단 안의 지지율을 검사합니다.",
            _ => string.Empty
        };

        private void RefreshSession()
        {
            var session = SpatialCalibrationSession.Current;
            sessionContent.style.display = session == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (session == null)
            {
                addProxyButton.SetEnabled(false);
                deleteProxyButton.SetEnabled(false);
                validateButton.SetEnabled(false);
                captureButton.SetEnabled(false);
                return;
            }

            BuildSessionSummary(session);
            BuildStageActions(session);
            BuildGeometryControls(session);
            BuildRuleControls(session);
            BuildValidationResults(session);
            addProxyButton.SetEnabled(true);
            deleteProxyButton.SetEnabled(session.Geometry.collisionProxies.Count > 1);
            validateButton.SetEnabled(true);
            captureButton.SetEnabled(session.LastReport?.Passed == true);
            captureButton.tooltip = session.LastReport?.Passed == true
                ? "정면·측면·상단·접촉 확대 캡처를 생성합니다."
                : "기술 검사 오류가 0개일 때 활성화됩니다.";
        }

        private void BuildSessionSummary(SpatialCalibrationSession session)
        {
            sessionSummary.Clear();
            AddSummary("검수 대상", session.Descriptor.Prefab.name);
            AddSummary("공간 관계", RelationshipLabels[Mathf.Max(0, Array.IndexOf(RelationshipValues, session.Template))]);
            AddSummary("세션", session.SessionId[..Mathf.Min(8, session.SessionId.Length)]);
            if (session.Template == SpatialCalibrationTemplate.SupportedBy)
                AddSummary("접촉면", $"{session.SupportedBySubjectFrameId} → {session.SupportedByTargetFrameId}");
            if (session.SourceWallObject != null)
            {
                AddSummary("기준 벽", session.SourceWallObject.name);
                AddSummary("측정한 벽면", $"{session.WallSurface.Size.x:0.##} × {session.WallSurface.Size.y:0.##} m");
            }
        }

        private void AddSummary(string caption, string value)
        {
            var item = new VisualElement();
            item.AddToClassList("summary-item");
            var captionLabel = new Label(caption); captionLabel.AddToClassList("summary-caption");
            var valueLabel = new Label(value); valueLabel.AddToClassList("summary-value");
            item.Add(captionLabel); item.Add(valueLabel); sessionSummary.Add(item);
        }

        private void BuildStageActions(SpatialCalibrationSession session)
        {
            stageActions.Clear();
            AddAction(stageActions, "오브젝트 선택", () => Selection.activeGameObject = session.SubjectObject);
            if (session.WallFixture != null)
                AddAction(stageActions, "벽 선택", () => Selection.activeGameObject = session.WallFixture);
            if (session.TargetObject != null)
                AddAction(stageActions, "받침 선택", () => Selection.activeGameObject = session.TargetObject);
            AddAction(stageActions, "전체 보기", () =>
            {
                Selection.objects = new UnityEngine.Object[] { session.SubjectObject, session.TargetObject }
                    .Where(value => value != null).ToArray();
                SceneView.lastActiveSceneView?.FrameSelected();
            });
        }

        private static void AddAction(VisualElement parent, string text, Action action)
        {
            var button = new Button(action) { text = text };
            button.AddToClassList("secondary-button");
            parent.Add(button);
        }

        private void BuildGeometryControls(SpatialCalibrationSession session)
        {
            geometryControls.Clear();
            if (session.Geometry.collisionProxies.Count == 0)
            {
                geometryControls.Add(new Label("충돌 영역이 없습니다. 하나 이상 추가해야 기술 검사를 실행할 수 있습니다."));
                BuildContactFrameControls(session);
                return;
            }
            selectedProxy = Mathf.Clamp(selectedProxy, 0, session.Geometry.collisionProxies.Count - 1);
            var selector = new SliderInt("편집할 영역", 1, session.Geometry.collisionProxies.Count)
            {
                value = selectedProxy + 1,
                showInputField = true
            };
            selector.RegisterValueChangedCallback(evt => { selectedProxy = evt.newValue - 1; BuildGeometryControls(session); SceneView.RepaintAll(); });
            geometryControls.Add(selector);
            var proxy = session.Geometry.collisionProxies[selectedProxy];
            var id = new TextField("영역 이름") { value = proxy.proxyId };
            id.RegisterValueChangedCallback(evt => { proxy.proxyId = evt.newValue; session.NotifyChanged(); });
            geometryControls.Add(id);
            var center = new Vector3Field("중심 위치") { value = proxy.localCenter };
            center.RegisterValueChangedCallback(evt => { proxy.localCenter = evt.newValue; session.NotifyChanged(); });
            geometryControls.Add(center);
            var size = new Vector3Field("크기") { value = proxy.size };
            size.RegisterValueChangedCallback(evt => { proxy.size = evt.newValue; session.NotifyChanged(); });
            geometryControls.Add(size);
            var rotation = new Vector3Field("회전") { value = proxy.localRotation.eulerAngles };
            rotation.RegisterValueChangedCallback(evt => { proxy.localRotation = Quaternion.Euler(evt.newValue); session.NotifyChanged(); });
            geometryControls.Add(rotation);
            BuildContactFrameControls(session);
        }

        private void BuildContactFrameControls(SpatialCalibrationSession session)
        {
            var frameIds = new List<string> { "bottom", "back", "top" };
            if (!frameIds.Contains(selectedContactFrameId)) selectedContactFrameId = "top";
            var selector = new DropdownField("접촉 프레임", frameIds, frameIds.IndexOf(selectedContactFrameId));
            selector.tooltip = "top은 이 에셋이 다른 물건을 받칠 때 사용할 검수 영역입니다.";
            selector.RegisterValueChangedCallback(evt =>
            {
                selectedContactFrameId = evt.newValue;
                BuildGeometryControls(session);
                SceneView.RepaintAll();
            });
            geometryControls.Add(selector);

            var frameId = selectedContactFrameId;
            var contact = session.Frame(frameId);
            var point = new Vector3Field("로컬 기준점") { value = contact.localPoint };
            point.RegisterValueChangedCallback(evt => session.UpdateContactFrame(
                frameId, evt.newValue, contact.localNormal, contact.localTangent, contact.size));
            geometryControls.Add(point);
            var normal = new Vector3Field("바깥쪽 법선") { value = contact.localNormal };
            normal.RegisterValueChangedCallback(evt => session.UpdateContactFrame(
                frameId, contact.localPoint, evt.newValue, contact.localTangent, contact.size));
            geometryControls.Add(normal);
            var tangent = new Vector3Field("가로 방향") { value = contact.localTangent };
            tangent.RegisterValueChangedCallback(evt => session.UpdateContactFrame(
                frameId, contact.localPoint, contact.localNormal, evt.newValue, contact.size));
            geometryControls.Add(tangent);
            var size = new Vector2Field("배치 가능 영역 크기") { value = contact.size };
            size.RegisterValueChangedCallback(evt => session.UpdateContactFrame(
                frameId, contact.localPoint, contact.localNormal, contact.localTangent, evt.newValue));
            geometryControls.Add(size);

            AddAction(geometryControls, $"{frameId} 자동 측정값으로 되돌리기", () =>
            {
                session.ResetContactFrame(frameId);
                BuildGeometryControls(session);
            });
            if (session.Template == SpatialCalibrationTemplate.SupportedBy &&
                string.Equals(frameId, session.SupportedBySubjectFrameId, StringComparison.OrdinalIgnoreCase))
                AddAction(geometryControls, "수정한 접촉면으로 받침에 다시 맞추기",
                    session.AlignSupportedBySubjectFrameToTarget);
        }

        private void BuildRuleControls(SpatialCalibrationSession session)
        {
            rulesControls.Clear();
            if (session.Template == SpatialCalibrationTemplate.SupportedBy)
            {
                var index = Mathf.Max(0, Array.IndexOf(SupportedBySubjectFrames, session.SupportedBySubjectFrameId));
                var frame = new DropdownField("오브젝트의 접촉면", SupportedBySubjectFrameLabels, index);
                frame.RegisterValueChangedCallback(evt =>
                {
                    var selected = SupportedBySubjectFrameLabels.IndexOf(evt.newValue);
                    if (selected >= 0) session.ConfigureSupportedByFrames(SupportedBySubjectFrames[selected], "top");
                });
                rulesControls.Add(frame);
                var targetTop = session.TargetFrame(session.SupportedByTargetFrameId);
                var targetRegion = new HelpBox(
                    $"받침의 승인된 top 영역 (읽기 전용) · 기준점 {targetTop.localPoint} · 크기 {targetTop.size.x:0.###} × {targetTop.size.y:0.###}m\n" +
                    "바꾸려면 받침 Descriptor를 검수 대상으로 열어 top 프레임을 수정하고 사용자 재승인을 받으세요.",
                    HelpBoxMessageType.Info);
                rulesControls.Add(targetRegion);
                AddAction(rulesControls, "선택한 면을 받침 상단에 다시 맞추기", session.AlignSupportedBySubjectFrameToTarget);
            }
            foreach (var rule in session.Rules)
            {
                var card = new VisualElement(); card.AddToClassList("rule-card");
                var title = new Label($"{RuleName(rule.kind)} · {rule.frame_id} → {rule.target}"); title.AddToClassList("rule-title"); card.Add(title);
                var minGap = new FloatField("최소 간격 (m)") { value = rule.minimum_gap };
                minGap.RegisterValueChangedCallback(evt => { rule.minimum_gap = evt.newValue; session.NotifyChanged(); }); card.Add(minGap);
                var maxGap = new FloatField("최대 간격 (m)") { value = rule.maximum_gap };
                maxGap.RegisterValueChangedCallback(evt => { rule.maximum_gap = evt.newValue; session.NotifyChanged(); }); card.Add(maxGap);
                var penetration = new FloatField("허용 관통 (m)") { value = rule.maximum_penetration };
                penetration.RegisterValueChangedCallback(evt => { rule.maximum_penetration = evt.newValue; session.NotifyChanged(); }); card.Add(penetration);
                var support = new Slider("최소 지지율", 0f, 1f) { value = rule.minimum_support, showInputField = true };
                support.RegisterValueChangedCallback(evt => { rule.minimum_support = evt.newValue; session.NotifyChanged(); }); card.Add(support);
                var direction = new Slider("최소 방향 일치", 0f, 1f) { value = rule.direction_alignment, showInputField = true };
                direction.RegisterValueChangedCallback(evt => { rule.direction_alignment = evt.newValue; session.NotifyChanged(); }); card.Add(direction);
                rulesControls.Add(card);
            }
        }

        private static string RuleName(string kind) => kind switch
        {
            "WallMounted" => "벽 장착",
            "WallBacked" => "벽 기대기",
            "FloorSupported" => "바닥 지지",
            "SupportedBy" => "다른 오브젝트의 지지",
            _ => kind
        };

        private void BuildValidationResults(SpatialCalibrationSession session)
        {
            validationSummary.Clear(); validationResults.Clear(); captureResults.Clear();
            var report = session.LastReport;
            if (report == null)
            {
                validationSummary.Add(new Label("아직 기술 검사를 실행하지 않았습니다."));
                return;
            }
            var summary = new Label(report.Passed
                ? "통과 · 기술 오류 0개 · 사용자 검수용 캡처를 만들 수 있습니다."
                : $"실패 · 기술 오류 {report.error_count}개 · 아래 항목을 수정하세요.");
            summary.AddToClassList(report.Passed ? "validation-pass" : "validation-fail");
            validationSummary.Add(summary);
            foreach (var error in report.errors)
            {
                var row = new Label(error); row.AddToClassList("result-row"); row.AddToClassList("result-error"); validationResults.Add(row);
            }
            foreach (var evidence in report.contacts)
            {
                var row = new Label($"{evidence.rule_id} · 간격 {evidence.gap:0.####}m · 관통 {evidence.penetration:0.####}m · 지지 {evidence.support:P0} · 방향 {evidence.direction_alignment:0.###}");
                row.AddToClassList("result-row"); validationResults.Add(row);
            }
            if (session.CaptureSet == null) return;
            var hash = new Label($"Capture Hash · {session.CaptureSet.capture_set_hash}"); hash.AddToClassList("capture-path"); captureResults.Add(hash);
            foreach (var path in session.DraftPaths)
            {
                var output = new Label(path); output.AddToClassList("capture-path"); captureResults.Add(output);
            }
        }

        private void RefreshStepper()
        {
            var session = SpatialCalibrationSession.Current;
            SetStep("stepper-setup", session == null, session != null);
            SetStep("stepper-stage", session != null && session.LastReport == null, session?.LastReport != null);
            SetStep("stepper-validate", session?.LastReport != null && !session.LastReport.Passed, session?.LastReport?.Passed == true);
            SetStep("stepper-capture", session?.LastReport?.Passed == true && session.CaptureSet == null, session?.CaptureSet != null);
        }

        private void SetStep(string name, bool active, bool done)
        {
            var element = rootVisualElement.Q<VisualElement>(name);
            element.EnableInClassList("step-active", active);
            element.EnableInClassList("step-done", done);
        }

        private void SetStatus(string title, string message, StatusTone tone)
        {
            statusTitle = title; statusMessage = message; statusTone = tone; RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (statusBanner == null) return;
            statusTitleLabel.text = statusTitle;
            statusMessageLabel.text = statusMessage;
            statusIcon.text = statusTone switch { StatusTone.Success => "✓", StatusTone.Warning => "!", StatusTone.Error => "×", _ => "i" };
            statusBanner.EnableInClassList("status-neutral", statusTone == StatusTone.Neutral);
            statusBanner.EnableInClassList("status-success", statusTone == StatusTone.Success);
            statusBanner.EnableInClassList("status-warning", statusTone == StatusTone.Warning);
            statusBanner.EnableInClassList("status-error", statusTone == StatusTone.Error);
        }

        private void OnSessionChanged()
        {
            if (rootVisualElement.childCount == 0) return;
            rootVisualElement.schedule.Execute(RefreshAll);
        }

        private void DuringSceneGUI(SceneView sceneView)
        {
            var session = SpatialCalibrationSession.Current;
            if (session?.SubjectObject == null || session.Geometry.collisionProxies.Count == 0) return;
            selectedProxy = Mathf.Clamp(selectedProxy, 0, session.Geometry.collisionProxies.Count - 1);
            var transform = session.SubjectObject.transform;
            for (var i = 0; i < session.Geometry.collisionProxies.Count; i++)
            {
                var proxy = session.Geometry.collisionProxies[i];
                var center = transform.TransformPoint(proxy.localCenter);
                var rotation = transform.rotation * proxy.localRotation;
                using (new Handles.DrawingScope(i == selectedProxy ? Color.cyan : new Color(0.2f, 0.9f, 0.4f), Matrix4x4.TRS(center, rotation, Vector3.one)))
                    Handles.DrawWireCube(Vector3.zero, Vector3.Scale(proxy.size, transform.lossyScale));
            }
            var selected = session.Geometry.collisionProxies[selectedProxy];
            var worldCenter = transform.TransformPoint(selected.localCenter);
            var worldRotation = transform.rotation * selected.localRotation;
            EditorGUI.BeginChangeCheck();
            worldCenter = Handles.PositionHandle(worldCenter, worldRotation);
            worldRotation = Handles.RotationHandle(worldRotation, worldCenter);
            using (new Handles.DrawingScope(Color.cyan, Matrix4x4.TRS(worldCenter, worldRotation, Vector3.one)))
            {
                boxHandle.center = Vector3.zero;
                boxHandle.size = Vector3.Scale(selected.size, transform.lossyScale);
                boxHandle.DrawHandle();
            }
            if (EditorGUI.EndChangeCheck())
            {
                selected.localCenter = transform.InverseTransformPoint(worldCenter);
                selected.localRotation = Quaternion.Inverse(transform.rotation) * worldRotation;
                var lossy = transform.lossyScale;
                selected.size = new Vector3(
                    boxHandle.size.x / Mathf.Max(0.0001f, Mathf.Abs(lossy.x)),
                    boxHandle.size.y / Mathf.Max(0.0001f, Mathf.Abs(lossy.y)),
                    boxHandle.size.z / Mathf.Max(0.0001f, Mathf.Abs(lossy.z)));
                session.NotifyChanged();
            }
            DrawFrame(transform, session.Frame("bottom"), Color.green);
            DrawFrame(transform, session.Frame("back"), new Color(1f, 0.55f, 0.1f));
            DrawFrame(transform, session.Frame("top"), new Color(0.2f, 0.65f, 1f));
            if (session.Template == SpatialCalibrationTemplate.SupportedBy && session.TargetObject != null)
            {
                DrawFrame(session.TargetObject.transform, session.TargetFrame("top"), new Color(0.9f, 0.25f, 0.9f));
                Handles.Label(session.TargetSurface("top").Origin, "받침 top · 승인된 배치 가능 영역");
            }
            if (session.Template is SpatialCalibrationTemplate.WallMounted or SpatialCalibrationTemplate.WallBackedFloorSupported)
                DrawWallSurface(session.WallSurface);
        }

        private static void DrawWallSurface(SpatialCalibrationSurface surface)
        {
            var x = surface.Tangent * surface.Size.x * 0.5f;
            var y = surface.Bitangent * surface.Size.y * 0.5f;
            var corners = new[] { surface.Origin - x - y, surface.Origin + x - y, surface.Origin + x + y, surface.Origin - x + y, surface.Origin - x - y };
            Handles.color = new Color(0.7f, 0.25f, 1f, 1f);
            Handles.DrawAAPolyLine(3f, corners);
            Handles.ArrowHandleCap(0, surface.Origin, Quaternion.LookRotation(surface.Normal),
                Mathf.Clamp(Mathf.Min(surface.Size.x, surface.Size.y) * 0.2f, 0.2f, 0.8f), EventType.Repaint);
        }

        private static void DrawFrame(Transform transform, ContactFrame frame, Color color)
        {
            var point = transform.TransformPoint(frame.localPoint);
            var normal = transform.TransformDirection(frame.localNormal).normalized;
            var localTangent = Vector3.ProjectOnPlane(frame.localTangent, frame.localNormal).normalized;
            if (localTangent.sqrMagnitude < 0.000001f) localTangent = Vector3.right;
            var localBitangent = Vector3.Cross(localTangent, frame.localNormal.normalized).normalized;
            var x = transform.TransformVector(localTangent * frame.size.x * 0.5f);
            var y = transform.TransformVector(localBitangent * frame.size.y * 0.5f);
            Handles.color = color;
            Handles.DrawSolidDisc(point, SceneView.currentDrawingSceneView.camera.transform.forward, 0.025f);
            Handles.DrawLine(point, point + normal * 0.25f, 2f);
            Handles.DrawAAPolyLine(2f,
                point - x - y,
                point + x - y,
                point + x + y,
                point - x + y,
                point - x - y);
        }

        private void RunSafe(Action action)
        {
            try { action(); RefreshAll(); }
            catch (Exception exception)
            {
                SetStatus("작업을 완료하지 못했습니다", exception.Message, StatusTone.Error);
                Debug.LogException(exception);
            }
        }

        private enum StatusTone { Neutral, Success, Warning, Error }
    }
}
