using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public sealed class SupportAssetIdentity
    {
        public string DescriptorId { get; }
        public string AssetGuid { get; }
        public string ApprovedGeometryHash { get; }
        public string CurrentGeometryHash { get; }
        public string GeometryFamilyHash { get; }

        public bool IsCurrent => !string.IsNullOrWhiteSpace(ApprovedGeometryHash) &&
                                 string.Equals(ApprovedGeometryHash, CurrentGeometryHash, StringComparison.OrdinalIgnoreCase);

        public SupportAssetIdentity(string descriptorId, string assetGuid, string approvedGeometryHash, string currentGeometryHash, string geometryFamilyHash = null)
        {
            DescriptorId = descriptorId?.Trim() ?? string.Empty;
            AssetGuid = assetGuid?.Trim().ToLowerInvariant() ?? string.Empty;
            ApprovedGeometryHash = approvedGeometryHash?.Trim().ToLowerInvariant() ?? string.Empty;
            CurrentGeometryHash = currentGeometryHash?.Trim().ToLowerInvariant() ?? string.Empty;
            GeometryFamilyHash = geometryFamilyHash?.Trim().ToLowerInvariant() ?? string.Empty;
        }
    }

    public sealed class ResolvedSupportContract
    {
        public InteractionSpatialContractPayload Interaction { get; }
        public string InteractionHash => Interaction?.interaction_hash ?? string.Empty;
        public string CanonicalSubjectDescriptorId { get; }
        public string CanonicalTargetDescriptorId { get; }
        public bool SubjectUsesGeometryFamilyAlias { get; }
        public bool TargetUsesGeometryFamilyAlias { get; }

        internal ResolvedSupportContract(
            InteractionSpatialContractPayload interaction,
            string canonicalSubjectDescriptorId,
            string canonicalTargetDescriptorId,
            bool subjectAlias,
            bool targetAlias)
        {
            Interaction = interaction;
            CanonicalSubjectDescriptorId = canonicalSubjectDescriptorId;
            CanonicalTargetDescriptorId = canonicalTargetDescriptorId;
            SubjectUsesGeometryFamilyAlias = subjectAlias;
            TargetUsesGeometryFamilyAlias = targetAlias;
        }
    }

    public sealed class SupportInteractionBinding
    {
        public SpatialContractDocument Document { get; }
        public string CanonicalSubjectDescriptorId { get; }
        public string CanonicalTargetDescriptorId { get; }
        public string SubjectGeometryHash { get; }
        public string TargetGeometryHash { get; }

        public SupportInteractionBinding(
            SpatialContractDocument document,
            string canonicalSubjectDescriptorId,
            string canonicalTargetDescriptorId,
            string subjectGeometryHash,
            string targetGeometryHash)
        {
            Document = document;
            CanonicalSubjectDescriptorId = canonicalSubjectDescriptorId?.Trim() ?? string.Empty;
            CanonicalTargetDescriptorId = canonicalTargetDescriptorId?.Trim() ?? string.Empty;
            SubjectGeometryHash = subjectGeometryHash?.Trim().ToLowerInvariant() ?? string.Empty;
            TargetGeometryHash = targetGeometryHash?.Trim().ToLowerInvariant() ?? string.Empty;
        }
    }

    /// <summary>
    /// Resolves only human-approved SupportedBy contracts. Asset identities deliberately carry both
    /// the hash approved with the interaction and the latest geometry hash; resolution never guesses
    /// through a stale binding. Geometry-family aliases let material-only variants reuse one pose.
    /// </summary>
    public sealed class SupportContractCatalog
    {
        private readonly Dictionary<string, SupportAssetIdentity> assets = new(StringComparer.Ordinal);
        private readonly List<RegisteredInteraction> interactions = new();

        public IReadOnlyList<SupportAssetIdentity> Assets => assets.Values
            .OrderBy(value => value.DescriptorId, StringComparer.Ordinal).ToArray();

        public void RegisterAsset(SupportAssetIdentity identity)
        {
            if (identity == null) throw new ArgumentNullException(nameof(identity));
            if (string.IsNullOrWhiteSpace(identity.DescriptorId)) throw new ArgumentException("DescriptorId is required.", nameof(identity));
            if (string.IsNullOrWhiteSpace(identity.AssetGuid)) throw new ArgumentException("AssetGuid is required.", nameof(identity));
            assets[identity.DescriptorId] = identity;
        }

        public bool RegisterApprovedInteraction(SupportInteractionBinding binding, out string reason)
        {
            reason = null;
            if (binding == null) { reason = "Interaction binding is required."; return false; }
            var document = binding.Document;
            var canonicalSubjectDescriptorId = binding.CanonicalSubjectDescriptorId;
            var canonicalTargetDescriptorId = binding.CanonicalTargetDescriptorId;
            if (document == null || document.contract_type != "interaction" || document.interaction == null)
            {
                reason = "Only an interaction contract can be registered.";
                return false;
            }
            if (!SpatialContractHashUtility.ValidateApproved(document, out reason)) return false;
            if (!string.Equals(document.interaction.relation, "SupportedBy", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Only SupportedBy interactions are supported.";
                return false;
            }
            if (!string.Equals(document.interaction.collision_policy, "contact-only", StringComparison.OrdinalIgnoreCase))
            {
                reason = "SupportedBy interactions must use the contact-only collision policy.";
                return false;
            }
            if (!assets.TryGetValue(canonicalSubjectDescriptorId ?? string.Empty, out var subject) ||
                !assets.TryGetValue(canonicalTargetDescriptorId ?? string.Empty, out var target))
            {
                reason = "Canonical subject and target identities must be registered first.";
                return false;
            }
            if (!string.Equals(document.interaction.subject_guid, subject.AssetGuid, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(document.interaction.target_key, "asset:" + target.AssetGuid, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Interaction GUIDs do not match the canonical asset identities.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(binding.SubjectGeometryHash) || string.IsNullOrWhiteSpace(binding.TargetGeometryHash) ||
                !string.Equals(binding.SubjectGeometryHash, subject.ApprovedGeometryHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(binding.TargetGeometryHash, target.ApprovedGeometryHash, StringComparison.OrdinalIgnoreCase))
            {
                reason = "Interaction binding geometry hashes do not match the approved canonical assets.";
                return false;
            }

            interactions.RemoveAll(value =>
                string.Equals(value.SubjectDescriptorId, canonicalSubjectDescriptorId, StringComparison.Ordinal) &&
                string.Equals(value.TargetDescriptorId, canonicalTargetDescriptorId, StringComparison.Ordinal));
            interactions.Add(new RegisteredInteraction(
                document.interaction,
                canonicalSubjectDescriptorId,
                canonicalTargetDescriptorId,
                binding.SubjectGeometryHash,
                binding.TargetGeometryHash,
                document.review?.contract_hash,
                document.review?.capture_set_hash));
            interactions.Sort(RegisteredInteraction.Compare);
            return true;
        }

        public bool TryResolve(string subjectDescriptorId, string targetDescriptorId, out ResolvedSupportContract contract, out string errorCode)
        {
            contract = null;
            errorCode = SurfaceArrangementErrorCodes.SupportContractMissing;
            if (!assets.TryGetValue(subjectDescriptorId ?? string.Empty, out var actualSubject) ||
                !assets.TryGetValue(targetDescriptorId ?? string.Empty, out var actualTarget)) return false;
            if (!actualSubject.IsCurrent || !actualTarget.IsCurrent)
            {
                errorCode = SurfaceArrangementErrorCodes.SupportContractStale;
                return false;
            }

            var matching = interactions.Where(value =>
            {
                if (!assets.TryGetValue(value.SubjectDescriptorId, out var canonicalSubject) ||
                    !assets.TryGetValue(value.TargetDescriptorId, out var canonicalTarget)) return false;
                return IdentityMatches(actualSubject, canonicalSubject) && IdentityMatches(actualTarget, canonicalTarget);
            }).OrderBy(value =>
            {
                var canonicalSubject = assets[value.SubjectDescriptorId];
                var canonicalTarget = assets[value.TargetDescriptorId];
                var subjectAlias = !string.Equals(actualSubject.AssetGuid, canonicalSubject.AssetGuid, StringComparison.OrdinalIgnoreCase);
                var targetAlias = !string.Equals(actualTarget.AssetGuid, canonicalTarget.AssetGuid, StringComparison.OrdinalIgnoreCase);
                return (subjectAlias ? 1 : 0) + (targetAlias ? 1 : 0);
            }).ThenBy(value => value.SubjectDescriptorId, StringComparer.Ordinal)
              .ThenBy(value => value.TargetDescriptorId, StringComparer.Ordinal)
              .ThenBy(value => value.Interaction?.interaction_hash, StringComparer.Ordinal)
              .ToArray();
            if (matching.Length == 0) return false;

            var sawStale = false;
            foreach (var value in matching)
            {
                var canonicalSubject = assets[value.SubjectDescriptorId];
                var canonicalTarget = assets[value.TargetDescriptorId];
                if (!canonicalSubject.IsCurrent || !canonicalTarget.IsCurrent ||
                    !string.Equals(value.SubjectGeometryHash, canonicalSubject.CurrentGeometryHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(value.TargetGeometryHash, canonicalTarget.CurrentGeometryHash, StringComparison.OrdinalIgnoreCase))
                {
                    sawStale = true;
                    continue;
                }
                contract = new ResolvedSupportContract(
                    value.Interaction,
                    value.SubjectDescriptorId,
                    value.TargetDescriptorId,
                    !string.Equals(actualSubject.AssetGuid, canonicalSubject.AssetGuid, StringComparison.OrdinalIgnoreCase),
                    !string.Equals(actualTarget.AssetGuid, canonicalTarget.AssetGuid, StringComparison.OrdinalIgnoreCase));
                errorCode = null;
                return true;
            }

            if (sawStale) errorCode = SurfaceArrangementErrorCodes.SupportContractStale;
            return false;
        }

        public string ComputeHash()
        {
            var value = new StringBuilder("support-contract-catalog@1");
            foreach (var asset in assets.Values.OrderBy(item => item.DescriptorId, StringComparer.Ordinal))
            {
                value.Append('|').Append(asset.DescriptorId)
                    .Append(':').Append(asset.AssetGuid)
                    .Append(':').Append(asset.ApprovedGeometryHash)
                    .Append(':').Append(asset.CurrentGeometryHash)
                    .Append(':').Append(asset.GeometryFamilyHash);
            }
            foreach (var interaction in interactions.OrderBy(item => item, Comparer<RegisteredInteraction>.Create(RegisteredInteraction.Compare)))
            {
                value.Append('|').Append(interaction.SubjectDescriptorId)
                    .Append('>').Append(interaction.TargetDescriptorId)
                    .Append(':').Append(interaction.Interaction?.interaction_hash ?? string.Empty)
                    .Append(':').Append(interaction.SubjectGeometryHash)
                    .Append(':').Append(interaction.TargetGeometryHash)
                    .Append(':').Append(interaction.ApprovalContractHash)
                    .Append(':').Append(interaction.ApprovalCaptureHash);
            }
            return Hash128.Compute(value.ToString()).ToString();
        }

        private static bool IdentityMatches(SupportAssetIdentity actual, SupportAssetIdentity canonical)
        {
            if (string.Equals(actual.AssetGuid, canonical.AssetGuid, StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrWhiteSpace(actual.GeometryFamilyHash) &&
                   string.Equals(actual.GeometryFamilyHash, canonical.GeometryFamilyHash, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class RegisteredInteraction
        {
            public InteractionSpatialContractPayload Interaction { get; }
            public string SubjectDescriptorId { get; }
            public string TargetDescriptorId { get; }
            public string SubjectGeometryHash { get; }
            public string TargetGeometryHash { get; }
            public string ApprovalContractHash { get; }
            public string ApprovalCaptureHash { get; }

            public RegisteredInteraction(
                InteractionSpatialContractPayload interaction,
                string subjectDescriptorId,
                string targetDescriptorId,
                string subjectGeometryHash,
                string targetGeometryHash,
                string approvalContractHash,
                string approvalCaptureHash)
            {
                Interaction = interaction;
                SubjectDescriptorId = subjectDescriptorId;
                TargetDescriptorId = targetDescriptorId;
                SubjectGeometryHash = subjectGeometryHash;
                TargetGeometryHash = targetGeometryHash;
                ApprovalContractHash = approvalContractHash?.Trim().ToLowerInvariant() ?? string.Empty;
                ApprovalCaptureHash = approvalCaptureHash?.Trim().ToLowerInvariant() ?? string.Empty;
            }

            public static int Compare(RegisteredInteraction left, RegisteredInteraction right)
            {
                var value = string.Compare(left.SubjectDescriptorId, right.SubjectDescriptorId, StringComparison.Ordinal);
                if (value != 0) return value;
                value = string.Compare(left.TargetDescriptorId, right.TargetDescriptorId, StringComparison.Ordinal);
                if (value != 0) return value;
                return string.Compare(left.Interaction?.interaction_hash, right.Interaction?.interaction_hash, StringComparison.Ordinal);
            }
        }
    }
}
