using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public sealed class SpatialCalibrationSession : IDisposable
    {
        public static SpatialCalibrationSession Current { get; private set; }
        public static event Action Changed;

        public string SessionId { get; } = Guid.NewGuid().ToString("N");
        public DecorAssetDescriptor Descriptor { get; }
        public DecorAssetDescriptor TargetDescriptor { get; }
        public GameObject TargetPrefab { get; }
        public GameObject SourceWallObject { get; }
        public SpatialWallNormalAxis WallNormalAxis { get; }
        public bool FlipWallNormal { get; }
        public SpatialCalibrationTemplate Template { get; }
        public string SupportedBySubjectFrameId { get; private set; }
        public string SupportedByTargetFrameId { get; private set; }
        public DecorGeometryProfile Geometry { get; }
        public DecorGeometryProfile TargetGeometry { get; }
        public GameObject SubjectObject { get; private set; }
        public GameObject TargetObject { get; private set; }
        public GameObject FloorFixture { get; private set; }
        public GameObject WallFixture { get; private set; }
        public SpatialCalibrationSurface WallSurface { get; private set; }
        public SpatialCalibrationReport LastReport { get; set; }
        public SpatialCaptureSet CaptureSet { get; set; }
        public List<string> DraftPaths { get; } = new();
        public string AgentProposalJson { get; set; }
        public IReadOnlyList<SpatialContactRuleContract> Rules => rules;
        public bool RequiresWallFixture => Template is SpatialCalibrationTemplate.WallMounted
            or SpatialCalibrationTemplate.WallBackedFloorSupported;
        public bool RequiresFloorFixture => Template is SpatialCalibrationTemplate.FloorSupported
            or SpatialCalibrationTemplate.WallBackedFloorSupported;

        private readonly List<SpatialContactRuleContract> rules = new();
        private readonly DecorGeometryProfile automaticGeometry;
        private Scene calibrationScene;
        private Scene previousActiveScene;
        private SpatialCalibrationPreviewStage previewStage;

        private SpatialCalibrationSession(
            DecorAssetDescriptor descriptor,
            GameObject targetPrefab,
            DecorAssetDescriptor targetDescriptor,
            SpatialCalibrationTemplate template,
            GameObject sourceWallObject,
            SpatialWallNormalAxis wallNormalAxis,
            bool flipWallNormal,
            string supportedBySubjectFrameId,
            string supportedByTargetFrameId)
        {
            Descriptor = descriptor;
            TargetDescriptor = targetDescriptor;
            TargetPrefab = targetDescriptor != null ? targetDescriptor.Prefab : targetPrefab;
            Template = template;
            SourceWallObject = sourceWallObject;
            WallNormalAxis = wallNormalAxis;
            FlipWallNormal = flipWallNormal;
            SupportedBySubjectFrameId = NormalizeSubjectFrame(supportedBySubjectFrameId);
            SupportedByTargetFrameId = NormalizeTargetFrame(supportedByTargetFrameId);
            Geometry = DecorAssetScanner.BuildGeometryProfile(descriptor.Prefab);
            Geometry.reviewed = false;
            automaticGeometry = CloneGeometry(Geometry);
            TargetGeometry = targetDescriptor != null
                ? CloneGeometry(targetDescriptor.Geometry)
                : TargetPrefab != null ? DecorAssetScanner.BuildGeometryProfile(TargetPrefab) : null;
            BuildRules();
            OpenScene();
        }

        public static SpatialCalibrationSession Begin(DecorAssetDescriptor descriptor, GameObject targetPrefab, SpatialCalibrationTemplate template)
            => Begin(descriptor, targetPrefab, template, null, SpatialWallNormalAxis.LocalForward, false);

        public static SpatialCalibrationSession Begin(
            DecorAssetDescriptor descriptor,
            GameObject targetPrefab,
            SpatialCalibrationTemplate template,
            string supportedBySubjectFrameId,
            string supportedByTargetFrameId)
            => BeginCore(descriptor, targetPrefab, null, template, null, SpatialWallNormalAxis.LocalForward, false,
                supportedBySubjectFrameId, supportedByTargetFrameId);

        public static SpatialCalibrationSession Begin(
            DecorAssetDescriptor descriptor,
            DecorAssetDescriptor targetDescriptor,
            SpatialCalibrationTemplate template,
            string supportedBySubjectFrameId,
            string supportedByTargetFrameId)
            => BeginCore(descriptor, null, targetDescriptor, template, null,
                SpatialWallNormalAxis.LocalForward, false,
                supportedBySubjectFrameId, supportedByTargetFrameId);

        public static SpatialCalibrationSession Begin(
            DecorAssetDescriptor descriptor,
            GameObject targetPrefab,
            SpatialCalibrationTemplate template,
            GameObject sourceWallObject,
            SpatialWallNormalAxis wallNormalAxis,
            bool flipWallNormal)
            => BeginCore(descriptor, targetPrefab, null, template, sourceWallObject, wallNormalAxis, flipWallNormal,
                "bottom", "top");

        private static SpatialCalibrationSession BeginCore(
            DecorAssetDescriptor descriptor,
            GameObject targetPrefab,
            DecorAssetDescriptor targetDescriptor,
            SpatialCalibrationTemplate template,
            GameObject sourceWallObject,
            SpatialWallNormalAxis wallNormalAxis,
            bool flipWallNormal,
            string supportedBySubjectFrameId,
            string supportedByTargetFrameId)
        {
            if (descriptor == null || descriptor.Prefab == null) throw new ArgumentException("A descriptor with a prefab is required.");
            if (template == SpatialCalibrationTemplate.SupportedBy && targetPrefab == null && targetDescriptor?.Prefab == null)
                throw new ArgumentException("SupportedBy requires a target prefab or approved target descriptor.");
            if (targetDescriptor != null && targetDescriptor.Geometry?.IsUsable != true)
                throw new ArgumentException(
                    "The target descriptor must contain current human-reviewed geometry.",
                    nameof(targetDescriptor));
            Current?.Dispose();
            Current = new SpatialCalibrationSession(
                descriptor, targetPrefab, targetDescriptor, template, sourceWallObject, wallNormalAxis, flipWallNormal,
                supportedBySubjectFrameId, supportedByTargetFrameId);
            Changed?.Invoke();
            return Current;
        }

        public void NotifyChanged()
        {
            LastReport = null;
            CaptureSet = null;
            DraftPaths.Clear();
            AgentProposalJson = null;
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        public ContactFrame Frame(string id)
        {
            return RequireFrame(Geometry, id, "subject");
        }

        public ContactFrame TargetFrame(string id)
        {
            if (TargetGeometry == null)
                throw new InvalidOperationException("A target geometry profile is required.");
            return RequireFrame(TargetGeometry, id, "target");
        }

        public SpatialCalibrationSurface TargetSurface(string frameId = "top")
        {
            if (TargetObject == null)
                throw new InvalidOperationException("A target object is required.");
            return SurfaceFromFrame(TargetObject.transform, TargetFrame(frameId));
        }

        public void UpdateContactFrame(
            string frameId,
            Vector3 localPoint,
            Vector3 localNormal,
            Vector3 localTangent,
            Vector2 size)
        {
            var frame = Frame(frameId);
            frame.localPoint = localPoint;
            frame.localNormal = NormalizeOr(localNormal, DefaultNormal(frameId));
            var projectedTangent = Vector3.ProjectOnPlane(localTangent, frame.localNormal);
            frame.localTangent = NormalizeOr(projectedTangent, DefaultTangent(frame.localNormal));
            frame.size = new Vector2(
                Mathf.Max(0.001f, Mathf.Abs(size.x)),
                Mathf.Max(0.001f, Mathf.Abs(size.y)));
            NotifyChanged();
        }

        public void ResetContactFrame(string frameId)
        {
            CopyFrame(RequireFrame(automaticGeometry, frameId, "automatic"), Frame(frameId));
            NotifyChanged();
        }

        /// <summary>
        /// Builds a session-only target support frame on the face opposite a reviewed contact
        /// frame. This is used when an asset such as a book is rotated onto its reviewed back:
        /// the opposite face becomes the world-up stacking region. The target descriptor and its
        /// approved geometry are never mutated.
        /// </summary>
        public void ConfigureTargetOppositeFrame(string sourceFrameId, string targetFrameId = "top")
        {
            if (Template != SpatialCalibrationTemplate.SupportedBy || TargetGeometry == null)
                throw new InvalidOperationException("A SupportedBy target geometry profile is required.");
            NormalizeTargetFrame(targetFrameId);
            var source = TargetFrame(sourceFrameId);
            var sourceNormal = NormalizeOr(source.localNormal, DefaultNormal(sourceFrameId));
            var oppositeNormal = -sourceNormal;
            var tangent = NormalizeOr(
                Vector3.ProjectOnPlane(source.localTangent, oppositeNormal),
                DefaultTangent(oppositeNormal));
            var maximumProjection = TargetGeometry.collisionProxies
                .Where(proxy => proxy != null)
                .SelectMany(ProxyCorners)
                .Select(point => Vector3.Dot(point, oppositeNormal))
                .DefaultIfEmpty(Vector3.Dot(source.localPoint, oppositeNormal))
                .Max();
            var sourceProjection = Vector3.Dot(source.localPoint, oppositeNormal);
            TargetGeometry.topContact = new ContactFrame
            {
                frameId = "top",
                localPoint = source.localPoint + oppositeNormal * (maximumProjection - sourceProjection),
                localNormal = oppositeNormal,
                localTangent = tangent,
                size = new Vector2(
                    Mathf.Max(0.001f, Mathf.Abs(source.size.x)),
                    Mathf.Max(0.001f, Mathf.Abs(source.size.y)))
            };
            SupportedByTargetFrameId = "top";
            NotifyChanged();
        }

        public void ConfigureSupportedByFrames(string subjectFrameId, string targetFrameId)
        {
            if (Template != SpatialCalibrationTemplate.SupportedBy)
                throw new InvalidOperationException("Contact frame selection is available only for SupportedBy calibration.");
            SupportedBySubjectFrameId = NormalizeSubjectFrame(subjectFrameId);
            SupportedByTargetFrameId = NormalizeTargetFrame(targetFrameId);
            var support = rules.Single(rule => string.Equals(rule.kind, "SupportedBy", StringComparison.Ordinal));
            support.frame_id = SupportedBySubjectFrameId;
            AlignSupportedBySubjectFrameToTarget(false);
            NotifyChanged();
        }

        public void AlignSupportedBySubjectFrameToTarget() => AlignSupportedBySubjectFrameToTarget(true);

        public Bounds CombinedWorldBounds()
        {
            var renderers = new List<Renderer>();
            if (SubjectObject != null) renderers.AddRange(SubjectObject.GetComponentsInChildren<Renderer>(true));
            if (TargetObject != null) renderers.AddRange(TargetObject.GetComponentsInChildren<Renderer>(true));
            if (renderers.Count == 0) return new Bounds(Vector3.zero, Vector3.one);
            var result = renderers[0].bounds;
            for (var i = 1; i < renderers.Count; i++) result.Encapsulate(renderers[i].bounds);
            var padding = new Vector3(
                Mathf.Max(0.35f, result.size.x * 0.15f),
                Mathf.Max(0.35f, result.size.y * 0.15f),
                Mathf.Max(0.35f, result.size.z * 0.15f));
            result.Expand(padding);
            return result;
        }

        public Bounds TargetWorldBounds()
        {
            var renderers = TargetObject != null ? TargetObject.GetComponentsInChildren<Renderer>(true) : Array.Empty<Renderer>();
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
            var result = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) result.Encapsulate(renderers[i].bounds);
            return result;
        }

        public void Dispose()
        {
            if (previewStage != null)
            {
                if (ReferenceEquals(StageUtility.GetCurrentStage(), previewStage)) StageUtility.GoBackToPreviousStage();
                UnityEngine.Object.DestroyImmediate(previewStage);
                previewStage = null;
            }
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded) SceneManager.SetActiveScene(previousActiveScene);
            if (ReferenceEquals(Current, this)) Current = null;
            Changed?.Invoke();
            SceneView.RepaintAll();
        }

        private void OpenScene()
        {
            previousActiveScene = SceneManager.GetActiveScene();
            previewStage = ScriptableObject.CreateInstance<SpatialCalibrationPreviewStage>();
            previewStage.hideFlags = HideFlags.HideAndDontSave;
            StageUtility.GoToStage(previewStage, true);
            calibrationScene = previewStage.scene;

            if (RequiresFloorFixture)
                FloorFixture = CreateFixture("Calibration Floor", new Vector3(6f, 0.1f, 6f),
                    new Vector3(0f, -0.05f, 0f), new Color(0.23f, 0.25f, 0.28f));
            if (RequiresWallFixture) CreateWallFixture();
            SubjectObject = InstantiateTemporary(Descriptor.Prefab, "Calibration Subject");
            if (TargetPrefab != null) TargetObject = InstantiateTemporary(TargetPrefab, "Calibration Target");
            MoveToCalibrationScene(FloorFixture);
            MoveToCalibrationScene(WallFixture);
            MoveToCalibrationScene(SubjectObject);
            MoveToCalibrationScene(TargetObject);

            PositionCanonicalExample();
            MoveToCalibrationScene(AddLight());
            Selection.activeGameObject = SubjectObject;
            SceneView.lastActiveSceneView?.FrameSelected();
        }

        private void PositionCanonicalExample()
        {
            var bottom = Geometry.bottomContact.localPoint;
            var back = Geometry.backContact.localPoint;
            var position = Vector3.zero;
            switch (Template)
            {
                case SpatialCalibrationTemplate.WallMounted:
                    position = WallSurface.Origin + WallSurface.Normal * 0.0075f - back;
                    break;
                case SpatialCalibrationTemplate.WallBackedFloorSupported:
                    var wallAnchor = WallSurface.Origin + WallSurface.Normal * 0.03f;
                    position = new Vector3(wallAnchor.x - back.x, -bottom.y, wallAnchor.z - back.z);
                    break;
                case SpatialCalibrationTemplate.FloorSupported:
                    position = new Vector3(-bottom.x, -bottom.y, -bottom.z);
                    break;
                case SpatialCalibrationTemplate.SupportedBy:
                    AlignSupportedBySubjectFrameToTarget(false);
                    return;
            }
            SubjectObject.transform.SetPositionAndRotation(position, Quaternion.identity);
        }

        private void BuildRules()
        {
            switch (Template)
            {
                case SpatialCalibrationTemplate.WallMounted:
                    rules.Add(Rule("wall", "WallMounted", "back", "surface:wall", 0.005f, 0.01f));
                    break;
                case SpatialCalibrationTemplate.WallBackedFloorSupported:
                    rules.Add(Rule("wall", "WallBacked", "back", "surface:wall", 0.01f, 0.05f));
                    rules.Add(Rule("floor", "FloorSupported", "bottom", "surface:floor", 0f, 0.01f));
                    break;
                case SpatialCalibrationTemplate.FloorSupported:
                    rules.Add(Rule("floor", "FloorSupported", "bottom", "surface:floor", 0f, 0.01f));
                    break;
                case SpatialCalibrationTemplate.SupportedBy:
                    rules.Add(Rule("support", "SupportedBy", SupportedBySubjectFrameId, "asset:target", 0f, 0.01f));
                    break;
            }
        }

        private static SpatialContactRuleContract Rule(string id, string kind, string frame, string target, float minimum, float maximum) => new()
        {
            id = id,
            kind = kind,
            frame_id = frame,
            target = target,
            minimum_gap = minimum,
            maximum_gap = maximum,
            maximum_penetration = 0f,
            minimum_support = 0.6f,
            direction_alignment = 0.95f
        };

        private void AlignSupportedBySubjectFrameToTarget(bool notify)
        {
            if (Template != SpatialCalibrationTemplate.SupportedBy || SubjectObject == null || TargetObject == null)
                throw new InvalidOperationException("SupportedBy subject and target objects are required before alignment.");
            var frame = Frame(SupportedBySubjectFrameId);
            var targetSurface = TargetSurface(SupportedByTargetFrameId);
            var targetNormal = targetSurface.Normal;
            var targetTangent = targetSurface.Tangent;
            var normalAlignment = Quaternion.FromToRotation(frame.localNormal.normalized, -targetNormal);
            var tangentAfterNormal = Vector3.ProjectOnPlane(normalAlignment * frame.localTangent, targetNormal).normalized;
            var tangentAlignment = tangentAfterNormal.sqrMagnitude > 0.000001f
                ? Quaternion.AngleAxis(
                    Vector3.SignedAngle(tangentAfterNormal, targetTangent, targetNormal),
                    targetNormal)
                : Quaternion.identity;
            var rotation = tangentAlignment * normalAlignment;
            SubjectObject.transform.SetPositionAndRotation(Vector3.zero, rotation);
            SubjectObject.transform.position = targetSurface.Origin
                                               - SubjectObject.transform.TransformVector(frame.localPoint);
            if (notify) NotifyChanged();
        }

        private static SpatialCalibrationSurface SurfaceFromFrame(Transform transform, ContactFrame frame)
        {
            var normal = NormalizeOr(transform.TransformDirection(frame.localNormal), Vector3.up);
            var tangent = Vector3.ProjectOnPlane(transform.TransformDirection(frame.localTangent), normal);
            tangent = NormalizeOr(tangent, DefaultTangent(normal));

            var localNormal = NormalizeOr(frame.localNormal, Vector3.up);
            var localTangent = NormalizeOr(
                Vector3.ProjectOnPlane(frame.localTangent, localNormal),
                DefaultTangent(localNormal));
            var localBitangent = Vector3.Cross(localTangent, localNormal).normalized;
            var bitangent = Vector3.ProjectOnPlane(transform.TransformDirection(localBitangent), normal);
            bitangent = NormalizeOr(bitangent, Vector3.Cross(tangent, normal));

            var width = transform.TransformVector(localTangent * frame.size.x).magnitude;
            var height = transform.TransformVector(localBitangent * frame.size.y).magnitude;
            return new SpatialCalibrationSurface(
                transform.TransformPoint(frame.localPoint),
                normal,
                tangent,
                bitangent,
                new Vector2(Mathf.Max(0.001f, width), Mathf.Max(0.001f, height)));
        }

        private static ContactFrame RequireFrame(DecorGeometryProfile geometry, string id, string owner)
        {
            var frame = geometry?.Frame(id);
            if (frame == null)
                throw new ArgumentOutOfRangeException(nameof(id), id,
                    $"The {owner} contact frame must be bottom, back, or top.");
            return frame;
        }

        private static DecorGeometryProfile CloneGeometry(DecorGeometryProfile source)
        {
            if (source == null) return null;
            var clone = JsonUtility.FromJson<DecorGeometryProfile>(JsonUtility.ToJson(source));
            clone.Normalize();
            return clone;
        }

        private static void CopyFrame(ContactFrame source, ContactFrame destination)
        {
            destination.frameId = source.frameId;
            destination.localPoint = source.localPoint;
            destination.localNormal = source.localNormal;
            destination.localTangent = source.localTangent;
            destination.size = source.size;
        }

        private static IEnumerable<Vector3> ProxyCorners(OrientedBoxProxy proxy)
        {
            var extents = new Vector3(
                Mathf.Abs(proxy.size.x),
                Mathf.Abs(proxy.size.y),
                Mathf.Abs(proxy.size.z)) * 0.5f;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
                yield return proxy.localCenter + proxy.localRotation * new Vector3(
                    extents.x * x,
                    extents.y * y,
                    extents.z * z);
        }

        private static Vector3 DefaultNormal(string frameId) =>
            string.Equals(frameId, "bottom", StringComparison.OrdinalIgnoreCase) ? Vector3.down :
            string.Equals(frameId, "back", StringComparison.OrdinalIgnoreCase) ? Vector3.back : Vector3.up;

        private static Vector3 DefaultTangent(Vector3 normal)
        {
            var tangent = Vector3.ProjectOnPlane(Vector3.right, normal);
            if (tangent.sqrMagnitude < 0.000001f)
                tangent = Vector3.ProjectOnPlane(Vector3.forward, normal);
            return tangent.normalized;
        }

        private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
            value.sqrMagnitude < 0.000001f ? fallback.normalized : value.normalized;

        private static string NormalizeSubjectFrame(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "bottom";
            if (string.Equals(value, "bottom", StringComparison.OrdinalIgnoreCase)) return "bottom";
            if (string.Equals(value, "back", StringComparison.OrdinalIgnoreCase)) return "back";
            if (string.Equals(value, "top", StringComparison.OrdinalIgnoreCase)) return "top";
            throw new ArgumentOutOfRangeException(nameof(value), value, "SupportedBy subject frame must be bottom, back, or top.");
        }

        private static string NormalizeTargetFrame(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "top", StringComparison.OrdinalIgnoreCase)) return "top";
            throw new ArgumentOutOfRangeException(nameof(value), value, "Surface Arrangement 0.1 supports only the target top frame.");
        }

        private static GameObject InstantiateTemporary(GameObject prefab, string label)
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            instance.name = label;
            SetFlags(instance);
            return instance;
        }

        private static GameObject CreateFixture(string label, Vector3 scale, Vector3 position, Color color)
        {
            var fixture = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fixture.name = label;
            fixture.transform.SetPositionAndRotation(position, Quaternion.identity);
            fixture.transform.localScale = scale;
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { color = color, hideFlags = HideFlags.HideAndDontSave };
            fixture.GetComponent<Renderer>().sharedMaterial = material;
            SetFlags(fixture);
            return fixture;
        }

        private void CreateWallFixture()
        {
            if (SourceWallObject == null)
            {
                var defaultOrigin = new Vector3(0f, RequiresFloorFixture ? 2f : 1.4f, 0f);
                WallFixture = CreateFixture("Calibration Wall", new Vector3(6f, 4f, 0.1f),
                    defaultOrigin + Vector3.back * 0.05f, new Color(0.28f, 0.3f, 0.34f));
                WallSurface = new SpatialCalibrationSurface(
                    defaultOrigin, Vector3.forward, Vector3.right, Vector3.up, new Vector2(6f, 4f));
                return;
            }

            var sourceSurface = SpatialWallSurfaceUtility.Analyze(SourceWallObject, WallNormalAxis, FlipWallNormal);
            var canonicalOrigin = new Vector3(
                0f,
                RequiresFloorFixture ? sourceSurface.Size.y * 0.5f : 1.4f,
                0f);
            var alignment = SpatialWallSurfaceUtility.CanonicalAlignment(sourceSurface);
            WallFixture = InstantiateTemporary(SourceWallObject, $"Selected Wall - {SourceWallObject.name}");
            WallFixture.transform.localScale = SourceWallObject.transform.lossyScale;
            foreach (var behaviour in WallFixture.GetComponentsInChildren<MonoBehaviour>(true))
                behaviour.enabled = false;
            WallFixture.transform.SetPositionAndRotation(
                canonicalOrigin + alignment * (SourceWallObject.transform.position - sourceSurface.Origin),
                alignment * SourceWallObject.transform.rotation);
            WallSurface = new SpatialCalibrationSurface(
                canonicalOrigin, Vector3.forward, Vector3.right, Vector3.up, sourceSurface.Size);
        }

        private static void SetFlags(GameObject root)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.hideFlags = HideFlags.DontSave;
        }

        private void MoveToCalibrationScene(GameObject root)
        {
            if (root != null && calibrationScene.IsValid()) SceneManager.MoveGameObjectToScene(root, calibrationScene);
        }

        private static GameObject AddLight()
        {
            var lightObject = new GameObject("Calibration Light") { hideFlags = HideFlags.DontSave };
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.color = new Color(1f, 0.92f, 0.82f);
            lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            return lightObject;
        }
    }

    internal sealed class SpatialCalibrationPreviewStage : PreviewSceneStage
    {
        protected override GUIContent CreateHeaderContent() => new("Spatial Calibration");
        protected override bool OnOpenStage() => base.OnOpenStage();
        protected override void OnCloseStage() => base.OnCloseStage();
    }
}
