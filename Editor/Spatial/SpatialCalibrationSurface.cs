using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public enum SpatialWallNormalAxis
    {
        LocalForward,
        LocalRight,
        LocalUp
    }

    public readonly struct SpatialCalibrationSurface
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Normal;
        public readonly Vector3 Tangent;
        public readonly Vector3 Bitangent;
        public readonly Vector2 Size;

        public SpatialCalibrationSurface(
            Vector3 origin,
            Vector3 normal,
            Vector3 tangent,
            Vector3 bitangent,
            Vector2 size)
        {
            Origin = origin;
            Normal = normal.normalized;
            Tangent = tangent.normalized;
            Bitangent = bitangent.normalized;
            Size = size;
        }
    }

    public static class SpatialWallSurfaceUtility
    {
        private const float PlanarNormalAlignment = 0.98f;
        private const float PlaneDepthBucket = 0.01f;

        public static SpatialCalibrationSurface Analyze(
            GameObject wall,
            SpatialWallNormalAxis normalAxis,
            bool flipNormal)
        {
            if (wall == null) throw new ArgumentNullException(nameof(wall));
            var normal = normalAxis switch
            {
                SpatialWallNormalAxis.LocalRight => wall.transform.right,
                SpatialWallNormalAxis.LocalUp => wall.transform.up,
                _ => wall.transform.forward
            };
            if (flipNormal) normal = -normal;
            normal.Normalize();

            var upCandidate = normalAxis == SpatialWallNormalAxis.LocalUp
                ? wall.transform.forward
                : wall.transform.up;
            var bitangent = Vector3.ProjectOnPlane(upCandidate, normal).normalized;
            if (bitangent.sqrMagnitude < 0.000001f)
                bitangent = Vector3.ProjectOnPlane(Vector3.up, normal).normalized;
            if (bitangent.sqrMagnitude < 0.000001f)
                bitangent = Vector3.ProjectOnPlane(Vector3.forward, normal).normalized;
            var tangent = Vector3.Cross(bitangent, normal).normalized;
            var points = WorldGeometryPoints(wall);
            ProjectedRange(points, normal, out _, out var maximumNormal);
            if (TryDominantPlanarDepth(wall, normal, out var dominantNormal))
                maximumNormal = dominantNormal;
            ProjectedRange(points, tangent, out var minimumTangent, out var maximumTangent);
            ProjectedRange(points, bitangent, out var minimumBitangent, out var maximumBitangent);
            return new SpatialCalibrationSurface(
                normal * maximumNormal
                    + tangent * ((minimumTangent + maximumTangent) * 0.5f)
                    + bitangent * ((minimumBitangent + maximumBitangent) * 0.5f),
                normal,
                tangent,
                bitangent,
                new Vector2(
                    Mathf.Max(0.01f, maximumTangent - minimumTangent),
                    Mathf.Max(0.01f, maximumBitangent - minimumBitangent)));
        }

        private static bool TryDominantPlanarDepth(GameObject wall, Vector3 normal, out float depth)
        {
            var clusters = new Dictionary<int, PlaneCluster>();
            foreach (var filter in wall.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null) continue;
                try
                {
                    var vertices = mesh.vertices;
                    var triangles = mesh.triangles;
                    var matrix = filter.transform.localToWorldMatrix;
                    for (var index = 0; index + 2 < triangles.Length; index += 3)
                    {
                        var first = matrix.MultiplyPoint3x4(vertices[triangles[index]]);
                        var second = matrix.MultiplyPoint3x4(vertices[triangles[index + 1]]);
                        var third = matrix.MultiplyPoint3x4(vertices[triangles[index + 2]]);
                        var cross = Vector3.Cross(second - first, third - first);
                        var doubleArea = cross.magnitude;
                        if (doubleArea < 0.000001f) continue;
                        if (Vector3.Dot(cross / doubleArea, normal) < PlanarNormalAlignment) continue;

                        var triangleDepth = Vector3.Dot((first + second + third) / 3f, normal);
                        var key = Mathf.RoundToInt(triangleDepth / PlaneDepthBucket);
                        clusters.TryGetValue(key, out var cluster);
                        cluster.area += doubleArea * 0.5f;
                        cluster.weightedDepth += triangleDepth * doubleArea * 0.5f;
                        clusters[key] = cluster;
                    }
                }
                catch (UnityException)
                {
                    // Some imported meshes are not CPU-readable. Bounds remain the deterministic fallback.
                }
            }

            if (clusters.Count == 0)
            {
                depth = 0f;
                return false;
            }

            var best = clusters.Values
                .OrderByDescending(cluster => cluster.area)
                .ThenByDescending(cluster => cluster.weightedDepth / cluster.area)
                .First();
            depth = best.weightedDepth / best.area;
            return true;
        }

        private struct PlaneCluster
        {
            public float area;
            public float weightedDepth;
        }

        public static Quaternion CanonicalAlignment(SpatialCalibrationSurface source)
        {
            var sourceBasis = Quaternion.LookRotation(source.Normal, source.Bitangent);
            var canonicalBasis = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            return canonicalBasis * Quaternion.Inverse(sourceBasis);
        }

        private static IReadOnlyList<Vector3> WorldGeometryPoints(GameObject value)
        {
            var points = new List<Vector3>();
            var renderers = value.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                foreach (var renderer in renderers)
                    AddBoundsCorners(points, renderer.localToWorldMatrix, renderer.localBounds);
                return points;
            }

            var colliders = value.GetComponentsInChildren<Collider>(true);
            if (colliders.Length > 0)
            {
                foreach (var collider in colliders)
                    AddBoundsCorners(points, Matrix4x4.identity, collider.bounds);
                return points;
            }

            AddBoundsCorners(points, value.transform.localToWorldMatrix, new Bounds(Vector3.zero, Vector3.one));
            return points;
        }

        private static void AddBoundsCorners(ICollection<Vector3> points, Matrix4x4 matrix, Bounds bounds)
        {
            var minimum = bounds.min;
            var maximum = bounds.max;
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++)
            {
                var local = new Vector3(
                    x == 0 ? minimum.x : maximum.x,
                    y == 0 ? minimum.y : maximum.y,
                    z == 0 ? minimum.z : maximum.z);
                points.Add(matrix.MultiplyPoint3x4(local));
            }
        }

        private static void ProjectedRange(
            IReadOnlyList<Vector3> points,
            Vector3 axis,
            out float minimum,
            out float maximum)
        {
            minimum = float.PositiveInfinity;
            maximum = float.NegativeInfinity;
            foreach (var point in points)
            {
                var projection = Vector3.Dot(point, axis);
                minimum = Mathf.Min(minimum, projection);
                maximum = Mathf.Max(maximum, projection);
            }
        }
    }
}
