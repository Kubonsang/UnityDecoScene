using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor.Tests
{
    public sealed class SpatialContractTests
    {
        [Test]
        public void ContractDocumentRoundTripsHumanGateFields()
        {
            var document = new SpatialContractDocument
            {
                contract_type = "asset",
                state = SpatialContractStates.AwaitingHumanReview,
                asset = new AssetSpatialContractPayload
                {
                    asset_guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    asset_path = "Assets/banner.prefab",
                    dependency_hash = "hash",
                    capture_set_hash = "capture"
                },
                technical = new SpatialTechnicalEvidence { passed = true, error_count = 0, report_hash = "report" }
            };
            var json = JsonUtility.ToJson(document);
            var loaded = JsonUtility.FromJson<SpatialContractDocument>(json);
            Assert.That(loaded.state, Is.EqualTo(SpatialContractStates.AwaitingHumanReview));
            Assert.That(loaded.technical.passed, Is.True);
            Assert.That(loaded.IsApproved, Is.False);
        }

        [Test]
        public void AssetDraftSerializationOmitsInactiveInteractionAndEmptyReview()
        {
            var document = new SpatialContractDocument
            {
                contract_type = "asset",
                state = SpatialContractStates.AwaitingHumanReview,
                asset = new AssetSpatialContractPayload { asset_guid = "0123456789abcdef0123456789abcdef" },
                technical = new SpatialTechnicalEvidence { passed = true }
            };
            var json = SpatialContractIO.SerializeForStorage(document, false);
            Assert.That(json, Does.Contain("\"asset\":"));
            Assert.That(json, Does.Not.Contain("\"interaction\":"));
            Assert.That(json, Does.Not.Contain("\"review\":"));
        }

        [Test]
        public void FloorCalibrationProducesDeterministicTechnicalEvidence()
        {
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            try
            {
                descriptor.InitializeFromScan("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", prefab, new Bounds(Vector3.zero, Vector3.one), DecorAssetType.Prop);
                var session = SpatialCalibrationSession.Begin(descriptor, null, SpatialCalibrationTemplate.FloorSupported);
                try
                {
                    Assert.That(session.FloorFixture, Is.Not.Null);
                    Assert.That(session.WallFixture, Is.Null);
                    var first = SpatialCalibrationValidator.Validate(session);
                    var second = SpatialCalibrationValidator.Validate(session);
                    Assert.That(first.error_count, Is.Zero);
                    Assert.That(SpatialCalibrationCapturePreflight.Inspect(session, first), Is.Empty);
                    Assert.That(first.status, Is.EqualTo(SpatialContractStates.AwaitingHumanReview));
                    Assert.That(second.report_hash, Is.EqualTo(first.report_hash));
                }
                finally { session.Dispose(); }
            }
            finally
            {
                Object.DestroyImmediate(descriptor);
                Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void ApprovedContractHashMatchesUnityCtxKnownVector()
        {
            var document = KnownApprovedContract();
            Assert.That(SpatialContractHashUtility.ComputeGeometryHash(document.asset), Is.EqualTo(
                "3100c2d781773f3002be4704bdcba83b9b8d810d937dce1c933db6c0001c150b"));
            Assert.That(SpatialContractHashUtility.ComputeContentHash(document), Is.EqualTo(
                "b4e4805e72353ebcef824c4cdb509c78e7b8ea509e6bd593f7ea0c6f2cb61a5a"));
            Assert.That(SpatialContractHashUtility.ValidateApproved(document, out var reason), Is.True, reason);
        }

        [Test]
        public void NegativeZeroHashMatchesUnityCtxApprovedTableVector()
        {
            var document = KnownApprovedNegativeZeroTableContract();
            Assert.That(System.BitConverter.ToInt32(
                System.BitConverter.GetBytes(document.asset.pivot_offset[0]), 0), Is.LessThan(0),
                "The fixture must retain IEEE-754 negative zero instead of testing ordinary zero.");
            Assert.That(SpatialContractHashUtility.ComputeGeometryHash(document.asset), Is.EqualTo(
                "03ef438f92d4be2408a85f88cecd0f57f4d67ee826dce2b711740bfe19ade10b"));
            Assert.That(SpatialContractHashUtility.ComputeContentHash(document), Is.EqualTo(
                "595585303e6629aa7f9d6755e91d5e2c2aae59f5ac51e6798caaf4b9a4fa447e"));
            Assert.That(SpatialContractHashUtility.ValidateApproved(document, out var reason), Is.True, reason);
        }

        [Test]
        public void LoadPreservesNegativeZeroTokenAndIgnoresStringContents()
        {
            var path = Path.Combine(Path.GetTempPath(), $"spatial-negative-zero-{System.Guid.NewGuid():N}.json");
            try
            {
                Assert.That(SpatialContractIO.PreserveNegativeZeroNumbers("{\"label\":\"keep -0 here\",\"value\":-0}"),
                    Is.EqualTo("{\"label\":\"keep -0 here\",\"value\":-1e-30}"));
                Assert.That(SpatialContractIO.PreserveNegativeZeroNumbers("{\"value\":-0.25}"),
                    Is.EqualTo("{\"value\":-0.25}"));
                const string raw =
                    "{\"contract_version\":1,\"contract_type\":\"asset\",\"state\":\"Draft\"," +
                    "\"asset\":{\"asset_guid\":\"negative-zero-load-fixture\",\"pivot_offset\":[-0,0,0]}}";
                File.WriteAllText(path, raw);

                var loaded = SpatialContractIO.Load(path);

                Assert.That(System.BitConverter.ToInt32(
                    System.BitConverter.GetBytes(loaded.asset.pivot_offset[0]), 0), Is.LessThan(0));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void ApprovedContractRejectsGeometryChangedAfterReview()
        {
            var document = KnownApprovedContract();
            document.asset.collision_proxies[0].size[0] = 2f;
            Assert.That(SpatialContractHashUtility.ValidateApproved(document, out var reason), Is.False);
            Assert.That(reason, Does.StartWith("CONTRACT_HASH_MISMATCH"));
        }

        [Test]
        public void ApprovedContractBatchSyncPreviewsAppliesAndBecomesIdempotent()
        {
            const string root = "Assets/__ApprovedContractSyncTests";
            AssetDatabase.DeleteAsset(root);
            Directory.CreateDirectory(Path.GetFullPath(root + "/Contracts"));
            AssetDatabase.Refresh();
            try
            {
                var source = new GameObject("Contract Sync Prefab");
                var prefabPath = root + "/sync.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
                Object.DestroyImmediate(source);
                var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
                descriptor.InitializeFromScan("sync", prefab, new Bounds(new Vector3(0f, 0.5f, 0f), Vector3.one), DecorAssetType.Prop);
                AssetDatabase.CreateAsset(descriptor, root + "/sync.asset");
                var catalog = ScriptableObject.CreateInstance<DecorCatalog>();
                catalog.ReplaceAll(new[] { descriptor });
                AssetDatabase.CreateAsset(catalog, root + "/catalog.asset");
                AssetDatabase.SaveAssets();

                var document = KnownApprovedContract();
                document.asset.asset_guid = AssetDatabase.AssetPathToGUID(prefabPath);
                document.asset.asset_path = prefabPath;
                document.asset.dependency_hash = AssetDatabase.GetAssetDependencyHash(prefabPath).ToString();
                document.asset.geometry_hash = SpatialContractHashUtility.ComputeGeometryHash(document.asset);
                document.review.contract_hash = SpatialContractHashUtility.ComputeContentHash(document);
                var contractPath = root + "/Contracts/" + document.asset.asset_guid + ".spatial.json";
                File.WriteAllText(Path.GetFullPath(contractPath), SpatialContractIO.SerializeForStorage(document));
                AssetDatabase.ImportAsset(contractPath, ImportAssetOptions.ForceSynchronousImport);

                var preview = ApprovedContractSyncService.Preview(catalog, root + "/Contracts");
                var previewItem = preview.items.Single();
                Assert.That(previewItem.status, Is.EqualTo(ApprovedContractSyncStatus.Ready), previewItem.message);
                Assert.That(ApprovedContractSyncService.Apply(preview), Is.EqualTo(1));
                Assert.That(descriptor.Geometry.reviewed, Is.True);
                Assert.That(descriptor.Geometry.dependencyHash, Is.EqualTo(document.asset.dependency_hash));

                var repeated = ApprovedContractSyncService.Preview(catalog, root + "/Contracts");
                Assert.That(repeated.items.Single().status, Is.EqualTo(ApprovedContractSyncStatus.Unchanged));
            }
            finally
            {
                AssetDatabase.DeleteAsset(root);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void SupportedByFlatFrameUsesThinnestObbFaceAndPersistsPoseIntent()
        {
            var subject = new GameObject("Flat Book Root");
            var subjectMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
            subjectMesh.transform.SetParent(subject.transform, false);
            subjectMesh.transform.localScale = new Vector3(0.2f, 0.5f, 0.355f);
            var target = new GameObject("Table Root");
            var targetMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
            targetMesh.transform.SetParent(target.transform, false);
            targetMesh.transform.localScale = new Vector3(2f, 0.5f, 1.5f);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            IReadOnlyList<string> paths = null;
            try
            {
                descriptor.InitializeFromScan("flat-book", subject,
                    new Bounds(Vector3.zero, new Vector3(0.2f, 0.5f, 0.355f)), DecorAssetType.Prop);
                var session = SpatialCalibrationSession.Begin(
                    descriptor, target, SpatialCalibrationTemplate.SupportedBy, "flat", "top");
                try
                {
                    Assert.That(session.Rules.Single().frame_id, Is.EqualTo("flat"));
                    Assert.That(Vector3.Dot(
                        session.SubjectObject.transform.TransformDirection(session.Frame("flat").localNormal).normalized,
                        Vector3.down), Is.GreaterThan(0.999f));
                    Assert.That(session.SubjectObject.GetComponentInChildren<Renderer>().bounds.size.y,
                        Is.EqualTo(0.2f).Within(1e-4f),
                        "Flat intent must put the reviewed compound OBB's thinnest axis in world Y.");
                    var report = SpatialCalibrationValidator.Validate(session);
                    Assert.That(report.error_count, Is.Zero, string.Join("\n", report.errors));
                    paths = SpatialContractIO.WriteDrafts(session, report, new SpatialCaptureSet
                    {
                        session_id = session.SessionId,
                        capture_set_hash = "flat-capture"
                    });
                    var interaction = SpatialContractIO.Load(paths.Single(path => path.EndsWith(".interaction.json")));
                    Assert.That(interaction.interaction.subject_frame, Is.EqualTo("flat"));
                    Assert.That(interaction.interaction.target_frame, Is.EqualTo("top"));

                    session.ConfigureSupportedByFrames("bottom", "top");
                    Assert.That(session.Rules.Single().frame_id, Is.EqualTo("bottom"));
                    Assert.That(session.LastReport, Is.Null);
                }
                finally { session.Dispose(); }
            }
            finally
            {
                foreach (var path in paths ?? System.Array.Empty<string>()) File.Delete(path);
                Object.DestroyImmediate(descriptor);
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void InteractionOnlyWriterCreatesNoAssetGeometryDraft()
        {
            var subject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            string path = null;
            string supersededAssetPath = null;
            try
            {
                descriptor.InitializeFromScan("interaction-only", subject,
                    new Bounds(Vector3.zero, Vector3.one), DecorAssetType.Prop);
                var session = SpatialCalibrationSession.Begin(
                    descriptor, target, SpatialCalibrationTemplate.SupportedBy, "bottom", "top");
                try
                {
                    var report = SpatialCalibrationValidator.Validate(session);
                    Assert.That(report.error_count, Is.Zero, string.Join("\n", report.errors));
                    var captures = new SpatialCaptureSet
                    {
                        session_id = session.SessionId,
                        capture_set_hash = "interaction-only-capture"
                    };
                    var previousDrafts = SpatialContractIO.WriteDrafts(session, report, captures);
                    supersededAssetPath = previousDrafts.Single(value => value.EndsWith(".spatial.json", System.StringComparison.OrdinalIgnoreCase));
                    Assert.That(File.Exists(supersededAssetPath), Is.True, "The reuse case must begin with an older asset draft.");

                    path = SpatialContractIO.WriteInteractionDraft(session, report, captures);

                    Assert.That(session.DraftPaths, Is.EqualTo(new[] { path }));
                    Assert.That(path, Does.EndWith(".interaction.json"));
                    Assert.That(File.Exists(supersededAssetPath), Is.False, "Interaction-only rewrite must retire the same-session asset draft.");
                    Assert.That(SpatialContractIO.Load(path).contract_type, Is.EqualTo("interaction"));
                }
                finally { session.Dispose(); }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(path)) File.Delete(path);
                if (!string.IsNullOrWhiteSpace(supersededAssetPath)) File.Delete(supersededAssetPath);
                Object.DestroyImmediate(descriptor);
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(target);
            }
        }

        private static SpatialContractDocument KnownApprovedContract()
        {
            var asset = new AssetSpatialContractPayload
            {
                asset_guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                asset_path = "Assets/Test.prefab",
                dependency_hash = "dep",
                units = "meter",
                forward = new[] { 0f, 0f, 1f },
                up = new[] { 0f, 1f, 0f },
                pivot_offset = new[] { 0f, 0.5f, 0f },
                collision_proxies = new List<SpatialObbContract>
                {
                    new() { id = "box", center = new[] { 0f, 0.5f, 0f }, size = new[] { 1f, 1f, 1f }, rotation = new[] { 0f, 0f, 0f, 1f } }
                },
                frames = new List<SpatialContactFrameContract>
                {
                    new() { id = "back", point = new[] { 0f, 0.5f, -0.5f }, normal = new[] { 0f, 0f, -1f }, tangent = new[] { 1f, 0f, 0f }, size = new[] { 1f, 1f } },
                    new() { id = "bottom", point = new[] { 0f, 0f, 0f }, normal = new[] { 0f, -1f, 0f }, tangent = new[] { 1f, 0f, 0f }, size = new[] { 1f, 1f } }
                },
                contacts = new List<SpatialContactRuleContract>
                {
                    new() { id = "floor", kind = "FloorSupported", frame_id = "bottom", target = "surface:floor", minimum_gap = 0f, maximum_gap = 0.01f, maximum_penetration = 0f, minimum_support = 0.6f, direction_alignment = 0.95f }
                },
                revision = 1,
                geometry_hash = "3100c2d781773f3002be4704bdcba83b9b8d810d937dce1c933db6c0001c150b",
                capture_set_hash = "capture"
            };
            return new SpatialContractDocument
            {
                contract_version = 1,
                contract_type = "asset",
                state = SpatialContractStates.Approved,
                asset = asset,
                technical = new SpatialTechnicalEvidence { passed = true, error_count = 0, report_hash = "report" },
                review = new SpatialHumanReview
                {
                    decision = SpatialContractStates.Approved,
                    contract_hash = "b4e4805e72353ebcef824c4cdb509c78e7b8ea509e6bd593f7ea0c6f2cb61a5a",
                    capture_set_hash = "capture",
                    reviewer = "local-user",
                    revision = 1
                }
            };
        }

        private static SpatialContractDocument KnownApprovedNegativeZeroTableContract()
        {
            var negativeZero = System.BitConverter.ToSingle(new byte[] { 0, 0, 0, 128 }, 0);
            var asset = new AssetSpatialContractPayload
            {
                asset_guid = "a3fa97880303a42f48b512df88e92628",
                asset_path = "Assets/KayKit_DungeonRemastered_1.1_SOURCE/Assets/fbx(unity)/table_medium.fbx",
                dependency_hash = "283f72daa9e5a3be1a1ef522e9b5d527",
                units = "meter",
                forward = new[] { 0f, 0f, 1f },
                up = new[] { 0f, 1f, 0f },
                pivot_offset = new[] { negativeZero, 0.5f, 0f },
                collision_proxies = new List<SpatialObbContract>
                {
                    new()
                    {
                        id = "renderer-0",
                        center = new[] { negativeZero, 0.5f, 0f },
                        size = new[] { 2f, 1f, 2f },
                        rotation = new[] { 0f, 0f, 0f, 1f }
                    }
                },
                frames = new List<SpatialContactFrameContract>
                {
                    new()
                    {
                        id = "back",
                        point = new[] { negativeZero, 0.5f, -1f },
                        normal = new[] { 0f, 0f, -1f },
                        tangent = new[] { 1f, 0f, 0f },
                        size = new[] { 2f, 1.000001f }
                    },
                    new()
                    {
                        id = "bottom",
                        point = new[] { negativeZero, negativeZero, 0f },
                        normal = new[] { 0f, -1f, 0f },
                        tangent = new[] { 1f, 0f, 0f },
                        size = new[] { 2f, 2f }
                    },
                    new()
                    {
                        id = "top",
                        point = new[] { negativeZero, 1f, 0f },
                        normal = new[] { 0f, 1f, 0f },
                        tangent = new[] { 1f, 0f, 0f },
                        size = new[] { 2f, 2f }
                    }
                },
                contacts = new List<SpatialContactRuleContract>
                {
                    new()
                    {
                        id = "floor",
                        kind = "FloorSupported",
                        frame_id = "bottom",
                        target = "surface:floor",
                        minimum_gap = 0f,
                        maximum_gap = 0.01f,
                        maximum_penetration = 0f,
                        minimum_support = 0.6f,
                        direction_alignment = 0.95f
                    }
                },
                revision = 1,
                geometry_hash = "03ef438f92d4be2408a85f88cecd0f57f4d67ee826dce2b711740bfe19ade10b",
                capture_set_hash = "f30da4a06f6652089688e95fbe19919f8b42783bb446f9874486ad91aebfe2a4"
            };
            return new SpatialContractDocument
            {
                contract_version = 1,
                contract_type = "asset",
                state = SpatialContractStates.Approved,
                asset = asset,
                technical = new SpatialTechnicalEvidence
                {
                    passed = true,
                    error_count = 0,
                    report_hash = "685b1cfc6d78dc1e9fab3abca0c9fd947d5e75d89e0045a069be3651b8119bb7"
                },
                review = new SpatialHumanReview
                {
                    decision = SpatialContractStates.Approved,
                    contract_hash = "595585303e6629aa7f9d6755e91d5e2c2aae59f5ac51e6798caaf4b9a4fa447e",
                    capture_set_hash = "f30da4a06f6652089688e95fbe19919f8b42783bb446f9874486ad91aebfe2a4",
                    reviewer = "local-user",
                    revision = 1
                }
            };
        }
    }
}
