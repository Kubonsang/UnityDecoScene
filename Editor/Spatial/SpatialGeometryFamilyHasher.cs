using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    /// <summary>
    /// Groups assets by the reviewed spatial envelope used for placement, not by render materials,
    /// UVs, vertex order, or decorative surface triangulation.
    /// </summary>
    public static class SpatialGeometryFamilyHasher
    {
        private const float Quantization = 10000f;

        public static string Compute(GameObject prefab, DecorGeometryProfile profile = null)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            profile = profile != null && profile.collisionProxies != null && profile.collisionProxies.Count > 0
                ? profile
                : DecorAssetScanner.BuildGeometryProfile(prefab);
            return Compute(profile);
        }

        public static string Compute(DecorGeometryProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                var proxies = (profile.collisionProxies ?? new List<OrientedBoxProxy>())
                    .Where(proxy => proxy != null)
                    .Select(ProxySignature.From)
                    .OrderBy(signature => signature)
                    .ToArray();
                writer.Write(proxies.Length);
                foreach (var proxy in proxies) proxy.Write(writer);
                QuantizedVector.From(profile.forwardAxis).Write(writer);
                QuantizedVector.From(profile.upAxis).Write(writer);
                QuantizedVector.From(profile.pivotOffset).Write(writer);
                WriteFrame(writer, profile.bottomContact);
                WriteFrame(writer, profile.backContact);
            }
            using var sha = SHA256.Create();
            return "proxy-v1:" + string.Concat(sha.ComputeHash(stream.ToArray()).Select(value => value.ToString("x2")));
        }

        private static void WriteFrame(BinaryWriter writer, ContactFrame frame)
        {
            writer.Write(frame != null);
            if (frame == null) return;
            QuantizedVector.From(frame.localPoint).Write(writer);
            QuantizedVector.From(frame.localNormal).Write(writer);
            QuantizedVector.From(frame.localTangent).Write(writer);
            writer.Write(Mathf.RoundToInt(frame.size.x * Quantization));
            writer.Write(Mathf.RoundToInt(frame.size.y * Quantization));
        }

        private readonly struct ProxySignature : IComparable<ProxySignature>
        {
            private readonly QuantizedVector center;
            private readonly QuantizedVector size;
            private readonly QuantizedQuaternion rotation;

            private ProxySignature(QuantizedVector center, QuantizedVector size, QuantizedQuaternion rotation)
            {
                this.center = center;
                this.size = size;
                this.rotation = rotation;
            }

            public static ProxySignature From(OrientedBoxProxy proxy) => new(
                QuantizedVector.From(proxy.localCenter),
                QuantizedVector.From(proxy.size),
                QuantizedQuaternion.From(proxy.localRotation));

            public int CompareTo(ProxySignature other)
            {
                var result = center.CompareTo(other.center);
                if (result != 0) return result;
                result = size.CompareTo(other.size);
                return result != 0 ? result : rotation.CompareTo(other.rotation);
            }

            public void Write(BinaryWriter writer)
            {
                center.Write(writer);
                size.Write(writer);
                rotation.Write(writer);
            }
        }

        private readonly struct QuantizedVector : IComparable<QuantizedVector>
        {
            private readonly int x;
            private readonly int y;
            private readonly int z;
            private QuantizedVector(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
            public static QuantizedVector From(Vector3 value) => new(
                Mathf.RoundToInt(value.x * Quantization),
                Mathf.RoundToInt(value.y * Quantization),
                Mathf.RoundToInt(value.z * Quantization));
            public int CompareTo(QuantizedVector other)
            {
                var result = x.CompareTo(other.x);
                if (result != 0) return result;
                result = y.CompareTo(other.y);
                return result != 0 ? result : z.CompareTo(other.z);
            }
            public void Write(BinaryWriter writer) { writer.Write(x); writer.Write(y); writer.Write(z); }
        }

        private readonly struct QuantizedQuaternion : IComparable<QuantizedQuaternion>
        {
            private readonly int x;
            private readonly int y;
            private readonly int z;
            private readonly int w;
            private QuantizedQuaternion(int x, int y, int z, int w) { this.x = x; this.y = y; this.z = z; this.w = w; }
            public static QuantizedQuaternion From(Quaternion value)
            {
                if (value.w < 0f || (Mathf.Approximately(value.w, 0f) &&
                    (value.x < 0f || (Mathf.Approximately(value.x, 0f) &&
                    (value.y < 0f || (Mathf.Approximately(value.y, 0f) && value.z < 0f))))))
                    value = new Quaternion(-value.x, -value.y, -value.z, -value.w);
                return new QuantizedQuaternion(
                    Mathf.RoundToInt(value.x * Quantization),
                    Mathf.RoundToInt(value.y * Quantization),
                    Mathf.RoundToInt(value.z * Quantization),
                    Mathf.RoundToInt(value.w * Quantization));
            }
            public int CompareTo(QuantizedQuaternion other)
            {
                var result = x.CompareTo(other.x);
                if (result != 0) return result;
                result = y.CompareTo(other.y);
                if (result != 0) return result;
                result = z.CompareTo(other.z);
                return result != 0 ? result : w.CompareTo(other.w);
            }
            public void Write(BinaryWriter writer) { writer.Write(x); writer.Write(y); writer.Write(z); writer.Write(w); }
        }
    }
}
