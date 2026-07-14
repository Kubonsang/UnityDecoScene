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
    }
}
