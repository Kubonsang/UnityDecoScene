using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class RoomCaptureService
    {
        private const int CaptureSize = 768;

        public static IReadOnlyList<string> CaptureAll(PreviewSession session)
        {
            if (session?.Plan?.Room == null) throw new InvalidOperationException("An active room preview is required.");
            var room = session.Plan.Room;
            var bounds = room.AuthoringBounds.bounds;
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, $"../Library/DungeonDecorator/Captures/{session.SessionId}"));
            Directory.CreateDirectory(directory);
            session.CapturePaths.Clear();

            var topPosition = bounds.center + Vector3.up * (bounds.extents.y + Mathf.Max(bounds.size.x, bounds.size.z) + 2f);
            session.CapturePaths.Add(Render(directory, "top", topPosition, Quaternion.LookRotation(Vector3.down, Vector3.forward), true, Mathf.Max(bounds.extents.x, bounds.extents.z) * 1.15f, 60f));

            var observationIndex = 0;
            foreach (var observation in room.ObservationPoints)
            {
                if (observation == null) continue;
                session.CapturePaths.Add(Render(directory, $"observation-{observationIndex++}", observation.transform.position, observation.transform.rotation, false, 5f, observation.FieldOfView));
            }

            var height = bounds.min.y + bounds.size.y * 0.65f;
            var insetX = bounds.size.x * 0.08f;
            var insetZ = bounds.size.z * 0.08f;
            var cornerA = new Vector3(bounds.min.x + insetX, height, bounds.min.z + insetZ);
            var cornerB = new Vector3(bounds.max.x - insetX, height, bounds.max.z - insetZ);
            session.CapturePaths.Add(Render(directory, "corner-a", cornerA, Quaternion.LookRotation((bounds.center - cornerA).normalized, Vector3.up), false, 5f, 65f));
            session.CapturePaths.Add(Render(directory, "corner-b", cornerB, Quaternion.LookRotation((bounds.center - cornerB).normalized, Vector3.up), false, 5f, 65f));
            return session.CapturePaths;
        }

        private static string Render(string directory, string name, Vector3 position, Quaternion rotation, bool orthographic, float orthographicSize, float fieldOfView)
        {
            var cameraObject = new GameObject("Concept Room Capture Camera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = cameraObject.AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(position, rotation);
            camera.orthographic = orthographic;
            camera.orthographicSize = Mathf.Max(0.1f, orthographicSize);
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.008f, 0.009f, 0.012f, 1f);
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
                texture.Apply(false, false);
                var path = Path.Combine(directory, $"{name}.png");
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
    }
}
