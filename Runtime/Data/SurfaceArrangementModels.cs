using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    public enum SurfaceArrangementPreset
    {
        Neat,
        InUse,
        Scattered
    }

    public static class SurfaceArrangementErrorCodes
    {
        public const string SupportContractMissing = "SUPPORT_CONTRACT_MISSING";
        public const string SupportContractStale = "SUPPORT_CONTRACT_STALE";
        public const string SupportRegionInvalid = "SUPPORT_REGION_INVALID";
        public const string NoFit = "SURFACE_ARRANGEMENT_NO_FIT";
        public const string Overlap = "SURFACE_ARRANGEMENT_OVERLAP";
        public const string OutOfBounds = "SURFACE_ARRANGEMENT_OUT_OF_BOUNDS";
        public const string StackSupportInsufficient = "STACK_SUPPORT_INSUFFICIENT";
    }

    [Serializable]
    public sealed class SurfaceArrangementMemberSpec
    {
        public string descriptor_id;
        [Min(0)] public int minimum_count = 1;
        [Min(0)] public int maximum_count = 1;
        [Range(0f, 1f)] public float selection_weight = 1f;
        public string affinity_group;

        public string DescriptorId => descriptor_id;
        public int MinimumCount => minimum_count;
        public int MaximumCount => maximum_count;
        public float SelectionWeight => selection_weight;
        public string AffinityGroup => affinity_group;
    }

    /// <summary>
    /// Versioned, engine-independent intent for arranging several props on one approved support frame.
    /// Fields intentionally use the storage schema's snake_case names so JsonUtility and unity-ctx share
    /// one wire shape without a migration DTO.
    /// </summary>
    [Serializable]
    public sealed class SurfaceArrangementSpec
    {
        public const int CurrentVersion = 1;
        public const int CurrentResolverVersion = 1;

        public int surface_arrangement_version = CurrentVersion;
        public string arrangement_id;
        public string target_element_id;
        public string target_frame_id = "top";
        public List<SurfaceArrangementMemberSpec> members = new();
        public string preset = nameof(SurfaceArrangementPreset.InUse);
        [Range(0f, 1f)] public float amount = 0.55f;
        [Range(0f, 1f)] public float orderliness = 0.45f;
        [Range(0f, 1f)] public float grouping = 0.75f;
        [Range(0f, 1f)] public float stacking = 0.55f;
        [Min(0f)] public float edge_margin = 0.08f;
        [Range(1, 3)] public int max_stack_height = 3;
        [Min(0)] public long seed_offset;
        public int resolver_version = CurrentResolverVersion;
        public string spec_hash;

        public string ArrangementId => arrangement_id;
        public string TargetElementId => target_element_id;
        public string TargetFrameId => string.IsNullOrWhiteSpace(target_frame_id) ? "top" : target_frame_id;
        public IReadOnlyList<SurfaceArrangementMemberSpec> Members => members;
        public SurfaceArrangementPreset Preset => Enum.TryParse(preset, true, out SurfaceArrangementPreset value)
            ? value
            : SurfaceArrangementPreset.InUse;
        public float Amount => Mathf.Clamp01(amount);
        public float Orderliness => Mathf.Clamp01(orderliness);
        public float Grouping => Mathf.Clamp01(grouping);
        public float Stacking => Mathf.Clamp01(stacking);
        public float EdgeMargin => Mathf.Max(0f, edge_margin);
        public int MaxStackHeight => Mathf.Clamp(max_stack_height, 1, 3);
        public long SeedOffset => Math.Max(0L, seed_offset);

        public void ApplyPreset(SurfaceArrangementPreset value)
        {
            preset = value.ToString();
            switch (value)
            {
                case SurfaceArrangementPreset.Neat:
                    amount = 0.45f; orderliness = 0.90f; grouping = 0.55f; stacking = 0.55f;
                    break;
                case SurfaceArrangementPreset.Scattered:
                    amount = 0.65f; orderliness = 0.15f; grouping = 0.25f; stacking = 0.15f;
                    break;
                default:
                    amount = 0.55f; orderliness = 0.45f; grouping = 0.75f; stacking = 0.55f;
                    break;
            }
        }
    }

    public static class SurfaceArrangementSpecUtility
    {
        public static bool Validate(SurfaceArrangementSpec spec, out string reason)
        {
            if (spec == null) return Fail("Spec is missing.", out reason);
            if (spec.surface_arrangement_version != SurfaceArrangementSpec.CurrentVersion) return Fail("surface_arrangement_version must be 1.", out reason);
            if (spec.resolver_version != SurfaceArrangementSpec.CurrentResolverVersion) return Fail("resolver_version must be 1.", out reason);
            if (string.IsNullOrWhiteSpace(spec.arrangement_id) || string.IsNullOrWhiteSpace(spec.target_element_id) || string.IsNullOrWhiteSpace(spec.target_frame_id))
                return Fail("arrangement_id, target_element_id, and target_frame_id are required.", out reason);
            if (!Enum.GetNames(typeof(SurfaceArrangementPreset)).Contains(spec.preset?.Trim(), StringComparer.Ordinal)) return Fail("preset must be Neat, InUse, or Scattered.", out reason);
            if (!Unit(spec.amount) || !Unit(spec.orderliness) || !Unit(spec.grouping) || !Unit(spec.stacking))
                return Fail("amount, orderliness, grouping, and stacking must be finite values in [0,1].", out reason);
            if (!Finite(spec.edge_margin) || spec.edge_margin < 0f) return Fail("edge_margin must be finite and non-negative.", out reason);
            if (spec.max_stack_height is < 1 or > 3) return Fail("max_stack_height must be in [1,3].", out reason);
            if (spec.seed_offset < 0L) return Fail("seed_offset must be non-negative.", out reason);
            if (spec.members == null || spec.members.Count is < 1 or > 12) return Fail("members must contain between 1 and 12 entries.", out reason);

            var minimumTotal = 0;
            var maximumTotal = 0;
            var hasSelectableMember = false;
            var descriptorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in spec.members)
            {
                if (member == null || string.IsNullOrWhiteSpace(member.descriptor_id) || string.IsNullOrWhiteSpace(member.affinity_group)) return Fail("Every member needs descriptor_id and affinity_group.", out reason);
                if (!descriptorIds.Add(member.descriptor_id.Trim())) return Fail($"Duplicate member descriptor_id '{member.descriptor_id.Trim()}'.", out reason);
                if (member.minimum_count < 0 || member.maximum_count < 1 || member.maximum_count < member.minimum_count || member.maximum_count > 12) return Fail("Member counts must satisfy 0 <= minimum_count <= maximum_count <= 12 and maximum_count >= 1.", out reason);
                if (!Unit(member.selection_weight)) return Fail("selection_weight must be finite and in [0,1].", out reason);
                minimumTotal += member.minimum_count;
                maximumTotal += member.maximum_count;
                hasSelectableMember |= member.selection_weight > 0f;
            }
            if (minimumTotal < 1 || minimumTotal > 12 || maximumTotal > 12) return Fail("Total member counts must satisfy 1 <= minimum total <= maximum total <= 12.", out reason);
            if (!hasSelectableMember) return Fail("At least one member must have positive selection_weight.", out reason);

            if (!string.IsNullOrWhiteSpace(spec.spec_hash))
            {
                var expected = ComputeSpecHash(spec);
                if (!string.Equals(spec.spec_hash.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                    return Fail($"spec_hash is stale; expected {expected}.", out reason);
            }
            reason = null;
            return true;
        }

        public static string ComputeSpecHash(SurfaceArrangementSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(CanonicalJson(spec)))
                .Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        public static string CanonicalJson(SurfaceArrangementSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            var json = new StringBuilder(1024).Append('{');
            Integer(json, "surface_arrangement_version", spec.surface_arrangement_version);
            Comma(json); String(json, "arrangement_id", spec.arrangement_id?.Trim());
            Comma(json); String(json, "target_element_id", spec.target_element_id?.Trim());
            Comma(json); String(json, "target_frame_id", spec.target_frame_id?.Trim());
            Comma(json); Name(json, "members"); json.Append('[');
            var orderedMembers = (spec.members ?? new List<SurfaceArrangementMemberSpec>())
                .Where(value => value != null)
                .OrderBy(value => value.descriptor_id?.Trim() ?? string.Empty, StringComparer.Ordinal)
                .ToArray();
            for (var index = 0; index < orderedMembers.Length; index++)
            {
                if (index > 0) json.Append(',');
                var member = orderedMembers[index];
                json.Append('{');
                String(json, "descriptor_id", member.descriptor_id?.Trim());
                Comma(json); Integer(json, "minimum_count", member.minimum_count);
                Comma(json); Integer(json, "maximum_count", member.maximum_count);
                Comma(json); Number(json, "selection_weight", member.selection_weight);
                Comma(json); String(json, "affinity_group", member.affinity_group?.Trim());
                json.Append('}');
            }
            json.Append(']');
            Comma(json); String(json, "preset", spec.preset?.Trim());
            Comma(json); Number(json, "amount", spec.amount);
            Comma(json); Number(json, "orderliness", spec.orderliness);
            Comma(json); Number(json, "grouping", spec.grouping);
            Comma(json); Number(json, "stacking", spec.stacking);
            Comma(json); Number(json, "edge_margin", spec.edge_margin);
            Comma(json); Integer(json, "max_stack_height", spec.max_stack_height);
            Comma(json); Integer(json, "seed_offset", spec.seed_offset);
            Comma(json); Integer(json, "resolver_version", spec.resolver_version);
            Comma(json); String(json, "spec_hash", string.Empty);
            return json.Append('}').ToString();
        }

        private static void Comma(StringBuilder value) => value.Append(',');
        private static void Integer(StringBuilder value, string name, int number) { Name(value, name); value.Append(number.ToString(CultureInfo.InvariantCulture)); }
        private static void Integer(StringBuilder value, string name, long number) { Name(value, name); value.Append(number.ToString(CultureInfo.InvariantCulture)); }
        private static void Number(StringBuilder value, string name, float number) { Name(value, name); AppendNumber(value, number); }
        private static void String(StringBuilder value, string name, string text) { Name(value, name); Quote(value, text ?? string.Empty); }
        private static void Name(StringBuilder value, string name) { Quote(value, name); value.Append(':'); }

        private static void AppendNumber(StringBuilder value, float number)
        {
            if (!Finite(number)) throw new ArgumentException("Arrangement numbers must be finite.");
            var rounded = Math.Round((double)number, 6, MidpointRounding.AwayFromZero);
            if (Math.Abs(rounded) < 0.0000005d) rounded = 0d;
            value.Append(rounded.ToString("0.######", CultureInfo.InvariantCulture));
        }

        private static void Quote(StringBuilder value, string text)
        {
            value.Append('"');
            foreach (var character in text ?? string.Empty)
            {
                switch (character)
                {
                    case '"': value.Append("\\\""); break;
                    case '\\': value.Append("\\\\"); break;
                    case '\b': value.Append("\\b"); break;
                    case '\f': value.Append("\\f"); break;
                    case '\n': value.Append("\\n"); break;
                    case '\r': value.Append("\\r"); break;
                    case '\t': value.Append("\\t"); break;
                    case '<': value.Append("\\u003c"); break;
                    case '>': value.Append("\\u003e"); break;
                    case '&': value.Append("\\u0026"); break;
                    default:
                        if (character < 0x20 || character == '\u2028' || character == '\u2029')
                            value.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else value.Append(character);
                        break;
                }
            }
            value.Append('"');
        }

        private static bool Unit(float value) => Finite(value) && value >= 0f && value <= 1f;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Fail(string value, out string reason) { reason = value; return false; }
    }
}
