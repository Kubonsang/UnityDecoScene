using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;
using Object = UnityEngine.Object;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class SurfaceArrangementCoverageTests
    {
        private readonly List<Object> cleanup = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var value in cleanup.Where(value => value != null)) Object.DestroyImmediate(value);
            cleanup.Clear();
        }

        [Test]
        public void ChangingOneArrangementCountOrSeedDoesNotMoveTheOtherArrangement()
        {
            foreach (var mutation in new[] { "count", "seed-offset" })
            {
                var fixture = CreateFixture();
                var baseline = Pack(fixture);
                var baselineB = ArrangementPlacements(baseline, fixture.SpecB.arrangement_id);
                var baselineHash = ArrangementPlacementHash(fixture, baseline, fixture.SpecB.arrangement_id);
                var baselineAHash = ArrangementPlacementHash(fixture, baseline, fixture.SpecA.arrangement_id);

                if (mutation == "count")
                {
                    fixture.SpecA.members[0].minimum_count = 2;
                    fixture.SpecA.members[0].maximum_count = 2;
                }
                else
                {
                    fixture.SpecA.seed_offset++;
                }

                var changed = Pack(fixture);
                Assert.That(ArrangementPlacementHash(fixture, changed, fixture.SpecA.arrangement_id),
                    Is.Not.EqualTo(baselineAHash), $"The {mutation} mutation did not exercise arrangement A.");
                AssertPlacementsEqual(baselineB, ArrangementPlacements(changed, fixture.SpecB.arrangement_id), mutation);
                Assert.That(ArrangementPlacementHash(fixture, changed, fixture.SpecB.arrangement_id),
                    Is.EqualTo(baselineHash), $"Arrangement B hash changed after A {mutation} mutation.");
            }
        }

        [Test]
        public void LockBasedPartialRegenerationPreservesOtherArrangementTransformsAndHash()
        {
            var fixture = CreateFixture();
            var baseline = Pack(fixture);
            var baselineB = ArrangementPlacements(baseline, fixture.SpecB.arrangement_id);
            var baselineHash = ArrangementPlacementHash(fixture, baseline, fixture.SpecB.arrangement_id);
            var frozen = RoomPreviewManager.BuildArrangementRegenerationLocks(
                baseline.Placements, fixture.SpecA.arrangement_id);

            fixture.SpecA.members[0].minimum_count = 2;
            fixture.SpecA.members[0].maximum_count = 2;
            fixture.SpecA.seed_offset += 11;
            var regenerated = new LayoutResult();
            regenerated.Placements.AddRange(frozen);
            SurfaceArrangementPacker.Append(
                new LayoutRequest(fixture.Plan, frozen, supportContracts: fixture.SupportCatalog),
                regenerated);

            AssertPlacementsEqual(baselineB, ArrangementPlacements(regenerated, fixture.SpecB.arrangement_id), "partial regeneration");
            Assert.That(ArrangementPlacementHash(fixture, regenerated, fixture.SpecB.arrangement_id),
                Is.EqualTo(baselineHash));
            Assert.That(regenerated.Placements.Select(value => value.placementId).Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(regenerated.Placements.Count), "Partial regeneration introduced duplicate placements.");
        }

        [Test]
        public void ArrangementApprovalBecomesStaleForEveryMaterialInputChange()
        {
            var mutations = new (string Name, Action<CoverageFixture, PreviewSession> Apply)[]
            {
                ("amount", (fixture, _) => fixture.SpecA.amount += 0.1f),
                ("orderliness", (fixture, _) => fixture.SpecA.orderliness += 0.1f),
                ("grouping", (fixture, _) => fixture.SpecA.grouping -= 0.1f),
                ("stacking", (fixture, _) => fixture.SpecA.stacking += 0.1f),
                ("member-count", (fixture, _) => fixture.SpecA.members[0].maximum_count++),
                ("seed-offset", (fixture, _) => fixture.SpecA.seed_offset++),
                ("plan-seed", (fixture, _) => SetPlanSeed(fixture.Plan, fixture.Plan.Seed + 1)),
                ("geometry-top-frame", (fixture, _) => fixture.Table.Geometry.topContact.size += Vector2.right * 0.1f),
                ("support-interaction", (fixture, session) => session.Request = new LayoutRequest(
                    fixture.Plan,
                    supportContracts: CreateSupportCatalog(fixture.Table, fixture.Token, 45f)))
            };

            foreach (var mutation in mutations)
            {
                var fixture = CreateFixture();
                var result = Pack(fixture);
                var session = Session(fixture, result);
                var baseline = RoomReviewSnapshotService.Capture(session);
                var baselineHash = RoomReviewHashUtility.ComputeInputHash(baseline);
                var run = ApprovedRun(baseline, baselineHash);
                Assert.That(RoomReviewHashUtility.IsCurrentApproval(run), Is.True, mutation.Name);

                mutation.Apply(fixture, session);
                var current = RoomReviewSnapshotService.Capture(session);
                run.inputs = current;
                run.inputHash = RoomReviewHashUtility.ComputeInputHash(current);

                Assert.That(run.inputHash, Is.Not.EqualTo(baselineHash), $"{mutation.Name} was omitted from the review fingerprint.");
                Assert.That(RoomReviewHashUtility.IsCurrentApproval(run), Is.False,
                    $"Approval remained current after {mutation.Name} changed.");
            }

            var captureFixture = CreateFixture();
            var captureRun = ApprovedRun(
                RoomReviewSnapshotService.Capture(Session(captureFixture, Pack(captureFixture))),
                null);
            captureRun.inputHash = RoomReviewHashUtility.ComputeInputHash(captureRun.inputs);
            captureRun.decision.inputHash = captureRun.inputHash;
            Assert.That(RoomReviewHashUtility.IsCurrentApproval(captureRun), Is.True);
            captureRun.capture.captureSetHash = "capture-v2";
            Assert.That(RoomReviewHashUtility.IsCurrentApproval(captureRun), Is.False,
                "Approval remained current after the capture set changed.");
        }

        private CoverageFixture CreateFixture()
        {
            var roomObject = Track(new GameObject("Two Arrangement Room"));
            var bounds = roomObject.AddComponent<BoxCollider>();
            bounds.isTrigger = true;
            bounds.center = new Vector3(0f, 2f, 0f);
            bounds.size = new Vector3(24f, 4f, 12f);
            var room = roomObject.AddComponent<ConceptRoom>();
            room.Configure(bounds, Array.Empty<Collider>());

            var table = CreateDescriptor("coverage-table", new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(4f, 1f, 4f)), DecorRole.Support);
            var token = CreateDescriptor("coverage-token", new Bounds(new Vector3(0f, 0.1f, 0f), new Vector3(0.3f, 0.2f, 0.3f)), DecorRole.StoryEvidence);
            var catalog = Track(ScriptableObject.CreateInstance<DecorCatalog>());
            catalog.ReplaceAll(new[] { table, token });
            var specA = Spec("arrangement-a", "target-a", token.AssetId, 1);
            var specB = Spec("arrangement-b", "target-b", token.AssetId, 3);
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(room, null, catalog, 20260715, 0.5f,
                Array.Empty<CompositionElement>(), new[] { specA, specB });
            var targetA = Target("target-a:0", "target-a", table, new Vector3(-5f, 0f, 0f));
            var targetB = Target("target-b:0", "target-b", table, new Vector3(5f, 0f, 0f));
            var support = CreateSupportCatalog(table, token, 180f);
            return new CoverageFixture(plan, specA, specB, targetA, targetB, table, token, support);
        }

        private DecorAssetDescriptor CreateDescriptor(string id, Bounds bounds, DecorRole role)
        {
            var prefab = Track(new GameObject("Prefab " + id));
            var descriptor = Track(ScriptableObject.CreateInstance<DecorAssetDescriptor>());
            descriptor.InitializeFromScan(id, prefab, bounds, DecorAssetType.Prop);
            descriptor.ConfigureMetadata("coverage", new[] { role }, PlacementSurface.Floor, new[] { id });
            descriptor.Geometry.dependencyHash = id + "-geometry";
            descriptor.Geometry.reviewed = true;
            descriptor.Geometry.Normalize();
            return descriptor;
        }

        private static SurfaceArrangementSpec Spec(string id, string target, string descriptorId, int count) => new()
        {
            arrangement_id = id,
            target_element_id = target,
            target_frame_id = "top",
            preset = nameof(SurfaceArrangementPreset.InUse),
            amount = 0.55f,
            orderliness = 0.45f,
            grouping = 0.75f,
            stacking = 0.55f,
            edge_margin = 0.08f,
            max_stack_height = 3,
            seed_offset = 7,
            members = new List<SurfaceArrangementMemberSpec>
            {
                new()
                {
                    descriptor_id = descriptorId,
                    minimum_count = count,
                    maximum_count = count,
                    selection_weight = 1f,
                    affinity_group = "coverage"
                }
            }
        };

        private static PlacedDecorItem Target(
            string placementId,
            string elementId,
            DecorAssetDescriptor descriptor,
            Vector3 position) => new()
        {
            placementId = placementId,
            elementId = elementId,
            descriptor = descriptor,
            role = DecorRole.Support,
            position = position,
            rotation = Quaternion.identity,
            scale = Vector3.one,
            worldBounds = SpatialGeometryUtility.CombinedAabb(
                SpatialGeometryUtility.BuildWorldObbs(descriptor, position, Quaternion.identity, Vector3.one))
        };

        private static SupportContractCatalog CreateSupportCatalog(
            DecorAssetDescriptor table,
            DecorAssetDescriptor token,
            float angleTolerance)
        {
            const string tableGuid = "55555555555555555555555555555555";
            const string tokenGuid = "66666666666666666666666666666666";
            var catalog = new SupportContractCatalog();
            catalog.RegisterAsset(new SupportAssetIdentity(
                table.AssetId, tableGuid, table.Geometry.dependencyHash, table.Geometry.dependencyHash, "coverage-table-family"));
            catalog.RegisterAsset(new SupportAssetIdentity(
                token.AssetId, tokenGuid, token.Geometry.dependencyHash, token.Geometry.dependencyHash, "coverage-token-family"));
            var document = ApprovedInteraction(tokenGuid, tableGuid, angleTolerance);
            Assert.That(catalog.RegisterApprovedInteraction(new SupportInteractionBinding(
                document, token.AssetId, table.AssetId,
                token.Geometry.dependencyHash, table.Geometry.dependencyHash), out var reason), Is.True, reason);
            return catalog;
        }

        private static SpatialContractDocument ApprovedInteraction(
            string subjectGuid,
            string targetGuid,
            float angleTolerance)
        {
            const string capture = "coverage-capture";
            var document = new SpatialContractDocument
            {
                contract_version = 1,
                contract_type = "interaction",
                state = SpatialContractStates.Approved,
                interaction = new InteractionSpatialContractPayload
                {
                    subject_guid = subjectGuid,
                    target_key = "asset:" + targetGuid,
                    relation = "SupportedBy",
                    subject_frame = "bottom",
                    target_frame = "top",
                    relative_position = new[] { 0f, 1f, 0f },
                    relative_rotation = new[] { 0f, 0f, 0f, 1f },
                    position_tolerance = new[] { 0.2f, 0.01f, 0.2f },
                    angle_tolerance = angleTolerance,
                    collision_policy = "contact-only",
                    revision = 1,
                    capture_set_hash = capture
                },
                technical = new SpatialTechnicalEvidence { passed = true, error_count = 0, report_hash = "coverage-report" },
                review = new SpatialHumanReview
                {
                    decision = SpatialContractStates.Approved,
                    capture_set_hash = capture,
                    reviewer = "coverage-test"
                }
            };
            document.interaction.interaction_hash = SpatialContractHashUtility.ComputeInteractionHash(document.interaction);
            document.review.contract_hash = SpatialContractHashUtility.ComputeContentHash(document);
            return document;
        }

        private static LayoutResult Pack(CoverageFixture fixture)
        {
            var result = new LayoutResult();
            result.Placements.Add(fixture.TargetA);
            result.Placements.Add(fixture.TargetB);
            SurfaceArrangementPacker.Append(
                new LayoutRequest(fixture.Plan, supportContracts: fixture.SupportCatalog), result);
            Assert.That(result.AssetGaps.gaps, Is.Empty,
                string.Join(" | ", result.AssetGaps.gaps.Select(value => $"{value.code}: {value.reason}")));
            return result;
        }

        private static PreviewSession Session(CoverageFixture fixture, LayoutResult result)
        {
            var session = new PreviewSession
            {
                SessionId = "surface-arrangement-coverage",
                Plan = fixture.Plan,
                Request = new LayoutRequest(fixture.Plan, supportContracts: fixture.SupportCatalog),
                AssetGaps = result.AssetGaps,
                ManifestHash = "coverage-manifest",
                GeometryProfileHash = "coverage-geometry"
            };
            session.Placements.AddRange(result.Placements);
            return session;
        }

        private static string ArrangementPlacementHash(
            CoverageFixture fixture,
            LayoutResult result,
            string arrangementId)
        {
            var evidence = SurfaceArrangementReviewEvidenceService.Build(
                Session(fixture, result), new ValidationReport());
            return evidence.Single(value => value.arrangementId == arrangementId).placementHash;
        }

        private static PlacedDecorItem[] ArrangementPlacements(LayoutResult result, string arrangementId) =>
            result.Placements.Where(value => string.Equals(value.arrangementId, arrangementId, StringComparison.Ordinal))
                .OrderBy(value => value.placementId, StringComparer.Ordinal).ToArray();

        private static void AssertPlacementsEqual(
            IReadOnlyList<PlacedDecorItem> expected,
            IReadOnlyList<PlacedDecorItem> actual,
            string reason)
        {
            Assert.That(actual.Select(value => value.placementId),
                Is.EqualTo(expected.Select(value => value.placementId)), reason);
            for (var index = 0; index < expected.Count; index++)
            {
                Assert.That(actual[index].position, Is.EqualTo(expected[index].position), reason);
                Assert.That(actual[index].rotation, Is.EqualTo(expected[index].rotation), reason);
                Assert.That(actual[index].supportPlacementId, Is.EqualTo(expected[index].supportPlacementId), reason);
                Assert.That(actual[index].stackLevel, Is.EqualTo(expected[index].stackLevel), reason);
            }
        }

        private static void SetPlanSeed(RoomCompositionPlan plan, int seed)
        {
            var serialized = new SerializedObject(plan);
            serialized.Update();
            serialized.FindProperty("seed").intValue = seed;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static RoomReviewRun ApprovedRun(RoomReviewInputs inputs, string inputHash)
        {
            inputHash ??= RoomReviewHashUtility.ComputeInputHash(inputs);
            return new RoomReviewRun
            {
                runId = "coverage-review",
                targetId = inputs.targetId,
                status = RoomReviewStates.Approved,
                inputs = inputs,
                inputHash = inputHash,
                technicalReportHash = "technical-v1",
                capture = new RoomReviewCapture { captureSetHash = "capture-v1" },
                decision = new RoomReviewDecision
                {
                    value = RoomReviewDecisions.Approved,
                    reviewer = "coverage-human",
                    inputHash = inputHash,
                    technicalReportHash = "technical-v1",
                    captureSetHash = "capture-v1"
                }
            };
        }

        private T Track<T>(T value) where T : Object
        {
            cleanup.Add(value);
            return value;
        }

        private sealed class CoverageFixture
        {
            public RoomCompositionPlan Plan { get; }
            public SurfaceArrangementSpec SpecA { get; }
            public SurfaceArrangementSpec SpecB { get; }
            public PlacedDecorItem TargetA { get; }
            public PlacedDecorItem TargetB { get; }
            public DecorAssetDescriptor Table { get; }
            public DecorAssetDescriptor Token { get; }
            public SupportContractCatalog SupportCatalog { get; }

            public CoverageFixture(
                RoomCompositionPlan plan,
                SurfaceArrangementSpec specA,
                SurfaceArrangementSpec specB,
                PlacedDecorItem targetA,
                PlacedDecorItem targetB,
                DecorAssetDescriptor table,
                DecorAssetDescriptor token,
                SupportContractCatalog supportCatalog)
            {
                Plan = plan;
                SpecA = specA;
                SpecB = specB;
                TargetA = targetA;
                TargetB = targetB;
                Table = table;
                Token = token;
                SupportCatalog = supportCatalog;
            }
        }
    }
}
