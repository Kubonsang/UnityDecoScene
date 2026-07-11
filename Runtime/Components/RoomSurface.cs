using System;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [DisallowMultipleComponent]
    public sealed class RoomSurface : MonoBehaviour
    {
        [SerializeField] private string surfaceId;
        [SerializeField] private RoomSurfaceType surfaceType = RoomSurfaceType.Wall;
        [SerializeField] private Collider sourceCollider;
        [SerializeField] private Vector3 localOrigin;
        [SerializeField] private Vector3 localNormal = Vector3.forward;
        [SerializeField] private Vector3 localTangent = Vector3.right;
        [SerializeField] private Vector2 size = Vector2.one;
        [SerializeField] private bool reviewed;
        [SerializeField] private bool supported = true;
        [SerializeField] private string unsupportedReason;

        public string SurfaceId => surfaceId;
        public RoomSurfaceType SurfaceType => surfaceType;
        public Collider SourceCollider => sourceCollider;
        public Vector3 Origin => transform.TransformPoint(localOrigin);
        public Vector3 Normal => transform.TransformDirection(localNormal).normalized;
        public Vector3 Tangent => transform.TransformDirection(localTangent).normalized;
        public Vector3 Bitangent => Vector3.Cross(Normal, Tangent).normalized;
        public Vector2 Size => size;
        public bool Reviewed => reviewed;
        public bool Supported => supported;
        public string UnsupportedReason => unsupportedReason;

        public void SetReviewed(bool value) => reviewed = value;

        public void Configure(string id, RoomSurfaceType type, Collider collider, Vector3 origin, Vector3 normal, Vector3 tangent, Vector2 dimensions, bool isReviewed, bool isSupported = true, string reason = "")
        {
            surfaceId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            surfaceType = type;
            sourceCollider = collider;
            localOrigin = transform.InverseTransformPoint(origin);
            localNormal = transform.InverseTransformDirection(normal.normalized);
            localTangent = transform.InverseTransformDirection(tangent.normalized);
            size = new Vector2(Mathf.Max(0.001f, dimensions.x), Mathf.Max(0.001f, dimensions.y));
            reviewed = isReviewed;
            supported = isSupported;
            unsupportedReason = reason ?? string.Empty;
        }

        public Vector3 Point(float horizontal, float vertical) => Origin + Tangent * horizontal + Bitangent * vertical;

        public bool ContainsProjectedPoint(Vector3 worldPoint, float margin = 0f)
        {
            var delta = worldPoint - Origin;
            return Mathf.Abs(Vector3.Dot(delta, Tangent)) <= size.x * 0.5f + margin &&
                   Mathf.Abs(Vector3.Dot(delta, Bitangent)) <= size.y * 0.5f + margin;
        }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(surfaceId)) surfaceId = Guid.NewGuid().ToString("N");
            if (localNormal.sqrMagnitude < 0.0001f) localNormal = Vector3.forward;
            if (localTangent.sqrMagnitude < 0.0001f) localTangent = Vector3.right;
            localNormal.Normalize();
            localTangent = Vector3.ProjectOnPlane(localTangent, localNormal).normalized;
            size = new Vector2(Mathf.Max(0.001f, size.x), Mathf.Max(0.001f, size.y));
        }

        private void OnDrawGizmosSelected()
        {
            var origin = Origin;
            var tangent = Tangent * size.x * 0.5f;
            var bitangent = Bitangent * size.y * 0.5f;
            Gizmos.color = reviewed && supported ? Color.green : Color.yellow;
            Gizmos.DrawLine(origin - tangent - bitangent, origin + tangent - bitangent);
            Gizmos.DrawLine(origin + tangent - bitangent, origin + tangent + bitangent);
            Gizmos.DrawLine(origin + tangent + bitangent, origin - tangent + bitangent);
            Gizmos.DrawLine(origin - tangent + bitangent, origin - tangent - bitangent);
            Gizmos.DrawRay(origin, Normal * 0.5f);
        }
    }
}
