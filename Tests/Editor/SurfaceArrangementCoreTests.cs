using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;
using Object = UnityEngine.Object;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class SurfaceArrangementCoreTests
    {
        private readonly List<Object> cleanup = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in cleanup.Where(value => value != null)) Object.DestroyImmediate(item);
            cleanup.Clear();
        }

        [Test]
        public void SpecHashMatchesUnityCtxGolden()
        {
            var spec = ArchiveSpec();
            Assert.That(SurfaceArrangementSpecUtility.ComputeSpecHash(spec),
                Is.EqualTo("8914a0165a43fa8b1c2f21933fdd9723d45dc9b179102031d439ea2c206d8679"));
        }

        [Test]
        public void ValidationRejectsDuplicateDescriptorAndEmptyAffinity()
        {
            var spec = ArchiveSpec();
            spec.members[1].descriptor_id = spec.members[0].descriptor_id;
            Assert.That(SurfaceArrangementSpecUtility.Validate(spec, out var duplicate), Is.False);
            Assert.That(duplicate, Does.Contain("Duplicate"));
            spec = ArchiveSpec();
            spec.members[0].affinity_group = "";
            Assert.That(SurfaceArrangementSpecUtility.Validate(spec, out var affinity), Is.False);
            Assert.That(affinity, Does.Contain("affinity_group"));
        }

        [Test]
        public void GeometryFamilyAliasResolvesButApprovalGeometryChangeIsStale()
        {
            const string subjectGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string aliasGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string targetGuid = "cccccccccccccccccccccccccccccccc";
            var catalog = new SupportContractCatalog();
            catalog.RegisterAsset(new SupportAssetIdentity("book", subjectGuid, "book-geometry", "book-geometry", "book-family"));
            catalog.RegisterAsset(new SupportAssetIdentity("book-red", aliasGuid, "red-geometry", "red-geometry", "book-family"));
            catalog.RegisterAsset(new SupportAssetIdentity("table", targetGuid, "table-geometry", "table-geometry", "table-family"));
            var document = ApprovedInteraction(subjectGuid, targetGuid);
            Assert.That(catalog.RegisterApprovedInteraction(new SupportInteractionBinding(
                document, "book", "table", "book-geometry", "table-geometry"), out var registerReason), Is.True, registerReason);
            Assert.That(catalog.TryResolve("book-red", "table", out var resolved, out var code), Is.True, code);
            Assert.That(resolved.SubjectUsesGeometryFamilyAlias, Is.True);

            catalog.RegisterAsset(new SupportAssetIdentity("book", subjectGuid, "book-geometry", "changed-geometry", "book-family"));
            Assert.That(catalog.TryResolve("book-red", "table", out _, out code), Is.False);
            Assert.That(code, Is.EqualTo(SurfaceArrangementErrorCodes.SupportContractStale));
        }

        [Test]
        public void PackerProducesIdenticalTransformsForSameSeed()
        {
            var fixture = CreateArrangementFixture();

            var first = Pack(fixture);
            var second = Pack(fixture);

            Assert.That(first.AssetGaps.gaps, Is.Empty, DescribeGaps(first));
            Assert.That(second.AssetGaps.gaps, Is.Empty, DescribeGaps(second));
            var left = ArrangementPlacements(first);
            var right = ArrangementPlacements(second);
            Assert.That(right.Select(value => value.placementId), Is.EqualTo(left.Select(value => value.placementId)));
            for (var index = 0; index < left.Length; index++)
            {
                Assert.That(right[index].position, Is.EqualTo(left[index].position), left[index].placementId);
                Assert.That(right[index].rotation, Is.EqualTo(left[index].rotation), left[index].placementId);
                Assert.That(right[index].supportPlacementId, Is.EqualTo(left[index].supportPlacementId), left[index].placementId);
                Assert.That(right[index].stackLevel, Is.EqualTo(left[index].stackLevel), left[index].placementId);
            }
        }

        [Test]
        public void ExplicitInteractionsPlaceBooksAndCandleWithoutExceedingThreeLevels()
        {
            var fixture = CreateArrangementFixture();
            fixture.Spec.stacking = 1f;

            var result = Pack(fixture);
            var arranged = ArrangementPlacements(result);
            var books = arranged.Where(value => value.descriptor.AssetId.StartsWith("book-", StringComparison.Ordinal)).ToArray();
            var candles = arranged.Where(value => value.descriptor.AssetId == "candle-lit").ToArray();

            Assert.That(result.AssetGaps.gaps, Is.Empty, DescribeGaps(result));
            Assert.That(books, Has.Length.EqualTo(4));
            Assert.That(candles, Has.Length.EqualTo(1));
            Assert.That(books.Max(value => value.stackLevel), Is.EqualTo(3), "The explicit book-on-book interaction should exercise the three-level limit.");
            Assert.That(arranged.All(value => value.stackLevel is >= 1 and <= 3), Is.True);
            Assert.That(books.Where(value => value.stackLevel > 1)
                .All(value => arranged.Any(support => support.placementId == value.supportPlacementId && support.descriptor.AssetId.StartsWith("book-", StringComparison.Ordinal))), Is.True);
            Assert.That(candles.Single().supportPlacementId, Is.EqualTo(fixture.Target.placementId));
        }

        [Test]
        public void PresetsChangeRotationCharacterWithoutBreakingHardGates()
        {
            var neat = CreateArrangementFixture();
            neat.Spec.ApplyPreset(SurfaceArrangementPreset.Neat);
            var scattered = CreateArrangementFixture();
            scattered.Spec.ApplyPreset(SurfaceArrangementPreset.Scattered);

            var neatResult = Pack(neat);
            var scatteredResult = Pack(scattered);

            Assert.That(neatResult.AssetGaps.gaps, Is.Empty, DescribeGaps(neatResult));
            Assert.That(scatteredResult.AssetGaps.gaps, Is.Empty, DescribeGaps(scatteredResult));
            Assert.That(AverageBookHeadingDeviation(scatteredResult),
                Is.GreaterThan(AverageBookHeadingDeviation(neatResult) + 5f),
                "Scattered should visibly vary book headings more than Neat while using the same approved poses.");
        }

        [Test]
        public void TwelveItemArrangementMeetsInteractivePerformanceTarget()
        {
            var fixture = CreateArrangementFixture(new Vector2(6f, 6f));
            foreach (var member in fixture.Spec.members)
            {
                member.minimum_count = 4;
                member.maximum_count = 4;
            }
            fixture.Spec.amount = 1f;

            Pack(fixture);
            Pack(fixture); // Warm managed code and Unity math before measuring the resolver itself.
            var samples = new List<double>(7);
            for (var sample = 0; sample < 7; sample++)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = Pack(fixture);
                stopwatch.Stop();
                Assert.That(result.AssetGaps.gaps, Is.Empty, DescribeGaps(result));
                Assert.That(ArrangementPlacements(result), Has.Length.EqualTo(12));
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            var minimum = samples[0];
            var median = samples[samples.Count / 2];
            Assert.Multiple(() =>
            {
                Assert.That(median, Is.LessThan(100d),
                    $"12-item arrangement median was {median:F2}ms (min {minimum:F2}ms; samples {string.Join(", ", samples.Select(value => value.ToString("F2")))}). ");
                Assert.That(minimum, Is.LessThan(100d),
                    $"No post-warmup 12-item sample met the 100ms target; min was {minimum:F2}ms.");
            });
        }

        [Test]
        public void SmallSupportReportsExactNoFitCodeInsteadOfFallingBack()
        {
            var fixture = CreateArrangementFixture(new Vector2(0.45f, 0.45f));

            var result = Pack(fixture);

            Assert.That(result.AssetGaps.gaps.Any(value => value.code == SurfaceArrangementErrorCodes.NoFit), Is.True);
            Assert.That(result.Placements.Any(value => value.arrangementId == fixture.Spec.arrangement_id &&
                                                       value.descriptor.AssetId.StartsWith("book-", StringComparison.Ordinal)), Is.False);
        }

        [Test]
        public void LockedArrangementItemIsNotDuplicatedDuringRegeneration()
        {
            var fixture = CreateArrangementFixture();
            var first = Pack(fixture);
            var locked = ArrangementPlacements(first).First(value => value.descriptor.AssetId.StartsWith("book-", StringComparison.Ordinal));
            locked.locked = true;
            var regenerated = new LayoutResult();
            regenerated.Placements.Add(fixture.Target);
            regenerated.Placements.Add(CloneLocked(locked));

            SurfaceArrangementPacker.Append(fixture.Request, regenerated);

            Assert.That(regenerated.Placements.Count(value => value.placementId == locked.placementId), Is.EqualTo(1));
            Assert.That(regenerated.Placements.Select(value => value.placementId).Distinct(StringComparer.Ordinal).Count(),
                Is.EqualTo(regenerated.Placements.Count));
        }

        [Test]
        public void ValidationDetectsArrangementGapAfterPreviewTransformMoves()
        {
            var fixture = CreateArrangementFixture();
            var result = Pack(fixture);
            var moved = ArrangementPlacements(result).First();
            moved.position += Vector3.up * 0.05f;
            moved.worldBounds = SpatialGeometryUtility.CombinedAabb(
                SpatialGeometryUtility.BuildWorldObbs(moved.descriptor, moved.position, moved.rotation, moved.scale));
            var session = new PreviewSession
            {
                SessionId = "moved-arrangement-preview",
                Plan = fixture.Plan,
                Request = fixture.Request,
                AssetGaps = result.AssetGaps
            };
            session.Placements.AddRange(result.Placements);

            var report = PreviewValidationService.Validate(session);

            Assert.That(report.issues.Any(value => value.code == SurfaceArrangementErrorCodes.SupportRegionInvalid &&
                                                   value.elementIds.Contains(moved.placementId)), Is.True);
        }

        private static SurfaceArrangementSpec ArchiveSpec() => new()
        {
            surface_arrangement_version = 1,
            arrangement_id = "archive-reading-table",
            target_element_id = "support-10-reading-table",
            target_frame_id = "top",
            members = new List<SurfaceArrangementMemberSpec>
            {
                new() { descriptor_id = "book-brown", minimum_count = 2, maximum_count = 3, selection_weight = 1f, affinity_group = "books" },
                new() { descriptor_id = "book-grey", minimum_count = 1, maximum_count = 2, selection_weight = 0.8f, affinity_group = "books" },
                new() { descriptor_id = "candle-lit", minimum_count = 1, maximum_count = 1, selection_weight = 0.65f, affinity_group = "light" }
            },
            preset = "InUse",
            amount = 0.55f,
            orderliness = 0.45f,
            grouping = 0.75f,
            stacking = 0.55f,
            edge_margin = 0.08f,
            max_stack_height = 3,
            seed_offset = 17,
            resolver_version = 1
        };

        private static SpatialContractDocument ApprovedInteraction(string subjectGuid, string targetGuid)
        {
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
                    subject_frame = "back",
                    target_frame = "top",
                    relative_position = new[] { 0f, 1f, 0f },
                    relative_rotation = new[] { -0.7071068f, 0f, 0f, 0.7071068f },
                    position_tolerance = new[] { 0.2f, 0.01f, 0.2f },
                    angle_tolerance = 180f,
                    collision_policy = "contact-only",
                    revision = 1,
                    capture_set_hash = "capture"
                },
                technical = new SpatialTechnicalEvidence { passed = true, error_count = 0, report_hash = "report" },
                review = new SpatialHumanReview
                {
                    decision = SpatialContractStates.Approved,
                    capture_set_hash = "capture",
                    reviewer = "local-user"
                }
            };
            document.interaction.interaction_hash = SpatialContractHashUtility.ComputeInteractionHash(document.interaction);
            document.review.contract_hash = SpatialContractHashUtility.ComputeContentHash(document);
            return document;
        }

        private ArrangementFixture CreateArrangementFixture(Vector2? tableTopSize = null)
        {
            var roomRoot = Track(new GameObject("Arrangement Test Room"));
            var authoringBounds = roomRoot.AddComponent<BoxCollider>();
            authoringBounds.isTrigger = true;
            authoringBounds.center = new Vector3(0f, 2f, 0f);
            authoringBounds.size = new Vector3(10f, 4f, 10f);
            var room = roomRoot.AddComponent<ConceptRoom>();
            room.Configure(authoringBounds, Array.Empty<Collider>());

            var tableSize = tableTopSize ?? new Vector2(3f, 2f);
            var table = CreateDescriptor("reading-table", new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(tableSize.x, 1f, tableSize.y)), DecorRole.Support);
            var brown = CreateDescriptor("book-brown", new Bounds(new Vector3(0f, 0.2f, 0f), new Vector3(0.4f, 0.4f, 0.12f)), DecorRole.StoryEvidence);
            var grey = CreateDescriptor("book-grey", new Bounds(new Vector3(0f, 0.2f, 0f), new Vector3(0.4f, 0.4f, 0.12f)), DecorRole.StoryEvidence);
            var candle = CreateDescriptor("candle-lit", new Bounds(new Vector3(0f, 0.15f, 0f), new Vector3(0.12f, 0.3f, 0.12f)), DecorRole.StoryEvidence);
            var catalog = Track(ScriptableObject.CreateInstance<DecorCatalog>());
            catalog.ReplaceAll(new[] { table, brown, grey, candle });
            var spec = ArchiveSpec();
            spec.target_element_id = "support-10-reading-table";
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(room, null, catalog, 20260715, 0.42f, Array.Empty<CompositionElement>(), new[] { spec });

            var target = new PlacedDecorItem
            {
                placementId = "support-10-reading-table:0",
                elementId = spec.target_element_id,
                role = DecorRole.Support,
                descriptor = table,
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one,
                worldBounds = SpatialGeometryUtility.CombinedAabb(SpatialGeometryUtility.BuildWorldObbs(table, Vector3.zero, Quaternion.identity, Vector3.one))
            };
            var supportCatalog = CreateSupportCatalog(table, brown, grey, candle);
            return new ArrangementFixture(plan, spec, target, new LayoutRequest(plan, supportContracts: supportCatalog));
        }

        private SupportContractCatalog CreateSupportCatalog(
            DecorAssetDescriptor table,
            DecorAssetDescriptor brown,
            DecorAssetDescriptor grey,
            DecorAssetDescriptor candle)
        {
            const string tableGuid = "11111111111111111111111111111111";
            const string brownGuid = "22222222222222222222222222222222";
            const string greyGuid = "33333333333333333333333333333333";
            const string candleGuid = "44444444444444444444444444444444";
            var catalog = new SupportContractCatalog();
            catalog.RegisterAsset(new SupportAssetIdentity(table.AssetId, tableGuid, table.Geometry.dependencyHash, table.Geometry.dependencyHash, "table-family"));
            catalog.RegisterAsset(new SupportAssetIdentity(brown.AssetId, brownGuid, brown.Geometry.dependencyHash, brown.Geometry.dependencyHash, "book-family"));
            catalog.RegisterAsset(new SupportAssetIdentity(grey.AssetId, greyGuid, grey.Geometry.dependencyHash, grey.Geometry.dependencyHash, "book-family"));
            catalog.RegisterAsset(new SupportAssetIdentity(candle.AssetId, candleGuid, candle.Geometry.dependencyHash, candle.Geometry.dependencyHash, "candle-family"));

            Register(catalog, ApprovedInteraction(brownGuid, tableGuid, "back", "top", new Vector3(0f, 1.06f, 0.2f), Quaternion.Euler(-90f, 0f, 0f)), brown.AssetId, table.AssetId, brown.Geometry.dependencyHash, table.Geometry.dependencyHash);
            Register(catalog, ApprovedInteraction(brownGuid, brownGuid, "back", "top", new Vector3(0f, 0f, 0.12f), Quaternion.identity), brown.AssetId, brown.AssetId, brown.Geometry.dependencyHash, brown.Geometry.dependencyHash);
            Register(catalog, ApprovedInteraction(candleGuid, tableGuid, "bottom", "top", new Vector3(0f, 1f, 0f), Quaternion.identity), candle.AssetId, table.AssetId, candle.Geometry.dependencyHash, table.Geometry.dependencyHash);
            return catalog;
        }

        private static void Register(
            SupportContractCatalog catalog,
            SpatialContractDocument document,
            string subjectId,
            string targetId,
            string subjectGeometryHash,
            string targetGeometryHash)
        {
            Assert.That(catalog.RegisterApprovedInteraction(new SupportInteractionBinding(
                document, subjectId, targetId, subjectGeometryHash, targetGeometryHash), out var reason), Is.True, reason);
        }

        private DecorAssetDescriptor CreateDescriptor(string id, Bounds bounds, DecorRole role)
        {
            var prefab = Track(new GameObject("Prefab " + id));
            var descriptor = Track(ScriptableObject.CreateInstance<DecorAssetDescriptor>());
            descriptor.InitializeFromScan(id, prefab, bounds, DecorAssetType.Prop);
            descriptor.ConfigureMetadata("gothic", new[] { role }, PlacementSurface.Floor, new[] { id });
            descriptor.Geometry.dependencyHash = id + "-geometry";
            descriptor.Geometry.reviewed = true;
            descriptor.Geometry.Normalize();
            return descriptor;
        }

        private static SpatialContractDocument ApprovedInteraction(
            string subjectGuid,
            string targetGuid,
            string subjectFrame,
            string targetFrame,
            Vector3 relativePosition,
            Quaternion relativeRotation)
        {
            var document = ApprovedInteraction(subjectGuid, targetGuid);
            document.interaction.subject_frame = subjectFrame;
            document.interaction.target_frame = targetFrame;
            document.interaction.relative_position = new[] { relativePosition.x, relativePosition.y, relativePosition.z };
            document.interaction.relative_rotation = new[] { relativeRotation.x, relativeRotation.y, relativeRotation.z, relativeRotation.w };
            document.interaction.interaction_hash = SpatialContractHashUtility.ComputeInteractionHash(document.interaction);
            document.review.contract_hash = SpatialContractHashUtility.ComputeContentHash(document);
            return document;
        }

        private static LayoutResult Pack(ArrangementFixture fixture)
        {
            var result = new LayoutResult();
            result.Placements.Add(fixture.Target);
            SurfaceArrangementPacker.Append(fixture.Request, result);
            return result;
        }

        private static PlacedDecorItem[] ArrangementPlacements(LayoutResult result) => result.Placements
            .Where(value => !string.IsNullOrWhiteSpace(value.arrangementId))
            .OrderBy(value => value.placementId, StringComparer.Ordinal)
            .ToArray();

        private static string DescribeGaps(LayoutResult result) => string.Join(" | ",
            result.AssetGaps.gaps.Select(value => $"{value.code}: {value.reason}"));

        private static float AverageBookHeadingDeviation(LayoutResult result)
        {
            var headings = ArrangementPlacements(result)
                .Where(value => value.descriptor.AssetId.StartsWith("book-", StringComparison.Ordinal))
                .Select(value => Mathf.Abs(Vector3.SignedAngle(
                    Vector3.right,
                    Vector3.ProjectOnPlane(value.rotation * Vector3.right, Vector3.up).normalized,
                    Vector3.up)))
                .ToArray();
            return headings.Length == 0 ? 0f : headings.Average();
        }

        private static PlacedDecorItem CloneLocked(PlacedDecorItem source) => new()
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
            locked = true
        };

        private T Track<T>(T item) where T : Object
        {
            cleanup.Add(item);
            return item;
        }

        private sealed class ArrangementFixture
        {
            public RoomCompositionPlan Plan { get; }
            public SurfaceArrangementSpec Spec { get; }
            public PlacedDecorItem Target { get; }
            public LayoutRequest Request { get; }

            public ArrangementFixture(RoomCompositionPlan plan, SurfaceArrangementSpec spec, PlacedDecorItem target, LayoutRequest request)
            {
                Plan = plan;
                Spec = spec;
                Target = target;
                Request = request;
            }
        }
    }
}
