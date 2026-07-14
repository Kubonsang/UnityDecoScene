using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    internal static class RoomReviewTestCache
    {
        public static void Seed(PreviewSession session)
        {
            var inputs = RoomReviewSnapshotService.Capture(session);
            var inputHash = RoomReviewHashUtility.ComputeInputHash(inputs);
            var runId = inputHash.Substring(0, 20);
            var targetDirectory = Path.GetDirectoryName(RoomReviewWorkflow.StatePathFor(inputs.targetId))
                                  ?? throw new InvalidOperationException("Review target directory is unavailable.");
            var captureDirectory = Path.Combine(targetDirectory, "runs", runId, "captures");
            Directory.CreateDirectory(captureDirectory);
            var ids = new[]
            {
                RoomCaptureViewIds.Top,
                RoomCaptureViewIds.PrimaryObservation,
                RoomCaptureViewIds.CornerA,
                RoomCaptureViewIds.CornerB
            };
            var entries = new List<RoomCaptureEntry>();
            foreach (var id in ids)
            {
                var content = $"test-capture|{inputHash}|{id}";
                var path = Path.Combine(captureDirectory, id + ".png");
                File.WriteAllText(path, content, new UTF8Encoding(false));
                entries.Add(new RoomCaptureEntry(id, path, RoomReviewHashUtility.Sha256(content)));
            }
            var manifest = new Manifest
            {
                version = 1,
                inputKey = inputHash,
                captureSetHash = RoomCaptureService.ComputeCaptureSetHash(entries),
                entries = entries.Select(value => new ManifestEntry
                {
                    viewId = value.viewId,
                    fileName = Path.GetFileName(value.path),
                    contentHash = value.contentHash
                }).ToList()
            };
            File.WriteAllText(Path.Combine(captureDirectory, "capture-manifest.json"), JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
        }

        [Serializable]
        private sealed class Manifest
        {
            public int version;
            public string inputKey;
            public string captureSetHash;
            public List<ManifestEntry> entries = new();
        }

        [Serializable]
        private sealed class ManifestEntry
        {
            public string viewId;
            public string fileName;
            public string contentHash;
        }
    }
}
