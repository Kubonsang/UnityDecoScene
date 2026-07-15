using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [Serializable]
    public sealed class PlacedDecorItem
    {
        public string placementId;
        public string elementId;
        public int instanceIndex;
        public DecorRole role;
        public CompositionRelation relation;
        public DecorAssetDescriptor descriptor;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 scale = Vector3.one;
        public Bounds worldBounds;
        public string surfaceId;
        public ContactEvidence contactEvidence;
        public List<string> surfaceIds = new();
        public List<ContactEvidence> contactEvidenceSet = new();
        public string arrangementId;
        public string affinityGroup;
        public string supportPlacementId;
        public int stackLevel;
        public bool locked;
        [NonSerialized] public GameObject previewObject;
    }

    public sealed class LayoutResult
    {
        public readonly List<PlacedDecorItem> Placements = new();
        public readonly AssetGapReport AssetGaps = new();
    }

    public sealed class PreviewSession
    {
        public string SessionId;
        public RoomCompositionPlan Plan;
        public LayoutRequest Request;
        public RoomAuthoringContext AuthoringContext;
        public GameObject Root;
        public readonly List<PlacedDecorItem> Placements = new();
        public AssetGapReport AssetGaps = new();
        public ValidationReport LastValidation;
        public string ManifestHash;
        public string GeometryProfileHash;
        public string AuthoringSourceHash;
        public string ObstacleGeometryHash;
        public PreviewApprovalSnapshot ApprovalSnapshot = new();
        public readonly List<string> CapturePaths = new();
    }

    internal static class PlacementBoundsUtility
    {
        public static Bounds TransformBounds(Bounds localBounds, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            var scaledCenter = Vector3.Scale(localBounds.center, scale);
            var scaledExtents = Vector3.Scale(localBounds.extents, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            var axisX = rotation * new Vector3(scaledExtents.x, 0f, 0f);
            var axisY = rotation * new Vector3(0f, scaledExtents.y, 0f);
            var axisZ = rotation * new Vector3(0f, 0f, scaledExtents.z);
            var worldExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
            return new Bounds(position + rotation * scaledCenter, worldExtents * 2f);
        }

        public static bool IntersectsWithPadding(Bounds a, Bounds b, float padding)
        {
            a.Expand(padding * 2f);
            b.Expand(padding * 2f);
            return a.Intersects(b);
        }
    }
}
