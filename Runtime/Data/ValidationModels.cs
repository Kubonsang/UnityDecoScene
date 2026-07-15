using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [Serializable]
    public sealed class ValidationIssue
    {
        public string code;
        public ValidationSeverity severity;
        public string message;
        public List<string> elementIds = new();

        public ValidationIssue() { }

        public ValidationIssue(string issueCode, ValidationSeverity issueSeverity, string issueMessage, params string[] relatedElementIds)
        {
            code = issueCode;
            severity = issueSeverity;
            message = issueMessage;
            if (relatedElementIds != null) elementIds.AddRange(relatedElementIds);
        }
    }

    [Serializable]
    public sealed class VisualQualityScores
    {
        public bool reviewed;
        [Range(0, 100)] public int mood;
        [Range(0, 100)] public int style;
        [Range(0, 100)] public int story;
        [Range(0, 100)] public int composition;
        [TextArea] public string feedback;
    }

    [Serializable]
    public sealed class ValidationReport
    {
        public string sessionId;
        public string manifestHash;
        public string geometryProfileHash;
        public string validationVersion;
        public string reportHash;
        public int seed;
        public List<ValidationIssue> issues = new();
        public VisualQualityScores visualScores = new();

        public bool HasErrors => issues.Exists(item => item.severity == ValidationSeverity.Error);
        public int ErrorCount => issues.FindAll(item => item.severity == ValidationSeverity.Error).Count;
        public int WarningCount => issues.FindAll(item => item.severity == ValidationSeverity.Warning).Count;

        public bool MeetsVisualThresholds(RoomConceptBrief brief)
        {
            if (brief == null) return visualScores.reviewed;
            return visualScores.reviewed &&
                   visualScores.mood >= brief.MinimumMoodScore &&
                   visualScores.style >= brief.MinimumStyleScore &&
                   visualScores.story >= brief.MinimumStoryScore &&
                   visualScores.composition >= brief.MinimumCompositionScore;
        }
    }

    [Serializable]
    public sealed class AssetGap
    {
        public string code;
        public string targetId;
        public DecorRole role;
        public string styleSet;
        public string reason;
        public int requestedCount;
        public int availableCount;
    }

    [Serializable]
    public sealed class AssetGapReport
    {
        public List<AssetGap> gaps = new();
        public bool HasGaps => gaps.Count > 0;
    }
}
