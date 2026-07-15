using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [Serializable]
    public sealed class OrientedBoxProxy
    {
        public string proxyId;
        public Vector3 localCenter;
        public Vector3 size = Vector3.one;
        public Quaternion localRotation = Quaternion.identity;

        public OrientedBoxProxy() { }

        public OrientedBoxProxy(string id, Vector3 center, Vector3 proxySize, Quaternion rotation)
        {
            proxyId = id;
            localCenter = center;
            size = proxySize;
            localRotation = rotation;
        }
    }

    [Serializable]
    public sealed class ContactFrame
    {
        public string frameId;
        public Vector3 localPoint;
        public Vector3 localNormal = Vector3.up;
        public Vector3 localTangent = Vector3.right;
        public Vector2 size = Vector2.one;
    }

    [Serializable]
    public sealed class ContactRules
    {
        public string ruleId;
        public string frameId;
        public ContactRequirement requirement = ContactRequirement.FloorSupported;
        [Min(0f)] public float minimumGap;
        [Min(0f)] public float maximumGap = 0.01f;
        [Min(0f)] public float maximumPenetration;
        [Range(0f, 1f)] public float minimumSupportCoverage = 0.6f;

        public static ContactRules Defaults(ContactRequirement requirement)
        {
            return requirement switch
            {
                ContactRequirement.WallMounted => new ContactRules { requirement = requirement, minimumGap = 0.005f, maximumGap = 0.01f, maximumPenetration = 0f, minimumSupportCoverage = 0.6f },
                ContactRequirement.WallBacked => new ContactRules { requirement = requirement, minimumGap = 0.01f, maximumGap = 0.05f, maximumPenetration = 0f, minimumSupportCoverage = 0.6f },
                ContactRequirement.CeilingMounted => new ContactRules { requirement = requirement, minimumGap = 0f, maximumGap = 0.01f, maximumPenetration = 0f, minimumSupportCoverage = 0.6f },
                ContactRequirement.FreeStanding => new ContactRules { requirement = requirement, minimumGap = 0f, maximumGap = float.MaxValue, maximumPenetration = 0f, minimumSupportCoverage = 0f },
                _ => new ContactRules { requirement = ContactRequirement.FloorSupported, minimumGap = 0f, maximumGap = 0.01f, maximumPenetration = 0f, minimumSupportCoverage = 0.6f }
            };
        }
    }

    [Serializable]
    public sealed class DecorGeometryProfile
    {
        public const int ContractVersion = 2;

        public int version = ContractVersion;
        public GeometrySource source = GeometrySource.Unknown;
        public List<OrientedBoxProxy> collisionProxies = new();
        public Vector3 forwardAxis = Vector3.forward;
        public Vector3 upAxis = Vector3.up;
        public Vector3 pivotOffset;
        public ContactFrame bottomContact = new() { frameId = "bottom", localNormal = Vector3.down, localTangent = Vector3.right };
        public ContactFrame backContact = new() { frameId = "back", localNormal = Vector3.back, localTangent = Vector3.right };
        public ContactFrame topContact = new() { frameId = "top", localNormal = Vector3.up, localTangent = Vector3.right };
        public ContactRules contact = ContactRules.Defaults(ContactRequirement.FloorSupported);
        public List<ContactRules> contacts = new();
        [Range(0f, 1f)] public float inferenceConfidence;
        public bool reviewed;
        public string dependencyHash;

        public bool IsUsable => version == ContractVersion && reviewed && collisionProxies != null && collisionProxies.Count > 0;

        public IReadOnlyList<ContactRules> EffectiveContacts
        {
            get
            {
                if (contacts != null && contacts.Count > 0) return contacts;
                return contact != null ? new[] { contact } : Array.Empty<ContactRules>();
            }
        }

        public void Normalize()
        {
            version = ContractVersion;
            forwardAxis = NormalizeAxis(forwardAxis, Vector3.forward);
            upAxis = NormalizeAxis(upAxis, Vector3.up);
            if (Mathf.Abs(Vector3.Dot(forwardAxis, upAxis)) > 0.999f) upAxis = Vector3.up;
            inferenceConfidence = Mathf.Clamp01(inferenceConfidence);
            collisionProxies ??= new List<OrientedBoxProxy>();
            foreach (var proxy in collisionProxies)
            {
                if (proxy == null) continue;
                proxy.size = new Vector3(Mathf.Max(0.001f, Mathf.Abs(proxy.size.x)), Mathf.Max(0.001f, Mathf.Abs(proxy.size.y)), Mathf.Max(0.001f, Mathf.Abs(proxy.size.z)));
                if (proxy.localRotation == default) proxy.localRotation = Quaternion.identity;
            }
            contact ??= ContactRules.Defaults(ContactRequirement.FloorSupported);
            NormalizeContact(contact);
            contacts ??= new List<ContactRules>();
            contacts.RemoveAll(value => value == null);
            foreach (var rules in contacts) NormalizeContact(rules);
            if (contacts.Count > 0) contact = contacts[0];
            bottomContact ??= new ContactFrame { frameId = "bottom", localNormal = Vector3.down, localTangent = Vector3.right };
            backContact ??= new ContactFrame { frameId = "back", localNormal = Vector3.back, localTangent = Vector3.right };
            topContact ??= new ContactFrame { frameId = "top", localNormal = Vector3.up, localTangent = Vector3.right };
        }

        public ContactFrame FrameFor(ContactRules rules)
        {
            if (rules == null) return null;
            if (string.Equals(rules.frameId, "back", StringComparison.OrdinalIgnoreCase)) return backContact;
            if (string.Equals(rules.frameId, "bottom", StringComparison.OrdinalIgnoreCase)) return bottomContact;
            return rules.requirement is ContactRequirement.WallBacked or ContactRequirement.WallMounted ? backContact : bottomContact;
        }

        public ContactFrame Frame(string frameId)
        {
            if (string.Equals(frameId, "bottom", StringComparison.OrdinalIgnoreCase)) return bottomContact;
            if (string.Equals(frameId, "back", StringComparison.OrdinalIgnoreCase)) return backContact;
            if (string.Equals(frameId, "top", StringComparison.OrdinalIgnoreCase)) return topContact;
            if (string.Equals(frameId, SpatialDerivedContactFrameResolver.FlatFrameId, StringComparison.OrdinalIgnoreCase) &&
                SpatialDerivedContactFrameResolver.TryResolveFlat(this, out var flat)) return flat;
            return null;
        }

        private static void NormalizeContact(ContactRules rules)
        {
            rules.minimumGap = Mathf.Max(0f, rules.minimumGap);
            rules.maximumGap = Mathf.Max(rules.minimumGap, rules.maximumGap);
            rules.maximumPenetration = Mathf.Max(0f, rules.maximumPenetration);
            rules.minimumSupportCoverage = Mathf.Clamp01(rules.minimumSupportCoverage);
        }

        private static Vector3 NormalizeAxis(Vector3 axis, Vector3 fallback) => axis.sqrMagnitude < 0.0001f ? fallback : axis.normalized;
    }

    /// <summary>
    /// Resolves interaction-only contact frames from reviewed collision proxies. The virtual
    /// <c>flat</c> frame is intentionally not written into Spatial Contract v1: its exact result is
    /// a deterministic function of the geometry hash and this immutable resolver identity. A
    /// SupportedBy interaction still requires human approval before the frame can be consumed.
    /// </summary>
    public static class SpatialDerivedContactFrameResolver
    {
        public const string FlatFrameId = "flat";
        public const int ResolverVersion = 1;
        public const string ResolverHash = "bb0b190a977e72d11cb1d13ed7ab06787c5f74f42b5effcf7088cc30a3652e4d";

        private const float Epsilon = 0.000001f;
        private const float MaximumFlatnessRatio = 1.1f;

        public static bool TryResolveFlat(DecorGeometryProfile geometry, out ContactFrame frame)
        {
            frame = null;
            if (geometry?.collisionProxies == null || geometry.collisionProxies.Count == 0) return false;

            var proxies = new List<OrientedBoxProxy>();
            foreach (var proxy in geometry.collisionProxies)
            {
                if (proxy == null || !Finite(proxy.localCenter) || !Finite(proxy.size) ||
                    !Finite(proxy.localRotation) || proxy.size.x <= Epsilon ||
                    proxy.size.y <= Epsilon || proxy.size.z <= Epsilon) return false;
                proxies.Add(proxy);
            }
            proxies.Sort((left, right) => string.Compare(
                left.proxyId ?? string.Empty, right.proxyId ?? string.Empty, StringComparison.Ordinal));
            if (proxies.Count == 0) return false;

            var corners = new List<Vector3>(proxies.Count * 8);
            foreach (var proxy in proxies)
            {
                var rotation = Normalize(proxy.localRotation);
                var extents = proxy.size * 0.5f;
                for (var x = -1; x <= 1; x += 2)
                for (var y = -1; y <= 1; y += 2)
                for (var z = -1; z <= 1; z += 2)
                    corners.Add(proxy.localCenter + rotation * new Vector3(
                        extents.x * x, extents.y * y, extents.z * z));
            }

            var bestRatio = float.PositiveInfinity;
            var bestThickness = float.PositiveInfinity;
            ContactFrame best = null;
            foreach (var proxy in proxies)
            {
                var rotation = Normalize(proxy.localRotation);
                var axes = new[]
                {
                    rotation * Vector3.right,
                    rotation * Vector3.up,
                    rotation * Vector3.forward
                };
                for (var normalIndex = 0; normalIndex < axes.Length; normalIndex++)
                {
                    var normalAxis = CanonicalDirection(axes[normalIndex]);
                    var first = axes[(normalIndex + 1) % 3].normalized;
                    var second = axes[(normalIndex + 2) % 3].normalized;
                    var firstSpan = Span(corners, first);
                    var secondSpan = Span(corners, second);
                    var tangent = CanonicalDirection(firstSpan >= secondSpan ? first : second);
                    tangent = Vector3.ProjectOnPlane(tangent, normalAxis).normalized;
                    if (tangent.sqrMagnitude <= Epsilon) continue;
                    var contactNormal = -normalAxis;
                    var bitangent = Vector3.Cross(tangent, contactNormal).normalized;
                    if (bitangent.sqrMagnitude <= Epsilon) continue;

                    Extents(corners, normalAxis, out var minimumNormal, out var maximumNormal);
                    Extents(corners, tangent, out var minimumTangent, out var maximumTangent);
                    Extents(corners, bitangent, out var minimumBitangent, out var maximumBitangent);
                    var thickness = maximumNormal - minimumNormal;
                    var tangentSpan = maximumTangent - minimumTangent;
                    var bitangentSpan = maximumBitangent - minimumBitangent;
                    var minimumPlanarSpan = Mathf.Min(tangentSpan, bitangentSpan);
                    if (!Finite(thickness) || !Finite(minimumPlanarSpan) ||
                        thickness <= Epsilon || minimumPlanarSpan <= Epsilon) continue;
                    var ratio = thickness / minimumPlanarSpan;
                    if (ratio > MaximumFlatnessRatio + Epsilon) continue;
                    if (ratio > bestRatio + Epsilon ||
                        Mathf.Abs(ratio - bestRatio) <= Epsilon && thickness >= bestThickness - Epsilon) continue;

                    bestRatio = ratio;
                    bestThickness = thickness;
                    best = new ContactFrame
                    {
                        frameId = FlatFrameId,
                        localPoint = normalAxis * minimumNormal +
                                     tangent * ((minimumTangent + maximumTangent) * 0.5f) +
                                     bitangent * ((minimumBitangent + maximumBitangent) * 0.5f),
                        localNormal = contactNormal,
                        localTangent = tangent,
                        size = new Vector2(tangentSpan, bitangentSpan)
                    };
                }
            }

            frame = best;
            return frame != null;
        }

        private static float Span(IReadOnlyList<Vector3> points, Vector3 axis)
        {
            Extents(points, axis.normalized, out var minimum, out var maximum);
            return maximum - minimum;
        }

        private static void Extents(
            IReadOnlyList<Vector3> points,
            Vector3 axis,
            out float minimum,
            out float maximum)
        {
            minimum = float.PositiveInfinity;
            maximum = float.NegativeInfinity;
            for (var index = 0; index < points.Count; index++)
            {
                var projection = Vector3.Dot(points[index], axis);
                minimum = Mathf.Min(minimum, projection);
                maximum = Mathf.Max(maximum, projection);
            }
        }

        private static Vector3 CanonicalDirection(Vector3 value)
        {
            value.Normalize();
            if (value.x < -Epsilon || Mathf.Abs(value.x) <= Epsilon && value.y < -Epsilon ||
                Mathf.Abs(value.x) <= Epsilon && Mathf.Abs(value.y) <= Epsilon && value.z < 0f)
                value = -value;
            return value;
        }

        private static Quaternion Normalize(Quaternion value)
        {
            var magnitude = Mathf.Sqrt(value.x * value.x + value.y * value.y +
                                       value.z * value.z + value.w * value.w);
            return magnitude <= Epsilon
                ? Quaternion.identity
                : new Quaternion(value.x / magnitude, value.y / magnitude, value.z / magnitude, value.w / magnitude);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(Quaternion value) =>
            Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
    }
}
