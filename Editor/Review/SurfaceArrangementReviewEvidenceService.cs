using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    /// <summary>
    /// Builds human-readable evidence from the same validated placements shown in room captures.
    /// It does not decide approval and does not mutate the composition or contracts.
    /// </summary>
    internal static class SurfaceArrangementReviewEvidenceService
    {
        public static List<SpatialArrangementReviewEvidence> Build(PreviewSession session, ValidationReport report)
        {
            var result = new List<SpatialArrangementReviewEvidence>();
            var specs = session?.Plan?.SurfaceArrangements;
            if (specs == null) return result;

            foreach (var spec in specs.Where(value => value != null)
                         .OrderBy(value => value.arrangement_id ?? string.Empty, StringComparer.Ordinal))
            {
                var target = session.Placements.Where(value => value != null &&
                                                               string.Equals(value.elementId, spec.target_element_id, StringComparison.Ordinal))
                    .OrderBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal)
                    .FirstOrDefault();
                var members = session.Placements.Where(value => value != null &&
                                                                 string.Equals(value.arrangementId, spec.arrangement_id, StringComparison.Ordinal))
                    .OrderBy(value => value.stackLevel)
                    .ThenBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal)
                    .ToArray();

                var evidence = new SpatialArrangementReviewEvidence
                {
                    arrangementId = spec.arrangement_id,
                    targetElementId = spec.target_element_id,
                    targetFrameId = spec.TargetFrameId,
                    preset = spec.Preset.ToString(),
                    memberCount = members.Length,
                    stackCount = members.Count(value => value.stackLevel > 1),
                    maximumStackLevel = members.Length == 0 ? 0 : members.Max(value => value.stackLevel),
                    minimumSupport = members.Length == 0
                        ? 0f
                        : members.Min(value => value.contactEvidence?.supportCoverage ?? 0f),
                    minimumEdgeDistance = MinimumRootEdgeDistance(target, spec.TargetFrameId, members),
                    specHash = SurfaceArrangementSpecUtility.ComputeSpecHash(spec),
                    placementHash = PlacementHash(target, members),
                    stackStructure = StackStructure(target, members),
                    errorCodes = RelevantErrors(spec, target, members, report),
                    captureViewIds = members.Length > 0 && target != null
                        ? RoomCaptureService.SurfaceArrangementViewIds(spec.arrangement_id)
                        : Array.Empty<string>()
                };
                result.Add(evidence);
            }
            return result;
        }

        private static float MinimumRootEdgeDistance(PlacedDecorItem target, string frameId, IReadOnlyList<PlacedDecorItem> members)
        {
            if (target?.descriptor?.Geometry == null || members == null ||
                !TryRootSurface(target, frameId, out var origin, out var tangent, out var bitangent, out var size)) return 0f;
            var direct = members.Where(value => value != null &&
                                                string.Equals(value.supportPlacementId, target.placementId, StringComparison.Ordinal))
                .ToArray();
            if (direct.Length == 0) return 0f;

            var minimum = float.PositiveInfinity;
            foreach (var member in direct)
            foreach (var box in SpatialGeometryUtility.BuildWorldObbs(member.descriptor, member.position, member.rotation, member.scale))
            {
                var delta = box.Center - origin;
                var projectedX = Mathf.Abs(Vector3.Dot(delta, tangent)) + ProjectedExtent(box, tangent);
                var projectedY = Mathf.Abs(Vector3.Dot(delta, bitangent)) + ProjectedExtent(box, bitangent);
                minimum = Mathf.Min(minimum, size.x * 0.5f - projectedX, size.y * 0.5f - projectedY);
            }
            return float.IsPositiveInfinity(minimum) ? 0f : minimum;
        }

        private static bool TryRootSurface(
            PlacedDecorItem target,
            string frameId,
            out Vector3 origin,
            out Vector3 tangent,
            out Vector3 bitangent,
            out Vector2 size)
        {
            origin = tangent = bitangent = default;
            size = default;
            var frame = target.descriptor.Geometry.Frame(frameId);
            if (frame == null) return false;
            var normal = (target.rotation * frame.localNormal).normalized;
            var localTangent = frame.localTangent.sqrMagnitude < 0.001f ? Vector3.right : frame.localTangent.normalized;
            var localBitangent = Vector3.Cross(frame.localNormal.normalized, localTangent).normalized;
            var tangentVector = target.rotation * Vector3.Scale(localTangent * frame.size.x, target.scale);
            var bitangentVector = target.rotation * Vector3.Scale(localBitangent * frame.size.y, target.scale);
            tangent = Vector3.ProjectOnPlane(tangentVector, normal).normalized;
            bitangent = Vector3.ProjectOnPlane(bitangentVector, normal).normalized;
            if (normal.sqrMagnitude < 0.9f || tangent.sqrMagnitude < 0.9f || bitangent.sqrMagnitude < 0.9f) return false;
            origin = target.position + target.rotation * Vector3.Scale(frame.localPoint, target.scale);
            size = new Vector2(tangentVector.magnitude, bitangentVector.magnitude);
            return size.x > 0.001f && size.y > 0.001f;
        }

        private static float ProjectedExtent(WorldObb box, Vector3 axis) =>
            Mathf.Abs(Vector3.Dot(box.AxisX, axis)) * box.Extents.x +
            Mathf.Abs(Vector3.Dot(box.AxisY, axis)) * box.Extents.y +
            Mathf.Abs(Vector3.Dot(box.AxisZ, axis)) * box.Extents.z;

        private static string[] StackStructure(PlacedDecorItem target, IReadOnlyList<PlacedDecorItem> members)
        {
            if (target == null || members == null || members.Count == 0) return Array.Empty<string>();
            var children = members.Where(value => value != null)
                .GroupBy(value => value.supportPlacementId ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => group.OrderBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal);
            if (!children.TryGetValue(target.placementId ?? string.Empty, out var roots)) return Array.Empty<string>();
            var result = new List<string>();
            foreach (var root in roots) AppendStackPath(target.descriptor?.AssetId ?? target.elementId, root, children, new HashSet<string>(StringComparer.Ordinal), result);
            return result.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        private static void AppendStackPath(
            string path,
            PlacedDecorItem item,
            IReadOnlyDictionary<string, PlacedDecorItem[]> children,
            HashSet<string> ancestors,
            ICollection<string> result)
        {
            var id = item.placementId ?? string.Empty;
            var label = $"{item.descriptor?.AssetId ?? "missing"}[{item.instanceIndex.ToString(CultureInfo.InvariantCulture)}]";
            var nextPath = string.IsNullOrWhiteSpace(path) ? label : path + " -> " + label;
            if (!ancestors.Add(id))
            {
                result.Add(nextPath + " -> cycle");
                return;
            }
            if (!children.TryGetValue(id, out var nested) || nested.Length == 0) result.Add(nextPath);
            else foreach (var child in nested) AppendStackPath(nextPath, child, children, ancestors, result);
            ancestors.Remove(id);
        }

        private static string[] RelevantErrors(
            SurfaceArrangementSpec spec,
            PlacedDecorItem target,
            IReadOnlyList<PlacedDecorItem> members,
            ValidationReport report)
        {
            if (report?.issues == null) return Array.Empty<string>();
            var ids = new HashSet<string>(StringComparer.Ordinal)
            {
                spec.arrangement_id ?? string.Empty,
                spec.target_element_id ?? string.Empty,
                target?.placementId ?? string.Empty
            };
            foreach (var member in members) ids.Add(member?.placementId ?? string.Empty);
            ids.Remove(string.Empty);
            return report.issues.Where(value => value != null && value.severity == ValidationSeverity.Error)
                .Where(value => (value.elementIds ?? new List<string>()).Any(ids.Contains) ||
                                (!string.IsNullOrWhiteSpace(spec.arrangement_id) &&
                                 (value.message ?? string.Empty).IndexOf(spec.arrangement_id, StringComparison.Ordinal) >= 0))
                .Select(value => value.code ?? "UNKNOWN")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        private static string PlacementHash(PlacedDecorItem target, IEnumerable<PlacedDecorItem> members)
        {
            var placements = new[] { target }.Concat(members ?? Array.Empty<PlacedDecorItem>())
                .Where(value => value != null)
                .OrderBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal)
                .Select(value => string.Join("|",
                    value.placementId ?? string.Empty,
                    value.supportPlacementId ?? string.Empty,
                    value.stackLevel.ToString(CultureInfo.InvariantCulture),
                    Vector(value.position),
                    Rotation(value.rotation),
                    Vector(value.scale)));
            return RoomReviewHashUtility.HashOrdinal(placements);
        }

        private static string Vector(Vector3 value) => string.Join(",",
            RoomReviewHashUtility.Quantize(value.x),
            RoomReviewHashUtility.Quantize(value.y),
            RoomReviewHashUtility.Quantize(value.z));

        private static string Rotation(Quaternion value)
        {
            value = value.normalized;
            if (value.w < 0f) value = new Quaternion(-value.x, -value.y, -value.z, -value.w);
            return string.Join(",",
                RoomReviewHashUtility.Quantize(value.x),
                RoomReviewHashUtility.Quantize(value.y),
                RoomReviewHashUtility.Quantize(value.z),
                RoomReviewHashUtility.Quantize(value.w));
        }
    }
}
