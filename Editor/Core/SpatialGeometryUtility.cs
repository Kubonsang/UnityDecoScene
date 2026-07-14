using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [Serializable]
    public sealed class ContactEvidence
    {
        public string surfaceId;
        public float gap;
        public float penetration;
        public float supportCoverage;
        public float directionAlignment;
        public Vector3 contactPoint;
        public bool valid;
    }

    public readonly struct WorldObb
    {
        public readonly Vector3 Center;
        public readonly Vector3 Extents;
        public readonly Vector3 AxisX;
        public readonly Vector3 AxisY;
        public readonly Vector3 AxisZ;

        public WorldObb(Vector3 center, Vector3 extents, Quaternion rotation)
        {
            Center = center;
            Extents = extents;
            AxisX = rotation * Vector3.right;
            AxisY = rotation * Vector3.up;
            AxisZ = rotation * Vector3.forward;
        }

        public Vector3 Axis(int index) => index switch { 0 => AxisX, 1 => AxisY, _ => AxisZ };
        public float Extent(int index) => index switch { 0 => Extents.x, 1 => Extents.y, _ => Extents.z };

        public Bounds ToAabb()
        {
            var extents = Abs(AxisX) * Extents.x + Abs(AxisY) * Extents.y + Abs(AxisZ) * Extents.z;
            return new Bounds(Center, extents * 2f);
        }

        private static Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
    }

    public static class SpatialGeometryUtility
    {
        private const float AxisEpsilon = 0.000001f;

        public static IReadOnlyList<WorldObb> BuildWorldObbs(DecorAssetDescriptor descriptor, Vector3 position, Quaternion rotation, Vector3 scale, float padding = 0f)
        {
            return BuildWorldObbs(descriptor?.Geometry?.collisionProxies, position, rotation, scale, padding);
        }

        public static IReadOnlyList<WorldObb> BuildWorldObbs(RoomObstacleProxy obstacle, float padding = 0f)
        {
            return obstacle == null
                ? Array.Empty<WorldObb>()
                : BuildWorldObbs(obstacle.collisionProxies, obstacle.position, obstacle.rotation, obstacle.scale, padding);
        }

        public static IReadOnlyList<WorldObb> BuildWorldObbs(IReadOnlyList<OrientedBoxProxy> proxies, Vector3 position, Quaternion rotation, Vector3 scale, float padding = 0f)
        {
            var result = new List<WorldObb>();
            if (proxies == null) return result;
            foreach (var proxy in proxies)
            {
                if (proxy == null) continue;
                var scaledCenter = Vector3.Scale(proxy.localCenter, scale);
                var extents = Vector3.Scale(proxy.size * 0.5f, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                extents += Vector3.one * Mathf.Max(0f, padding);
                result.Add(new WorldObb(position + rotation * scaledCenter, extents, rotation * proxy.localRotation));
            }
            return result;
        }

        public static Bounds CombinedAabb(IReadOnlyList<WorldObb> boxes)
        {
            if (boxes == null || boxes.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var result = boxes[0].ToAabb();
            for (var i = 1; i < boxes.Count; i++) result.Encapsulate(boxes[i].ToAabb());
            return result;
        }

        public static bool Intersects(IReadOnlyList<WorldObb> left, IReadOnlyList<WorldObb> right)
        {
            if (left == null || right == null) return false;
            foreach (var a in left)
            foreach (var b in right)
            {
                if (!a.ToAabb().Intersects(b.ToAabb())) continue;
                if (Intersects(a, b)) return true;
            }
            return false;
        }

        public static bool Intersects(WorldObb a, WorldObb b)
        {
            var axes = new List<Vector3>(15) { a.AxisX, a.AxisY, a.AxisZ, b.AxisX, b.AxisY, b.AxisZ };
            for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++) axes.Add(Vector3.Cross(a.Axis(i), b.Axis(j)));

            var delta = b.Center - a.Center;
            foreach (var rawAxis in axes)
            {
                if (rawAxis.sqrMagnitude <= AxisEpsilon) continue;
                var axis = rawAxis.normalized;
                var distance = Mathf.Abs(Vector3.Dot(delta, axis));
                var radiusA = ProjectionRadius(a, axis);
                var radiusB = ProjectionRadius(b, axis);
                if (distance >= radiusA + radiusB - AxisEpsilon) return false;
            }
            return true;
        }

        public static ContactEvidence EvaluateContact(DecorAssetDescriptor descriptor, Vector3 position, Quaternion rotation, Vector3 scale, RoomSurface surface)
        {
            return EvaluateContact(descriptor, descriptor?.Geometry?.contact, position, rotation, scale, surface);
        }

        public static ContactEvidence EvaluateContact(DecorAssetDescriptor descriptor, ContactRules rules, Vector3 position, Quaternion rotation, Vector3 scale, RoomSurface surface)
        {
            var profile = descriptor?.Geometry;
            if (profile == null || rules == null || surface == null) return new ContactEvidence { valid = false };
            var frame = profile.FrameFor(rules);
            if (frame == null) return new ContactEvidence { valid = false, surfaceId = surface.SurfaceId };

            var contactPoint = position + rotation * Vector3.Scale(frame.localPoint, scale);
            var contactNormal = (rotation * frame.localNormal).normalized;
            var signedDistance = Vector3.Dot(contactPoint - surface.Origin, surface.Normal);
            var gap = Mathf.Max(0f, signedDistance);
            var penetration = Mathf.Max(0f, -signedDistance);
            var alignment = Vector3.Dot(contactNormal, -surface.Normal);
            var coverage = CalculateCoverage(frame, contactPoint, rotation, scale, surface);
            var withinSurface = surface.ContainsProjectedPoint(contactPoint, 0.001f);
            var valid = withinSurface && alignment >= 0.95f && penetration <= rules.maximumPenetration + AxisEpsilon &&
                        gap >= rules.minimumGap - AxisEpsilon && gap <= rules.maximumGap + AxisEpsilon &&
                        coverage >= rules.minimumSupportCoverage - AxisEpsilon;
            return new ContactEvidence
            {
                surfaceId = surface.SurfaceId,
                gap = gap,
                penetration = penetration,
                supportCoverage = coverage,
                directionAlignment = alignment,
                contactPoint = contactPoint,
                valid = valid
            };
        }

        public static Quaternion AlignContactFrame(DecorGeometryProfile profile, RoomSurface surface)
        {
            return AlignContactFrame(profile, profile?.contact, surface);
        }

        public static Quaternion AlignContactFrame(DecorGeometryProfile profile, ContactRules rules, RoomSurface surface)
        {
            var frame = profile?.FrameFor(rules);
            var localNormal = frame?.localNormal ?? Vector3.down;
            var localTangent = frame?.localTangent ?? Vector3.right;
            var targetNormal = -surface.Normal;
            var normalRotation = Quaternion.FromToRotation(localNormal, targetNormal);
            var rotatedTangent = Vector3.ProjectOnPlane(normalRotation * localTangent, surface.Normal).normalized;
            var targetTangent = surface.Tangent;
            if (rotatedTangent.sqrMagnitude < AxisEpsilon || targetTangent.sqrMagnitude < AxisEpsilon) return normalRotation;
            var tangentAngle = Vector3.SignedAngle(rotatedTangent, targetTangent, targetNormal);
            return Quaternion.AngleAxis(tangentAngle, targetNormal) * normalRotation;
        }

        public static Vector3 PlaceContactAtSurface(DecorGeometryProfile profile, ContactFrame frame, RoomSurface surface, Vector3 surfacePoint, Quaternion rotation, Vector3 scale, float gap)
        {
            var localPoint = frame?.localPoint ?? Vector3.zero;
            return surfacePoint + surface.Normal * gap - rotation * Vector3.Scale(localPoint, scale);
        }

        private static float CalculateCoverage(ContactFrame frame, Vector3 contactPoint, Quaternion rotation, Vector3 scale, RoomSurface surface)
        {
            var width = Mathf.Abs(frame.size.x * scale.x);
            var heightScale = Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            var height = Mathf.Abs(frame.size.y * heightScale);
            if (width <= AxisEpsilon || height <= AxisEpsilon) return 0f;
            var delta = contactPoint - surface.Origin;
            var horizontal = Mathf.Abs(Vector3.Dot(delta, surface.Tangent));
            var vertical = Mathf.Abs(Vector3.Dot(delta, surface.Bitangent));
            var overlapWidth = Mathf.Max(0f, Mathf.Min(width * 0.5f, surface.Size.x * 0.5f - horizontal) + Mathf.Min(width * 0.5f, surface.Size.x * 0.5f + horizontal));
            var overlapHeight = Mathf.Max(0f, Mathf.Min(height * 0.5f, surface.Size.y * 0.5f - vertical) + Mathf.Min(height * 0.5f, surface.Size.y * 0.5f + vertical));
            return Mathf.Clamp01((overlapWidth * overlapHeight) / (width * height));
        }

        private static float ProjectionRadius(WorldObb box, Vector3 axis) =>
            Mathf.Abs(Vector3.Dot(box.AxisX, axis)) * box.Extents.x +
            Mathf.Abs(Vector3.Dot(box.AxisY, axis)) * box.Extents.y +
            Mathf.Abs(Vector3.Dot(box.AxisZ, axis)) * box.Extents.z;
    }
}
