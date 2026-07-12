using NUnit.Framework;
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
    }
}
