using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [InitializeOnLoad]
    public static class SpatialDungeonCalibrationBatch
    {
        private const string RequestPath = "Library/DungeonDecorator/BatchCalibration/run.request";
        private const string ReportPath = "Library/DungeonDecorator/BatchCalibration/latest-report.json";
        private static double nextPoll;
        private static bool running;

        private static readonly CalibrationCase[] Cases =
        {
            new("banner_patternA_red", "Assets/DecoratorV02Test/Data/banner_patternA_red.asset", SpatialCalibrationTemplate.WallMounted),
            new("bookcase_double_decoratedA", "Assets/DecoratorV02Test/Data/bookcase_double_decoratedA.asset", SpatialCalibrationTemplate.WallBackedFloorSupported),
            new("barrel_small_stack", "Assets/DecoratorV02Test/Data/barrel_small_stack.asset", SpatialCalibrationTemplate.FloorSupported),
            new("chair", "Assets/DecoratorV02Test/Data/chair.asset", SpatialCalibrationTemplate.FloorSupported),
            new("chest_large", "Assets/DecoratorV02Test/Data/chest_large.asset", SpatialCalibrationTemplate.FloorSupported),
            new("table_long_decorated_A", "Assets/DecoratorV02Test/Data/table_long_decorated_A.asset", SpatialCalibrationTemplate.FloorSupported)
        };

        static SpatialDungeonCalibrationBatch()
        {
            EditorApplication.update += Poll;
            EditorApplication.delayCall += Poll;
        }

        [MenuItem("Tools/Concept Room Decorator/Run Dungeon Calibration Batch")]
        public static void RequestRun()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RequestPath) ?? "Library");
            File.WriteAllText(RequestPath, DateTime.UtcNow.ToString("O"));
            nextPoll = 0d;
            Debug.Log("Dungeon calibration batch requested. It will run when Unity is idle.");
        }

        private static void Poll()
        {
            if (running || EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 0.5d;
            if (!File.Exists(RequestPath)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            running = true;
            EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            var report = new BatchReport
            {
                startedUtc = DateTime.UtcNow.ToString("O"),
                projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath
            };
            try
            {
                SpatialCalibrationSession.Current?.Dispose();
                var wall = FindWallCandidate();
                if (wall != null)
                {
                    var wallChoice = ChooseWallFace(wall);
                    report.wallName = HierarchyPath(wall.transform);
                    report.wallAxis = wallChoice.Axis.ToString();
                    report.wallSurfaceSize = new[] { wallChoice.Surface.Size.x, wallChoice.Surface.Size.y };
                }

                foreach (var item in Cases)
                    report.results.Add(RunCase(item, wall));
            }
            catch (Exception exception)
            {
                report.fatalError = exception.ToString();
                Debug.LogException(exception);
            }
            finally
            {
                SpatialCalibrationSession.Current?.Dispose();
                report.finishedUtc = DateTime.UtcNow.ToString("O");
                report.passed = report.results.Count == Cases.Length && report.results.All(value => value.passed);
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath) ?? "Library");
                File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
                if (File.Exists(RequestPath)) File.Delete(RequestPath);
                running = false;
                Debug.Log($"Dungeon calibration batch finished: passed={report.passed}, cases={report.results.Count}, report={Path.GetFullPath(ReportPath)}");
            }
        }

        private static CaseResult RunCase(CalibrationCase item, GameObject wall)
        {
            var result = new CaseResult { id = item.Id, template = item.Template.ToString() };
            try
            {
                var descriptor = AssetDatabase.LoadAssetAtPath<DecorAssetDescriptor>(item.DescriptorPath);
                if (descriptor == null || descriptor.Prefab == null)
                    throw new InvalidOperationException($"Descriptor is missing or invalid: {item.DescriptorPath}");

                var usesWall = item.Template is SpatialCalibrationTemplate.WallMounted
                    or SpatialCalibrationTemplate.WallBackedFloorSupported;
                if (usesWall && wall == null)
                    throw new InvalidOperationException("No reviewed straight wall candidate was found in a loaded scene.");

                var axis = usesWall ? ChooseWallFace(wall).Axis : SpatialWallNormalAxis.LocalForward;
                var session = SpatialCalibrationSession.Begin(
                    descriptor, null, item.Template, usesWall ? wall : null, axis, false);
                result.sessionId = session.SessionId;
                var framingBounds = session.CombinedWorldBounds();
                result.framingCenter = new[] { framingBounds.center.x, framingBounds.center.y, framingBounds.center.z };
                result.framingSize = new[] { framingBounds.size.x, framingBounds.size.y, framingBounds.size.z };
                var validation = SpatialCalibrationValidator.Validate(session);
                result.errorCount = validation.error_count;
                result.errors = validation.errors.ToArray();
                result.technicalReportHash = validation.report_hash;
                if (!validation.Passed) return result;

                var captures = SpatialCalibrationCaptureService.Capture(session, validation);
                var drafts = SpatialContractIO.WriteDrafts(session, validation, captures);
                result.passed = true;
                result.captureHash = captures.capture_set_hash;
                result.captureDirectory = Path.GetDirectoryName(captures.raw_paths.FirstOrDefault()) ?? string.Empty;
                result.rawPaths = captures.raw_paths.ToArray();
                result.evidencePaths = captures.evidence_paths.ToArray();
                result.draftPaths = drafts.ToArray();
                result.contacts = validation.contacts.Select(value => new ContactResult
                {
                    ruleId = value.rule_id,
                    gap = value.gap,
                    penetration = value.penetration,
                    support = value.support,
                    direction = value.direction_alignment
                }).ToArray();
                return result;
            }
            catch (Exception exception)
            {
                result.errors = new[] { exception.Message };
                Debug.LogException(exception);
                return result;
            }
            finally
            {
                SpatialCalibrationSession.Current?.Dispose();
            }
        }

        public static WallChoice ChooseWallFace(GameObject wall)
        {
            if (wall == null) throw new ArgumentNullException(nameof(wall));
            var choices = new[]
            {
                Choice(wall, SpatialWallNormalAxis.LocalForward),
                Choice(wall, SpatialWallNormalAxis.LocalRight)
            };
            return choices
                .OrderByDescending(value => value.Surface.Size.x * value.Surface.Size.y)
                .ThenBy(value => value.Axis)
                .First();
        }

        private static WallChoice Choice(GameObject wall, SpatialWallNormalAxis axis) =>
            new(axis, SpatialWallSurfaceUtility.Analyze(wall, axis, false));

        private static GameObject FindWallCandidate()
        {
            var selected = Selection.activeGameObject;
            if (IsWallCandidate(selected)) return selected;

            var candidates = new List<GameObject>();
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded || string.IsNullOrWhiteSpace(scene.path)) continue;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    if (IsWallCandidate(transform.gameObject)) candidates.Add(transform.gameObject);
            }

            return candidates
                .Select(value => new { Value = value, Choice = ChooseWallFace(value), Score = WallScore(value) })
                .OrderByDescending(value => value.Score)
                .ThenByDescending(value => value.Choice.Surface.Size.x * value.Choice.Surface.Size.y)
                .ThenBy(value => HierarchyPath(value.Value.transform), StringComparer.Ordinal)
                .Select(value => value.Value)
                .FirstOrDefault();
        }

        private static bool IsWallCandidate(GameObject value)
        {
            if (value == null || EditorUtility.IsPersistent(value) || !value.activeInHierarchy) return false;
            if (value.name.IndexOf("wall", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return value.GetComponentInChildren<Renderer>(true) != null
                || value.GetComponentInChildren<Collider>(true) != null;
        }

        private static int WallScore(GameObject value)
        {
            var score = 0;
            var name = value.name;
            if (name.IndexOf("straight", StringComparison.OrdinalIgnoreCase) >= 0) score += 100;
            if (name.IndexOf("corner", StringComparison.OrdinalIgnoreCase) >= 0) score -= 100;
            if (name.IndexOf("half", StringComparison.OrdinalIgnoreCase) >= 0) score -= 20;
            return score;
        }

        private static string HierarchyPath(Transform value)
        {
            var names = new Stack<string>();
            while (value != null) { names.Push(value.name); value = value.parent; }
            return string.Join("/", names);
        }

        private readonly struct CalibrationCase
        {
            public readonly string Id;
            public readonly string DescriptorPath;
            public readonly SpatialCalibrationTemplate Template;
            public CalibrationCase(string id, string descriptorPath, SpatialCalibrationTemplate template)
            {
                Id = id; DescriptorPath = descriptorPath; Template = template;
            }
        }

        public readonly struct WallChoice
        {
            public readonly SpatialWallNormalAxis Axis;
            public readonly SpatialCalibrationSurface Surface;
            public WallChoice(SpatialWallNormalAxis axis, SpatialCalibrationSurface surface)
            {
                Axis = axis; Surface = surface;
            }
        }

        [Serializable]
        private sealed class BatchReport
        {
            public string startedUtc;
            public string finishedUtc;
            public string projectPath;
            public string wallName;
            public string wallAxis;
            public float[] wallSurfaceSize;
            public bool passed;
            public string fatalError;
            public List<CaseResult> results = new();
        }

        [Serializable]
        private sealed class CaseResult
        {
            public string id;
            public string template;
            public string sessionId;
            public bool passed;
            public int errorCount;
            public string technicalReportHash;
            public string captureHash;
            public string captureDirectory;
            public float[] framingCenter = Array.Empty<float>();
            public float[] framingSize = Array.Empty<float>();
            public string[] rawPaths = Array.Empty<string>();
            public string[] evidencePaths = Array.Empty<string>();
            public string[] draftPaths = Array.Empty<string>();
            public string[] errors = Array.Empty<string>();
            public ContactResult[] contacts = Array.Empty<ContactResult>();
        }

        [Serializable]
        private sealed class ContactResult
        {
            public string ruleId;
            public float gap;
            public float penetration;
            public float support;
            public float direction;
        }
    }
}
