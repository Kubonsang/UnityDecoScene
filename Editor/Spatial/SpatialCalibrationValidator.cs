using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class SpatialCalibrationValidator
    {
        private const float Epsilon = 0.000001f;

        public static SpatialCalibrationReport Validate(SpatialCalibrationSession session)
        {
            if (session?.SubjectObject == null) throw new ArgumentNullException(nameof(session));
            var report = new SpatialCalibrationReport { session_id = session.SessionId };
            foreach (var rule in session.Rules)
            {
                var surface = ResolveSurface(session, rule);
                var frame = session.Frame(rule.frame_id);
                var evidence = Evaluate(session.SubjectObject.transform, frame, surface, rule);
                report.contacts.Add(evidence);
                if (evidence.penetration > rule.maximum_penetration + Epsilon) report.errors.Add($"[{rule.id}] penetration {evidence.penetration:0.####}m exceeds {rule.maximum_penetration:0.####}m.");
                if (evidence.gap < rule.minimum_gap - Epsilon || evidence.gap > rule.maximum_gap + Epsilon) report.errors.Add($"[{rule.id}] gap {evidence.gap:0.####}m is outside {rule.minimum_gap:0.####}-{rule.maximum_gap:0.####}m.");
                if (evidence.support < rule.minimum_support - Epsilon) report.errors.Add($"[{rule.id}] support {evidence.support:P0} is below {rule.minimum_support:P0}.");
                if (evidence.direction_alignment < rule.direction_alignment - Epsilon) report.errors.Add($"[{rule.id}] direction alignment {evidence.direction_alignment:0.###} is below {rule.direction_alignment:0.###}.");
            }
            if (session.Geometry.collisionProxies == null || session.Geometry.collisionProxies.Count == 0) report.errors.Add("[GEOMETRY] At least one collision OBB is required.");
            report.error_count = report.errors.Count;
            report.status = report.Passed ? SpatialContractStates.AwaitingHumanReview : SpatialContractStates.TechnicalFailed;
            report.report_hash = HashReport(report);
            session.LastReport = report;
            return report;
        }

        private static SpatialCalibrationSurface ResolveSurface(SpatialCalibrationSession session, SpatialContactRuleContract rule)
        {
            if (rule.target == "surface:wall") return session.WallSurface;
            if (rule.target == "surface:floor") return new SpatialCalibrationSurface(Vector3.zero, Vector3.up, Vector3.right, Vector3.forward, new Vector2(6f, 6f));
            return session.TargetSurface(session.SupportedByTargetFrameId);
        }

        private static SpatialContactEvidence Evaluate(Transform subject, ContactFrame frame, SpatialCalibrationSurface surface, SpatialContactRuleContract rule)
        {
            var point = subject.TransformPoint(frame.localPoint);
            var normal = subject.TransformDirection(frame.localNormal).normalized;
            var signedDistance = Vector3.Dot(point - surface.Origin, surface.Normal);
            var gap = Mathf.Max(0f, signedDistance);
            var penetration = Mathf.Max(0f, -signedDistance);
            var alignment = Vector3.Dot(normal, -surface.Normal);
            var width = Mathf.Abs(frame.size.x * subject.lossyScale.x);
            var height = Mathf.Abs(frame.size.y * Mathf.Max(subject.lossyScale.y, subject.lossyScale.z));
            var delta = point - surface.Origin;
            var horizontal = Mathf.Abs(Vector3.Dot(delta, surface.Tangent));
            var vertical = Mathf.Abs(Vector3.Dot(delta, surface.Bitangent));
            var overlapWidth = Overlap(width, surface.Size.x, horizontal);
            var overlapHeight = Overlap(height, surface.Size.y, vertical);
            var support = width <= Epsilon || height <= Epsilon ? 0f : Mathf.Clamp01(overlapWidth * overlapHeight / (width * height));
            return new SpatialContactEvidence
            {
                rule_id = rule.id,
                target = rule.target,
                gap = gap,
                penetration = penetration,
                support = support,
                direction_alignment = alignment,
                contact_point = SpatialContractArrays.Vector(point),
                valid = penetration <= rule.maximum_penetration + Epsilon && gap >= rule.minimum_gap - Epsilon && gap <= rule.maximum_gap + Epsilon && support >= rule.minimum_support - Epsilon && alignment >= rule.direction_alignment - Epsilon
            };
        }

        private static float Overlap(float subjectSize, float targetSize, float offset)
        {
            var left = Mathf.Max(-subjectSize * 0.5f + offset, -targetSize * 0.5f);
            var right = Mathf.Min(subjectSize * 0.5f + offset, targetSize * 0.5f);
            return Mathf.Max(0f, right - left);
        }

        private static string HashReport(SpatialCalibrationReport report)
        {
            var previous = report.report_hash;
            report.report_hash = null;
            var data = Encoding.UTF8.GetBytes(JsonUtility.ToJson(report));
            report.report_hash = previous;
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(data).Select(value => value.ToString("x2")));
        }

    }
}
