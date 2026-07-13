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
        public string template;
        public string relationSource;
        public string suggestedTemplate;
        public string status = SpatialCalibrationWorkflowStates.Pending;
        public string sessionId;
        public string technicalReportHash;
        public string captureHash;
        public string captureDirectory;
        public string draftPath;
        public string reviewer;
        public string reviewedUtc;
        public string comment;
        public string[] errors = Array.Empty<string>();
        public string[] rawPaths = Array.Empty<string>();
        public string[] evidencePaths = Array.Empty<string>();
        public SpatialCalibrationWorkflowContact[] contacts = Array.Empty<SpatialCalibrationWorkflowContact>();
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
