using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class McpSurfaceArrangementTests
    {
        [Test]
        public void PublicSessionNonceCannotInvokePrivateReviewConfirmations()
        {
            McpBridgeHost.Start();

            Assert.That(McpBridgeHost.SessionNonce, Is.Not.Null.And.Not.Empty);
            Assert.That(McpBridgeHost.ReviewNonce, Is.Not.Null.And.Not.Empty);
            Assert.That(McpBridgeHost.ReviewNonce, Is.Not.EqualTo(McpBridgeHost.SessionNonce));
            Assert.That(McpBridgeHost.IsAuthorizedNonce(McpBridgeHost.SessionNonce, "inspect_room"), Is.True);
            Assert.That(McpBridgeHost.IsAuthorizedNonce(McpBridgeHost.SessionNonce, "confirm_spatial_contract_approval"), Is.False);
            Assert.That(McpBridgeHost.IsAuthorizedNonce(McpBridgeHost.ReviewNonce, "confirm_spatial_contract_approval"), Is.True);
            Assert.That(McpBridgeHost.IsAuthorizedNonce(McpBridgeHost.ReviewNonce, "inspect_room"), Is.False);
        }

        [Test]
        public void TrustedExecutableResolverRejectsPathLookupAndProjectFiles()
        {
            var external = Path.GetTempFileName();
            var projectLocal = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/DungeonDecorator/fake-node.exe"));
            Directory.CreateDirectory(Path.GetDirectoryName(projectLocal));
            File.WriteAllText(projectLocal, "not an executable");
            try
            {
                Assert.That(SpatialCalibrationWorkflow.ResolveTrustedExecutablePath("node"), Is.Empty);
                Assert.That(SpatialCalibrationWorkflow.ResolveTrustedExecutablePath(projectLocal), Is.Empty);
                Assert.That(SpatialCalibrationWorkflow.ResolveTrustedExecutablePath(external), Is.EqualTo(Path.GetFullPath(external)));
            }
            finally
            {
                File.Delete(projectLocal);
                File.Delete(external);
            }
        }

        private readonly List<UnityEngine.Object> cleanup = new();
        private string assetRoot;
        private SpatialCalibrationSession createdSession;

        [SetUp]
        public void SetUp()
        {
            Assert.That(SpatialCalibrationSession.Current, Is.Null,
                "Close an interactive Spatial Calibration session before running MCP isolation tests.");
            assetRoot = $"Assets/__McpSurfaceArrangementTests_{Guid.NewGuid():N}";
        }

        [TearDown]
        public void TearDown()
        {
            createdSession?.Dispose();
            createdSession = null;
            if (!string.IsNullOrWhiteSpace(assetRoot)) AssetDatabase.DeleteAsset(assetRoot);
            foreach (var value in cleanup.Where(value => value != null))
                UnityEngine.Object.DestroyImmediate(value);
            cleanup.Clear();
        }

        [Test]
        public void InspectionReturnsActiveAndRoomReviewEvidenceWithoutApprovalAuthority()
        {
            var roomObject = Track(new GameObject("MCP Evidence Room"));
            var room = roomObject.AddComponent<ConceptRoom>();
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            var spec = Spec("tabletop-active");
            plan.Configure(room, null, null, 42, 0.5f, Array.Empty<CompositionElement>(), new[] { spec });
            var session = new PreviewSession
            {
                SessionId = "active-preview-session",
                Plan = plan,
                Request = new LayoutRequest(plan)
            };
            var report = new ValidationReport { reportHash = "active-technical" };
            var roomEvidence = Evidence("tabletop-room", 5);
            var roomReview = new RoomReviewRun
            {
                runId = "room-review-run",
                status = RoomReviewStates.AwaitingHumanReview,
                capture = new RoomReviewCapture { captureSetHash = "room-capture" },
                arrangementEvidence = new List<SpatialArrangementReviewEvidence> { roomEvidence }
            };
            var calibrationEvidence = Evidence("legacy-calibration", 9);
            var calibration = new SpatialCalibrationWorkflowState
            {
                items = new List<SpatialCalibrationWorkflowItem>
                {
                    new()
                    {
                        id = "legacy-item",
                        reviewKind = SpatialCalibrationReviewKinds.Arrangement,
                        status = SpatialCalibrationWorkflowStates.RevisionRequested,
                        arrangementEvidence = calibrationEvidence
                    }
                }
            };

            var result = McpBridgeHost.BuildSurfaceArrangementInspection(
                session, report, roomReview, calibration, null);

            Assert.That(result.authority, Does.Contain("cannot pass, approve, apply, or write"));
            Assert.That(result.activePreviewSessionId, Is.EqualTo("active-preview-session"));
            Assert.That(result.activeTechnicalReportHash, Is.EqualTo("active-technical"));
            Assert.That(result.activePreviewEvidence.Select(value => value.arrangementId),
                Is.EqualTo(new[] { "tabletop-active" }));
            Assert.That(result.roomReviewRunId, Is.EqualTo("room-review-run"));
            Assert.That(result.roomReviewStatus, Is.EqualTo(RoomReviewStates.AwaitingHumanReview));
            Assert.That(result.roomReviewCaptureHash, Is.EqualTo("room-capture"));
            Assert.That(result.roomReviewEvidence.Single(), Is.SameAs(roomEvidence));
            Assert.That(result.reviews.Single().evidence, Is.SameAs(calibrationEvidence),
                "Legacy calibration evidence remains available for backward-compatible clients.");
        }

        [Test]
        public void CreateAndRefineInheritancePreservesOpaqueAdapterContextAndSupportCatalog()
        {
            var roomObject = Track(new GameObject("MCP Context Room"));
            var room = roomObject.AddComponent<ConceptRoom>();
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            var original = Spec("tabletop-context");
            plan.Configure(room, null, null, 7, 0.5f, Array.Empty<CompositionElement>(), new[] { original });
            var context = new RoomAuthoringContext { adapterId = "gnf-library", sourceHash = "source-v1" };
            var supportContracts = new SupportContractCatalog();
            var session = new PreviewSession
            {
                SessionId = "source-preview",
                Plan = plan,
                AuthoringContext = context,
                Request = new LayoutRequest(plan, authoringContext: context, supportContracts: supportContracts)
            };
            var args = new McpBridgeHost.CreatePreviewArgs { sourcePreviewSessionId = "source-preview" };

            Assert.That(McpBridgeHost.TryResolvePreviewInheritance(args, room, session, out var inherited, out var error), Is.True, error);
            Assert.That(inherited.AuthoringContext, Is.SameAs(context));
            Assert.That(inherited.SupportContracts, Is.SameAs(supportContracts));
            Assert.That(inherited.Arrangements.Single().arrangement_id, Is.EqualTo("tabletop-context"));
            Assert.That(inherited.Arrangements.Single(), Is.Not.SameAs(original),
                "The old transient plan may be destroyed after refinement, so arrangement DTOs must be cloned.");
            inherited.Arrangements.Single().amount = 0.9f;
            Assert.That(original.amount, Is.EqualTo(0.55f).Within(0.0001f));

            args.surfaceArrangements = Array.Empty<SurfaceArrangementSpec>();
            Assert.That(McpBridgeHost.TryResolvePreviewInheritance(args, room, session, out var explicitClear, out error), Is.True, error);
            Assert.That(explicitClear.Arrangements, Is.Empty, "An explicit empty array must clear arrangements rather than inherit them.");
            Assert.That(explicitClear.AuthoringContext, Is.SameAs(context));
            Assert.That(explicitClear.SupportContracts, Is.SameAs(supportContracts));

            args.sourcePreviewSessionId = "stale-preview";
            Assert.That(McpBridgeHost.TryResolvePreviewInheritance(args, room, session, out _, out error), Is.False);
            Assert.That(error, Does.Contain("does not match"));

            var otherRoomObject = Track(new GameObject("Other MCP Room"));
            var otherRoom = otherRoomObject.AddComponent<ConceptRoom>();
            var otherArgs = new McpBridgeHost.CreatePreviewArgs();
            Assert.That(McpBridgeHost.TryResolvePreviewInheritance(otherArgs, otherRoom, session, out var isolated, out error), Is.True, error);
            Assert.That(isolated.Arrangements, Is.Empty);
            Assert.That(isolated.AuthoringContext, Is.Null);
            Assert.That(isolated.SupportContracts, Is.Null, "Opaque adapter state must never leak across rooms.");
        }

        [Test]
        public void SupportedByMcpRejectsPrefabOnlyAndUsesReviewedTargetDescriptorWithoutMutation()
        {
            AssetDatabase.CreateFolder("Assets", assetRoot.Substring("Assets/".Length));
            var subjectPrefab = CreatePrefab("Subject", new Vector3(0.2f, 0.3f, 0.2f));
            var targetPrefab = CreatePrefab("Target", new Vector3(1.5f, 0.5f, 1f));
            var subject = CreateDescriptor("subject", subjectPrefab, new Bounds(Vector3.zero, new Vector3(0.2f, 0.3f, 0.2f)), false);
            var unreviewedTarget = CreateDescriptor("unreviewed-target", targetPrefab, new Bounds(Vector3.zero, new Vector3(1.5f, 0.5f, 1f)), false);
            var target = CreateDescriptor("target", targetPrefab, new Bounds(Vector3.zero, new Vector3(1.5f, 0.5f, 1f)), true);
            var subjectPath = AssetDatabase.GetAssetPath(subject);
            var targetPath = AssetDatabase.GetAssetPath(target);
            var subjectBefore = EditorJsonUtility.ToJson(subject);
            var targetBefore = EditorJsonUtility.ToJson(target);

            var prefabOnly = McpBridgeHost.BeginSpatialCalibration(new McpBridgeHost.BeginSpatialCalibrationArgs
            {
                descriptorAssetPath = subjectPath,
                template = nameof(SpatialCalibrationTemplate.SupportedBy),
                targetPrefabPath = AssetDatabase.GetAssetPath(targetPrefab),
                subjectFrameId = "bottom",
                targetFrameId = "top"
            }, false);
            Assert.That(prefabOnly.ok, Is.False);
            Assert.That(prefabOnly.error, Does.Contain("targetDescriptorAssetPath"));
            Assert.That(SpatialCalibrationSession.Current, Is.Null);

            var unreviewedDescriptor = McpBridgeHost.BeginSpatialCalibration(new McpBridgeHost.BeginSpatialCalibrationArgs
            {
                descriptorAssetPath = subjectPath,
                template = nameof(SpatialCalibrationTemplate.SupportedBy),
                targetDescriptorAssetPath = AssetDatabase.GetAssetPath(unreviewedTarget),
                subjectFrameId = "bottom",
                targetFrameId = "top"
            }, false);
            Assert.That(unreviewedDescriptor.ok, Is.False);
            Assert.That(unreviewedDescriptor.error, Does.Contain("human-reviewed geometry"));
            Assert.That(SpatialCalibrationSession.Current, Is.Null);

            var approvedDescriptor = McpBridgeHost.BeginSpatialCalibration(new McpBridgeHost.BeginSpatialCalibrationArgs
            {
                descriptorAssetPath = subjectPath,
                template = nameof(SpatialCalibrationTemplate.SupportedBy),
                targetDescriptorAssetPath = targetPath,
                subjectFrameId = "bottom",
                targetFrameId = "top"
            }, false);

            createdSession = SpatialCalibrationSession.Current;
            Assert.That(approvedDescriptor.ok, Is.True, approvedDescriptor.error);
            Assert.That(createdSession, Is.Not.Null);
            Assert.That(createdSession.TargetDescriptor, Is.SameAs(target));
            Assert.That(createdSession.TargetGeometry, Is.Not.SameAs(target.Geometry));
            Assert.That(EditorJsonUtility.ToJson(subject), Is.EqualTo(subjectBefore));
            Assert.That(EditorJsonUtility.ToJson(target), Is.EqualTo(targetBefore));
        }

        private GameObject CreatePrefab(string name, Vector3 size)
        {
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.name = name;
            source.transform.localScale = size;
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, $"{assetRoot}/{name}.prefab");
            UnityEngine.Object.DestroyImmediate(source);
            return prefab;
        }

        private DecorAssetDescriptor CreateDescriptor(string id, GameObject prefab, Bounds bounds, bool reviewed)
        {
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            descriptor.InitializeFromScan(id, prefab, bounds, DecorAssetType.Prop);
            if (reviewed) descriptor.ConfigureGeometry(ReviewedGeometry(bounds));
            AssetDatabase.CreateAsset(descriptor, $"{assetRoot}/{id}.asset");
            return descriptor;
        }

        private static DecorGeometryProfile ReviewedGeometry(Bounds bounds)
        {
            var geometry = new DecorGeometryProfile
            {
                source = GeometrySource.Collider,
                reviewed = true,
                collisionProxies = new List<OrientedBoxProxy>
                {
                    new("target", bounds.center, bounds.size, Quaternion.identity)
                },
                topContact = new ContactFrame
                {
                    frameId = "top",
                    localPoint = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z),
                    localNormal = Vector3.up,
                    localTangent = Vector3.right,
                    size = new Vector2(bounds.size.x, bounds.size.z)
                }
            };
            geometry.Normalize();
            return geometry;
        }

        private static SurfaceArrangementSpec Spec(string id) => new()
        {
            arrangement_id = id,
            target_element_id = "table",
            target_frame_id = "top",
            preset = nameof(SurfaceArrangementPreset.InUse),
            members = new List<SurfaceArrangementMemberSpec>
            {
                new()
                {
                    descriptor_id = "book",
                    minimum_count = 1,
                    maximum_count = 2,
                    selection_weight = 1f,
                    affinity_group = "reading"
                }
            }
        };

        private static SpatialArrangementReviewEvidence Evidence(string id, int count) => new()
        {
            arrangementId = id,
            memberCount = count,
            captureViewIds = Array.Empty<string>(),
            errorCodes = Array.Empty<string>(),
            stackStructure = Array.Empty<string>()
        };

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            cleanup.Add(value);
            return value;
        }
    }
}
