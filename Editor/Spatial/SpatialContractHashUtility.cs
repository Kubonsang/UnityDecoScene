using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    /// <summary>
    /// Implements the Spatial Contract v1 normalization and hashing rules used by unity-ctx.
    /// Hash verification is intentionally local so approved contracts remain safe when the
    /// optional unity-ctx process is disconnected.
    /// </summary>
    public static class SpatialContractHashUtility
    {
        public static string ComputeGeometryHash(AssetSpatialContractPayload asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            var json = new StringBuilder(1024);
            WriteAsset(json, asset, string.Empty);
            return Sha256(json.ToString());
        }

        public static string ComputeInteractionHash(InteractionSpatialContractPayload interaction)
        {
            if (interaction == null) throw new ArgumentNullException(nameof(interaction));
            var json = new StringBuilder(512);
            WriteInteraction(json, interaction, string.Empty);
            return Sha256(json.ToString());
        }

        public static string ComputeContentHash(SpatialContractDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var json = new StringBuilder(1536);
            json.Append('{');
            Property(json, "contract_version", Math.Max(1, document.contract_version));
            json.Append(',');
            Property(json, "contract_type", document.contract_type ?? string.Empty);
            json.Append(',');
            Property(json, "state", string.Empty);
            if (document.contract_type == "asset" && document.asset != null)
            {
                json.Append(',');
                Name(json, "asset");
                WriteAsset(json, document.asset, ComputeGeometryHash(document.asset));
            }
            else if (document.contract_type == "interaction" && document.interaction != null)
            {
                json.Append(',');
                Name(json, "interaction");
                WriteInteraction(json, document.interaction, ComputeInteractionHash(document.interaction));
            }
            if (document.technical != null)
            {
                json.Append(',');
                Name(json, "technical");
                WriteTechnical(json, document.technical);
            }
            json.Append('}');
            return Sha256(json.ToString());
        }

        public static bool ValidateApproved(SpatialContractDocument document, out string reason)
        {
            reason = null;
            if (document == null) return Fail("CONTRACT_INVALID document is missing.", out reason);
            if (document.contract_version != 1) return Fail("CONTRACT_INVALID contract_version must be 1.", out reason);
            if (!document.IsApproved) return Fail("CONTRACT_NOT_APPROVED human approval is missing.", out reason);
            if (document.technical == null || !document.technical.passed || document.technical.error_count != 0)
                return Fail("CONTRACT_NOT_APPROVED technical evidence must pass with zero errors.", out reason);
            if (document.review == null || string.IsNullOrWhiteSpace(document.review.reviewer))
                return Fail("CONTRACT_NOT_APPROVED reviewer identity is missing.", out reason);

            string captureHash;
            if (document.contract_type == "asset")
            {
                var asset = document.asset;
                // JsonUtility may construct an omitted reference-type field. contract_type is the
                // authoritative union discriminator; only the active payload participates in hashes.
                if (asset == null)
                    return Fail("CONTRACT_INVALID asset payload is missing.", out reason);
                if (string.IsNullOrWhiteSpace(asset.asset_guid) || asset.asset_guid.Length != 32 ||
                    asset.asset_guid.Any(value => !Uri.IsHexDigit(value)))
                    return Fail("CONTRACT_INVALID asset GUID must contain 32 hexadecimal characters.", out reason);
                if (string.IsNullOrWhiteSpace(asset.asset_path) || !asset.asset_path.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal))
                    return Fail("CONTRACT_INVALID asset path must be under Assets/.", out reason);
                if (string.IsNullOrWhiteSpace(asset.dependency_hash) || !string.Equals(asset.units?.Trim(), "meter", StringComparison.OrdinalIgnoreCase))
                    return Fail("CONTRACT_INVALID dependency hash and meter units are required.", out reason);
                if (asset.collision_proxies == null || asset.collision_proxies.Count == 0 || asset.frames == null || asset.frames.Count == 0 || asset.contacts == null || asset.contacts.Count == 0)
                    return Fail("CONTRACT_INVALID geometry proxies, frames, and contacts are required.", out reason);
                var geometryHash = ComputeGeometryHash(asset);
                if (!Same(asset.geometry_hash, geometryHash))
                    return Fail($"CONTRACT_HASH_MISMATCH geometry hash is stale; expected {geometryHash}.", out reason);
                captureHash = asset.capture_set_hash;
            }
            else if (document.contract_type == "interaction")
            {
                var interaction = document.interaction;
                if (interaction == null)
                    return Fail("CONTRACT_INVALID interaction payload is missing.", out reason);
                var interactionHash = ComputeInteractionHash(interaction);
                if (!Same(interaction.interaction_hash, interactionHash))
                    return Fail($"CONTRACT_HASH_MISMATCH interaction hash is stale; expected {interactionHash}.", out reason);
                captureHash = interaction.capture_set_hash;
            }
            else return Fail("CONTRACT_INVALID unsupported contract type.", out reason);

            if (string.IsNullOrWhiteSpace(captureHash) || !Same(captureHash, document.review.capture_set_hash))
                return Fail("CONTRACT_CAPTURE_HASH_MISMATCH review capture is stale.", out reason);
            var contractHash = ComputeContentHash(document);
            if (!Same(contractHash, document.review.contract_hash))
                return Fail($"CONTRACT_HASH_MISMATCH review hash is stale; expected {contractHash}.", out reason);
            return true;
        }

        private static bool Same(string left, string right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool Fail(string value, out string reason)
        {
            reason = value;
            return false;
        }

        private static void WriteAsset(StringBuilder json, AssetSpatialContractPayload asset, string geometryHash)
        {
            json.Append('{');
            Property(json, "asset_guid", (asset.asset_guid ?? string.Empty).Trim().ToLowerInvariant());
            json.Append(','); Property(json, "asset_path", (asset.asset_path ?? string.Empty).Trim().Replace('\\', '/'));
            json.Append(','); Property(json, "dependency_hash", asset.dependency_hash ?? string.Empty);
            json.Append(','); Property(json, "units", (asset.units ?? string.Empty).Trim().ToLowerInvariant());
            json.Append(','); ArrayProperty(json, "forward", asset.forward, 3);
            json.Append(','); ArrayProperty(json, "up", asset.up, 3);
            json.Append(','); ArrayProperty(json, "pivot_offset", asset.pivot_offset, 3);
            json.Append(','); Name(json, "collision_proxies"); WriteObbs(json, asset.collision_proxies);
            if (asset.clearance_proxies != null && asset.clearance_proxies.Count > 0)
            {
                json.Append(','); Name(json, "clearance_proxies"); WriteObbs(json, asset.clearance_proxies);
            }
            json.Append(','); Name(json, "frames"); WriteFrames(json, asset.frames);
            json.Append(','); Name(json, "contacts"); WriteContacts(json, asset.contacts);
            json.Append(','); Property(json, "revision", Math.Max(1, asset.revision));
            json.Append(','); Property(json, "geometry_hash", geometryHash ?? string.Empty);
            json.Append(','); Property(json, "capture_set_hash", asset.capture_set_hash ?? string.Empty);
            json.Append('}');
        }

        private static void WriteInteraction(StringBuilder json, InteractionSpatialContractPayload value, string interactionHash)
        {
            json.Append('{');
            Property(json, "subject_guid", (value.subject_guid ?? string.Empty).Trim().ToLowerInvariant());
            json.Append(','); Property(json, "target_key", value.target_key ?? string.Empty);
            json.Append(','); Property(json, "relation", value.relation ?? string.Empty);
            json.Append(','); Property(json, "subject_frame", value.subject_frame ?? string.Empty);
            json.Append(','); Property(json, "target_frame", value.target_frame ?? string.Empty);
            json.Append(','); ArrayProperty(json, "relative_position", value.relative_position, 3);
            json.Append(','); ArrayProperty(json, "relative_rotation", value.relative_rotation, 4);
            json.Append(','); ArrayProperty(json, "position_tolerance", value.position_tolerance, 3);
            json.Append(','); Property(json, "angle_tolerance", value.angle_tolerance);
            json.Append(','); Property(json, "collision_policy", value.collision_policy ?? string.Empty);
            json.Append(','); Property(json, "revision", Math.Max(1, value.revision));
            json.Append(','); Property(json, "interaction_hash", interactionHash ?? string.Empty);
            json.Append(','); Property(json, "capture_set_hash", value.capture_set_hash ?? string.Empty);
            json.Append('}');
        }

        private static void WriteTechnical(StringBuilder json, SpatialTechnicalEvidence value)
        {
            json.Append('{');
            Property(json, "passed", value.passed);
            json.Append(','); Property(json, "error_count", value.error_count);
            json.Append(','); Property(json, "report_hash", value.report_hash ?? string.Empty);
            json.Append('}');
        }

        private static void WriteObbs(StringBuilder json, IEnumerable<SpatialObbContract> source)
        {
            json.Append('[');
            var first = true;
            foreach (var value in (source ?? Enumerable.Empty<SpatialObbContract>()).Where(item => item != null).OrderBy(item => item.id, StringComparer.Ordinal))
            {
                if (!first) json.Append(',');
                first = false;
                json.Append('{');
                Property(json, "id", value.id ?? string.Empty);
                json.Append(','); ArrayProperty(json, "center", value.center, 3);
                json.Append(','); ArrayProperty(json, "size", value.size, 3);
                json.Append(','); ArrayProperty(json, "rotation", value.rotation, 4);
                json.Append('}');
            }
            json.Append(']');
        }

        private static void WriteFrames(StringBuilder json, IEnumerable<SpatialContactFrameContract> source)
        {
            json.Append('[');
            var first = true;
            foreach (var value in (source ?? Enumerable.Empty<SpatialContactFrameContract>()).Where(item => item != null).OrderBy(item => item.id, StringComparer.Ordinal))
            {
                if (!first) json.Append(',');
                first = false;
                json.Append('{');
                Property(json, "id", value.id ?? string.Empty);
                json.Append(','); ArrayProperty(json, "point", value.point, 3);
                json.Append(','); ArrayProperty(json, "normal", value.normal, 3);
                json.Append(','); ArrayProperty(json, "tangent", value.tangent, 3);
                json.Append(','); ArrayProperty(json, "size", value.size, 2);
                json.Append('}');
            }
            json.Append(']');
        }

        private static void WriteContacts(StringBuilder json, IEnumerable<SpatialContactRuleContract> source)
        {
            json.Append('[');
            var first = true;
            foreach (var value in (source ?? Enumerable.Empty<SpatialContactRuleContract>()).Where(item => item != null).OrderBy(item => item.id, StringComparer.Ordinal))
            {
                if (!first) json.Append(',');
                first = false;
                json.Append('{');
                Property(json, "id", value.id ?? string.Empty);
                json.Append(','); Property(json, "kind", value.kind ?? string.Empty);
                json.Append(','); Property(json, "frame_id", value.frame_id ?? string.Empty);
                json.Append(','); Property(json, "target", value.target ?? string.Empty);
                json.Append(','); Property(json, "minimum_gap", value.minimum_gap);
                json.Append(','); Property(json, "maximum_gap", value.maximum_gap);
                json.Append(','); Property(json, "maximum_penetration", value.maximum_penetration);
                json.Append(','); Property(json, "minimum_support", value.minimum_support);
                json.Append(','); Property(json, "direction_alignment", value.direction_alignment);
                json.Append('}');
            }
            json.Append(']');
        }

        private static void ArrayProperty(StringBuilder json, string name, IReadOnlyList<float> values, int count)
        {
            Name(json, name);
            json.Append('[');
            for (var index = 0; index < count; index++)
            {
                if (index > 0) json.Append(',');
                Number(json, values != null && index < values.Count ? values[index] : 0f);
            }
            json.Append(']');
        }

        private static void Property(StringBuilder json, string name, string value) { Name(json, name); Quote(json, value); }
        private static void Property(StringBuilder json, string name, int value) { Name(json, name); json.Append(value.ToString(CultureInfo.InvariantCulture)); }
        private static void Property(StringBuilder json, string name, bool value) { Name(json, name); json.Append(value ? "true" : "false"); }
        private static void Property(StringBuilder json, string name, float value) { Name(json, name); Number(json, value); }

        private static void Name(StringBuilder json, string value) { Quote(json, value); json.Append(':'); }

        private static void Number(StringBuilder json, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("Spatial Contract numbers must be finite.");
            var rounded = Math.Round((double)value, 6, MidpointRounding.AwayFromZero);
            if (Math.Abs(rounded) < 0.0000005d) rounded = 0d;
            json.Append(rounded.ToString("0.######", CultureInfo.InvariantCulture));
        }

        private static void Quote(StringBuilder json, string value)
        {
            json.Append('"');
            foreach (var character in value ?? string.Empty)
            {
                switch (character)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\b': json.Append("\\b"); break;
                    case '\f': json.Append("\\f"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    default:
                        if (character < 0x20) json.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else json.Append(character);
                        break;
                }
            }
            json.Append('"');
        }

        private static string Sha256(string value)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)).Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }
}
