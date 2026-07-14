using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class SpatialCalibrationCapturePreflight
    {
        private const float SurfaceEpsilon = 0.0001f;

        public static IReadOnlyList<string> Inspect(
            SpatialCalibrationSession session,
            SpatialCalibrationReport report)
        {
            var issues = new List<string>();
            if (session?.SubjectObject == null)
            {
                issues.Add("CAPTURE_SESSION_MISSING: Calibration subject is unavailable.");
                return issues;
            }
            if (report == null || !report.Passed)
                issues.Add("CAPTURE_TECHNICAL_GATE: Technical validation must pass before capture.");
            if (!session.RequiresWallFixture && session.WallFixture != null)
                issues.Add("CAPTURE_UNEXPECTED_WALL: This relationship must not render a wall fixture.");
            if (!session.RequiresFloorFixture && session.FloorFixture != null)
                issues.Add("CAPTURE_UNEXPECTED_FLOOR: This relationship must not render a floor fixture.");

            foreach (var rule in session.Rules)
            {
                var surface = ResolveSurface(session, rule);
                foreach (var proxy in session.Geometry.collisionProxies)
                {
                    var minimumDistance = Corners(session.SubjectObject.transform, proxy)
                        .Min(point => Vector3.Dot(point - surface.Origin, surface.Normal));
                    if (minimumDistance < -SurfaceEpsilon)
                    {
                        issues.Add(
                            $"CAPTURE_PROXY_SURFACE_INTERSECTION: {proxy.proxyId} crosses {rule.target} by {-minimumDistance:0.####}m.");
                    }
                }
            }
            return issues.Distinct(StringComparer.Ordinal).ToArray();
        }

        public static void EnsureCanCapture(
            SpatialCalibrationSession session,
            SpatialCalibrationReport report)
        {
            var issues = Inspect(session, report);
            if (issues.Count > 0) throw new InvalidOperationException(string.Join("\n", issues));
        }

        private static SpatialCalibrationSurface ResolveSurface(
            SpatialCalibrationSession session,
            SpatialContactRuleContract rule)
        {
            if (rule.target == "surface:wall") return session.WallSurface;
            if (rule.target == "surface:floor")
                return new SpatialCalibrationSurface(
                    Vector3.zero, Vector3.up, Vector3.right, Vector3.forward, new Vector2(6f, 6f));
            var bounds = session.TargetWorldBounds();
            return new SpatialCalibrationSurface(
                new Vector3(bounds.center.x, bounds.max.y, bounds.center.z),
                Vector3.up, Vector3.right, Vector3.forward, new Vector2(bounds.size.x, bounds.size.z));
        }

        private static IEnumerable<Vector3> Corners(Transform subject, OrientedBoxProxy proxy)
        {
            var center = subject.TransformPoint(proxy.localCenter);
            var rotation = subject.rotation * proxy.localRotation;
            var extent = Vector3.Scale(proxy.size, Abs(subject.lossyScale)) * 0.5f;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
                yield return center + rotation * new Vector3(extent.x * x, extent.y * y, extent.z * z);
        }

        private static Vector3 Abs(Vector3 value) => new(
            Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
    }
}
