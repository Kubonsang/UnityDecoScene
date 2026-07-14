using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class RoomAuthoringErrorCodes
    {
        public const string GeometryOverlap = "ROOM_GEOMETRY_OVERLAP";
        public const string GeometryUnreviewed = "ROOM_GEOMETRY_UNREVIEWED";
        public const string SourceStale = "AUTHORING_SOURCE_STALE";
        public const string ApplySourceChanged = "APPLY_SOURCE_CHANGED";
    }

    public enum RoomObstaclePolicy
    {
        Solid,
        ContactSurface,
        Ignore
    }

    /// <summary>
    /// Host-project bridge for building room authoring data without introducing a package dependency
    /// on that host. Apply remains a Unity-side, user-authorized operation and is intentionally absent.
    /// </summary>
    public interface IRoomAuthoringAdapter
    {
        string AdapterId { get; }
        string AdapterVersion { get; }
        RoomAuthoringContext CreateContext(RoomCompositionPlan plan);
        string GetCurrentSourceHash(RoomAuthoringContext context);
    }

    [Serializable]
    public sealed class RoomSurfaceMetadata
    {
        public string surfaceId;
        public string cellId;
        public string side;
        public List<RoomAuthoringMetadataEntry> entries = new();

        public string GetValue(string key)
        {
            var entry = entries?.FirstOrDefault(value => value != null && string.Equals(value.key, key, StringComparison.Ordinal));
            return entry?.value;
        }
    }

    [Serializable]
    public sealed class RoomAuthoringMetadataEntry
    {
        public string key;
        public string value;
    }

    [Serializable]
    public sealed class RoomObstacleProxy
    {
        public string stableId;
        public string label;
        public RoomObstaclePolicy policy = RoomObstaclePolicy.Solid;
        public bool reviewed;
        public string contactSurfaceId;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 scale = Vector3.one;
        public List<OrientedBoxProxy> collisionProxies = new();
        [NonSerialized] public GameObject sourceObject;

        public bool IsUsable => policy == RoomObstaclePolicy.Ignore ||
                                reviewed && collisionProxies != null && collisionProxies.Any(IsValidProxy);

        private static bool IsValidProxy(OrientedBoxProxy proxy) => proxy != null &&
            proxy.size.x > 0f && proxy.size.y > 0f && proxy.size.z > 0f;
    }

    [Serializable]
    public sealed class RoomAuthoringContext
    {
        public string adapterId;
        public string adapterVersion;
        public string sourceId;
        public string sourceHash;
        public List<RoomSurface> surfaces = new();
        public List<RoomSurfaceMetadata> surfaceMetadata = new();
        public List<RoomObstacleProxy> obstacles = new();
        [NonSerialized] public IRoomAuthoringAdapter adapter;

        public string ComputeObstacleHash()
        {
            var text = new StringBuilder("room-obstacles@1");
            foreach (var obstacle in (obstacles ?? new List<RoomObstacleProxy>())
                         .Where(value => value != null)
                         .OrderBy(value => value.stableId ?? string.Empty, StringComparer.Ordinal))
            {
                text.Append('|').Append(obstacle.stableId ?? string.Empty)
                    .Append(':').Append(obstacle.policy)
                    .Append(':').Append(obstacle.reviewed ? '1' : '0')
                    .Append(':').Append(obstacle.contactSurfaceId ?? string.Empty)
                    .Append(':').Append(Vector(obstacle.position))
                    .Append(':').Append(QuaternionValue(obstacle.rotation))
                    .Append(':').Append(Vector(obstacle.scale));
                foreach (var proxy in (obstacle.collisionProxies ?? new List<OrientedBoxProxy>())
                             .Where(value => value != null)
                             .OrderBy(value => value.proxyId ?? string.Empty, StringComparer.Ordinal))
                {
                    text.Append('/').Append(proxy.proxyId ?? string.Empty)
                        .Append('@').Append(Vector(proxy.localCenter))
                        .Append('@').Append(Vector(proxy.size))
                        .Append('@').Append(QuaternionValue(proxy.localRotation));
                }
            }
            return Hash128.Compute(text.ToString()).ToString();
        }

        public bool IsSourceCurrent(out string currentHash)
        {
            currentHash = sourceHash ?? string.Empty;
            if (adapter == null) return true;
            try
            {
                currentHash = adapter.GetCurrentSourceHash(this) ?? string.Empty;
                return !string.IsNullOrWhiteSpace(sourceHash) &&
                       string.Equals(sourceHash, currentHash, StringComparison.Ordinal);
            }
            catch
            {
                currentHash = string.Empty;
                return false;
            }
        }

        private static string Vector(Vector3 value) => string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", value.x, value.y, value.z);

        private static string QuaternionValue(Quaternion value)
        {
            value.Normalize();
            if (value.w < 0f) value = new Quaternion(-value.x, -value.y, -value.z, -value.w);
            return string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R},{3:R}", value.x, value.y, value.z, value.w);
        }
    }

    public sealed class LayoutRequest
    {
        public RoomCompositionPlan Plan { get; }
        public IReadOnlyList<PlacedDecorItem> LockedPlacements { get; }
        public RoomAuthoringContext AuthoringContext { get; }

        public LayoutRequest(RoomCompositionPlan plan, IReadOnlyList<PlacedDecorItem> lockedPlacements = null, RoomAuthoringContext authoringContext = null)
        {
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            LockedPlacements = lockedPlacements;
            AuthoringContext = authoringContext;
        }
    }

    [Serializable]
    public sealed class PreviewApprovalSnapshot
    {
        public string sourceHash;
        public string obstacleHash;
        public string placementHash;
        public string technicalReportHash;
        public string captureSetHash;
        public bool humanApproved;
    }
}
