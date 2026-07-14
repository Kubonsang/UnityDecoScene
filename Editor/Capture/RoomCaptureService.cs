using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class RoomCaptureService
    {
        private const int CaptureSize = 768;
        private const int ManifestVersion = 1;
        private const string ManifestFileName = "capture-manifest.json";

        private static readonly string[] FixedViewIds =
        {
            RoomCaptureViewIds.Top,
            RoomCaptureViewIds.PrimaryObservation,
            RoomCaptureViewIds.CornerA,
            RoomCaptureViewIds.CornerB
        };

        public static IReadOnlyList<string> CaptureAll(PreviewSession session)
        {
            ValidateSession(session);
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
            var floorFocus = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.3f, bounds.center.z);
            session.CapturePaths.Add(Render(directory, "corner-a", cornerA, Quaternion.LookRotation((floorFocus - cornerA).normalized, Vector3.up), false, 5f, 58f));
            session.CapturePaths.Add(Render(directory, "corner-b", cornerB, Quaternion.LookRotation((floorFocus - cornerB).normalized, Vector3.up), false, 5f, 58f));
            return session.CapturePaths;
        }

        /// <summary>
        /// Captures the four stable room-review views and writes a cache manifest. This overload
        /// deliberately renders a fresh set; use the input-key overload for verified cache reuse.
        /// </summary>
        public static RoomCaptureSet CaptureSet(PreviewSession session, string outputDirectory)
        {
            ValidateSession(session);
            var inputKey = ComputeDefaultInputKey(session);
            return CaptureSetCore(session, outputDirectory, inputKey, false);
        }

        /// <summary>
        /// Reuses a prior set only when its manifest has the same input key and every PNG still
        /// matches its recorded SHA-256. A cache hit performs no rendering.
        /// </summary>
        public static RoomCaptureSet CaptureSet(PreviewSession session, string outputDirectory, string inputKey)
        {
            ValidateSession(session);
            if (string.IsNullOrWhiteSpace(inputKey)) throw new ArgumentException("A non-empty capture input key is required.", nameof(inputKey));
            return CaptureSetCore(session, outputDirectory, inputKey, true);
        }

        public static string ComputeCaptureSetHash(IEnumerable<RoomCaptureEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var canonical = new StringBuilder();
            foreach (var entry in entries.Where(value => value != null).OrderBy(value => value.viewId, StringComparer.Ordinal))
                canonical.Append(entry.viewId ?? string.Empty).Append('\n').Append(entry.contentHash ?? string.Empty).Append('\n');
            return Sha256(Encoding.UTF8.GetBytes(canonical.ToString()));
        }

        private static RoomCaptureSet CaptureSetCore(PreviewSession session, string outputDirectory, string inputKey, bool allowCache)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
            var directory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(directory);
            var views = BuildFixedViews(session.Plan.Room);

            if (allowCache && TryReadCachedSet(directory, inputKey, views, out var cached))
            {
                PublishPaths(session, cached);
                return cached;
            }

            var result = new RoomCaptureSet { inputKey = inputKey, renderedCount = views.Count, reusedFromCache = false };
            foreach (var view in views)
            {
                var path = Render(directory, view.Id, view.Position, view.Rotation, view.Orthographic, view.OrthographicSize, view.FieldOfView);
                result.entries.Add(new RoomCaptureEntry(view.Id, path, HashFile(path)));
            }
            result.captureSetHash = ComputeCaptureSetHash(result.entries);
            WriteManifest(directory, result);
            PublishPaths(session, result);
            return result;
        }

        private static IReadOnlyList<CaptureView> BuildFixedViews(ConceptRoom room)
        {
            var bounds = room.AuthoringBounds.bounds;
            var maximumSpan = Mathf.Max(bounds.size.x, bounds.size.z);
            var topPosition = bounds.center + Vector3.up * (bounds.extents.y + maximumSpan + 2f);
            var views = new List<CaptureView>(4)
            {
                new(RoomCaptureViewIds.Top, topPosition, Quaternion.LookRotation(Vector3.down, Vector3.forward), true, Mathf.Max(bounds.extents.x, bounds.extents.z) * 1.15f, 60f)
            };

            var primary = room.ObservationPoints
                .Where(point => point != null && point.Primary)
                .OrderBy(point => point.Label ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(point => HierarchyPath(point.transform), StringComparer.Ordinal)
                .FirstOrDefault();
            if (primary != null)
            {
                views.Add(new CaptureView(RoomCaptureViewIds.PrimaryObservation, primary.transform.position, primary.transform.rotation, false, 5f, primary.FieldOfView));
            }
            else
            {
                var fallbackPosition = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.65f, bounds.min.z + bounds.size.z * 0.08f);
                var fallbackFocus = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.3f, bounds.center.z);
                views.Add(new CaptureView(RoomCaptureViewIds.PrimaryObservation, fallbackPosition, LookAt(fallbackPosition, fallbackFocus), false, 5f, 60f));
            }

            var height = bounds.min.y + bounds.size.y * 0.65f;
            var insetX = bounds.size.x * 0.08f;
            var insetZ = bounds.size.z * 0.08f;
            var cornerA = new Vector3(bounds.min.x + insetX, height, bounds.min.z + insetZ);
            var cornerB = new Vector3(bounds.max.x - insetX, height, bounds.max.z - insetZ);
            var floorFocus = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.3f, bounds.center.z);
            views.Add(new CaptureView(RoomCaptureViewIds.CornerA, cornerA, LookAt(cornerA, floorFocus), false, 5f, 58f));
            views.Add(new CaptureView(RoomCaptureViewIds.CornerB, cornerB, LookAt(cornerB, floorFocus), false, 5f, 58f));
            return views;
        }

        private static bool TryReadCachedSet(string directory, string inputKey, IReadOnlyList<CaptureView> views, out RoomCaptureSet set)
        {
            set = null;
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath)) return false;

            CaptureManifest manifest;
            try { manifest = JsonUtility.FromJson<CaptureManifest>(File.ReadAllText(manifestPath, Encoding.UTF8)); }
            catch { return false; }
            if (manifest == null || manifest.version != ManifestVersion || !string.Equals(manifest.inputKey, inputKey, StringComparison.Ordinal)) return false;
            if (manifest.entries == null || manifest.entries.Count != views.Count) return false;

            var expected = views.ToDictionary(view => view.Id, StringComparer.Ordinal);
            var entries = new List<RoomCaptureEntry>(views.Count);
            foreach (var cached in manifest.entries)
            {
                if (cached == null || string.IsNullOrWhiteSpace(cached.viewId) || !expected.ContainsKey(cached.viewId)) return false;
                var expectedFileName = cached.viewId + ".png";
                if (!string.Equals(cached.fileName, expectedFileName, StringComparison.Ordinal)) return false;
                var path = Path.GetFullPath(Path.Combine(directory, cached.fileName));
                if (!File.Exists(path)) return false;
                var actualHash = HashFile(path);
                if (!string.Equals(actualHash, cached.contentHash, StringComparison.Ordinal)) return false;
                entries.Add(new RoomCaptureEntry(cached.viewId, path, actualHash));
            }

            if (entries.Select(entry => entry.viewId).Distinct(StringComparer.Ordinal).Count() != FixedViewIds.Length) return false;
            var captureSetHash = ComputeCaptureSetHash(entries);
            if (!string.Equals(captureSetHash, manifest.captureSetHash, StringComparison.Ordinal)) return false;
            set = new RoomCaptureSet
            {
                inputKey = inputKey,
                captureSetHash = captureSetHash,
                entries = entries.OrderBy(entry => Array.IndexOf(FixedViewIds, entry.viewId)).ToList(),
                renderedCount = 0,
                reusedFromCache = true
            };
            return true;
        }

        private static void WriteManifest(string directory, RoomCaptureSet set)
        {
            var manifest = new CaptureManifest
            {
                version = ManifestVersion,
                inputKey = set.inputKey,
                captureSetHash = set.captureSetHash,
                entries = set.entries.Select(entry => new CaptureManifestEntry
                {
                    viewId = entry.viewId,
                    fileName = Path.GetFileName(entry.path),
                    contentHash = entry.contentHash
                }).ToList()
            };
            File.WriteAllText(Path.Combine(directory, ManifestFileName), JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
        }

        private static void PublishPaths(PreviewSession session, RoomCaptureSet set)
        {
            session.CapturePaths.Clear();
            session.CapturePaths.AddRange(set.entries.Select(entry => entry.path));
        }

        private static string ComputeDefaultInputKey(PreviewSession session)
        {
            var canonical = new StringBuilder();
            canonical.Append(session.ManifestHash ?? string.Empty).Append('|')
                .Append(session.GeometryProfileHash ?? string.Empty).Append('|')
                .Append(session.Plan.Seed).Append('|');
            foreach (var placement in session.Placements.Where(value => value != null).OrderBy(value => value.placementId, StringComparer.Ordinal))
            {
                canonical.Append(placement.placementId).Append(':')
                    .Append(placement.descriptor != null ? placement.descriptor.AssetId : string.Empty).Append(':');
                Append(canonical, placement.position);
                Append(canonical, Canonical(placement.rotation));
                Append(canonical, placement.scale);
                canonical.Append('|');
            }
            foreach (var view in BuildFixedViews(session.Plan.Room))
            {
                canonical.Append(view.Id).Append(':');
                Append(canonical, view.Position);
                Append(canonical, Canonical(view.Rotation));
                canonical.Append(':').Append(Quantize(view.FieldOfView)).Append(':').Append(Quantize(view.OrthographicSize)).Append('|');
            }
            return Sha256(Encoding.UTF8.GetBytes(canonical.ToString()));
        }

        private static void Append(StringBuilder builder, Vector3 value) => builder.Append(Quantize(value.x)).Append(',').Append(Quantize(value.y)).Append(',').Append(Quantize(value.z)).Append(';');
        private static void Append(StringBuilder builder, Quaternion value) => builder.Append(Quantize(value.x)).Append(',').Append(Quantize(value.y)).Append(',').Append(Quantize(value.z)).Append(',').Append(Quantize(value.w)).Append(';');
        private static int Quantize(float value) => Mathf.RoundToInt(value * 10000f);

        private static Quaternion Canonical(Quaternion value)
        {
            value = value.normalized;
            if (value.w < 0f || (Mathf.Approximately(value.w, 0f) && (value.x < 0f || (Mathf.Approximately(value.x, 0f) && (value.y < 0f || (Mathf.Approximately(value.y, 0f) && value.z < 0f))))))
                value = new Quaternion(-value.x, -value.y, -value.z, -value.w);
            return value;
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static string Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(bytes));
        }

        private static string Hex(IEnumerable<byte> bytes) => string.Concat(bytes.Select(value => value.ToString("x2")));

        private static Quaternion LookAt(Vector3 position, Vector3 target)
        {
            var direction = target - position;
            return direction.sqrMagnitude > 0.000001f ? Quaternion.LookRotation(direction.normalized, Vector3.up) : Quaternion.identity;
        }

        private static string HierarchyPath(Transform value)
        {
            if (value == null) return string.Empty;
            var names = new Stack<string>();
            while (value != null)
            {
                names.Push(value.name);
                value = value.parent;
            }
            return string.Join("/", names);
        }

        private static void ValidateSession(PreviewSession session)
        {
            if (session?.Plan?.Room == null || session.Plan.Room.AuthoringBounds == null)
                throw new InvalidOperationException("An active room preview with authoring bounds is required.");
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

        [Serializable]
        private sealed class CaptureManifest
        {
            public int version;
            public string inputKey;
            public string captureSetHash;
            public List<CaptureManifestEntry> entries = new();
        }

        [Serializable]
        private sealed class CaptureManifestEntry
        {
            public string viewId;
            public string fileName;
            public string contentHash;
        }

        private readonly struct CaptureView
        {
            public readonly string Id;
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly bool Orthographic;
            public readonly float OrthographicSize;
            public readonly float FieldOfView;

            public CaptureView(string id, Vector3 position, Quaternion rotation, bool orthographic, float orthographicSize, float fieldOfView)
            {
                Id = id;
                Position = position;
                Rotation = rotation;
                Orthographic = orthographic;
                OrthographicSize = orthographicSize;
                FieldOfView = fieldOfView;
            }
        }
    }
}
