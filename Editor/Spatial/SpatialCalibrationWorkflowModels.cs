using System;
using System.Collections.Generic;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class SpatialCalibrationWorkflowStates
    {
        public const string Pending = "Pending";
        public const string NeedsRelationReview = "NeedsRelationReview";
        public const string Running = "Running";
        public const string TechnicalFailed = "TechnicalFailed";
        public const string AwaitingHumanReview = "AwaitingHumanReview";
        public const string Approved = "Approved";
        public const string RevisionRequested = "RevisionRequested";
        public const string UnableToJudge = "UnableToJudge";
        public const string Stale = "Stale";
    }

    public static class SpatialCalibrationReviewKinds
    {
        public const string Asset = "asset";
        public const string Interaction = "interaction";
        public const string Arrangement = "arrangement";
    }

    public static class SpatialArrangementRevisionIssues
    {
        public const string TooEmpty = "ARRANGEMENT_TOO_EMPTY";
        public const string TooComplex = "ARRANGEMENT_TOO_COMPLEX";
        public const string TooNeat = "ARRANGEMENT_TOO_NEAT";
        public const string TooMessy = "ARRANGEMENT_TOO_MESSY";
        public const string UnnaturalStack = "ARRANGEMENT_STACK_UNNATURAL";
        public const string AwkwardRelationship = "ARRANGEMENT_RELATIONSHIP_AWKWARD";
    }

    [Serializable]
    public sealed class SpatialCalibrationWorkflowState
    {
        public int schemaVersion = 1;
        public string workflowId;
        public string projectPath;
        public string status = SpatialCalibrationWorkflowStates.Pending;
        public string createdUtc;
        public string updatedUtc;
        public string wallHierarchyPath;
        public int batchSize = 8;
        public bool autoContinue;
        public string reviewPagePath;
        public string agentBriefPath;
        public List<SpatialCalibrationWorkflowItem> items = new();
    }

    [Serializable]
    public sealed class SpatialCalibrationWorkflowItem
    {
        public string id;
        public string assetId;
        public string displayName;
        public string descriptorGuid;
        public string descriptorPath;
        public string dependencyHash;
        public string geometryFamilyHash;
        public string geometryFamilyCanonicalId;
        public string reviewGroupKey;
        public string interactionSignature;
        public string template;
        public string relationSource;
        public string suggestedTemplate;
        public string reviewKind = SpatialCalibrationReviewKinds.Asset;
        public string status = SpatialCalibrationWorkflowStates.Pending;
        public string sessionId;
        public string technicalReportHash;
        public string captureHash;
        public string captureDirectory;
        public string draftPath;
        public string reviewer;
        public string reviewedUtc;
        public string comment;
        public string revisionComment;
        public string[] revisionIssueCodes = Array.Empty<string>();
        public string[] errors = Array.Empty<string>();
        public string[] rawPaths = Array.Empty<string>();
        public string[] evidencePaths = Array.Empty<string>();
        public SpatialCalibrationWorkflowContact[] contacts = Array.Empty<SpatialCalibrationWorkflowContact>();
        public SpatialArrangementReviewEvidence arrangementEvidence;
    }

    [Serializable]
    public sealed class SpatialArrangementReviewEvidence
    {
        public string arrangementId;
        public string targetElementId;
        public string targetFrameId = "top";
        public string preset;
        public int memberCount;
        public int stackCount;
        public int maximumStackLevel;
        public float minimumSupport;
        public float minimumEdgeDistance;
        public string specHash;
        public string placementHash;
        public string[] stackStructure = Array.Empty<string>();
        public string[] errorCodes = Array.Empty<string>();
        public string[] captureViewIds = Array.Empty<string>();
    }

    [Serializable]
    public sealed class SpatialCalibrationWorkflowContact
    {
        public string ruleId;
        public string target;
        public float gap;
        public float penetration;
        public float support;
        public float direction;
    }

    [Serializable]
    public sealed class SpatialCalibrationAgentBrief
    {
        public int schemaVersion = 1;
        public string workflowId;
        public string status;
        public string nextAction;
        public string reviewUrl;
        public int total;
        public int pending;
        public int awaitingReview;
        public int approved;
        public int revisionRequested;
        public int unableToJudge;
        public int blocked;
        public List<SpatialCalibrationAgentBlocker> blockers = new();
    }

    [Serializable]
    public sealed class SpatialCalibrationAgentBlocker
    {
        public string assetId;
        public string status;
        public string suggestedTemplate;
        public string[] errorCodes = Array.Empty<string>();
    }
}
