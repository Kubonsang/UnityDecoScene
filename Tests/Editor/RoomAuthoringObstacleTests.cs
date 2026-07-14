using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;
using Object = UnityEngine.Object;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class RoomAuthoringObstacleTests
    {
        private readonly List<Object> cleanup = new();

        [TearDown]
        public void TearDown()
        {
            RoomPreviewManager.DiscardPreview();
            foreach (var item in cleanup.Where(value => value != null)) Object.DestroyImmediate(item);
            cleanup.Clear();
        }

        [Test]
        public void GenericWorldObbBuilderAppliesObstacleTransform()
        {
            var obstacle = Obstacle("pillar", new Vector3(3f, 0f, 0f), new Vector3(2f, 4f, 2f));
            obstacle.rotation = Quaternion.Euler(0f, 90f, 0f);
            obstacle.scale = new Vector3(2f, 1f, 1f);
            obstacle.collisionProxies[0].localCenter = Vector3.right;

            var boxes = SpatialGeometryUtility.BuildWorldObbs(obstacle);

            Assert.That(boxes, Has.Count.EqualTo(1));
            Assert.That(Vector3.Distance(boxes[0].Center, new Vector3(3f, 0f, -2f)), Is.LessThan(0.0001f));
            Assert.That(Vector3.Distance(boxes[0].Extents, new Vector3(2f, 2f, 1f)), Is.LessThan(0.0001f));
        }

        [Test]
        public void LayoutRejectsCandidatesThatIntersectReviewedSolidRoomGeometry()
        {
            var plan = CreatePlan();
            var context = Context(Obstacle("room-blocker", new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f)));

            var result = DeterministicLayoutEngine.Generate(new LayoutRequest(plan, null, context));

            Assert.That(result.Placements, Is.Empty);
            Assert.That(result.AssetGaps.HasGaps, Is.True);
        }

        [Test]
        public void LinkedContactSurfaceObstacleDoesNotRejectItsOwnPlacement()
        {
            var plan = CreatePlan();
            var obstacle = Obstacle("floor-shell", new Vector3(0f, 2f, 0f), new Vector3(8f, 4f, 8f));
            obstacle.policy = RoomObstaclePolicy.ContactSurface;
            obstacle.contactSurfaceId = "floor-main";

            var result = DeterministicLayoutEngine.Generate(new LayoutRequest(plan, null, Context(obstacle)));

            Assert.That(result.Placements, Has.Count.EqualTo(1));
            Assert.That(result.Placements[0].surfaceId, Is.EqualTo("floor-main"));
        }

        [Test]
        public void ValidatorReportsFixedGeometryOverlapAndUnreviewedGeometry()
        {
            var plan = CreatePlan();
            var descriptor = plan.Catalog.Find("hero");
            var solid = Obstacle("corner-pillar", new Vector3(0f, 0.25f, 0f), Vector3.one);
            var unknown = Obstacle("unknown-wall", Vector3.zero, Vector3.one);
            unknown.reviewed = false;
            var context = Context(solid, unknown);
            var session = Session(plan, context);
            session.Placements.Add(new PlacedDecorItem
            {
                placementId = "hero:0",
                elementId = "hero",
                role = DecorRole.Hero,
                descriptor = descriptor,
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one,
                worldBounds = new Bounds(new Vector3(0f, 0.25f, 0f), Vector3.one * 0.5f),
                surfaceId = "floor-main",
                surfaceIds = new List<string> { "floor-main" }
            });

            var report = PreviewValidationService.Validate(session);

            Assert.That(report.issues.Any(value => value.code == RoomAuthoringErrorCodes.GeometryOverlap), Is.True);
            Assert.That(report.issues.Any(value => value.code == RoomAuthoringErrorCodes.GeometryUnreviewed), Is.True);
        }

        [Test]
        public void SourceAndObstacleHashesAreBoundIntoReviewInput()
        {
            var plan = CreatePlan();
            var context = Context(Obstacle("pillar-a", Vector3.zero, Vector3.one));
            var session = Session(plan, context);
            var first = RoomReviewSnapshotService.Capture(session);

            session.AuthoringSourceHash = "source-b";
            context.obstacles[0].position = Vector3.right;
            session.ObstacleGeometryHash = context.ComputeObstacleHash();
            var second = RoomReviewSnapshotService.Capture(session);

            Assert.That(RoomReviewHashUtility.ComputeInputHash(second), Is.Not.EqualTo(RoomReviewHashUtility.ComputeInputHash(first)));
            Assert.That(second.items.Any(value => value.id == "authoring:source"), Is.True);
            Assert.That(second.items.Any(value => value.id == "authoring:obstacles"), Is.True);
        }

        [Test]
        public void StaleAdapterSourceBlocksValidationAndApply()
        {
            var plan = CreatePlan();
            var adapter = new MutableAdapter { CurrentHash = "source-a" };
            var context = Context();
            context.adapter = adapter;
            context.adapterId = adapter.AdapterId;
            context.adapterVersion = adapter.AdapterVersion;
            context.sourceHash = "source-a";
            var session = RoomPreviewManager.GeneratePreview(new LayoutRequest(plan, null, context), false);
            adapter.CurrentHash = "source-b";

            var report = PreviewValidationService.Validate(session);
            var exception = Assert.Throws<InvalidOperationException>(() => RoomPreviewManager.ApplyCurrent());

            Assert.That(report.issues.Any(value => value.code == RoomAuthoringErrorCodes.SourceStale), Is.True);
            StringAssert.Contains(RoomAuthoringErrorCodes.ApplySourceChanged, exception.Message);
        }

        private RoomCompositionPlan CreatePlan()
        {
            var roomObject = Track(new GameObject("Authoring Room"));
            var bounds = roomObject.AddComponent<BoxCollider>();
            bounds.isTrigger = true;
            bounds.center = new Vector3(0f, 2f, 0f);
            bounds.size = new Vector3(8f, 4f, 8f);
            var floorObject = new GameObject("Floor");
            floorObject.transform.SetParent(roomObject.transform, false);
            var floor = floorObject.AddComponent<BoxCollider>();
            floor.center = new Vector3(0f, -0.05f, 0f);
            floor.size = new Vector3(8f, 0.1f, 8f);
            var room = roomObject.AddComponent<ConceptRoom>();
            room.Configure(bounds, new Collider[] { floor });
            var surfaceObject = new GameObject("Floor Surface");
            surfaceObject.transform.SetParent(roomObject.transform, false);
            var surface = surfaceObject.AddComponent<RoomSurface>();
            surface.Configure("floor-main", RoomSurfaceType.Floor, floor, Vector3.zero, Vector3.up, Vector3.right, new Vector2(8f, 8f), true);
            room.RefreshChildren();

            var prefab = Track(new GameObject("Hero Prefab"));
            var descriptor = Track(ScriptableObject.CreateInstance<DecorAssetDescriptor>());
            descriptor.InitializeFromScan("hero", prefab, new Bounds(new Vector3(0f, 0.25f, 0f), Vector3.one * 0.5f), DecorAssetType.Prop);
            descriptor.ConfigureMetadata("gothic", new[] { DecorRole.Hero }, PlacementSurface.Floor, new[] { "archive" });
            var catalog = Track(ScriptableObject.CreateInstance<DecorCatalog>());
            catalog.ReplaceAll(new[] { descriptor });
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(room, null, catalog, 20260715, 0.42f, new[]
            {
                new CompositionElement { elementId = "hero", descriptorId = "hero", role = DecorRole.Hero, preferredZone = PreferredZone.Center }
            });
            return plan;
        }

        private static RoomAuthoringContext Context(params RoomObstacleProxy[] obstacles) => new()
        {
            adapterId = "test-adapter",
            adapterVersion = "1",
            sourceId = "test-source",
            sourceHash = "source-a",
            obstacles = obstacles?.ToList() ?? new List<RoomObstacleProxy>()
        };

        private static RoomObstacleProxy Obstacle(string id, Vector3 position, Vector3 size) => new()
        {
            stableId = id,
            label = id,
            reviewed = true,
            position = position,
            collisionProxies = new List<OrientedBoxProxy>
            {
                new(id + ":box", Vector3.zero, size, Quaternion.identity)
            }
        };

        private static PreviewSession Session(RoomCompositionPlan plan, RoomAuthoringContext context) => new()
        {
            SessionId = "authoring-test",
            Plan = plan,
            AuthoringContext = context,
            AuthoringSourceHash = context.sourceHash,
            ObstacleGeometryHash = context.ComputeObstacleHash(),
            ManifestHash = "manifest",
            GeometryProfileHash = "geometry"
        };

        private T Track<T>(T item) where T : Object
        {
            cleanup.Add(item);
            return item;
        }

        private sealed class MutableAdapter : IRoomAuthoringAdapter
        {
            public string AdapterId => "mutable-test";
            public string AdapterVersion => "1";
            public string CurrentHash;
            public RoomAuthoringContext CreateContext(RoomCompositionPlan plan) => throw new NotSupportedException();
            public string GetCurrentSourceHash(RoomAuthoringContext context) => CurrentHash;
        }
    }
}
