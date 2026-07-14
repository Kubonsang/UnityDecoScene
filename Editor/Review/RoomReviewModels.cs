using System;
using System.Collections.Generic;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class RoomReviewStates
    {
        public const string Pending = "Pending";
        public const string TechnicalFailed = "TechnicalFailed";
        public const string AwaitingHumanReview = "AwaitingHumanReview";
        public const string Approved = "Approved";
        public const string RevisionRequested = "RevisionRequested";
        public const string UnableToJudge = "UnableToJudge";
        public const string Stale = "Stale";
    }

    public static class RoomReviewDecisions
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string RevisionRequested = "RevisionRequested";
        public const string UnableToJudge = "UnableToJudge";
    }

    public static class RoomReviewNextActions
    {
        public const string RunReview = "RUN_REVIEW";
        public const string FixTechnicalBlockers = "FIX_TECHNICAL_BLOCKERS";
        public const string OpenReview = "OPEN_REVIEW";
        public const string RevisePreview = "REVISE_PREVIEW";
        public const string Recapture = "RECAPTURE";
        public const string ApplyAvailable = "APPLY_AVAILABLE";
    }

    [Flags]
    public enum RoomReviewChangeScope
    {
        None = 0,
        RoomShell = 1 << 0,
        Composition = 1 << 1,
        Placement = 1 << 2,
        Asset = 1 << 3,
        Concept = 1 << 4,
        ValidationRules = 1 << 5,
        CaptureProfile = 1 << 6,
        Presentation = 1 << 7,
        All = RoomShell | Composition | Placement | Asset | Concept | ValidationRules | CaptureProfile | Presentation
    }

    [Serializable]
    public sealed class RoomReviewInputItem
    {
        public string id;
        public RoomReviewChangeScope scope;
        public string hash;
    }

    [Serializable]
    public sealed class RoomReviewInputs
    {
        public int schemaVersion = 1;
        public string targetId;
        public string roomShellHash;
        public string compositionHash;
        public string placementHash;
        public string assetDependencyHash;
        public string conceptHash;
        public string validationRulesHash;
        public string captureProfileHash;
        public string presentationHash;
        public List<RoomReviewInputItem> items = new();
    }

    [Serializable]
    public sealed class RoomReviewChangeSet
    {
        public RoomReviewChangeScope scope;
        public string[] affectedIds = Array.Empty<string>();

        public bool HasChanges => scope != RoomReviewChangeScope.None;
    }

    [Serializable]
    public sealed class RoomReviewCaptureView
    {
        public string id;
        public string contentHash;
        public string imagePath;
        public bool required = true;
    }

    [Serializable]
    public sealed class RoomReviewCapture
    {
        public string captureSetHash;
        public string captureProfileHash;
        public string createdUtc;
        public List<RoomReviewCaptureView> views = new();
    }

    [Serializable]
    public sealed class RoomReviewDecision
    {
        public string value = RoomReviewDecisions.Pending;
        public string reviewer;
        public string reviewedUtc;
        public string[] issueCodes = Array.Empty<string>();
        public string comment;
        public string inputHash;
        public string technicalReportHash;
        public string captureSetHash;
    }

    [Serializable]
    public sealed class RoomReviewRun
    {
        public int schemaVersion = 1;
        public string runId;
        public string targetId;
        public string targetName;
        public string status = RoomReviewStates.Pending;
        public string createdUtc;
        public string updatedUtc;
        public string inputHash;
        public RoomReviewInputs inputs = new();
        public RoomReviewChangeSet changes = new();
        public string technicalReportHash;
        public int technicalErrorCount;
        public string[] technicalErrorCodes = Array.Empty<string>();
        public RoomReviewCapture capture = new();
        public RoomReviewDecision decision = new();
        public string reviewUrl;
    }

    /// <summary>
    /// Small orchestration payload. It deliberately excludes full inputs, transforms,
    /// capture paths, images, validation messages, and review comments.
    /// </summary>
    [Serializable]
    public sealed class RoomReviewAgentBrief
    {
        public int schemaVersion = 1;
        public string runId;
        public string targetId;
        public string targetName;
        public string state;
        public string nextAction;
        public string reviewUrl;
        public string[] changeScopes = Array.Empty<string>();
        public int affectedCount;
        public string[] affectedIds = Array.Empty<string>();
        public string technicalStatus;
        public int technicalErrorCount;
        public string[] technicalErrorCodes = Array.Empty<string>();
        public string technicalReportHash;
        public string captureSetHash;
        public int requiredViewCount;
        public string decision;
        public bool stale;
    }
}
