using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class SpatialCalibrationCaptureService
    {
        private const int CaptureSize = 640;

        public static SpatialCaptureSet Capture(SpatialCalibrationSession session, SpatialCalibrationReport report)
        {
            if (session?.SubjectObject == null) throw new ArgumentNullException(nameof(session));
            report ??= SpatialCalibrationValidator.Validate(session);
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, $"../Library/DungeonDecorator/SpatialCaptures/{session.SessionId}"));
            Directory.CreateDirectory(directory);
            var set = new SpatialCaptureSet { session_id = session.SessionId };
            var bounds = session.CombinedWorldBounds();
            var distance = Mathf.Max(2.5f, bounds.extents.magnitude * 2.2f);
            var center = bounds.center;
            var views = new[]
            {
                new View("front", center + Vector3.forward * distance, Quaternion.LookRotation(Vector3.back, Vector3.up), true),
                new View("side", center + Vector3.right * distance, Quaternion.LookRotation(Vector3.left, Vector3.up), true),
                new View("top", center + Vector3.up * distance, Quaternion.LookRotation(Vector3.down, Vector3.forward), true),
                new View("contact", ContactCameraPosition(report, center, distance), Quaternion.identity, false)
            };

            foreach (var view in views)
            {
                var rotation = view.Name == "contact" ? Quaternion.LookRotation((center - view.Position).normalized, Vector3.up) : view.Rotation;
                set.raw_paths.Add(Render(directory, view.Name, view.Position, rotation, view.Orthographic, bounds, session, report, false));
                set.evidence_paths.Add(Render(directory, view.Name + "-evidence", view.Position, rotation, view.Orthographic, bounds, session, report, true));
            }

            set.report_path = Path.Combine(directory, "technical-report.json");
            File.WriteAllText(set.report_path, JsonUtility.ToJson(report, true));
            set.capture_set_hash = HashFiles(set.raw_paths.Concat(set.evidence_paths).Append(set.report_path));
            session.CaptureSet = set;
            return set;
        }

        private static Vector3 ContactCameraPosition(SpatialCalibrationReport report, Vector3 fallback, float distance)
        {
            var center = report?.contacts != null && report.contacts.Count > 0
                ? report.contacts.Select(item => SpatialContractArrays.Vector(item.contact_point)).Aggregate(Vector3.zero, (sum, value) => sum + value) / report.contacts.Count
                : fallback;
            return center + new Vector3(0.8f, 0.65f, 1f).normalized * Mathf.Max(1.2f, distance * 0.45f);
        }

        private static string Render(string directory, string name, Vector3 position, Quaternion rotation, bool orthographic, Bounds bounds, SpatialCalibrationSession session, SpatialCalibrationReport report, bool evidence)
        {
            var cameraObject = new GameObject("Spatial Calibration Capture Camera") { hideFlags = HideFlags.HideAndDontSave };
            var calibrationScene = session.SubjectObject.scene;
            if (calibrationScene.IsValid()) SceneManager.MoveGameObjectToScene(cameraObject, calibrationScene);
            var camera = cameraObject.AddComponent<Camera>();
            if (calibrationScene.IsValid()) camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(calibrationScene);
            camera.transform.SetPositionAndRotation(position, rotation);
            camera.orthographic = orthographic;
            camera.orthographicSize = Mathf.Max(0.5f, Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.2f);
            camera.fieldOfView = 42f;
            camera.nearClipPlane = 0.02f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.055f, 0.06f, 0.07f);
            camera.allowHDR = true;

            var renderTexture = RenderTexture.GetTemporary(CaptureSize, CaptureSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var texture = new Texture2D(CaptureSize, CaptureSize, TextureFormat.RGB24, false, false);
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, CaptureSize, CaptureSize), 0, 0, false);
                if (evidence) DrawEvidence(texture, camera, session, report);
                texture.Apply(false, false);
                var path = Path.Combine(directory, name + ".png");
                File.WriteAllBytes(path, texture.EncodeToPNG());
                return path;
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        private static void DrawEvidence(Texture2D texture, Camera camera, SpatialCalibrationSession session, SpatialCalibrationReport report)
        {
            var subject = session.SubjectObject.transform;
            if (session.Template is SpatialCalibrationTemplate.WallMounted or SpatialCalibrationTemplate.WallBackedFloorSupported)
                DrawSurfaceEvidence(texture, camera, session.WallSurface);
            var boxColor = new Color(0.15f, 1f, 0.35f);
            foreach (var proxy in session.Geometry.collisionProxies)
            {
                var center = subject.TransformPoint(proxy.localCenter);
                var rotation = subject.rotation * proxy.localRotation;
                var size = Vector3.Scale(proxy.size, subject.lossyScale);
                var corners = BoxCorners(center, rotation, size);
                var edges = new[] { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
                for (var i = 0; i < edges.Length; i += 2)
                    DrawWorldLine(texture, camera, corners[edges[i]], corners[edges[i + 1]], boxColor, 2);
            }

            if (report?.contacts == null) return;
            foreach (var contact in report.contacts)
            {
                var point = SpatialContractArrays.Vector(contact.contact_point);
                var contactColor = contact.valid ? new Color(1f, 0.78f, 0.12f) : new Color(1f, 0.2f, 0.12f);
                DrawWorldLine(texture, camera, point - Vector3.right * 0.07f, point + Vector3.right * 0.07f, contactColor, 2);
                DrawWorldLine(texture, camera, point - Vector3.up * 0.07f, point + Vector3.up * 0.07f, contactColor, 2);
                var rule = session.Rules.FirstOrDefault(value => value.id == contact.rule_id);
                if (rule == null) continue;
                var normal = subject.TransformDirection(session.Frame(rule.frame_id).localNormal).normalized;
                DrawWorldArrow(texture, camera, point, point + normal * 0.32f, new Color(0.2f, 0.65f, 1f));
            }
        }

        private static void DrawSurfaceEvidence(
            Texture2D texture,
            Camera camera,
            SpatialCalibrationSurface surface)
        {
            var horizontal = surface.Tangent * surface.Size.x * 0.5f;
            var vertical = surface.Bitangent * surface.Size.y * 0.5f;
            var corners = new[]
            {
                surface.Origin - horizontal - vertical,
                surface.Origin + horizontal - vertical,
                surface.Origin + horizontal + vertical,
                surface.Origin - horizontal + vertical
            };
            var color = new Color(0.72f, 0.28f, 1f);
            for (var index = 0; index < corners.Length; index++)
                DrawWorldLine(texture, camera, corners[index], corners[(index + 1) % corners.Length], color, 2);
            DrawWorldArrow(
                texture,
                camera,
                surface.Origin,
                surface.Origin + surface.Normal * Mathf.Clamp(Mathf.Min(surface.Size.x, surface.Size.y) * 0.2f, 0.2f, 0.8f),
                color);
        }

        private static Vector3[] BoxCorners(Vector3 center, Quaternion rotation, Vector3 size)
        {
            var extent = size * 0.5f;
            var corners = new[]
            {
                new Vector3(-extent.x,-extent.y,-extent.z), new Vector3(extent.x,-extent.y,-extent.z), new Vector3(extent.x,extent.y,-extent.z), new Vector3(-extent.x,extent.y,-extent.z),
                new Vector3(-extent.x,-extent.y,extent.z), new Vector3(extent.x,-extent.y,extent.z), new Vector3(extent.x,extent.y,extent.z), new Vector3(-extent.x,extent.y,extent.z)
            };
            for (var i = 0; i < corners.Length; i++) corners[i] = center + rotation * corners[i];
            return corners;
        }

        private static void DrawWorldArrow(Texture2D texture, Camera camera, Vector3 start, Vector3 end, Color color)
        {
            var a = camera.WorldToScreenPoint(start);
            var b = camera.WorldToScreenPoint(end);
            if (a.z <= 0f || b.z <= 0f) return;
            DrawPixelLine(texture, a, b, color, 2);
            var direction = ((Vector2)b - (Vector2)a).normalized;
            var side = new Vector2(-direction.y, direction.x);
            var tip = (Vector2)b;
            DrawPixelLine(texture, tip, tip - direction * 12f + side * 6f, color, 2);
            DrawPixelLine(texture, tip, tip - direction * 12f - side * 6f, color, 2);
        }

        private static void DrawWorldLine(Texture2D texture, Camera camera, Vector3 start, Vector3 end, Color color, int thickness)
        {
            var a = camera.WorldToScreenPoint(start);
            var b = camera.WorldToScreenPoint(end);
            if (a.z <= 0f || b.z <= 0f) return;
            DrawPixelLine(texture, a, b, color, thickness);
        }

        private static void DrawPixelLine(Texture2D texture, Vector2 start, Vector2 end, Color color, int thickness)
        {
            var steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(Mathf.Abs(end.x - start.x), Mathf.Abs(end.y - start.y))));
            for (var step = 0; step <= steps; step++)
            {
                var point = Vector2.Lerp(start, end, step / (float)steps);
                var x = Mathf.RoundToInt(point.x);
                var y = Mathf.RoundToInt(point.y);
                for (var dx = -thickness; dx <= thickness; dx++)
                for (var dy = -thickness; dy <= thickness; dy++)
                    if (x + dx >= 0 && x + dx < texture.width && y + dy >= 0 && y + dy < texture.height)
                        texture.SetPixel(x + dx, y + dy, color);
            }
        }

        private static string HashFiles(IEnumerable<string> paths)
        {
            using var sha = SHA256.Create();
            foreach (var path in paths.OrderBy(value => value, StringComparer.Ordinal))
            {
                var bytes = File.ReadAllBytes(path);
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return string.Concat(sha.Hash.Select(value => value.ToString("x2")));
        }

        private readonly struct View
        {
            public readonly string Name;
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly bool Orthographic;
            public View(string name, Vector3 position, Quaternion rotation, bool orthographic) { Name = name; Position = position; Rotation = rotation; Orthographic = orthographic; }
        }
    }

    [ExecuteAlways]
    internal sealed class SpatialCaptureOverlayRenderer : MonoBehaviour
    {
        private readonly List<Line> lines = new();
        private Material material;
        private bool enabledOverlay;

        public void Configure(SpatialCalibrationSession session, SpatialCalibrationReport report, bool show)
        {
            enabledOverlay = show;
            if (!show || session?.SubjectObject == null) return;
            foreach (var proxy in session.Geometry.collisionProxies)
            {
                var center = session.SubjectObject.transform.TransformPoint(proxy.localCenter);
                var rotation = session.SubjectObject.transform.rotation * proxy.localRotation;
                AddBox(center, rotation, Vector3.Scale(proxy.size, session.SubjectObject.transform.lossyScale), new Color(0.2f, 0.95f, 0.45f));
            }
            if (report?.contacts == null) return;
            foreach (var contact in report.contacts)
            {
                var point = SpatialContractArrays.Vector(contact.contact_point);
                var color = contact.valid ? new Color(0.2f, 0.95f, 0.45f) : new Color(1f, 0.35f, 0.2f);
                lines.Add(new Line(point - Vector3.right * 0.06f, point + Vector3.right * 0.06f, color));
                lines.Add(new Line(point - Vector3.up * 0.06f, point + Vector3.up * 0.06f, color));
            }
        }

        private void OnPostRender()
        {
            if (!enabledOverlay || lines.Count == 0) return;
            material ??= new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
            material.SetPass(0);
            GL.PushMatrix();
            GL.Begin(GL.LINES);
            foreach (var line in lines)
            {
                GL.Color(line.Color);
                GL.Vertex(line.A);
                GL.Vertex(line.B);
            }
            GL.End();
            GL.PopMatrix();
        }

        private void OnDestroy()
        {
            if (material != null) DestroyImmediate(material);
        }

        private void AddBox(Vector3 center, Quaternion rotation, Vector3 size, Color color)
        {
            var e = size * 0.5f;
            var corners = new[]
            {
                new Vector3(-e.x,-e.y,-e.z), new Vector3(e.x,-e.y,-e.z), new Vector3(e.x,e.y,-e.z), new Vector3(-e.x,e.y,-e.z),
                new Vector3(-e.x,-e.y,e.z), new Vector3(e.x,-e.y,e.z), new Vector3(e.x,e.y,e.z), new Vector3(-e.x,e.y,e.z)
            };
            for (var i = 0; i < corners.Length; i++) corners[i] = center + rotation * corners[i];
            var edges = new[] { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
            for (var i = 0; i < edges.Length; i += 2) lines.Add(new Line(corners[edges[i]], corners[edges[i + 1]], color));
        }

        private readonly struct Line
        {
            public readonly Vector3 A;
            public readonly Vector3 B;
            public readonly Color Color;
            public Line(Vector3 a, Vector3 b, Color color) { A = a; B = b; Color = color; }
        }
    }
}
