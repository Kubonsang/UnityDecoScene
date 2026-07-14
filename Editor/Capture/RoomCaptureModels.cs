using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class RoomCaptureViewIds
    {
        public const string Top = "top";
        public const string PrimaryObservation = "primary-observation";
        public const string CornerA = "corner-a";
        public const string CornerB = "corner-b";
        public const string WallContactSidePrefix = "wall-contact-side-";

        public static bool IsWallContactSide(string viewId) =>
            !string.IsNullOrWhiteSpace(viewId) &&
            viewId.StartsWith(WallContactSidePrefix, StringComparison.Ordinal);
    }

    [Serializable]
    public sealed class RoomCaptureEntry
    {
        public string viewId;
        public string path;
        public string contentHash;

        public RoomCaptureEntry() { }

        public RoomCaptureEntry(string id, string filePath, string hash)
        {
            viewId = id;
            path = filePath;
            contentHash = hash;
        }
    }

    [Serializable]
    public sealed class RoomCaptureSet
    {
        public string inputKey;
        public string captureSetHash;
        public List<RoomCaptureEntry> entries = new();
        public int renderedCount;
        public bool reusedFromCache;

        public IReadOnlyList<string> Paths => entries.Select(entry => entry.path).ToArray();
    }
}
