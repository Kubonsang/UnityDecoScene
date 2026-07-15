using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    internal readonly struct ArrangementSupportFrameResolution
    {
        public ContactFrame Frame { get; }
        public int ResolverVersion { get; }
        public string ResolverHash { get; }
        public string GeometryHash { get; }
        public string ResolutionHash { get; }

        public ArrangementSupportFrameResolution(
            ContactFrame frame,
            int resolverVersion,
            string resolverHash,
            string geometryHash,
            string resolutionHash)
        {
            Frame = frame;
            ResolverVersion = resolverVersion;
            ResolverHash = resolverHash;
            GeometryHash = geometryHash;
            ResolutionHash = resolutionHash;
        }
    }

    internal readonly struct ArrangementSupportSurface
    {
        public string FrameId { get; }
        public Vector3 Origin { get; }
        public Vector3 Normal { get; }
        public Vector3 Tangent { get; }
        public Vector3 Bitangent { get; }
        public Vector2 Size { get; }
        public string GeometryHash { get; }
        public string ResolutionHash { get; }

        public ArrangementSupportSurface(
            string frameId,
            Vector3 origin,
            Vector3 normal,
            Vector3 tangent,
            Vector3 bitangent,
            Vector2 size,
            string geometryHash,
            string resolutionHash)
        {
            FrameId = frameId;
            Origin = origin;
            Normal = normal;
            Tangent = tangent;
            Bitangent = bitangent;
            Size = size;
            GeometryHash = geometryHash;
            ResolutionHash = resolutionHash;
        }
    }

    /// <summary>
    /// Canonical v0.1 resolver for reviewed support regions. A stacked asset is known to rest on
    /// its reviewed back frame, so its support region is the parallel face opposite that frame.
    /// Compound proxies determine the opposite plane while the human-reviewed frame owns its
    /// usable footprint. No Renderer/AABB or single-OBB fallback is permitted.
    /// </summary>
    internal static class ArrangementSupportSurfaceResolver
    {
        public const int ResolverVersion = SurfaceArrangementSpec.CurrentResolverVersion;
        public const string ResolverHash = "2226ace083f82c6edb3be2cfacbea6c56b72defe76ff9c2819695950141d076c";
        private const float Epsilon = 0.000001f;

        public static bool TryResolveReviewedFrame(
            DecorGeometryProfile geometry,
            string frameId,
            out ArrangementSupportFrameResolution resolution,
            out string errorCode)
        {
            resolution = default;
            errorCode = SurfaceArrangementErrorCodes.SupportRegionInvalid;
            if (!TryValidateGeometry(geometry) || string.IsNullOrWhiteSpace(frameId)) return false;
            var source = geometry.Frame(frameId);
            if (!TryCanonicalFrame(source, frameId, out var frame)) return false;
            var geometryHash = SpatialGeometryFamilyHasher.Compute(geometry);
            resolution = BuildResolution(frame, geometryHash, "reviewed:" + frameId.ToLowerInvariant());
            errorCode = null;
            return true;
        }

        public static bool TryResolveOppositeBackFrame(
            DecorGeometryProfile geometry,
            string sourceFrameId,
            string targetFrameId,
            out ArrangementSupportFrameResolution resolution,
            out string errorCode)
        {
            resolution = default;
            errorCode = SurfaceArrangementErrorCodes.SupportRegionInvalid;
            if (!string.Equals(sourceFrameId, "back", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(targetFrameId, "top", StringComparison.OrdinalIgnoreCase) ||
                !TryValidateGeometry(geometry)) return false;
            if (!TryCanonicalFrame(geometry.Frame(sourceFrameId), sourceFrameId, out var source)) return false;

            var oppositeNormal = -source.localNormal;
            var maximumProjection = float.NegativeInfinity;
            foreach (var point in ProxyCorners(geometry.collisionProxies))
                maximumProjection = Mathf.Max(maximumProjection, Vector3.Dot(point, oppositeNormal));
            if (!Finite(maximumProjection)) return false;

            var sourceProjection = Vector3.Dot(source.localPoint, oppositeNormal);
            var separation = maximumProjection - sourceProjection;
            if (!Finite(sourceProjection) || !Finite(separation) || separation <= Epsilon) return false;
            var frame = new ContactFrame
            {
                frameId = "top",
                localPoint = source.localPoint + oppositeNormal * separation,
                localNormal = oppositeNormal,
                localTangent = source.localTangent,
                size = source.size
            };
            if (!TryCanonicalFrame(frame, targetFrameId, out frame)) return false;
            var geometryHash = SpatialGeometryFamilyHasher.Compute(geometry);
            resolution = BuildResolution(frame, geometryHash, "opposite:back>top");
            errorCode = null;
            return true;
        }

        public static bool TryResolveReviewedSurface(
            DecorGeometryProfile geometry,
            string frameId,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            out ArrangementSupportSurface surface,
            out string errorCode)
        {
            surface = default;
            if (!TryResolveReviewedFrame(geometry, frameId, out var frame, out errorCode)) return false;
            return TryTransform(frame, position, rotation, scale, out surface, out errorCode);
        }

        public static bool TryResolveOppositeBackSurface(
            DecorGeometryProfile geometry,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            out ArrangementSupportSurface surface,
            out string errorCode)
        {
            surface = default;
            if (!TryResolveOppositeBackFrame(geometry, "back", "top", out var frame, out errorCode)) return false;
            return TryTransform(frame, position, rotation, scale, out surface, out errorCode);
        }

        public static bool TryTransform(
            ArrangementSupportFrameResolution resolution,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            out ArrangementSupportSurface surface,
            out string errorCode)
        {
            surface = default;
            errorCode = SurfaceArrangementErrorCodes.SupportRegionInvalid;
            var frame = resolution.Frame;
            if (frame == null || !Finite(position) || !Finite(rotation) || !Finite(scale) ||
                scale.x <= Epsilon || scale.y <= Epsilon || scale.z <= Epsilon) return false;

            rotation = Normalize(rotation);
            var normal = rotation * frame.localNormal;
            var tangentVector = rotation * Vector3.Scale(frame.localTangent * frame.size.x, scale);
            var localBitangent = Vector3.Cross(frame.localTangent, frame.localNormal);
            var bitangentVector = rotation * Vector3.Scale(localBitangent * frame.size.y, scale);
            var tangent = Vector3.ProjectOnPlane(tangentVector, normal);
            var bitangent = Vector3.ProjectOnPlane(bitangentVector, normal);
            if (normal.sqrMagnitude <= Epsilon || tangent.sqrMagnitude <= Epsilon || bitangent.sqrMagnitude <= Epsilon)
                return false;
            normal.Normalize();
            tangent.Normalize();
            bitangent.Normalize();
            var size = new Vector2(tangentVector.magnitude, bitangentVector.magnitude);
            if (!Finite(size) || size.x <= Epsilon || size.y <= Epsilon) return false;

            surface = new ArrangementSupportSurface(
                frame.frameId,
                position + rotation * Vector3.Scale(frame.localPoint, scale),
                normal,
                tangent,
                bitangent,
                size,
                resolution.GeometryHash,
                resolution.ResolutionHash);
            errorCode = null;
            return true;
        }

        public static bool TryTransformFrame(
            ContactFrame source,
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            out ArrangementSupportSurface surface,
            out string errorCode)
        {
            surface = default;
            errorCode = SurfaceArrangementErrorCodes.SupportRegionInvalid;
            if (source == null || string.IsNullOrWhiteSpace(source.frameId) ||
                !TryCanonicalFrame(source, source.frameId, out var frame)) return false;
            var resolution = BuildResolution(frame, string.Empty, "transform-only");
            return TryTransform(resolution, position, rotation, scale, out surface, out errorCode);
        }

        private static ArrangementSupportFrameResolution BuildResolution(
            ContactFrame frame,
            string geometryHash,
            string mode)
        {
            var canonical = string.Join("|", new[]
            {
                ResolverVersion.ToString(CultureInfo.InvariantCulture), ResolverHash, geometryHash, mode,
                frame.frameId ?? string.Empty, Vector(frame.localPoint), Vector(frame.localNormal),
                Vector(frame.localTangent), Vector(frame.size)
            });
            return new ArrangementSupportFrameResolution(
                frame, ResolverVersion, ResolverHash, geometryHash, Hash128.Compute(canonical).ToString());
        }

        private static bool TryValidateGeometry(DecorGeometryProfile geometry)
        {
            if (geometry?.IsUsable != true || geometry.collisionProxies == null || geometry.collisionProxies.Count == 0)
                return false;
            if (!Finite(geometry.forwardAxis) || !Finite(geometry.upAxis) || !Finite(geometry.pivotOffset))
                return false;
            foreach (var proxy in geometry.collisionProxies)
            {
                if (proxy == null || !Finite(proxy.localCenter) || !Finite(proxy.size) || !Finite(proxy.localRotation) ||
                    proxy.size.x <= Epsilon || proxy.size.y <= Epsilon || proxy.size.z <= Epsilon ||
                    MagnitudeSquared(proxy.localRotation) <= Epsilon) return false;
            }
            return true;
        }

        private static bool TryCanonicalFrame(ContactFrame source, string expectedId, out ContactFrame frame)
        {
            frame = null;
            if (source == null || !string.Equals(source.frameId, expectedId, StringComparison.OrdinalIgnoreCase) ||
                !Finite(source.localPoint) || !Finite(source.localNormal) || !Finite(source.localTangent) ||
                !Finite(source.size) || source.localNormal.sqrMagnitude <= Epsilon ||
                source.size.x <= Epsilon || source.size.y <= Epsilon) return false;
            var normal = source.localNormal.normalized;
            var tangent = Vector3.ProjectOnPlane(source.localTangent, normal);
            if (tangent.sqrMagnitude <= Epsilon) return false;
            frame = new ContactFrame
            {
                frameId = expectedId.ToLowerInvariant(),
                localPoint = source.localPoint,
                localNormal = normal,
                localTangent = tangent.normalized,
                size = source.size
            };
            return true;
        }

        private static IEnumerable<Vector3> ProxyCorners(IEnumerable<OrientedBoxProxy> proxies)
        {
            foreach (var proxy in proxies.Where(value => value != null)
                         .OrderBy(value => value.proxyId ?? string.Empty, StringComparer.Ordinal))
            {
                var rotation = Normalize(proxy.localRotation);
                var extents = proxy.size * 0.5f;
                for (var x = -1; x <= 1; x += 2)
                for (var y = -1; y <= 1; y += 2)
                for (var z = -1; z <= 1; z += 2)
                    yield return proxy.localCenter + rotation * new Vector3(
                        extents.x * x, extents.y * y, extents.z * z);
            }
        }

        private static Quaternion Normalize(Quaternion value)
        {
            var inverse = 1f / Mathf.Sqrt(MagnitudeSquared(value));
            return new Quaternion(value.x * inverse, value.y * inverse, value.z * inverse, value.w * inverse);
        }

        private static float MagnitudeSquared(Quaternion value) =>
            value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Quaternion value) => Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
        private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string Vector(Vector2 value) => Number(value.x) + "," + Number(value.y);
        private static string Vector(Vector3 value) => Number(value.x) + "," + Number(value.y) + "," + Number(value.z);
    }
}
