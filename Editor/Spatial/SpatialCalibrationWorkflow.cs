using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [InitializeOnLoad]
    public static class SpatialCalibrationWorkflow
    {
        public const string StateRelativePath = "Library/DungeonDecorator/CalibrationWorkflow/state.json";
        public const string RequestRelativePath = "Library/DungeonDecorator/CalibrationWorkflow/run.request";
        public const string ReviewRelativePath = "Library/DungeonDecorator/CalibrationWorkflow/review.html";
        public const string AgentBriefRelativePath = "Library/DungeonDecorator/CalibrationWorkflow/agent-brief.json";
        private const string ReviewTemplatePath = "Packages/com.unitydecoscene.dungeon-decorator/Editor/Spatial/Templates/CalibrationReviewTemplate.html";
        private const double PollInterval = 0.5d;

        private static readonly Dictionary<string, SpatialCalibrationTemplate> ApprovedRelationDefaults =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["banner_patternA_red"] = SpatialCalibrationTemplate.WallMounted,
                ["bookcase_double_decoratedA"] = SpatialCalibrationTemplate.WallBackedFloorSupported,
                ["barrel_small_stack"] = SpatialCalibrationTemplate.FloorSupported,
                ["chair"] = SpatialCalibrationTemplate.FloorSupported,
                ["chest_large"] = SpatialCalibrationTemplate.FloorSupported,
                ["table_long_decorated_A"] = SpatialCalibrationTemplate.FloorSupported
            };

        private static double nextPoll;
        private static bool running;
        private static Process reviewBridge;

        public static event Action Changed;

        static SpatialCalibrationWorkflow()
        {
            EditorApplication.update += PollRequest;
            AssemblyReloadEvents.beforeAssemblyReload += StopReviewBridge;
        }

        public static string StatePath => ProjectPath(StateRelativePath);
        public static string ReviewPath => ProjectPath(ReviewRelativePath);
        public static string AgentBriefPath => ProjectPath(AgentBriefRelativePath);

        [MenuItem("Tools/Concept Room Decorator/Fast Calibration/Scan or Resume")]
        public static void ScanOrResumeMenu() => ScanOrResume();

        [MenuItem("Tools/Concept Room Decorator/Fast Calibration/Run Next Batch")]
        public static void RequestNextBatch()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectPath(RequestRelativePath)) ?? ProjectRoot());
            File.WriteAllText(ProjectPath(RequestRelativePath), "run_next", new UTF8Encoding(false));
            nextPoll = 0d;
        }

        [MenuItem("Tools/Concept Room Decorator/Fast Calibration/Open Human Review")]
        public static void OpenReviewMenu() => OpenReview();

        public static SpatialCalibrationWorkflowState LoadState()
        {
            if (!File.Exists(StatePath)) return null;
            return JsonUtility.FromJson<SpatialCalibrationWorkflowState>(File.ReadAllText(StatePath));
        }

        public static SpatialCalibrationWorkflowState ScanOrResume()
        {
            var state = LoadState() ?? NewState();
            var previous = state.items.ToDictionary(item => item.descriptorGuid, StringComparer.OrdinalIgnoreCase);
            var next = new List<SpatialCalibrationWorkflowItem>();
            foreach (var guid in AssetDatabase.FindAssets("t:DecorAssetDescriptor").OrderBy(value => value, StringComparer.Ordinal))
            {
                var descriptorPath = AssetDatabase.GUIDToAssetPath(guid);
                var descriptor = AssetDatabase.LoadAssetAtPath<DecorAssetDescriptor>(descriptorPath);
                if (descriptor == null || descriptor.Prefab == null) continue;
                var prefabPath = AssetDatabase.GetAssetPath(descriptor.Prefab);
                var dependencyHash = AssetDatabase.GetAssetDependencyHash(prefabPath).ToString();
                if (!previous.TryGetValue(guid, out var item)) item = NewItem(guid, descriptorPath, descriptor, dependencyHash);
                else if (!string.Equals(item.dependencyHash, dependencyHash, StringComparison.Ordinal))
                {
                    item.status = SpatialCalibrationWorkflowStates.Stale;
                    item.dependencyHash = dependencyHash;
                    ClearEvidence(item);
                }
                item.displayName = descriptor.Prefab.name;
                item.assetId = descriptor.AssetId;
                item.descriptorPath = descriptorPath;
                if (string.IsNullOrWhiteSpace(item.template)) ResolveRelation(item, descriptor);
                next.Add(item);
            }
            state.items = next;
            state.status = DeriveWorkflowStatus(state);
            SaveAndGenerate(state);
            return state;
        }

        public static void SetRelation(string itemId, SpatialCalibrationTemplate template)
        {
            var state = ScanOrResume();
            var item = state.items.FirstOrDefault(value => value.id == itemId);
            if (item == null) throw new InvalidOperationException("Calibration item was not found.");
            item.template = template.ToString();
            item.suggestedTemplate = string.Empty;
            item.relationSource = "HumanOverride";
            item.status = SpatialCalibrationWorkflowStates.Pending;
            ClearEvidence(item);
            SaveAndGenerate(state);
        }

        public static SpatialCalibrationTemplate? InferTemplate(DecorAssetDescriptor descriptor, out string source)
        {
            source = "DescriptorContract";
            if (descriptor == null) return null;
            if (ApprovedRelationDefaults.TryGetValue(descriptor.AssetId ?? string.Empty, out var approved))
            {
                source = "ApprovedDefault";
                return approved;
            }
            var requirement = descriptor.Geometry?.contact?.requirement ?? ContactRequirement.FreeStanding;
            switch (requirement)
            {
                case ContactRequirement.WallMounted: return SpatialCalibrationTemplate.WallMounted;
                case ContactRequirement.WallBacked: return SpatialCalibrationTemplate.WallBackedFloorSupported;
                case ContactRequirement.FloorSupported: return SpatialCalibrationTemplate.FloorSupported;
            }
            if (descriptor.Surface == PlacementSurface.Wall) return SpatialCalibrationTemplate.WallMounted;
            if (descriptor.Surface == PlacementSurface.Floor) return SpatialCalibrationTemplate.FloorSupported;
            source = "NeedsHumanRelation";
            return null;
        }

        public static void RunNextBatch(int? requestedBatchSize = null)
        {
            if (running) return;
            running = true;
            var state = ScanOrResume();
            try
            {
                var batchSize = Mathf.Clamp(requestedBatchSize ?? state.batchSize, 1, 32);
                var candidates = state.items
                    .Where(item => item.status is SpatialCalibrationWorkflowStates.Pending
                        or SpatialCalibrationWorkflowStates.RevisionRequested
                        or SpatialCalibrationWorkflowStates.Stale)
                    .Where(item => Enum.TryParse<SpatialCalibrationTemplate>(item.template, out _))
                    .Take(batchSize)
                    .ToArray();
                var needsWall = candidates.Any(item => Enum.Parse<SpatialCalibrationTemplate>(item.template) is
                    SpatialCalibrationTemplate.WallMounted or SpatialCalibrationTemplate.WallBackedFloorSupported);
                var wall = needsWall ? FindWallCandidate(state.wallHierarchyPath) : null;
                if (needsWall && wall == null) throw new InvalidOperationException("Reviewed straight wall is required. Select one in Hierarchy and run again.");
                if (wall != null) state.wallHierarchyPath = HierarchyPath(wall.transform);

                foreach (var item in candidates)
                {
                    item.status = SpatialCalibrationWorkflowStates.Running;
                    SaveState(state);
                    RunItem(item, wall);
                    SaveAndGenerate(state);
                }
                state.status = DeriveWorkflowStatus(state);
                SaveAndGenerate(state);
            }
            catch (Exception exception)
            {
                state.status = "Blocked";
                SaveAndGenerate(state);
                Debug.LogException(exception);
            }
            finally
            {
                SpatialCalibrationSession.Current?.Dispose();
                running = false;
                Changed?.Invoke();
            }
        }

        public static SpatialCalibrationAgentBrief BuildAgentBrief(SpatialCalibrationWorkflowState state)
        {
            var brief = new SpatialCalibrationAgentBrief
            {
                workflowId = state.workflowId,
                status = state.status,
                reviewUrl = "http://127.0.0.1:4174/workflow",
                total = state.items.Count,
                pending = state.items.Count(item => item.status is SpatialCalibrationWorkflowStates.Pending or SpatialCalibrationWorkflowStates.Stale),
                awaitingReview = state.items.Count(item => item.status == SpatialCalibrationWorkflowStates.AwaitingHumanReview),
                approved = state.items.Count(item => item.status == SpatialCalibrationWorkflowStates.Approved),
                blocked = state.items.Count(item => item.status is SpatialCalibrationWorkflowStates.TechnicalFailed or SpatialCalibrationWorkflowStates.NeedsRelationReview)
            };
            brief.nextAction = brief.awaitingReview > 0 ? "WAIT_FOR_HUMAN_REVIEW"
                : brief.blocked > 0 ? "FIX_ONLY_BLOCKERS"
                : brief.pending > 0 ? "RUN_NEXT_BATCH"
                : "CALIBRATION_COMPLETE";
            brief.blockers = state.items
                .Where(item => item.status is SpatialCalibrationWorkflowStates.TechnicalFailed or SpatialCalibrationWorkflowStates.NeedsRelationReview)
                .Select(item => new SpatialCalibrationAgentBlocker
                {
                    assetId = item.assetId,
                    status = item.status,
                    suggestedTemplate = item.suggestedTemplate,
                    errorCodes = (item.errors ?? Array.Empty<string>())
                        .Select(ErrorCode).Distinct(StringComparer.Ordinal).ToArray()
                }).ToList();
            return brief;
        }

        public static string RenderReviewHtml(SpatialCalibrationWorkflowState state, string template)
        {
            if (string.IsNullOrWhiteSpace(template)) throw new ArgumentException("Review template is empty.", nameof(template));
            var json = JsonUtility.ToJson(state).Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
            return template.Replace("__WORKFLOW_JSON__", json).Replace("__GENERATED_UTC__", DateTime.UtcNow.ToString("O"));
        }

        public static void OpenReview()
        {
            var state = ScanOrResume();
            if (!File.Exists(state.reviewPagePath)) GenerateArtifacts(state);
            StartReviewBridge();
            Application.OpenURL(new Uri(state.reviewPagePath).AbsoluteUri);
        }

        private static void PollRequest()
        {
            if (running || EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + PollInterval;
            var request = ProjectPath(RequestRelativePath);
            if (!File.Exists(request) || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            File.Delete(request);
            EditorApplication.delayCall += () => RunNextBatch();
        }

        private static SpatialCalibrationWorkflowState NewState()
        {
            var now = DateTime.UtcNow.ToString("O");
            return new SpatialCalibrationWorkflowState
            {
                workflowId = Guid.NewGuid().ToString("N"),
                projectPath = ProjectRoot(),
                createdUtc = now,
                updatedUtc = now,
                reviewPagePath = ReviewPath,
                agentBriefPath = AgentBriefPath
            };
        }

        private static SpatialCalibrationWorkflowItem NewItem(string guid, string path, DecorAssetDescriptor descriptor, string dependencyHash)
        {
            var item = new SpatialCalibrationWorkflowItem
            {
                id = guid,
                descriptorGuid = guid,
                descriptorPath = path,
                dependencyHash = dependencyHash,
                assetId = descriptor.AssetId,
                displayName = descriptor.Prefab.name
            };
            ResolveRelation(item, descriptor);
            return item;
        }

        private static void ResolveRelation(SpatialCalibrationWorkflowItem item, DecorAssetDescriptor descriptor)
        {
            var inferred = InferTemplate(descriptor, out var source);
            item.relationSource = source;
            if (inferred.HasValue)
            {
                item.template = inferred.Value.ToString();
                item.status = SpatialCalibrationWorkflowStates.Pending;
            }
            else
            {
                item.template = string.Empty;
                item.suggestedTemplate = descriptor.Surface == PlacementSurface.Wall
                    ? SpatialCalibrationTemplate.WallMounted.ToString()
                    : SpatialCalibrationTemplate.FloorSupported.ToString();
                item.status = SpatialCalibrationWorkflowStates.NeedsRelationReview;
            }
        }

        private static void RunItem(SpatialCalibrationWorkflowItem item, GameObject wall)
        {
            try
            {
                var descriptor = AssetDatabase.LoadAssetAtPath<DecorAssetDescriptor>(item.descriptorPath);
                if (descriptor == null || descriptor.Prefab == null) throw new InvalidOperationException("DESCRIPTOR_MISSING");
                var template = Enum.Parse<SpatialCalibrationTemplate>(item.template);
                var usesWall = template is SpatialCalibrationTemplate.WallMounted or SpatialCalibrationTemplate.WallBackedFloorSupported;
                var axis = usesWall ? SpatialDungeonCalibrationBatch.ChooseWallFace(wall).Axis : SpatialWallNormalAxis.LocalForward;
                var session = SpatialCalibrationSession.Begin(descriptor, null, template, usesWall ? wall : null, axis, false);
                item.sessionId = session.SessionId;
                var report = SpatialCalibrationValidator.Validate(session);
                item.technicalReportHash = report.report_hash;
                item.errors = report.errors.ToArray();
                item.contacts = report.contacts.Select(contact => new SpatialCalibrationWorkflowContact
                {
                    ruleId = contact.rule_id,
                    target = contact.target,
                    gap = contact.gap,
                    penetration = contact.penetration,
                    support = contact.support,
                    direction = contact.direction_alignment
                }).ToArray();
                if (!report.Passed)
                {
                    item.status = SpatialCalibrationWorkflowStates.TechnicalFailed;
                    return;
                }
                var captures = SpatialCalibrationCaptureService.Capture(session, report);
                var drafts = SpatialContractIO.WriteDrafts(session, report, captures);
                item.captureHash = captures.capture_set_hash;
                item.captureDirectory = Path.GetDirectoryName(captures.raw_paths.FirstOrDefault()) ?? string.Empty;
                item.rawPaths = captures.raw_paths.ToArray();
                item.evidencePaths = captures.evidence_paths.ToArray();
                item.draftPath = drafts.FirstOrDefault() ?? string.Empty;
                item.status = SpatialCalibrationWorkflowStates.AwaitingHumanReview;
            }
            catch (Exception exception)
            {
                item.status = SpatialCalibrationWorkflowStates.TechnicalFailed;
                item.errors = new[] { exception.Message };
                Debug.LogException(exception);
            }
            finally
            {
                SpatialCalibrationSession.Current?.Dispose();
            }
        }

        private static GameObject FindWallCandidate(string preferredPath)
        {
            if (IsWallCandidate(Selection.activeGameObject)) return Selection.activeGameObject;
            var candidates = new List<GameObject>();
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    if (IsWallCandidate(transform.gameObject)) candidates.Add(transform.gameObject);
            }
            return candidates
                .OrderByDescending(value => string.Equals(HierarchyPath(value.transform), preferredPath, StringComparison.Ordinal))
                .ThenByDescending(value => value.name.IndexOf("straight", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenByDescending(value =>
                {
                    var surface = SpatialDungeonCalibrationBatch.ChooseWallFace(value).Surface;
                    return surface.Size.x * surface.Size.y;
                })
                .ThenBy(value => HierarchyPath(value.transform), StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static bool IsWallCandidate(GameObject value) => value != null
            && !EditorUtility.IsPersistent(value)
            && value.activeInHierarchy
            && value.name.IndexOf("wall", StringComparison.OrdinalIgnoreCase) >= 0
            && (value.GetComponentInChildren<Renderer>(true) != null || value.GetComponentInChildren<Collider>(true) != null);

        private static string HierarchyPath(Transform value)
        {
            var names = new Stack<string>();
            while (value != null) { names.Push(value.name); value = value.parent; }
            return string.Join("/", names);
        }

        private static void SaveAndGenerate(SpatialCalibrationWorkflowState state)
        {
            state.status = DeriveWorkflowStatus(state);
            SaveState(state);
            GenerateArtifacts(state);
            Changed?.Invoke();
        }

        private static void SaveState(SpatialCalibrationWorkflowState state)
        {
            state.updatedUtc = DateTime.UtcNow.ToString("O");
            state.reviewPagePath = ReviewPath;
            state.agentBriefPath = AgentBriefPath;
            AtomicWrite(StatePath, JsonUtility.ToJson(state, true) + Environment.NewLine);
        }

        private static void GenerateArtifacts(SpatialCalibrationWorkflowState state)
        {
            var templateAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(ReviewTemplatePath);
            if (templateAsset == null) throw new FileNotFoundException("Calibration review template was not found.", ReviewTemplatePath);
            AtomicWrite(ReviewPath, RenderReviewHtml(state, templateAsset.text));
            AtomicWrite(AgentBriefPath, JsonUtility.ToJson(BuildAgentBrief(state), true) + Environment.NewLine);
        }

        private static string DeriveWorkflowStatus(SpatialCalibrationWorkflowState state)
        {
            if (state.items.Any(item => item.status == SpatialCalibrationWorkflowStates.AwaitingHumanReview)) return SpatialCalibrationWorkflowStates.AwaitingHumanReview;
            if (state.items.Any(item => item.status == SpatialCalibrationWorkflowStates.TechnicalFailed)) return SpatialCalibrationWorkflowStates.TechnicalFailed;
            if (state.items.Any(item => item.status == SpatialCalibrationWorkflowStates.NeedsRelationReview)) return SpatialCalibrationWorkflowStates.NeedsRelationReview;
            if (state.items.Count > 0 && state.items.All(item => item.status == SpatialCalibrationWorkflowStates.Approved)) return SpatialCalibrationWorkflowStates.Approved;
            return SpatialCalibrationWorkflowStates.Pending;
        }

        private static void ClearEvidence(SpatialCalibrationWorkflowItem item)
        {
            item.sessionId = string.Empty;
            item.technicalReportHash = string.Empty;
            item.captureHash = string.Empty;
            item.captureDirectory = string.Empty;
            item.draftPath = string.Empty;
            item.rawPaths = Array.Empty<string>();
            item.evidencePaths = Array.Empty<string>();
            item.contacts = Array.Empty<SpatialCalibrationWorkflowContact>();
            item.errors = Array.Empty<string>();
            item.reviewer = string.Empty;
            item.reviewedUtc = string.Empty;
            item.comment = string.Empty;
        }

        private static string ErrorCode(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return "UNKNOWN";
            var separator = error.IndexOfAny(new[] { ':', ' ', '\n', '\r' });
            return (separator < 0 ? error : error.Substring(0, separator)).Trim();
        }

        private static void StartReviewBridge()
        {
            if (PortOpen(4174) || reviewBridge is { HasExited: false }) return;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(SpatialCalibrationWorkflow).Assembly);
            var server = Path.Combine(package.resolvedPath, "Tools~", "SpatialReviewBridge", "server.mjs");
            if (!File.Exists(server)) return;
            try
            {
                reviewBridge = Process.Start(new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"\"{server}\" --project \"{ProjectRoot()}\"",
                    WorkingDirectory = ProjectRoot(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch (Exception exception) { Debug.LogWarning($"Spatial review bridge could not start: {exception.Message}"); }
        }

        private static void StopReviewBridge()
        {
            try { if (reviewBridge is { HasExited: false }) reviewBridge.Kill(); }
            catch { }
            reviewBridge?.Dispose();
            reviewBridge = null;
        }

        private static bool PortOpen(int port)
        {
            try
            {
                using var client = new TcpClient();
                return client.ConnectAsync("127.0.0.1", port).Wait(100);
            }
            catch { return false; }
        }

        private static void AtomicWrite(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ProjectRoot());
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static string ProjectRoot() => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        private static string ProjectPath(string relative) => Path.GetFullPath(Path.Combine(ProjectRoot(), relative));
    }
}
