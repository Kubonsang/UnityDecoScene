using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class RoomReviewHashUtility
    {
        public const float FloatQuantum = 0.0001f;
        private const double QuantizationScale = 10000d;
        private const string InputSchema = "room-review-input@1";
        private static readonly RoomReviewChangeScope[] IndividualScopes =
        {
            RoomReviewChangeScope.RoomShell,
            RoomReviewChangeScope.Composition,
            RoomReviewChangeScope.Placement,
            RoomReviewChangeScope.Asset,
            RoomReviewChangeScope.Concept,
            RoomReviewChangeScope.ValidationRules,
            RoomReviewChangeScope.CaptureProfile,
            RoomReviewChangeScope.Presentation
        };

        public static long Quantize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), "Review hash inputs must be finite.");
            return checked((long)Math.Round(value * QuantizationScale, MidpointRounding.AwayFromZero));
        }

        public static string Sha256(string value) => Sha256(Encoding.UTF8.GetBytes(value ?? string.Empty));

        public static string HashParts(params string[] parts) => HashParts((IEnumerable<string>)parts);

        public static string HashParts(IEnumerable<string> parts)
        {
            if (parts == null) throw new ArgumentNullException(nameof(parts));
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                var values = parts.Select(Normalize).ToArray();
                writer.Write(values.Length);
                foreach (var value in values) writer.Write(value);
            }
            return Sha256(stream.ToArray());
        }

        public static string HashOrdinal(IEnumerable<string> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            return HashParts(values.Select(Normalize).OrderBy(value => value, StringComparer.Ordinal));
        }

        public static string HashQuantized(IEnumerable<float> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                var quantized = values.Select(Quantize).ToArray();
                writer.Write(quantized.Length);
                foreach (var value in quantized) writer.Write(value);
            }
            return Sha256(stream.ToArray());
        }

        public static string ComputeInputHash(RoomReviewInputs inputs)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(InputSchema);
                writer.Write(inputs.schemaVersion);
                writer.Write(Normalize(inputs.targetId));
                writer.Write(Normalize(inputs.roomShellHash));
                writer.Write(Normalize(inputs.compositionHash));
                writer.Write(Normalize(inputs.placementHash));
                writer.Write(Normalize(inputs.assetDependencyHash));
                writer.Write(Normalize(inputs.conceptHash));
                writer.Write(Normalize(inputs.validationRulesHash));
                writer.Write(Normalize(inputs.captureProfileHash));
                writer.Write(Normalize(inputs.presentationHash));

                var items = CanonicalItems(inputs.items).ToArray();
                writer.Write(items.Length);
                foreach (var item in items)
                {
                    writer.Write((int)item.scope);
                    writer.Write(Normalize(item.id));
                    writer.Write(Normalize(item.hash));
                }
            }
            return Sha256(stream.ToArray());
        }

        public static RoomReviewChangeSet Diff(RoomReviewInputs previous, RoomReviewInputs current)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (previous == null)
            {
                var initialScope = ScalarScope(current);
                foreach (var item in CanonicalItems(current.items)) initialScope |= item.scope & RoomReviewChangeScope.All;
                return new RoomReviewChangeSet
                {
                    scope = initialScope,
                    affectedIds = CanonicalItems(current.items).Select(item => Normalize(item.id))
                        .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray()
                };
            }

            var scope = RoomReviewChangeScope.None;
            var affected = new SortedSet<string>(StringComparer.Ordinal);
            if (!Same(previous.targetId, current.targetId))
            {
                scope |= RoomReviewChangeScope.RoomShell;
                AddIfPresent(affected, previous.targetId);
                AddIfPresent(affected, current.targetId);
            }
            if (!Same(previous.roomShellHash, current.roomShellHash)) scope |= RoomReviewChangeScope.RoomShell;
            if (!Same(previous.compositionHash, current.compositionHash)) scope |= RoomReviewChangeScope.Composition;
            if (!Same(previous.placementHash, current.placementHash)) scope |= RoomReviewChangeScope.Placement;
            if (!Same(previous.assetDependencyHash, current.assetDependencyHash)) scope |= RoomReviewChangeScope.Asset;
            if (!Same(previous.conceptHash, current.conceptHash)) scope |= RoomReviewChangeScope.Concept;
            if (!Same(previous.validationRulesHash, current.validationRulesHash)) scope |= RoomReviewChangeScope.ValidationRules;
            if (!Same(previous.captureProfileHash, current.captureProfileHash)) scope |= RoomReviewChangeScope.CaptureProfile;
            if (!Same(previous.presentationHash, current.presentationHash)) scope |= RoomReviewChangeScope.Presentation;

            var before = GroupItems(previous.items);
            var after = GroupItems(current.items);
            var keys = new HashSet<ItemKey>(before.Keys);
            keys.UnionWith(after.Keys);
            foreach (var key in keys)
            {
                before.TryGetValue(key, out var beforeHashes);
                after.TryGetValue(key, out var afterHashes);
                if (SequenceEqual(beforeHashes, afterHashes)) continue;
                scope |= key.Scope & RoomReviewChangeScope.All;
                AddIfPresent(affected, key.Id);
            }

            return new RoomReviewChangeSet { scope = scope, affectedIds = affected.ToArray() };
        }

        public static bool IsCurrentApproval(RoomReviewRun run)
        {
            if (run?.decision == null || run.capture == null) return false;
            if (!string.Equals(run.status, RoomReviewStates.Approved, StringComparison.Ordinal) ||
                !string.Equals(run.decision.value, RoomReviewDecisions.Approved, StringComparison.Ordinal)) return false;

            var inputHash = string.IsNullOrWhiteSpace(run.inputHash) && run.inputs != null
                ? ComputeInputHash(run.inputs)
                : Normalize(run.inputHash);
            return inputHash.Length > 0 && Normalize(run.technicalReportHash).Length > 0 &&
                   Normalize(run.capture.captureSetHash).Length > 0 &&
                   Same(run.decision.inputHash, inputHash) &&
                   Same(run.decision.technicalReportHash, run.technicalReportHash) &&
                   Same(run.decision.captureSetHash, run.capture.captureSetHash);
        }

        public static RoomReviewAgentBrief BuildAgentBrief(RoomReviewRun run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            var changes = run.changes ?? new RoomReviewChangeSet();
            var capture = run.capture;
            var decision = run.decision?.value ?? RoomReviewDecisions.Pending;
            var stale = string.Equals(run.status, RoomReviewStates.Stale, StringComparison.Ordinal) ||
                        (string.Equals(decision, RoomReviewDecisions.Approved, StringComparison.Ordinal) && !IsCurrentApproval(run));
            var affected = (changes.affectedIds ?? Array.Empty<string>()).Select(Normalize)
                .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return new RoomReviewAgentBrief
            {
                runId = Truncate(Normalize(run.runId), 128),
                targetId = Truncate(Normalize(run.targetId), 128),
                targetName = Truncate(Normalize(run.targetName), 96),
                state = Normalize(run.status),
                nextAction = NextAction(run, stale),
                reviewUrl = Normalize(run.reviewUrl),
                changeScopes = ScopeNames(changes.scope),
                affectedCount = affected.Length,
                affectedIds = affected.Take(12).Select(value => Truncate(value, 96)).ToArray(),
                technicalStatus = TechnicalStatus(run),
                technicalErrorCount = Math.Max(0, run.technicalErrorCount),
                technicalErrorCodes = (run.technicalErrorCodes ?? Array.Empty<string>()).Select(Normalize)
                    .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)
                    .Take(12).Select(value => Truncate(value, 96)).ToArray(),
                technicalReportHash = Normalize(run.technicalReportHash),
                captureSetHash = Normalize(capture?.captureSetHash),
                requiredViewCount = capture?.views?.Count(view => view != null && view.required) ?? 0,
                decision = decision,
                stale = stale
            };
        }

        public static string ToCompactAgentBriefJson(RoomReviewRun run) => JsonUtility.ToJson(BuildAgentBrief(run));

        private static IEnumerable<RoomReviewInputItem> CanonicalItems(IEnumerable<RoomReviewInputItem> items) =>
            (items ?? Array.Empty<RoomReviewInputItem>()).Where(item => item != null)
            .OrderBy(item => (int)item.scope)
            .ThenBy(item => Normalize(item.id), StringComparer.Ordinal)
            .ThenBy(item => Normalize(item.hash), StringComparer.Ordinal);

        private static Dictionary<ItemKey, string[]> GroupItems(IEnumerable<RoomReviewInputItem> items) =>
            CanonicalItems(items).GroupBy(item => new ItemKey(item.scope, Normalize(item.id)))
                .ToDictionary(group => group.Key,
                    group => group.Select(item => Normalize(item.hash)).OrderBy(hash => hash, StringComparer.Ordinal).ToArray());

        private static RoomReviewChangeScope ScalarScope(RoomReviewInputs inputs)
        {
            var scope = RoomReviewChangeScope.None;
            if (!string.IsNullOrEmpty(Normalize(inputs.targetId)) || !string.IsNullOrEmpty(Normalize(inputs.roomShellHash))) scope |= RoomReviewChangeScope.RoomShell;
            if (!string.IsNullOrEmpty(Normalize(inputs.compositionHash))) scope |= RoomReviewChangeScope.Composition;
            if (!string.IsNullOrEmpty(Normalize(inputs.placementHash))) scope |= RoomReviewChangeScope.Placement;
            if (!string.IsNullOrEmpty(Normalize(inputs.assetDependencyHash))) scope |= RoomReviewChangeScope.Asset;
            if (!string.IsNullOrEmpty(Normalize(inputs.conceptHash))) scope |= RoomReviewChangeScope.Concept;
            if (!string.IsNullOrEmpty(Normalize(inputs.validationRulesHash))) scope |= RoomReviewChangeScope.ValidationRules;
            if (!string.IsNullOrEmpty(Normalize(inputs.captureProfileHash))) scope |= RoomReviewChangeScope.CaptureProfile;
            if (!string.IsNullOrEmpty(Normalize(inputs.presentationHash))) scope |= RoomReviewChangeScope.Presentation;
            return scope;
        }

        private static string[] ScopeNames(RoomReviewChangeScope scope) => IndividualScopes
            .Where(value => (scope & value) != 0).Select(value => value.ToString()).ToArray();

        private static string TechnicalStatus(RoomReviewRun run)
        {
            if (string.IsNullOrWhiteSpace(run.technicalReportHash)) return "NotRun";
            return run.technicalErrorCount > 0 || string.Equals(run.status, RoomReviewStates.TechnicalFailed, StringComparison.Ordinal)
                ? "Failed"
                : "Passed";
        }

        private static string NextAction(RoomReviewRun run, bool stale)
        {
            if (run.technicalErrorCount > 0 || string.Equals(run.status, RoomReviewStates.TechnicalFailed, StringComparison.Ordinal))
                return RoomReviewNextActions.FixTechnicalBlockers;
            if (string.Equals(run.status, RoomReviewStates.Stale, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(run.capture?.captureSetHash)) return RoomReviewNextActions.OpenReview;
            if (stale || string.Equals(run.status, RoomReviewStates.Pending, StringComparison.Ordinal)) return RoomReviewNextActions.RunReview;
            if (string.Equals(run.status, RoomReviewStates.AwaitingHumanReview, StringComparison.Ordinal)) return RoomReviewNextActions.OpenReview;
            if (string.Equals(run.status, RoomReviewStates.RevisionRequested, StringComparison.Ordinal)) return RoomReviewNextActions.RevisePreview;
            if (string.Equals(run.status, RoomReviewStates.UnableToJudge, StringComparison.Ordinal)) return RoomReviewNextActions.Recapture;
            if (IsCurrentApproval(run)) return RoomReviewNextActions.ApplyAvailable;
            return RoomReviewNextActions.RunReview;
        }

        private static bool SequenceEqual(string[] left, string[] right)
        {
            left ??= Array.Empty<string>();
            right ??= Array.Empty<string>();
            return left.SequenceEqual(right, StringComparer.Ordinal);
        }

        private static void AddIfPresent(ISet<string> values, string value)
        {
            value = Normalize(value);
            if (value.Length > 0) values.Add(value);
        }

        private static bool Same(string left, string right) => string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
        private static string Normalize(string value) => value ?? string.Empty;
        private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value.Substring(0, maximum);

        private static string Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2")));
        }

        private readonly struct ItemKey : IEquatable<ItemKey>
        {
            public readonly RoomReviewChangeScope Scope;
            public readonly string Id;

            public ItemKey(RoomReviewChangeScope scope, string id)
            {
                Scope = scope;
                Id = id ?? string.Empty;
            }

            public bool Equals(ItemKey other) => Scope == other.Scope && string.Equals(Id, other.Id, StringComparison.Ordinal);
            public override bool Equals(object obj) => obj is ItemKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return ((int)Scope * 397) ^ StringComparer.Ordinal.GetHashCode(Id); }
            }
        }
    }
}
