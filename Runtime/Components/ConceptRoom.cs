using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [DisallowMultipleComponent]
    public sealed class ConceptRoom : MonoBehaviour
    {
        [SerializeField] private string roomId;
        [SerializeField] private BoxCollider authoringBounds;
        [SerializeField] private List<Collider> floorColliders = new();
        [SerializeField] private List<RoomObservationPoint> observationPoints = new();
        [SerializeField] private List<KeepClearZone> keepClearZones = new();
        [SerializeField] private List<RoomSurface> surfaces = new();
        [Min(0f)] [SerializeField] private float boundaryPadding = 0.05f;

        public string RoomId => roomId;
        public BoxCollider AuthoringBounds => authoringBounds;
        public IReadOnlyList<Collider> FloorColliders => floorColliders;
        public IReadOnlyList<RoomObservationPoint> ObservationPoints => observationPoints;
        public IReadOnlyList<KeepClearZone> KeepClearZones => keepClearZones;
        public IReadOnlyList<RoomSurface> Surfaces => surfaces;
        public float BoundaryPadding => boundaryPadding;

        public bool TrySampleFloor(Vector3 worldPosition, out Vector3 hitPoint, out Vector3 normal)
        {
            hitPoint = default;
            normal = Vector3.up;

            var top = authoringBounds != null
                ? authoringBounds.bounds.max.y + 1f
                : worldPosition.y + 10f;
            var ray = new Ray(new Vector3(worldPosition.x, top, worldPosition.z), Vector3.down);
            var bestDistance = float.PositiveInfinity;
            var found = false;

            foreach (var floor in floorColliders)
            {
                if (floor == null || !floor.enabled) continue;
                if (!floor.Raycast(ray, out var hit, Mathf.Infinity)) continue;
                if (hit.distance >= bestDistance) continue;
                bestDistance = hit.distance;
                hitPoint = hit.point;
                normal = hit.normal;
                found = true;
            }

            return found;
        }

        public void Configure(BoxCollider bounds, IEnumerable<Collider> floors)
        {
            authoringBounds = bounds;
            floorColliders = floors != null ? new List<Collider>(floors) : new List<Collider>();
            RefreshChildren();
        }

        public bool ContainsBounds(Bounds worldBounds, bool allowSurfaceContact = false)
        {
            if (authoringBounds == null) return false;

            var paddedSize = new Vector3(
                Mathf.Max(0.001f, authoringBounds.size.x - (allowSurfaceContact ? 0f : boundaryPadding * 2f)),
                authoringBounds.size.y + 0.002f,
                Mathf.Max(0.001f, authoringBounds.size.z - (allowSurfaceContact ? 0f : boundaryPadding * 2f)));
            var localBounds = new Bounds(authoringBounds.center, paddedSize);
            var min = worldBounds.min;
            var max = worldBounds.max;
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++)
            {
                var corner = new Vector3(x == 0 ? min.x : max.x, y == 0 ? min.y : max.y, z == 0 ? min.z : max.z);
                var local = authoringBounds.transform.InverseTransformPoint(corner);
                if (!localBounds.Contains(local)) return false;
            }

            return true;
        }

        public void RefreshChildren()
        {
            observationPoints = new List<RoomObservationPoint>(GetComponentsInChildren<RoomObservationPoint>(true));
            keepClearZones = new List<KeepClearZone>(GetComponentsInChildren<KeepClearZone>(true));
            surfaces = new List<RoomSurface>(GetComponentsInChildren<RoomSurface>(true));
        }

        public IEnumerable<RoomSurface> GetReviewedSurfaces(RoomSurfaceType type)
        {
            foreach (var surface in surfaces)
                if (surface != null && surface.SurfaceType == type && surface.Reviewed && surface.Supported)
                    yield return surface;
        }

        private void Reset()
        {
            roomId = Guid.NewGuid().ToString("N");
            authoringBounds = GetComponent<BoxCollider>();
            if (authoringBounds == null) authoringBounds = gameObject.AddComponent<BoxCollider>();
            authoringBounds.isTrigger = true;
            RefreshChildren();
        }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(roomId)) roomId = Guid.NewGuid().ToString("N");
            boundaryPadding = Mathf.Max(0f, boundaryPadding);
        }

        private void OnDrawGizmosSelected()
        {
            if (authoringBounds == null) return;
            var oldMatrix = Gizmos.matrix;
            Gizmos.matrix = authoringBounds.transform.localToWorldMatrix;
            Gizmos.color = new Color(0.1f, 0.75f, 1f, 0.7f);
            Gizmos.DrawWireCube(authoringBounds.center, authoringBounds.size);
            Gizmos.matrix = oldMatrix;
        }
    }
}
