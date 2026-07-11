using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [InitializeOnLoad]
    public static class McpBridgeHost
    {
        private static readonly ConcurrentQueue<PendingRequest> Pending = new();
        private static TcpListener listener;
        private static Thread listenerThread;
        private static CancellationTokenSource cancellation;
        private static string nonce;

        public static bool IsRunning => listener != null;
        public static int Port { get; private set; }
        public static string SessionFilePath => Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/DungeonDecorator/session.json"));

        static McpBridgeHost()
        {
            EditorApplication.delayCall += Start;
            EditorApplication.update += ProcessPendingRequests;
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
        }

        public static void Start()
        {
            if (listener != null) return;
            try
            {
                cancellation = new CancellationTokenSource();
                nonce = Guid.NewGuid().ToString("N");
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                WriteSessionFile();
                listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "ConceptRoomDecorator-MCP" };
                listenerThread.Start();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Concept Room Decorator MCP bridge could not start: {exception.Message}");
                Stop();
            }
        }

        public static void Stop()
        {
            cancellation?.Cancel();
            try { listener?.Stop(); } catch { }
            listener = null;
            Port = 0;
            if (File.Exists(SessionFilePath))
            {
                try { File.Delete(SessionFilePath); } catch { }
            }
        }

        private static void ListenLoop()
        {
            while (listener != null && cancellation != null && !cancellation.IsCancellationRequested)
            {
                try
                {
                    using var client = listener.AcceptTcpClient();
                    client.ReceiveTimeout = 35000;
                    client.SendTimeout = 35000;
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    BridgeEnvelope envelope;
                    try { envelope = JsonUtility.FromJson<BridgeEnvelope>(line); }
                    catch (Exception exception)
                    {
                        writer.WriteLine(JsonUtility.ToJson(BridgeResponse.Fail($"Invalid request: {exception.Message}")));
                        continue;
                    }

                    if (envelope == null || envelope.nonce != nonce)
                    {
                        writer.WriteLine(JsonUtility.ToJson(BridgeResponse.Fail("The Unity session nonce is invalid.")));
                        continue;
                    }

                    var pending = new PendingRequest(envelope);
                    Pending.Enqueue(pending);
                    if (!pending.Completed.Wait(TimeSpan.FromSeconds(30)))
                    {
                        writer.WriteLine(JsonUtility.ToJson(BridgeResponse.Fail("Unity did not complete the request within 30 seconds.")));
                        continue;
                    }
                    writer.WriteLine(JsonUtility.ToJson(pending.Response ?? BridgeResponse.Fail("Unity returned no response.")));
                }
                catch (SocketException)
                {
                    if (listener == null || cancellation == null || cancellation.IsCancellationRequested) return;
                }
                catch (Exception exception)
                {
                    if (listener != null) Debug.LogWarning($"Concept Room Decorator MCP connection failed: {exception.Message}");
                }
            }
        }

        private static void ProcessPendingRequests()
        {
            var processed = 0;
            while (processed++ < 4 && Pending.TryDequeue(out var pending))
            {
                try { pending.Response = ExecuteTool(pending.Envelope.tool, pending.Envelope.argumentsJson); }
                catch (Exception exception) { pending.Response = BridgeResponse.Fail(exception.Message); }
                finally { pending.Completed.Set(); }
            }
        }

        private static BridgeResponse ExecuteTool(string tool, string argumentsJson)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return BridgeResponse.Fail("Unity is compiling or updating assets. Retry when the Editor is idle.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return BridgeResponse.Fail("MCP room decoration tools are available only in Edit Mode.");

            return tool switch
            {
                "inspect_room" => InspectRoom(Parse<RoomArgs>(argumentsJson)),
                "get_concept_brief" => GetConceptBrief(Parse<AssetPathArgs>(argumentsJson)),
                "render_asset_catalog" => RenderAssetCatalog(Parse<AssetPathArgs>(argumentsJson)),
                "search_decor_assets" => SearchDecorAssets(Parse<SearchAssetsArgs>(argumentsJson)),
                "create_composition_preview" => CreateCompositionPreview(Parse<CreatePreviewArgs>(argumentsJson), false),
                "refine_composition" => CreateCompositionPreview(Parse<CreatePreviewArgs>(argumentsJson), true),
                "lock_preview_group" => LockPreview(Parse<LockArgs>(argumentsJson)),
                "validate_preview" => ValidatePreview(),
                "capture_preview_views" => CapturePreviewViews(),
                "submit_visual_review" => SubmitVisualReview(Parse<VisualReviewArgs>(argumentsJson)),
                "discard_preview" => DiscardPreview(),
                "inspect_spatial_calibration" => InspectSpatialCalibration(),
                "capture_spatial_calibration" => CaptureSpatialCalibration(),
                "get_spatial_contract_draft" => GetSpatialContractDraft(),
                "submit_spatial_contract_proposal" => SubmitSpatialContractProposal(Parse<SpatialProposalArgs>(argumentsJson)),
                "get_deterministic_validation_report" => GetDeterministicValidationReport(),
                _ => BridgeResponse.Fail($"Unknown tool '{tool}'.")
            };
        }

        private static BridgeResponse InspectRoom(RoomArgs args)
        {
            var room = FindRoom(args?.roomId);
            if (room == null) return BridgeResponse.Fail("No matching ConceptRoom is loaded.");
            var bounds = room.AuthoringBounds != null ? room.AuthoringBounds.bounds : new Bounds(room.transform.position, Vector3.zero);
            var result = new RoomInfoDto
            {
                roomId = room.RoomId,
                objectName = room.name,
                boundsCenter = bounds.center,
                boundsSize = bounds.size,
                floorColliderCount = room.FloorColliders.Count,
                surfaceCount = room.Surfaces.Count,
                reviewedSurfaceCount = room.Surfaces.Count(value => value != null && value.Reviewed && value.Supported),
                keepClearZoneCount = room.KeepClearZones.Count,
                observationPoints = room.ObservationPoints.Where(point => point != null).Select(point => new ObservationDto
                {
                    label = point.Label,
                    position = point.transform.position,
                    forward = point.transform.forward,
                    fieldOfView = point.FieldOfView,
                    primary = point.Primary
                }).ToArray()
            };
            return BridgeResponse.Success(result);
        }

        private static BridgeResponse GetConceptBrief(AssetPathArgs args)
        {
            var brief = LoadAsset<RoomConceptBrief>(args?.assetPath) ?? RoomPreviewManager.Current?.Plan?.ConceptBrief;
            if (brief == null) return BridgeResponse.Fail("No RoomConceptBrief was found at the requested path or active preview.");
            var result = new ConceptBriefDto
            {
                title = brief.ConceptTitle,
                roomPurpose = brief.RoomPurpose,
                occupantsAndFaction = brief.OccupantsAndFaction,
                storyOrEvidence = brief.StoryOrEvidence,
                heroSubject = brief.HeroSubject,
                moodKeywords = brief.MoodKeywords.ToArray(),
                materialKeywords = brief.MaterialKeywords.ToArray(),
                colorKeywords = brief.ColorKeywords.ToArray(),
                requiredMotifs = brief.RequiredMotifs.ToArray(),
                forbiddenMotifs = brief.ForbiddenMotifs.ToArray(),
                referenceImagePaths = brief.ReferenceImages.Where(image => image != null).Select(AssetDatabase.GetAssetPath).ToArray()
            };
            var images = brief.ReferenceImages.Where(image => image != null).Select(image => Path.GetFullPath(Path.Combine(Application.dataPath, "..", AssetDatabase.GetAssetPath(image)))).ToArray();
            return BridgeResponse.Success(result, images);
        }

        private static BridgeResponse RenderAssetCatalog(AssetPathArgs args)
        {
            var catalog = LoadAsset<DecorCatalog>(args?.assetPath) ?? RoomPreviewManager.Current?.Plan?.Catalog;
            if (catalog == null) return BridgeResponse.Fail("No DecorCatalog was found at the requested path or active preview.");
            var path = CatalogSheetRenderer.Render(catalog);
            return BridgeResponse.Success(new PathResultDto { paths = new[] { path } }, new[] { path });
        }

        private static BridgeResponse SearchDecorAssets(SearchAssetsArgs args)
        {
            var catalog = LoadAsset<DecorCatalog>(args?.assetPath) ?? RoomPreviewManager.Current?.Plan?.Catalog;
            if (catalog == null) return BridgeResponse.Fail("No DecorCatalog was found at the requested path or active preview.");
            Enum.TryParse(args?.role, true, out DecorRole requestedRole);
            var hasRole = !string.IsNullOrWhiteSpace(args?.role);
            var query = args?.query?.Trim() ?? string.Empty;
            var style = args?.styleSet?.Trim() ?? string.Empty;
            var matches = catalog.Assets
                .Where(item => item != null && item.Prefab != null)
                .Where(item => item.Reviewed)
                .Where(item => !hasRole || item.HasRole(requestedRole))
                .Where(item => string.IsNullOrWhiteSpace(style) || string.Equals(item.StyleSet, style, StringComparison.OrdinalIgnoreCase))
                .Where(item => MatchesQuery(item, query))
                .Select(item => new AssetDto
                {
                    assetId = item.AssetId,
                    name = item.Prefab.name,
                    assetType = item.AssetType.ToString(),
                    roles = item.Roles.Select(value => value.ToString()).ToArray(),
                    styleSet = item.StyleSet,
                    surface = item.Surface.ToString(),
                    motifs = item.Motifs.ToArray(),
                    reviewed = item.Reviewed,
                    geometryReviewed = item.Geometry != null && item.Geometry.IsUsable,
                    contactRequirement = item.Geometry?.contact?.requirement.ToString() ?? string.Empty,
                    prefabPath = AssetDatabase.GetAssetPath(item.Prefab)
                }).ToArray();
            return BridgeResponse.Success(new AssetSearchResultDto { assets = matches });
        }

        private static BridgeResponse CreateCompositionPreview(CreatePreviewArgs args, bool preserveLocks)
        {
            if (args == null) return BridgeResponse.Fail("Preview arguments are required.");
            var room = FindRoom(args.roomId);
            var brief = LoadAsset<RoomConceptBrief>(args.briefAssetPath);
            var catalog = LoadAsset<DecorCatalog>(args.catalogAssetPath);
            if (room == null) return BridgeResponse.Fail("The requested ConceptRoom is not loaded.");
            if (brief == null) return BridgeResponse.Fail("The requested RoomConceptBrief asset was not found.");
            if (catalog == null) return BridgeResponse.Fail("The requested DecorCatalog asset was not found.");

            var elements = (args.elements ?? Array.Empty<CompositionElementDto>()).Select(ToCompositionElement).ToArray();
            var plan = ScriptableObject.CreateInstance<RoomCompositionPlan>();
            plan.hideFlags = HideFlags.HideAndDontSave;
            plan.Configure(room, brief, catalog, args.seed, Mathf.Clamp01(args.density), elements);
            var session = RoomPreviewManager.GeneratePreview(plan, preserveLocks);
            return BridgeResponse.Success(new PreviewResultDto
            {
                sessionId = session.SessionId,
                placementCount = session.Placements.Count,
                assetGapCount = session.AssetGaps.gaps.Count,
                manifestHash = session.ManifestHash,
                geometryProfileHash = session.GeometryProfileHash,
                seed = session.Plan.Seed,
                placements = session.Placements.Select(item => new PlacementDto
                {
                    placementId = item.placementId,
                    elementId = item.elementId,
                    assetId = item.descriptor != null ? item.descriptor.AssetId : string.Empty,
                    role = item.role.ToString(),
                    position = item.position,
                    eulerAngles = item.rotation.eulerAngles,
                    scale = item.scale,
                    locked = item.locked
                }).ToArray()
            });
        }

        private static BridgeResponse LockPreview(LockArgs args)
        {
            if (RoomPreviewManager.Current == null) return BridgeResponse.Fail("There is no active preview.");
            var changed = RoomPreviewManager.SetLocked(args?.ids ?? Array.Empty<string>(), args == null || args.locked);
            return BridgeResponse.Success(new StatusDto { message = changed ? "Preview lock state updated." : "No matching preview placement was found." });
        }

        private static BridgeResponse ValidatePreview()
        {
            var report = RoomPreviewManager.ValidateCurrent();
            return report == null ? BridgeResponse.Fail("There is no active preview.") : BridgeResponse.Success(report);
        }

        private static BridgeResponse CapturePreviewViews()
        {
            if (RoomPreviewManager.Current == null) return BridgeResponse.Fail("There is no active preview.");
            var paths = RoomCaptureService.CaptureAll(RoomPreviewManager.Current).ToArray();
            return BridgeResponse.Success(new PathResultDto { paths = paths }, paths);
        }

        private static BridgeResponse SubmitVisualReview(VisualReviewArgs args)
        {
            if (RoomPreviewManager.Current == null) return BridgeResponse.Fail("There is no active preview.");
            var scores = new VisualQualityScores
            {
                reviewed = true,
                mood = Mathf.Clamp(args.mood, 0, 100),
                style = Mathf.Clamp(args.style, 0, 100),
                story = Mathf.Clamp(args.story, 0, 100),
                composition = Mathf.Clamp(args.composition, 0, 100),
                feedback = args.feedback
            };
            RoomPreviewManager.SetVisualReview(scores);
            var passed = RoomPreviewManager.Current.LastValidation.MeetsVisualThresholds(RoomPreviewManager.Current.Plan.ConceptBrief);
            return BridgeResponse.Success(new VisualReviewResultDto { passed = passed, scores = scores });
        }

        private static BridgeResponse DiscardPreview()
        {
            RoomPreviewManager.DiscardPreview();
            return BridgeResponse.Success(new StatusDto { message = "Preview discarded." });
        }

        private static BridgeResponse InspectSpatialCalibration()
        {
            var session = SpatialCalibrationSession.Current;
            if (session == null) return BridgeResponse.Fail("No Spatial Calibration session is open. Start one from the Spatial Calibration window.");
            return BridgeResponse.Success(new SpatialCalibrationInfoDto
            {
                sessionId = session.SessionId,
                subjectName = session.Descriptor?.Prefab != null ? session.Descriptor.Prefab.name : string.Empty,
                subjectAssetPath = session.Descriptor?.Prefab != null ? AssetDatabase.GetAssetPath(session.Descriptor.Prefab) : string.Empty,
                targetName = session.TargetPrefab != null ? session.TargetPrefab.name : string.Empty,
                template = session.Template.ToString(),
                collisionProxyCount = session.Geometry?.collisionProxies?.Count ?? 0,
                contactRuleCount = session.Rules.Count,
                technicalState = session.LastReport?.status ?? SpatialContractStates.Draft,
                technicalErrorCount = session.LastReport?.error_count ?? 0,
                captureSetHash = session.CaptureSet?.capture_set_hash ?? string.Empty,
                draftPaths = session.DraftPaths.ToArray(),
                hasAgentProposal = !string.IsNullOrWhiteSpace(session.AgentProposalJson)
            });
        }

        private static BridgeResponse CaptureSpatialCalibration()
        {
            var session = SpatialCalibrationSession.Current;
            if (session == null) return BridgeResponse.Fail("No Spatial Calibration session is open.");
            var report = SpatialCalibrationValidator.Validate(session);
            var captures = SpatialCalibrationCaptureService.Capture(session, report);
            SpatialContractIO.WriteDrafts(session, report, captures);
            var paths = captures.raw_paths.Concat(captures.evidence_paths).ToArray();
            return BridgeResponse.Success(new SpatialCaptureResultDto
            {
                sessionId = session.SessionId,
                technicalPassed = report.Passed,
                technicalErrorCount = report.error_count,
                reportHash = report.report_hash,
                captureSetHash = captures.capture_set_hash,
                rawPaths = captures.raw_paths.ToArray(),
                evidencePaths = captures.evidence_paths.ToArray(),
                draftPaths = session.DraftPaths.ToArray()
            }, paths);
        }

        private static BridgeResponse GetSpatialContractDraft()
        {
            var session = SpatialCalibrationSession.Current;
            if (session == null) return BridgeResponse.Fail("No Spatial Calibration session is open.");
            if (session.DraftPaths.Count == 0) return BridgeResponse.Fail("No draft exists. Capture and validate the calibration first.");
            var drafts = session.DraftPaths.Where(File.Exists).Select(path => new SpatialDraftDto
            {
                path = path,
                json = File.ReadAllText(path)
            }).ToArray();
            return BridgeResponse.Success(new SpatialDraftResultDto { drafts = drafts });
        }

        private static BridgeResponse SubmitSpatialContractProposal(SpatialProposalArgs args)
        {
            var session = SpatialCalibrationSession.Current;
            if (session == null) return BridgeResponse.Fail("No Spatial Calibration session is open.");
            if (string.IsNullOrWhiteSpace(args?.proposalJson)) return BridgeResponse.Fail("proposalJson is required.");
            session.AgentProposalJson = args.proposalJson;
            return BridgeResponse.Success(new StatusDto
            {
                message = "Proposal stored in the temporary calibration session. It has not passed, approved, applied, or written any contract."
            });
        }

        private static BridgeResponse GetDeterministicValidationReport()
        {
            var session = SpatialCalibrationSession.Current;
            if (session == null) return BridgeResponse.Fail("No Spatial Calibration session is open.");
            return BridgeResponse.Success(SpatialCalibrationValidator.Validate(session));
        }

        private static ConceptRoom FindRoom(string roomId)
        {
            var rooms = UnityEngine.Object.FindObjectsByType<ConceptRoom>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (string.IsNullOrWhiteSpace(roomId)) return rooms.FirstOrDefault();
            return rooms.FirstOrDefault(room => string.Equals(room.RoomId, roomId, StringComparison.OrdinalIgnoreCase) || string.Equals(room.name, roomId, StringComparison.OrdinalIgnoreCase));
        }

        private static T LoadAsset<T>(string path) where T : UnityEngine.Object => string.IsNullOrWhiteSpace(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path.Replace('\\', '/'));
        private static T Parse<T>(string json) where T : new() => string.IsNullOrWhiteSpace(json) || json == "{}" ? new T() : JsonUtility.FromJson<T>(json);

        private static bool MatchesQuery(DecorAssetDescriptor descriptor, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            var haystack = string.Join(" ", new[] { descriptor.name, descriptor.Prefab.name, descriptor.StyleSet }
                .Concat(descriptor.Themes).Concat(descriptor.Factions).Concat(descriptor.Eras).Concat(descriptor.Materials).Concat(descriptor.Motifs));
            return haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static CompositionElement ToCompositionElement(CompositionElementDto source)
        {
            Enum.TryParse(source.role, true, out DecorRole role);
            Enum.TryParse(source.relation, true, out CompositionRelation relation);
            Enum.TryParse(source.preferredZone, true, out PreferredZone zone);
            return new CompositionElement
            {
                elementId = string.IsNullOrWhiteSpace(source.elementId) ? Guid.NewGuid().ToString("N") : source.elementId,
                descriptorId = source.descriptorId,
                role = role,
                relation = relation,
                anchorElementId = source.anchorElementId,
                count = Mathf.Max(1, source.count),
                preferredZone = zone,
                spacing = Mathf.Max(0f, source.spacing),
                locked = source.locked
            };
        }

        private static void WriteSessionFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionFilePath) ?? string.Empty);
            var info = new SessionInfo
            {
                port = Port,
                nonce = nonce,
                projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                editorVersion = Application.unityVersion
            };
            File.WriteAllText(SessionFilePath, JsonUtility.ToJson(info, true), new UTF8Encoding(false));
        }

        [Serializable] private sealed class BridgeEnvelope { public string nonce; public string tool; public string argumentsJson; }
        [Serializable] private sealed class SessionInfo { public int port; public string nonce; public string projectPath; public string editorVersion; }
        [Serializable] private sealed class RoomArgs { public string roomId; }
        [Serializable] private sealed class AssetPathArgs { public string assetPath; }
        [Serializable] private sealed class SearchAssetsArgs { public string assetPath; public string query; public string role; public string styleSet; }
        [Serializable] private sealed class LockArgs { public string[] ids; public bool locked = true; }
        [Serializable] private sealed class VisualReviewArgs { public int mood; public int style; public int story; public int composition; public string feedback; }
        [Serializable] private sealed class SpatialProposalArgs { public string proposalJson; }
        [Serializable] private sealed class CreatePreviewArgs { public string roomId; public string briefAssetPath; public string catalogAssetPath; public int seed = 12345; public float density = 0.5f; public CompositionElementDto[] elements; }
        [Serializable] private sealed class CompositionElementDto { public string elementId; public string descriptorId; public string role; public string relation; public string anchorElementId; public int count = 1; public string preferredZone; public float spacing = 1f; public bool locked; }
        [Serializable] private sealed class RoomInfoDto { public string roomId; public string objectName; public Vector3 boundsCenter; public Vector3 boundsSize; public int floorColliderCount; public int surfaceCount; public int reviewedSurfaceCount; public int keepClearZoneCount; public ObservationDto[] observationPoints; }
        [Serializable] private sealed class ObservationDto { public string label; public Vector3 position; public Vector3 forward; public float fieldOfView; public bool primary; }
        [Serializable] private sealed class ConceptBriefDto { public string title; public string roomPurpose; public string occupantsAndFaction; public string storyOrEvidence; public string heroSubject; public string[] moodKeywords; public string[] materialKeywords; public string[] colorKeywords; public string[] requiredMotifs; public string[] forbiddenMotifs; public string[] referenceImagePaths; }
        [Serializable] private sealed class AssetDto { public string assetId; public string name; public string assetType; public string[] roles; public string styleSet; public string surface; public string[] motifs; public bool reviewed; public bool geometryReviewed; public string contactRequirement; public string prefabPath; }
        [Serializable] private sealed class AssetSearchResultDto { public AssetDto[] assets; }
        [Serializable] private sealed class PathResultDto { public string[] paths; }
        [Serializable] private sealed class StatusDto { public string message; }
        [Serializable] private sealed class PreviewResultDto { public string sessionId; public int placementCount; public int assetGapCount; public string manifestHash; public string geometryProfileHash; public int seed; public PlacementDto[] placements; }
        [Serializable] private sealed class PlacementDto { public string placementId; public string elementId; public string assetId; public string role; public Vector3 position; public Vector3 eulerAngles; public Vector3 scale; public bool locked; }
        [Serializable] private sealed class VisualReviewResultDto { public bool passed; public VisualQualityScores scores; }
        [Serializable] private sealed class SpatialCalibrationInfoDto { public string sessionId; public string subjectName; public string subjectAssetPath; public string targetName; public string template; public int collisionProxyCount; public int contactRuleCount; public string technicalState; public int technicalErrorCount; public string captureSetHash; public string[] draftPaths; public bool hasAgentProposal; }
        [Serializable] private sealed class SpatialCaptureResultDto { public string sessionId; public bool technicalPassed; public int technicalErrorCount; public string reportHash; public string captureSetHash; public string[] rawPaths; public string[] evidencePaths; public string[] draftPaths; }
        [Serializable] private sealed class SpatialDraftDto { public string path; public string json; }
        [Serializable] private sealed class SpatialDraftResultDto { public SpatialDraftDto[] drafts; }

        [Serializable]
        private sealed class BridgeResponse
        {
            public bool ok;
            public string resultJson;
            public string error;
            public string[] imagePaths;

            public static BridgeResponse Success(object result, IEnumerable<string> images = null) => new()
            {
                ok = true,
                resultJson = JsonUtility.ToJson(result, true),
                imagePaths = images?.Where(File.Exists).ToArray() ?? Array.Empty<string>()
            };

            public static BridgeResponse Fail(string message) => new() { ok = false, error = message, resultJson = "{}", imagePaths = Array.Empty<string>() };
        }

        private sealed class PendingRequest
        {
            public readonly BridgeEnvelope Envelope;
            public readonly ManualResetEventSlim Completed = new(false);
            public BridgeResponse Response;
            public PendingRequest(BridgeEnvelope envelope) => Envelope = envelope;
        }
    }
}
