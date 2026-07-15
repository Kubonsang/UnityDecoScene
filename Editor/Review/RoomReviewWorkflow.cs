using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [Serializable]
    public sealed class RoomReviewVerificationResult
    {
        public bool valid;
        public string reason;
        public string runId;
        public string inputHash;
        public string technicalReportHash;
        public string captureSetHash;
        public int technicalErrorCount;
    }

    [InitializeOnLoad]
    public static class RoomReviewWorkflow
    {
        public const string RootRelativePath = "Library/DungeonDecorator/RoomReviews";
        public const string CurrentRelativePath = RootRelativePath + "/current.json";
        public const string ReviewUrl = "http://127.0.0.1:4174/room-review";
        private const string ReviewTemplatePath = "Packages/com.unitydecoscene.dungeon-decorator/Editor/Review/Templates/RoomReviewTemplate.html";

        private static double nextRefresh;

        public static event Action Changed;

        static RoomReviewWorkflow()
        {
            EditorApplication.update += RefreshExternalDecision;
        }

        public static string RootPath => ProjectPath(RootRelativePath);
        public static string CurrentPath => ProjectPath(CurrentRelativePath);

        [MenuItem("Tools/Concept Room Decorator/Room Review/Inspect Current Room")]
        public static void PrepareCurrentMenu() => PrepareCurrent(true);

        [MenuItem("Tools/Concept Room Decorator/Room Review/Open Human Review")]
        public static void OpenReviewMenu() => OpenReview();

        public static RoomReviewRun PrepareCurrent(bool openReview)
        {
            if (RoomPreviewManager.Current == null) throw new InvalidOperationException("Generate a room preview before starting review.");
            return Prepare(RoomPreviewManager.Current, openReview);
        }

        public static RoomReviewRun Prepare(PreviewSession session, bool openReview = false)
        {
            if (session?.Plan?.Room == null) throw new ArgumentException("An active room preview is required.", nameof(session));
            RoomPreviewManager.SynchronizePreviewTransforms();
            var inputs = RoomReviewSnapshotService.Capture(session);
            var inputHash = RoomReviewHashUtility.ComputeInputHash(inputs);
            var previous = LoadForTarget(inputs.targetId);
            var report = PreviewValidationService.Validate(session);
            session.ApprovalSnapshot ??= new PreviewApprovalSnapshot();
            session.ApprovalSnapshot.technicalReportHash = report.reportHash;
            session.ApprovalSnapshot.humanApproved = false;
            var now = DateTime.UtcNow.ToString("O");
            var changes = RoomReviewHashUtility.Diff(previous?.inputs, inputs);
            var runId = inputHash.Substring(0, 20);
            var run = new RoomReviewRun
            {
                runId = runId,
                targetId = inputs.targetId,
                targetName = session.Plan.Room.name,
                createdUtc = previous != null && string.Equals(previous.runId, runId, StringComparison.Ordinal) ? previous.createdUtc : now,
                updatedUtc = now,
                inputHash = inputHash,
                inputs = inputs,
                changes = changes,
                technicalReportHash = report.reportHash,
                technicalErrorCount = report.ErrorCount,
                technicalErrorCodes = report.issues.Where(value => value.severity == ValidationSeverity.Error)
                    .Select(value => value.code ?? "UNKNOWN").Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                arrangementEvidence = SurfaceArrangementReviewEvidenceService.Build(session, report),
                reviewUrl = ReviewUrl
            };

            if (report.HasErrors)
            {
                run.status = RoomReviewStates.TechnicalFailed;
                Save(run);
                Changed?.Invoke();
                return run;
            }

            var targetDirectory = TargetDirectory(inputs.targetId);
            var captureDirectory = Path.Combine(targetDirectory, "runs", runId, "captures");
            var captured = RoomCaptureService.CaptureSet(session, captureDirectory, inputHash);
            run.capture = new RoomReviewCapture
            {
                captureSetHash = captured.captureSetHash,
                captureProfileHash = inputs.captureProfileHash,
                createdUtc = captured.reusedFromCache && previous?.capture != null &&
                             string.Equals(previous.capture.captureSetHash, captured.captureSetHash, StringComparison.Ordinal)
                    ? previous.capture.createdUtc
                    : now,
                views = captured.entries.Select(value => new RoomReviewCaptureView
                {
                    id = value.viewId,
                    contentHash = value.contentHash,
                    imagePath = value.path,
                    required = true
                }).ToList()
            };
            session.ApprovalSnapshot.captureSetHash = run.capture.captureSetHash;

            var evidenceUnchanged = previous != null &&
                                    string.Equals(previous.inputHash, run.inputHash, StringComparison.Ordinal) &&
                                    string.Equals(previous.technicalReportHash, run.technicalReportHash, StringComparison.Ordinal) &&
                                    string.Equals(previous.capture?.captureSetHash, run.capture.captureSetHash, StringComparison.Ordinal) &&
                                    DecisionMatches(previous.decision, run);
            if (evidenceUnchanged)
            {
                run.decision = previous.decision;
                run.status = previous.status;
                run.changes = new RoomReviewChangeSet();
            }
            else if (previous != null && string.Equals(previous.decision?.value, RoomReviewDecisions.Approved, StringComparison.Ordinal))
            {
                run.decision = previous.decision;
                run.status = RoomReviewStates.Stale;
            }
            else
            {
                run.decision = new RoomReviewDecision();
                run.status = RoomReviewStates.AwaitingHumanReview;
            }

            Save(run);
            session.ApprovalSnapshot.humanApproved = RoomReviewHashUtility.IsCurrentApproval(run);
            Changed?.Invoke();
            if (openReview) OpenReview();
            return run;
        }

        public static RoomReviewRun GetDisplayState(PreviewSession session = null)
        {
            session ??= RoomPreviewManager.Current;
            if (session?.Plan?.Room == null) return LoadCurrent();
            var run = LoadForTarget(StableTargetId(session.Plan.Room));
            if (run == null) return null;
            var currentInputs = RoomReviewSnapshotService.Capture(session);
            var currentHash = RoomReviewHashUtility.ComputeInputHash(currentInputs);
            if (string.Equals(currentHash, run.inputHash, StringComparison.Ordinal)) return run;
            var display = JsonUtility.FromJson<RoomReviewRun>(JsonUtility.ToJson(run));
            display.status = RoomReviewStates.Stale;
            display.changes = RoomReviewHashUtility.Diff(run.inputs, currentInputs);
            return display;
        }

        public static RoomReviewAgentBrief GetAgentBrief()
        {
            var run = GetDisplayState();
            return run == null ? null : RoomReviewHashUtility.BuildAgentBrief(run);
        }

        public static bool HasCurrentHumanApproval(PreviewSession session, ValidationReport report, out string reason)
        {
            reason = string.Empty;
            if (session?.Plan?.Room == null)
            {
                reason = "No active room preview.";
                return false;
            }
            if (report == null || report.HasErrors)
            {
                reason = "Technical validation must pass before Apply.";
                return false;
            }

            var run = LoadForTarget(StableTargetId(session.Plan.Room));
            if (run == null)
            {
                reason = "No human room review has been prepared.";
                return false;
            }
            var inputs = RoomReviewSnapshotService.Capture(session);
            var inputHash = RoomReviewHashUtility.ComputeInputHash(inputs);
            if (!string.Equals(inputHash, run.inputHash, StringComparison.Ordinal))
            {
                reason = "The room changed after its last review.";
                return false;
            }
            if (!string.Equals(report.reportHash, run.technicalReportHash, StringComparison.Ordinal))
            {
                reason = "The technical report changed after its last review.";
                return false;
            }
            if (!RoomReviewHashUtility.IsCurrentApproval(run))
            {
                reason = "The latest room evidence has not been explicitly approved by the user.";
                return false;
            }
            if (!ValidateCaptureEvidence(run.capture, out reason)) return false;
            session.ApprovalSnapshot ??= new PreviewApprovalSnapshot();
            session.ApprovalSnapshot.technicalReportHash = report.reportHash;
            session.ApprovalSnapshot.captureSetHash = run.capture.captureSetHash;
            session.ApprovalSnapshot.humanApproved = true;
            return true;
        }

        public static RoomReviewVerificationResult VerifyCurrent(
            string runId,
            string inputHash,
            string technicalReportHash,
            string captureSetHash)
        {
            var result = new RoomReviewVerificationResult
            {
                runId = runId ?? string.Empty,
                inputHash = inputHash ?? string.Empty,
                technicalReportHash = technicalReportHash ?? string.Empty,
                captureSetHash = captureSetHash ?? string.Empty
            };
            var session = RoomPreviewManager.Current;
            if (session?.Plan?.Room == null) return Fail(result, "No active room preview is available in Unity.");
            RoomPreviewManager.SynchronizePreviewTransforms();
            var state = LoadForTarget(StableTargetId(session.Plan.Room));
            if (state == null) return Fail(result, "The current review state is missing.");
            if (!Same(runId, state.runId) || !Same(inputHash, state.inputHash) ||
                !Same(technicalReportHash, state.technicalReportHash) || !Same(captureSetHash, state.capture?.captureSetHash))
                return Fail(result, "The browser review evidence is stale.");

            var inputs = RoomReviewSnapshotService.Capture(session);
            var actualInputHash = RoomReviewHashUtility.ComputeInputHash(inputs);
            if (!Same(actualInputHash, state.inputHash)) return Fail(result, "The room changed while it was being reviewed.");
            var report = PreviewValidationService.Validate(session);
            result.technicalErrorCount = report.ErrorCount;
            if (report.HasErrors) return Fail(result, "Technical validation no longer passes.");
            if (!Same(report.reportHash, state.technicalReportHash)) return Fail(result, "The technical report changed while it was being reviewed.");
            if (!ValidateCaptureEvidence(state.capture, out var captureReason)) return Fail(result, captureReason);
            result.valid = true;
            result.reason = "Current deterministic and capture evidence matches the review run.";
            result.inputHash = actualInputHash;
            result.technicalReportHash = report.reportHash;
            result.captureSetHash = state.capture.captureSetHash;
            return result;
        }

        public static void OpenReview()
        {
            var run = LoadCurrent();
            if (run == null) throw new InvalidOperationException("Prepare a room review first.");
            SpatialCalibrationWorkflow.EnsureReviewBridgeRunning();
            Application.OpenURL(ReviewUrl);
        }

        public static RoomReviewRun LoadCurrent()
        {
            if (!File.Exists(CurrentPath)) return null;
            try
            {
                var pointer = JsonUtility.FromJson<CurrentPointer>(File.ReadAllText(CurrentPath, Encoding.UTF8));
                if (pointer == null || string.IsNullOrWhiteSpace(pointer.statePath)) return null;
                var statePath = Path.GetFullPath(pointer.statePath);
                return Inside(RootPath, statePath) ? Load(statePath) : null;
            }
            catch { return null; }
        }

        public static string StatePathFor(string targetId) => Path.Combine(TargetDirectory(targetId), "state.json");

        private static RoomReviewRun LoadForTarget(string targetId) => Load(StatePathFor(targetId));

        private static RoomReviewRun Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var value = JsonUtility.FromJson<RoomReviewRun>(File.ReadAllText(path, Encoding.UTF8));
                return value?.schemaVersion == 1 ? value : null;
            }
            catch { return null; }
        }

        private static void Save(RoomReviewRun run)
        {
            var targetDirectory = TargetDirectory(run.targetId);
            Directory.CreateDirectory(targetDirectory);
            var statePath = Path.Combine(targetDirectory, "state.json");
            AtomicWrite(statePath, JsonUtility.ToJson(run, true) + Environment.NewLine);
            var runDirectory = Path.Combine(targetDirectory, "runs", run.runId);
            Directory.CreateDirectory(runDirectory);
            AtomicWrite(Path.Combine(runDirectory, "run.json"), JsonUtility.ToJson(run, true) + Environment.NewLine);
            AtomicWrite(Path.Combine(targetDirectory, "agent-brief.json"), RoomReviewHashUtility.ToCompactAgentBriefJson(run) + Environment.NewLine);
            if (File.Exists(ReviewTemplatePath)) AtomicWrite(Path.Combine(targetDirectory, "review.html"), File.ReadAllText(ReviewTemplatePath, Encoding.UTF8));
            AtomicWrite(CurrentPath, JsonUtility.ToJson(new CurrentPointer
            {
                roomKey = SafeKey(run.targetId),
                statePath = Path.GetFullPath(statePath)
            }, true) + Environment.NewLine);
        }

        private static bool ValidateCaptureEvidence(RoomReviewCapture capture, out string reason)
        {
            reason = string.Empty;
            if (capture?.views == null || capture.views.Count == 0 || string.IsNullOrWhiteSpace(capture.captureSetHash))
            {
                reason = "The review capture set is missing.";
                return false;
            }
            var entries = new List<RoomCaptureEntry>();
            foreach (var view in capture.views.Where(value => value != null))
            {
                if (string.IsNullOrWhiteSpace(view.imagePath) || !Inside(RootPath, view.imagePath) || !File.Exists(view.imagePath))
                {
                    reason = $"Review image '{view.id}' is missing or outside the review cache.";
                    return false;
                }
                var actual = HashFile(view.imagePath);
                if (!Same(actual, view.contentHash))
                {
                    reason = $"Review image '{view.id}' changed after capture.";
                    return false;
                }
                entries.Add(new RoomCaptureEntry(view.id, view.imagePath, actual));
            }
            if (!Same(RoomCaptureService.ComputeCaptureSetHash(entries), capture.captureSetHash))
            {
                reason = "The review capture-set hash is stale.";
                return false;
            }
            return true;
        }

        private static bool DecisionMatches(RoomReviewDecision decision, RoomReviewRun run) => decision != null &&
            !string.IsNullOrWhiteSpace(decision.value) && decision.value != RoomReviewDecisions.Pending &&
            Same(decision.inputHash, run.inputHash) && Same(decision.technicalReportHash, run.technicalReportHash) &&
            Same(decision.captureSetHash, run.capture?.captureSetHash);

        private static RoomReviewVerificationResult Fail(RoomReviewVerificationResult result, string reason)
        {
            result.valid = false;
            result.reason = reason;
            return result;
        }

        private static void RefreshExternalDecision()
        {
            if (EditorApplication.timeSinceStartup < nextRefresh) return;
            nextRefresh = EditorApplication.timeSinceStartup + 0.75d;
            if (!File.Exists(CurrentPath)) return;
            var pointerStamp = File.GetLastWriteTimeUtc(CurrentPath);
            var state = LoadCurrent();
            var statePath = state != null ? StatePathFor(state.targetId) : string.Empty;
            var stateStamp = !string.IsNullOrWhiteSpace(statePath) && File.Exists(statePath) ? File.GetLastWriteTimeUtc(statePath) : default;
            if (pointerStamp == lastPointerStamp && stateStamp == lastStateStamp) return;
            var stateChanged = stateStamp != lastStateStamp;
            lastPointerStamp = pointerStamp;
            lastStateStamp = stateStamp;
            if (stateChanged && state != null) RefreshDerivedArtifacts(state);
            Changed?.Invoke();
        }

        private static DateTime lastPointerStamp;
        private static DateTime lastStateStamp;

        private static void RefreshDerivedArtifacts(RoomReviewRun run)
        {
            var targetDirectory = TargetDirectory(run.targetId);
            var runDirectory = Path.Combine(targetDirectory, "runs", run.runId);
            Directory.CreateDirectory(runDirectory);
            AtomicWrite(Path.Combine(runDirectory, "run.json"), JsonUtility.ToJson(run, true) + Environment.NewLine);
            AtomicWrite(Path.Combine(targetDirectory, "agent-brief.json"), RoomReviewHashUtility.ToCompactAgentBriefJson(run) + Environment.NewLine);
        }

        private static string StableTargetId(ConceptRoom room) => !string.IsNullOrWhiteSpace(room.RoomId)
            ? room.RoomId
            : GlobalObjectId.GetGlobalObjectIdSlow(room).ToString();

        private static string TargetDirectory(string targetId) => Path.Combine(RootPath, SafeKey(targetId));

        private static string SafeKey(string value)
        {
            var source = string.IsNullOrWhiteSpace(value) ? "unsaved-room" : value;
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(source.Length);
            foreach (var character in source) builder.Append(invalid.Contains(character) || character is '/' or '\\' ? '-' : character);
            var safe = builder.ToString().Trim('.', ' ');
            return string.IsNullOrWhiteSpace(safe) ? "unsaved-room" : safe;
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
        }

        private static void AtomicWrite(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RootPath);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static bool Inside(string root, string candidate)
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
            return !string.IsNullOrWhiteSpace(relative) && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        }

        private static bool Same(string left, string right) => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);
        private static string ProjectRoot() => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        private static string ProjectPath(string relative) => Path.GetFullPath(Path.Combine(ProjectRoot(), relative));

        [Serializable]
        private sealed class CurrentPointer
        {
            public int schemaVersion = 1;
            public string roomKey;
            public string statePath;
        }
    }
}
