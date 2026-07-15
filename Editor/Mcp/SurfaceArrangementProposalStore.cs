using System;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [Serializable]
    public sealed class SurfaceArrangementProposal
    {
        public int schemaVersion = 1;
        public string arrangementId;
        public string targetElementId;
        public string targetFrameId = "top";
        public string preset = "InUse";
        public float amount = 0.55f;
        public float orderliness = 0.45f;
        public float grouping = 0.75f;
        public float stacking = 0.55f;
        public float edgeMargin = 0.08f;
        public int maxStackHeight = 3;
        public int seedOffset;
        public string membersJson = "[]";
        public string rationale;
        public string suggestedUtc;
    }

    public static class SurfaceArrangementProposalStore
    {
        private const string SessionKey = "DungeonDecorator.SurfaceArrangement.Proposal.v1";

        public static SurfaceArrangementProposal Load()
        {
            var json = SessionState.GetString(SessionKey, string.Empty);
            return string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<SurfaceArrangementProposal>(json);
        }

        public static SurfaceArrangementProposal Submit(SurfaceArrangementProposal proposal)
        {
            Validate(proposal);
            proposal.targetFrameId = "top";
            proposal.suggestedUtc = DateTime.UtcNow.ToString("O");
            SessionState.SetString(SessionKey, JsonUtility.ToJson(proposal));
            return proposal;
        }

        public static void Clear() => SessionState.EraseString(SessionKey);

        private static void Validate(SurfaceArrangementProposal value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (string.IsNullOrWhiteSpace(value.arrangementId)) throw new ArgumentException("arrangementId is required.");
            if (string.IsNullOrWhiteSpace(value.targetElementId)) throw new ArgumentException("targetElementId is required.");
            if (!string.IsNullOrWhiteSpace(value.targetFrameId)
                && !string.Equals(value.targetFrameId, "top", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Surface Arrangement 0.1 supports only targetFrameId 'top'.");
            if (value.preset is not ("Neat" or "InUse" or "Scattered"))
                throw new ArgumentException("preset must be Neat, InUse, or Scattered.");
            ValidateSlider(value.amount, nameof(value.amount));
            ValidateSlider(value.orderliness, nameof(value.orderliness));
            ValidateSlider(value.grouping, nameof(value.grouping));
            ValidateSlider(value.stacking, nameof(value.stacking));
            if (!float.IsFinite(value.edgeMargin) || value.edgeMargin < 0.04f)
                throw new ArgumentOutOfRangeException(nameof(value.edgeMargin), "edgeMargin must be at least 0.04m.");
            if (value.maxStackHeight is < 1 or > 3)
                throw new ArgumentOutOfRangeException(nameof(value.maxStackHeight), "maxStackHeight must be between 1 and 3.");
            if (value.seedOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(value.seedOffset), "seedOffset must be non-negative.");
            if (string.IsNullOrWhiteSpace(value.membersJson)) value.membersJson = "[]";
            if (value.membersJson.Length > 20000)
                throw new ArgumentException("membersJson is too large.", nameof(value.membersJson));
            try { JsonUtility.FromJson<MemberEnvelope>($"{{\"items\":{value.membersJson}}}"); }
            catch (Exception exception) { throw new ArgumentException("membersJson must be a JSON array.", exception); }
            value.rationale = (value.rationale ?? string.Empty).Trim();
            if (value.rationale.Length > 2000)
                throw new ArgumentException("rationale is too long.", nameof(value.rationale));
        }

        private static void ValidateSlider(float value, string name)
        {
            if (!float.IsFinite(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(name, "Arrangement sliders must be between 0 and 1.");
        }

        [Serializable]
        private sealed class MemberEnvelope
        {
#pragma warning disable CS0649
            public MemberProposal[] items;
#pragma warning restore CS0649
        }

        [Serializable]
        private sealed class MemberProposal
        {
#pragma warning disable CS0649
            public string descriptorId;
            public int minimumCount;
            public int maximumCount;
#pragma warning restore CS0649
        }
    }
}
