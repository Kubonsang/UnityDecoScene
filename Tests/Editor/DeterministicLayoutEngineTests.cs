using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;
using Object = UnityEngine.Object;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class DeterministicLayoutEngineTests
    {
        private readonly List<Object> cleanup = new();
        private readonly List<string> assetCleanup = new();

        [TearDown]
        public void TearDown()
        {
            RoomPreviewManager.DiscardPreview();
            foreach (var item in cleanup.Where(item => item != null)) Object.DestroyImmediate(item);
            foreach (var path in assetCleanup) AssetDatabase.DeleteAsset(path);
            cleanup.Clear();
            assetCleanup.Clear();
        }

        [Test]
        public void SamePlanAndSeedProduceSameTransforms()
        {
            var plan = CreatePlan("gothic", new CompositionElement
            {
                elementId = "hero",
                descriptorId = "hero-asset",
                role = DecorRole.Hero,
                count = 1,
                preferredZone = PreferredZone.Focal
            });

            var first = DeterministicLayoutEngine.Generate(plan);
            var second = DeterministicLayoutEngine.Generate(plan);

            Assert.That(first.Placements.Count, Is.EqualTo(1));
            Assert.That(second.Placements.Count, Is.EqualTo(1));
            Assert.That(second.Placements[0].position, Is.EqualTo(first.Placements[0].position));
            Assert.That(second.Placements[0].rotation, Is.EqualTo(first.Placements[0].rotation));
        }

        [Test]
        public void MixedStyleSetBecomesAssetGapInsteadOfFallbackPlacement()
        {
            var room = CreateRoom();
            var hero = CreateDescriptor("hero-asset", "gothic", DecorRole.Hero);
            var support = CreateDescriptor("support-asset", "science-fiction", DecorRole.Support);
            var catalog = Track(ScriptableObject.CreateInstance<DecorCatalog>());
            catalog.ReplaceAll(new[] { hero, support });
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(room, null, catalog, 44, 0.5f, new[]
            {
                new CompositionElement { elementId = "hero", descriptorId = hero.AssetId, role = DecorRole.Hero, preferredZone = PreferredZone.Focal },
                new CompositionElement { elementId = "support", descriptorId = support.AssetId, role = DecorRole.Support, preferredZone = PreferredZone.Perimeter }
            });

            var result = DeterministicLayoutEngine.Generate(plan);

            Assert.That(result.Placements.Any(item => item.descriptor == support), Is.False);
            Assert.That(result.AssetGaps.gaps.Any(gap => gap.reason.Contains("does not match")), Is.True);
        }

        [Test]
        public void TechnicalValidationReportsOverlappingPlacements()
        {
            var plan = CreatePlan("gothic", new CompositionElement { elementId = "hero", descriptorId = "hero-asset", role = DecorRole.Hero });
            var descriptor = plan.Catalog.Find("hero-asset");
            var bounds = new Bounds(new Vector3(0f, 0.5f, 0f), Vector3.one);
            var session = new PreviewSession { SessionId = "test", Plan = plan };
            session.Placements.Add(new PlacedDecorItem { placementId = "a", elementId = "hero", role = DecorRole.Hero, descriptor = descriptor, worldBounds = bounds, position = Vector3.zero, scale = Vector3.one });
            session.Placements.Add(new PlacedDecorItem { placementId = "b", elementId = "support", role = DecorRole.Support, descriptor = descriptor, worldBounds = bounds, position = Vector3.zero, scale = Vector3.one });

            var report = PreviewValidationService.Validate(session);

            Assert.That(report.issues.Any(issue => issue.code == "OBB_OVERLAP" && issue.severity == ValidationSeverity.Error), Is.True);
        }

        [Test]
        public void UnreviewedGeometryBecomesAssetGap()
        {
            var plan = CreatePlan("gothic", new CompositionElement { elementId = "hero", descriptorId = "hero-asset", role = DecorRole.Hero });
            plan.Catalog.Find("hero-asset").Geometry.reviewed = false;

            var result = DeterministicLayoutEngine.Generate(plan);

            Assert.That(result.Placements, Is.Empty);
            Assert.That(result.AssetGaps.gaps.Any(gap => gap.reason.Contains("GEOMETRY_UNREVIEWED")), Is.True);
        }

        [Test]
        public void WallBackedPlacementUsesReviewedSurfaceAndValidGap()
        {
            var room = CreateRoom();
            var wallObject = new GameObject("North Wall Surface");
            cleanup.Add(wallObject);
            wallObject.transform.SetParent(room.transform, false);
            var wall = wallObject.AddComponent<RoomSurface>();
            wall.Configure("wall-north", RoomSurfaceType.Wall, room.AuthoringBounds, new Vector3(0f, 2f, 4f), Vector3.back, Vector3.left, new Vector2(8f, 4f), true);
            room.RefreshChildren();
            var descriptor = CreateDescriptor("bookcase", "gothic", DecorRole.Hero);
            descriptor.Geometry.contact = ContactRules.Defaults(ContactRequirement.WallBacked);
            descriptor.Geometry.reviewed = true;
            var catalog = Track(ScriptableObject.CreateInstance<DecorCatalog>());
            catalog.ReplaceAll(new[] { descriptor });
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(room, null, catalog, 7, 0.5f, new[] { new CompositionElement { elementId = "hero", descriptorId = descriptor.AssetId, role = DecorRole.Hero } });

            var result = DeterministicLayoutEngine.Generate(plan);

            Assert.That(result.Placements.Count, Is.EqualTo(1));
            Assert.That(result.Placements[0].surfaceId, Is.EqualTo("wall-north"));
            Assert.That(result.Placements[0].contactEvidence.valid, Is.True);
            Assert.That(result.Placements[0].contactEvidence.gap, Is.InRange(0.01f, 0.05f));
        }

        [Test]
        public void SharedSpatialFixtureMatchesObbSatVerdicts()
        {
            var guid = AssetDatabase.FindAssets("spatial_cases").First();
            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            var fixture = JsonUtility.FromJson<SpatialFixture>(File.ReadAllText(assetPath));
            foreach (var item in fixture.cases)
            {
                var left = ToWorldObb(item.left);
                var right = ToWorldObb(item.right);
                Assert.That(SpatialGeometryUtility.Intersects(left, right), Is.EqualTo(item.overlap), item.id);
            }
        }

        [Test]
        public void VisualQualityRequiresEveryAxisToPass()
        {
            var brief = Track(ScriptableObject.CreateInstance<RoomConceptBrief>());
            var report = new ValidationReport
            {
                visualScores = new VisualQualityScores { reviewed = true, mood = 80, style = 80, story = 80, composition = 69 }
            };

            Assert.That(report.MeetsVisualThresholds(brief), Is.False);
            report.visualScores.composition = 70;
            Assert.That(report.MeetsVisualThresholds(brief), Is.True);
        }

        [Test]
        public void PreviewApplyCreatesOneDecorationContainerAfterReview()
        {
            const string prefabPath = "Assets/__ConceptRoomDecoratorTestPrefab.prefab";
            AssetDatabase.DeleteAsset(prefabPath);
            assetCleanup.Add(prefabPath);
            var source = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            source.name = "Reviewed Hero";
            source.transform.localScale = Vector3.one * 0.5f;
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);

            var room = CreateRoom();
            var descriptor = Track(ScriptableObject.CreateInstance<DecorAssetDescriptor>());
            descriptor.InitializeFromScan("apply-hero", prefab, new Bounds(Vector3.zero, Vector3.one * 0.5f), DecorAssetType.Prop);
            descriptor.ConfigureMetadata("gothic", new[] { DecorRole.Hero }, PlacementSurface.Floor, new[] { "altar" });
            var catalog = Track(ScriptableObject.CreateInstance<DecorCatalog>());
            catalog.ReplaceAll(new[] { descriptor });
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(room, null, catalog, 99, 0.5f, new[]
            {
                new CompositionElement { elementId = "hero", descriptorId = descriptor.AssetId, role = DecorRole.Hero, preferredZone = PreferredZone.Focal }
            });

            var preview = RoomPreviewManager.GeneratePreview(plan);
            Assert.That(preview.Placements.Count, Is.EqualTo(1));
            Assert.That((preview.Root.hideFlags & HideFlags.DontSaveInEditor) != 0, Is.True);
            RoomPreviewManager.SetVisualReview(new VisualQualityScores { mood = 100, style = 100, story = 100, composition = 100, feedback = "Reviewed in test." });

            var container = RoomPreviewManager.ApplyCurrent();

            Assert.That(container, Is.Not.Null);
            Assert.That(container.transform.childCount, Is.EqualTo(1));
            Assert.That(RoomPreviewManager.Current, Is.Null);
            Undo.PerformUndo();
            Assert.That(container == null, Is.True);
        }

        private RoomCompositionPlan CreatePlan(string styleSet, CompositionElement element)
        {
            var room = CreateRoom();
            var descriptor = CreateDescriptor(element.descriptorId, styleSet, element.role);
            var catalog = Track(ScriptableObject.CreateInstance<DecorCatalog>());
            catalog.ReplaceAll(new[] { descriptor });
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(room, null, catalog, 12345, 0.5f, new[] { element });
            return plan;
        }

        private ConceptRoom CreateRoom()
        {
            var root = Track(new GameObject("Test Room"));
            var authoringBounds = root.AddComponent<BoxCollider>();
            authoringBounds.isTrigger = true;
            authoringBounds.center = new Vector3(0f, 2f, 0f);
            authoringBounds.size = new Vector3(8f, 4f, 8f);
            var floorObject = new GameObject("Floor");
            floorObject.transform.SetParent(root.transform, false);
            floorObject.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            var floor = floorObject.AddComponent<BoxCollider>();
            floor.size = new Vector3(8f, 0.2f, 8f);
            var room = root.AddComponent<ConceptRoom>();
            room.Configure(authoringBounds, new Collider[] { floor });
            var surfaceObject = new GameObject("Floor Surface");
            surfaceObject.transform.SetParent(root.transform, false);
            var surface = surfaceObject.AddComponent<RoomSurface>();
            surface.Configure("floor-main", RoomSurfaceType.Floor, floor, new Vector3(0f, 0f, 0f), Vector3.up, Vector3.right, new Vector2(8f, 8f), true);
            room.RefreshChildren();
            return room;
        }

        private DecorAssetDescriptor CreateDescriptor(string id, string styleSet, DecorRole role)
        {
            var prefab = Track(new GameObject($"Prefab {id}"));
            var descriptor = Track(ScriptableObject.CreateInstance<DecorAssetDescriptor>());
            descriptor.InitializeFromScan(id, prefab, new Bounds(new Vector3(0f, 0.25f, 0f), new Vector3(0.5f, 0.5f, 0.5f)), DecorAssetType.Prop);
            descriptor.ConfigureMetadata(styleSet, new[] { role }, PlacementSurface.Floor, new[] { role.ToString() });
            return descriptor;
        }

        private T Track<T>(T item) where T : Object
        {
            cleanup.Add(item);
            return item;
        }

        private static WorldObb ToWorldObb(SpatialBox box) => new(
            new Vector3(box.center[0], box.center[1], box.center[2]),
            new Vector3(box.size[0], box.size[1], box.size[2]) * 0.5f,
            new Quaternion(box.rotation[0], box.rotation[1], box.rotation[2], box.rotation[3]));

        [Serializable] private sealed class SpatialFixture { public SpatialCase[] cases; }
        [Serializable] private sealed class SpatialCase { public string id; public SpatialBox left; public SpatialBox right; public bool overlap; }
        [Serializable] private sealed class SpatialBox { public float[] center; public float[] size; public float[] rotation; }
    }
}
