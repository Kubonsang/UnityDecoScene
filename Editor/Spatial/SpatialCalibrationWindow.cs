using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public sealed class SpatialCalibrationWindow : EditorWindow
    {
        [SerializeField] private DecorAssetDescriptor descriptor;
        [SerializeField] private GameObject targetPrefab;
        [SerializeField] private GameObject wallSource;
        [SerializeField] private SpatialWallNormalAxis wallNormalAxis = SpatialWallNormalAxis.LocalForward;
        [SerializeField] private bool flipWallNormal;
        [SerializeField] private SpatialCalibrationTemplate template = SpatialCalibrationTemplate.WallMounted;
        private int selectedProxy;
        private Vector2 scroll;
        private string status;
        private readonly BoxBoundsHandle boxHandle = new();

        [MenuItem("Window/Concept Room Decorator/Spatial Calibration")]
        public static void Open() => GetWindow<SpatialCalibrationWindow>("Spatial Calibration");

        private void OnEnable()
        {
            SceneView.duringSceneGui += DuringSceneGUI;
            SpatialCalibrationSession.Changed += Repaint;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= DuringSceneGUI;
            SpatialCalibrationSession.Changed -= Repaint;
            SpatialCalibrationSession.Current?.Dispose();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Agent Spatial Contract Studio", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Arrange a canonical example with normal Unity transforms. Geometry comes from reviewed compound OBBs; the example defines contact and interaction intent. Calibration uses a temporary PreviewSceneStage and never changes the source prefab or active scene.", MessageType.Info);
            descriptor = (DecorAssetDescriptor)EditorGUILayout.ObjectField("Subject Descriptor", descriptor, typeof(DecorAssetDescriptor), false);
            template = (SpatialCalibrationTemplate)EditorGUILayout.EnumPopup("Relationship", template);
            var usesWall = template is SpatialCalibrationTemplate.WallMounted or SpatialCalibrationTemplate.WallBackedFloorSupported;
            using (new EditorGUI.DisabledScope(!usesWall))
            {
                wallSource = (GameObject)EditorGUILayout.ObjectField(
                    "Wall Object", wallSource, typeof(GameObject), true);
                wallNormalAxis = (SpatialWallNormalAxis)EditorGUILayout.EnumPopup("Wall Face Axis", wallNormalAxis);
                flipWallNormal = EditorGUILayout.Toggle("Flip Inward Normal", flipWallNormal);
                if (GUILayout.Button("Use Selected Scene Object as Wall"))
                {
                    wallSource = Selection.activeGameObject;
                    status = wallSource != null
                        ? $"Selected wall: {wallSource.name}. Confirm the purple inward-normal arrow after opening the stage."
                        : "Select a wall GameObject in the scene first.";
                }
            }
            if (usesWall && wallSource == null)
                EditorGUILayout.HelpBox(
                    "No wall is selected. The neutral calibration wall will be used, which may be hard to distinguish from the background.",
                    MessageType.Warning);
            using (new EditorGUI.DisabledScope(template != SpatialCalibrationTemplate.SupportedBy))
                targetPrefab = (GameObject)EditorGUILayout.ObjectField("Target Prefab", targetPrefab, typeof(GameObject), false);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(descriptor == null || descriptor.Prefab == null || template == SpatialCalibrationTemplate.SupportedBy && targetPrefab == null))
                {
                    if (GUILayout.Button("Open Calibration Stage")) RunSafe(() =>
                    {
                        SpatialCalibrationSession.Begin(
                            descriptor, targetPrefab, template, wallSource, wallNormalAxis, flipWallNormal);
                        selectedProxy = 0;
                        status = "Calibration stage ready. Move the subject to the intended valid pose.";
                    });
                }
                using (new EditorGUI.DisabledScope(SpatialCalibrationSession.Current == null))
                    if (GUILayout.Button("Discard")) { SpatialCalibrationSession.Current.Dispose(); status = "Calibration discarded without scene changes."; }
            }

            if (!string.IsNullOrWhiteSpace(status)) EditorGUILayout.HelpBox(status, MessageType.None);
            var session = SpatialCalibrationSession.Current;
            if (session == null)
            {
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawStage(session);
            DrawGeometry(session);
            DrawRules(session);
            DrawValidation(session);
            EditorGUILayout.EndScrollView();
        }

        private void DrawStage(SpatialCalibrationSession session)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("1. Canonical Example", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Session", session.SessionId);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select Subject")) Selection.activeGameObject = session.SubjectObject;
                using (new EditorGUI.DisabledScope(session.TargetObject == null))
                    if (GUILayout.Button("Select Target")) Selection.activeGameObject = session.TargetObject;
                using (new EditorGUI.DisabledScope(session.WallFixture == null))
                    if (GUILayout.Button("Select Wall")) Selection.activeGameObject = session.WallFixture;
                if (GUILayout.Button("Frame All"))
                {
                    Selection.objects = new UnityEngine.Object[] { session.SubjectObject, session.TargetObject }.Where(value => value != null).ToArray();
                    SceneView.lastActiveSceneView?.FrameSelected();
                }
            }
            if (session.SourceWallObject != null)
            {
                EditorGUILayout.LabelField("Wall Source", session.SourceWallObject.name);
                EditorGUILayout.LabelField(
                    "Wall Surface",
                    $"{session.WallSurface.Size.x:0.###} x {session.WallSurface.Size.y:0.###}m - inward {session.WallSurface.Normal}");
            }
        }

        private void DrawGeometry(SpatialCalibrationSession session)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("2. Compound OBB", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Source: {session.Geometry.source}    Proxies: {session.Geometry.collisionProxies.Count}");
            if (session.Geometry.collisionProxies.Count > 0)
            {
                selectedProxy = Mathf.Clamp(EditorGUILayout.IntSlider("Selected Proxy", selectedProxy + 1, 1, session.Geometry.collisionProxies.Count) - 1, 0, session.Geometry.collisionProxies.Count - 1);
                var proxy = session.Geometry.collisionProxies[selectedProxy];
                proxy.proxyId = EditorGUILayout.TextField("ID", proxy.proxyId);
                proxy.localCenter = EditorGUILayout.Vector3Field("Local Center", proxy.localCenter);
                proxy.size = EditorGUILayout.Vector3Field("Size", proxy.size);
                proxy.localRotation = Quaternion.Euler(EditorGUILayout.Vector3Field("Rotation", proxy.localRotation.eulerAngles));
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add OBB"))
                {
                    var bounds = new Bounds(session.Descriptor.LocalBoundsCenter, session.Descriptor.LocalBoundsSize);
                    session.Geometry.collisionProxies.Add(new OrientedBoxProxy($"manual-{session.Geometry.collisionProxies.Count}", bounds.center, bounds.size, Quaternion.identity));
                    selectedProxy = session.Geometry.collisionProxies.Count - 1;
                    session.NotifyChanged();
                }
                using (new EditorGUI.DisabledScope(session.Geometry.collisionProxies.Count <= 1))
                    if (GUILayout.Button("Delete Selected")) { session.Geometry.collisionProxies.RemoveAt(selectedProxy); selectedProxy = Mathf.Max(0, selectedProxy - 1); session.NotifyChanged(); }
            }
        }

        private static void DrawRules(SpatialCalibrationSession session)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("3. Contact Rules", EditorStyles.boldLabel);
            foreach (var rule in session.Rules)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField($"{rule.kind} - {rule.frame_id} -> {rule.target}", EditorStyles.boldLabel);
                    rule.minimum_gap = EditorGUILayout.FloatField("Minimum Gap (m)", rule.minimum_gap);
                    rule.maximum_gap = EditorGUILayout.FloatField("Maximum Gap (m)", rule.maximum_gap);
                    rule.maximum_penetration = EditorGUILayout.FloatField("Maximum Penetration", rule.maximum_penetration);
                    rule.minimum_support = EditorGUILayout.Slider("Minimum Support", rule.minimum_support, 0f, 1f);
                    rule.direction_alignment = EditorGUILayout.Slider("Direction Alignment", rule.direction_alignment, 0f, 1f);
                }
            }
        }

        private void DrawValidation(SpatialCalibrationSession session)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("4. Validate and Capture", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Run Technical Validation")) RunSafe(() =>
                {
                    var report = SpatialCalibrationValidator.Validate(session);
                    status = report.Passed ? "Technical gate passed. Human capture review is still required." : $"Technical gate failed with {report.error_count} errors.";
                });
                if (GUILayout.Button("Capture 4 Views")) RunSafe(() =>
                {
                    var report = SpatialCalibrationValidator.Validate(session);
                    var captures = SpatialCalibrationCaptureService.Capture(session, report);
                    var paths = SpatialContractIO.WriteDrafts(session, report, captures);
                    status = $"Captured {captures.raw_paths.Count} views and wrote {paths.Count} draft contract(s).";
                    EditorUtility.RevealInFinder(captures.raw_paths[0]);
                });
            }

            var report = session.LastReport;
            if (report != null)
            {
                EditorGUILayout.HelpBox(report.Passed ? "Technical errors: 0 - Awaiting actual user review" : $"Technical errors: {report.error_count}", report.Passed ? MessageType.Info : MessageType.Error);
                foreach (var error in report.errors) EditorGUILayout.HelpBox(error, MessageType.Error);
                foreach (var evidence in report.contacts)
                    EditorGUILayout.LabelField($"{evidence.rule_id}: gap {evidence.gap:0.####}m - penetration {evidence.penetration:0.####}m - support {evidence.support:P0} - direction {evidence.direction_alignment:0.###}");
            }
            if (session.CaptureSet != null)
            {
                EditorGUILayout.LabelField("Capture Hash", session.CaptureSet.capture_set_hash);
                foreach (var path in session.DraftPaths) EditorGUILayout.SelectableLabel(path, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
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
                selected.size = new Vector3(boxHandle.size.x / Mathf.Max(0.0001f, Mathf.Abs(lossy.x)), boxHandle.size.y / Mathf.Max(0.0001f, Mathf.Abs(lossy.y)), boxHandle.size.z / Mathf.Max(0.0001f, Mathf.Abs(lossy.z)));
                session.NotifyChanged();
            }

            DrawFrame(transform, session.Frame("bottom"), Color.green);
            DrawFrame(transform, session.Frame("back"), new Color(1f, 0.55f, 0.1f));
            if (session.Template is SpatialCalibrationTemplate.WallMounted or SpatialCalibrationTemplate.WallBackedFloorSupported)
                DrawWallSurface(session.WallSurface);
        }

        private static void DrawWallSurface(SpatialCalibrationSurface surface)
        {
            var x = surface.Tangent * surface.Size.x * 0.5f;
            var y = surface.Bitangent * surface.Size.y * 0.5f;
            var corners = new[]
            {
                surface.Origin - x - y,
                surface.Origin + x - y,
                surface.Origin + x + y,
                surface.Origin - x + y,
                surface.Origin - x - y
            };
            Handles.color = new Color(0.7f, 0.25f, 1f, 1f);
            Handles.DrawAAPolyLine(3f, corners);
            Handles.ArrowHandleCap(
                0,
                surface.Origin,
                Quaternion.LookRotation(surface.Normal),
                Mathf.Clamp(Mathf.Min(surface.Size.x, surface.Size.y) * 0.2f, 0.2f, 0.8f),
                EventType.Repaint);
        }

        private static void DrawFrame(Transform transform, ContactFrame frame, Color color)
        {
            var point = transform.TransformPoint(frame.localPoint);
            var normal = transform.TransformDirection(frame.localNormal).normalized;
            Handles.color = color;
            Handles.DrawSolidDisc(point, SceneView.currentDrawingSceneView.camera.transform.forward, 0.025f);
            Handles.DrawLine(point, point + normal * 0.25f, 2f);
        }

        private void RunSafe(Action action)
        {
            try { action(); Repaint(); }
            catch (Exception exception) { status = exception.Message; Debug.LogException(exception); }
        }
    }
}
