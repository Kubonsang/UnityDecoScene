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
            var frozen = RoomPreviewManager.BuildCompatibleRegenerationLocks(
                fixture.Plan,
                RoomPreviewManager.BuildArrangementRegenerationLocks(
                    baseline.Placements, fixture.SpecA.arrangement_id));

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
        public void GeneralLockPreservationDropsDeletedExcessAndBrokenArrangementPlacements()
        {
            var fixture = CreateFixture();
            var baseline = Pack(fixture);
            foreach (var placement in baseline.Placements.Where(value => !string.IsNullOrWhiteSpace(value.arrangementId)))
                placement.locked = true;
            fixture.TargetA.locked = true;
            fixture.TargetB.locked = true;

            // A's old descriptor was deleted from the spec. B now resolves two slots; one of those
            // slots points at the wrong root and the third is beyond the new resolved count.
            fixture.SpecA.members[0].descriptor_id = "replacement-token";
            fixture.SpecB.members[0].minimum_count = 2;
            fixture.SpecB.members[0].maximum_count = 2;
            var b = ArrangementPlacements(baseline, fixture.SpecB.arrangement_id);
            b[0].supportPlacementId = fixture.TargetB.placementId;
            b[0].stackLevel = 1;
            b[1].supportPlacementId = fixture.TargetA.placementId;
            b[1].stackLevel = 1;

            var preserved = RoomPreviewManager.BuildCompatibleRegenerationLocks(fixture.Plan, baseline.Placements);

            Assert.That(preserved.Where(value => !string.IsNullOrWhiteSpace(value.arrangementId))
                    .Select(value => value.placementId),
                Is.EqualTo(new[] { b[0].placementId }));
            Assert.That(preserved.Any(value => value.placementId == b[1].placementId), Is.False,
                "A placement supported by another arrangement's target root was preserved.");
            Assert.That(preserved.Any(value => value.placementId == b[2].placementId), Is.False,
                "A placement beyond the descriptor's resolved count was preserved.");
            Assert.That(preserved.Any(value => value.arrangementId == fixture.SpecA.arrangement_id), Is.False,
                "A placement for a descriptor deleted from its arrangement spec was preserved.");
        }

        [Test]
        public void GeneralLockPreservationRequiresTheEntireSupportChainToBeLocked()
        {
            var fixture = CreateFixture();
            var baseline = Pack(fixture);
            var b = ArrangementPlacements(baseline, fixture.SpecB.arrangement_id);
            b[0].supportPlacementId = fixture.TargetB.placementId;
            b[0].stackLevel = 1;
            b[1].supportPlacementId = b[0].placementId;
            b[1].stackLevel = 2;
            b[2].supportPlacementId = b[1].placementId;
            b[2].stackLevel = 3;
            b[2].locked = true;

            var childOnly = RoomPreviewManager.BuildCompatibleRegenerationLocks(fixture.Plan, baseline.Placements);

            Assert.That(childOnly.Any(value => value.placementId == b[2].placementId), Is.False,
                "A locked child must not survive while its regenerated support chain can move or disappear.");

            fixture.TargetB.locked = true;
            b[0].locked = true;
            b[1].locked = true;
            var fullChain = RoomPreviewManager.BuildCompatibleRegenerationLocks(fixture.Plan, baseline.Placements);

            Assert.That(fullChain.Where(value => value.arrangementId == fixture.SpecB.arrangement_id)
                    .Select(value => value.placementId),
                Is.EqualTo(b.Select(value => value.placementId)));
            Assert.That(fullChain.Any(value => value.placementId == fixture.TargetB.placementId), Is.True);
        }

        [Test]
        public void ValidationRejectsResolvedCountWrongRootAndCyclicStackGraph()
        {
            var fixture = CreateFixture();
            var result = Pack(fixture);
            var b = ArrangementPlacements(result, fixture.SpecB.arrangement_id);
            var extra = ClonePlacement(b[2]);
            extra.placementId = $"arrangement:{fixture.SpecB.arrangement_id}:{fixture.Token.AssetId}:3";
            extra.instanceIndex = 3;
            extra.supportPlacementId = fixture.TargetB.placementId;
            extra.stackLevel = 1;
            result.Placements.Add(extra);
            b[0].supportPlacementId = fixture.TargetA.placementId;
            b[0].stackLevel = 1;
            b[1].supportPlacementId = b[2].placementId;
            b[1].stackLevel = 2;
            b[2].supportPlacementId = b[1].placementId;
            b[2].stackLevel = 3;

            var report = PreviewValidationService.Validate(Session(fixture, result));

            Assert.That(report.issues.Any(value => value.code == SurfaceArrangementErrorCodes.NoFit &&
                                                   value.elementIds.Contains(extra.placementId)), Is.True,
                "The descriptor's resolved-count overflow was not reported.");
            Assert.That(report.issues.Any(value => value.code == SurfaceArrangementErrorCodes.SupportRegionInvalid &&
                                                   value.elementIds.Contains(b[0].placementId)), Is.True,
                "A support chain terminating at the wrong target root was not reported.");
            Assert.That(report.issues.Any(value => value.code == SurfaceArrangementErrorCodes.StackSupportInsufficient &&
                                                   (value.elementIds.Contains(b[1].placementId) ||
                                                    value.elementIds.Contains(b[2].placementId))), Is.True,
                "A cyclic same-arrangement stack graph was not reported.");
        }

        [Test]
        public void GeometryProfileHashCoversObbsAxesPivotFramesAndContactRules()
        {
            var fixture = CreateFixture();
            var previous = RoomPreviewManager.ComputeGeometryHash(fixture.Plan, fixture.SupportCatalog);

            AssertHashChanges(() => fixture.Token.Geometry.collisionProxies[0].localCenter += Vector3.right * 0.01f);
            AssertHashChanges(() => fixture.Token.Geometry.forwardAxis = Vector3.left);
            AssertHashChanges(() => fixture.Token.Geometry.upAxis = Vector3.forward);
            AssertHashChanges(() => fixture.Token.Geometry.pivotOffset += Vector3.up * 0.02f);
            AssertHashChanges(() => fixture.Token.Geometry.bottomContact.localPoint += Vector3.up * 0.01f);
            AssertHashChanges(() => fixture.Token.Geometry.backContact.localNormal = Vector3.left);
            AssertHashChanges(() => fixture.Token.Geometry.topContact.size += Vector2.one * 0.03f);
            AssertHashChanges(() => fixture.Token.Geometry.EffectiveContacts[0].maximumPenetration += 0.001f);
            AssertHashChanges(() => fixture.Token.Geometry.EffectiveContacts[0].minimumSupportCoverage += 0.01f);

            void AssertHashChanges(Action mutation)
            {
                mutation();
                var current = RoomPreviewManager.ComputeGeometryHash(fixture.Plan, fixture.SupportCatalog);
                Assert.That(current, Is.Not.EqualTo(previous));
                previous = current;
            }
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

        private static PlacedDecorItem ClonePlacement(PlacedDecorItem source) => new()
        {
            placementId = source.placementId,
            elementId = source.elementId,
            instanceIndex = source.instanceIndex,
            role = source.role,
            relation = source.relation,
            descriptor = source.descriptor,
            position = source.position,
            rotation = source.rotation,
            scale = source.scale,
            worldBounds = source.worldBounds,
            surfaceId = source.surfaceId,
            contactEvidence = source.contactEvidence,
            surfaceIds = new List<string>(source.surfaceIds),
            contactEvidenceSet = new List<ContactEvidence>(source.contactEvidenceSet),
            arrangementId = source.arrangementId,
            affinityGroup = source.affinityGroup,
            supportPlacementId = source.supportPlacementId,
            stackLevel = source.stackLevel,
            locked = source.locked
        };

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
