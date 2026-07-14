using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityDecoScene.DungeonDecorator.Editor;
using Object = UnityEngine.Object;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class RoomCaptureServiceTests
    {
        private readonly List<Object> cleanup = new();
        private readonly List<string> directories = new();
        private readonly List<Scene> previewScenes = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in cleanup.Where(item => item != null)) Object.DestroyImmediate(item);
            foreach (var scene in previewScenes.Where(scene => scene.IsValid() && scene.isLoaded)) EditorSceneManager.ClosePreviewScene(scene);
            foreach (var directory in directories.Where(Directory.Exists)) Directory.Delete(directory, true);
            cleanup.Clear();
            directories.Clear();
            previewScenes.Clear();
        }

        [Test]
        public void CaptureSetUsesFixedViewIdsAndPathIndependentHash()
        {
            var session = CreateSession(addPrimaryObservation: false);
            var directory = NewDirectory();

            var result = RoomCaptureService.CaptureSet(session, directory);

            Assert.That(result.entries.Select(entry => entry.viewId), Is.EqualTo(new[]
            {
                RoomCaptureViewIds.Top,
                RoomCaptureViewIds.PrimaryObservation,
                RoomCaptureViewIds.CornerA,
                RoomCaptureViewIds.CornerB
            }));
            Assert.That(result.renderedCount, Is.EqualTo(4));
            Assert.That(result.entries.All(entry => File.Exists(entry.path) && entry.contentHash.Length == 64), Is.True);
            var relocated = result.entries.Select(entry => new RoomCaptureEntry(entry.viewId, Path.Combine("different", Path.GetFileName(entry.path)), entry.contentHash));
            Assert.That(RoomCaptureService.ComputeCaptureSetHash(relocated), Is.EqualTo(result.captureSetHash));
        }

        [Test]
        public void CaptureSetAddsDeterministicSideCloseupsForWallBackedAndMountedLights()
        {
            var session = CreateSession(addPrimaryObservation: true);
            var wall = AddWallSurface(session);
            AddWallPlacement(session, wall, "hero-bookcase:0", "bookcase-double", DecorAssetType.Prop, ContactRequirement.WallBacked, new Vector3(-1.4f, 1.4f, 3.65f), new Vector3(1.8f, 2.8f, 0.45f));
            AddWallPlacement(session, wall, "light/torch:1", "torch-mounted", DecorAssetType.Light, ContactRequirement.WallMounted, new Vector3(1.4f, 2.1f, 3.7f), new Vector3(0.35f, 0.8f, 0.35f));

            var first = RoomCaptureService.CaptureSet(session, NewDirectory());
            var second = RoomCaptureService.CaptureSet(session, NewDirectory());

            Assert.That(first.entries.Take(4).Select(value => value.viewId), Is.EqualTo(new[]
            {
                RoomCaptureViewIds.Top,
                RoomCaptureViewIds.PrimaryObservation,
                RoomCaptureViewIds.CornerA,
                RoomCaptureViewIds.CornerB
            }));
            Assert.That(first.entries, Has.Count.EqualTo(6));
            Assert.That(first.entries.Skip(4).All(value => RoomCaptureViewIds.IsWallContactSide(value.viewId)), Is.True);
            Assert.That(first.entries.Skip(4).Select(value => value.viewId), Is.EqualTo(second.entries.Skip(4).Select(value => value.viewId)));
            Assert.That(first.entries.Skip(4).All(value => Path.GetFileName(value.path) == value.viewId + ".png"), Is.True);
            Assert.That(first.entries.Skip(4).All(value => value.contentHash.Length == 64), Is.True);
        }

        [Test]
        public void ContactCloseupCacheReusesTheDynamicViewSet()
        {
            var session = CreateSession(addPrimaryObservation: true);
            var wall = AddWallSurface(session);
            AddWallPlacement(session, wall, "hero-bookcase:0", "bookcase-double", DecorAssetType.Prop, ContactRequirement.WallBacked, new Vector3(0f, 1.4f, 3.65f), new Vector3(1.8f, 2.8f, 0.45f));
            var directory = NewDirectory();

            var first = RoomCaptureService.CaptureSet(session, directory, "wall-contact-input-v1");
            var cached = RoomCaptureService.CaptureSet(session, directory, "wall-contact-input-v1");

            Assert.That(first.renderedCount, Is.EqualTo(5));
            Assert.That(cached.renderedCount, Is.Zero);
            Assert.That(cached.reusedFromCache, Is.True);
            Assert.That(cached.entries.Select(value => value.viewId), Is.EqualTo(first.entries.Select(value => value.viewId)));
            Assert.That(cached.captureSetHash, Is.EqualTo(first.captureSetHash));
        }

        [Test]
        public void WallBackedCloseupUsesTheOriginalRuleIndexForDualContactFurniture()
        {
            var session = CreateSession(addPrimaryObservation: true);
            var wall = AddWallSurface(session);
            AddWallPlacement(session, wall, "hero-bookcase:0", "bookcase-double", DecorAssetType.Prop, ContactRequirement.WallBacked, new Vector3(0f, 1.4f, 3.65f), new Vector3(1.8f, 2.8f, 0.45f));

            var placement = session.Placements.Single();
            var floorRule = ContactRules.Defaults(ContactRequirement.FloorSupported);
            floorRule.frameId = "bottom";
            var wallRule = placement.descriptor.Geometry.contact;
            placement.descriptor.Geometry.contacts = new List<ContactRules> { floorRule, wallRule };
            placement.surfaceIds = new List<string> { "floor-main", wall.SurfaceId };
            placement.contactEvidenceSet = new List<ContactEvidence>
            {
                new() { surfaceId = "floor-main", contactPoint = new Vector3(0f, 0f, 3.65f), valid = true },
                new() { surfaceId = wall.SurfaceId, contactPoint = new Vector3(0f, 1.4f, wall.Origin.z), valid = true }
            };

            var result = RoomCaptureService.CaptureSet(session, NewDirectory());

            Assert.That(result.entries, Has.Count.EqualTo(5));
            Assert.That(RoomCaptureViewIds.IsWallContactSide(result.entries[4].viewId), Is.True);
        }

        [Test]
        public void WallMountedNonLightDoesNotAddTorchCloseup()
        {
            var session = CreateSession(addPrimaryObservation: true);
            var wall = AddWallSurface(session);
            AddWallPlacement(session, wall, "banner:0", "banner-red", DecorAssetType.Prop, ContactRequirement.WallMounted, new Vector3(0f, 2f, 3.7f), new Vector3(1.2f, 1.8f, 0.1f));

            var result = RoomCaptureService.CaptureSet(session, NewDirectory());

            Assert.That(result.entries, Has.Count.EqualTo(4));
            Assert.That(result.entries.Any(value => RoomCaptureViewIds.IsWallContactSide(value.viewId)), Is.False);
        }

        [Test]
        public void MatchingInputKeyAndVerifiedFilesReuseWithoutRendering()
        {
            var session = CreateSession(addPrimaryObservation: true);
            var directory = NewDirectory();
            var first = RoomCaptureService.CaptureSet(session, directory, "stable-input-v1");

            var second = RoomCaptureService.CaptureSet(session, directory, "stable-input-v1");

            Assert.That(first.renderedCount, Is.EqualTo(4));
            Assert.That(second.renderedCount, Is.Zero);
            Assert.That(second.reusedFromCache, Is.True);
            Assert.That(second.captureSetHash, Is.EqualTo(first.captureSetHash));
            Assert.That(second.entries.Select(entry => entry.contentHash), Is.EqualTo(first.entries.Select(entry => entry.contentHash)));
        }

        [Test]
        public void CorruptCachedPngForcesCompleteRerender()
        {
            var session = CreateSession(addPrimaryObservation: true);
            var directory = NewDirectory();
            var first = RoomCaptureService.CaptureSet(session, directory, "stable-input-v1");
            File.AppendAllText(first.entries[0].path, "corrupt");

            var repaired = RoomCaptureService.CaptureSet(session, directory, "stable-input-v1");

            Assert.That(repaired.renderedCount, Is.EqualTo(4));
            Assert.That(repaired.reusedFromCache, Is.False);
            Assert.That(repaired.entries.All(entry => entry.contentHash.Length == 64), Is.True);
        }

        [Test]
        public void LegacyCaptureAllStillCapturesEveryObservationPoint()
        {
            var session = CreateSession(addPrimaryObservation: true);
            var secondObservation = new GameObject("Secondary Observation");
            secondObservation.transform.SetParent(session.Plan.Room.transform, false);
            secondObservation.transform.localPosition = new Vector3(3f, 1.6f, 0f);
            secondObservation.transform.rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
            secondObservation.AddComponent<RoomObservationPoint>();
            session.Plan.Room.RefreshChildren();

            var paths = RoomCaptureService.CaptureAll(session);
            directories.Add(Path.GetDirectoryName(paths[0]));

            Assert.That(paths.Select(Path.GetFileNameWithoutExtension), Is.EqualTo(new[]
            {
                RoomCaptureViewIds.Top,
                "observation-0",
                "observation-1",
                RoomCaptureViewIds.CornerA,
                RoomCaptureViewIds.CornerB
            }));
        }

        [Test]
        public void CaptureExcludesObjectsFromOtherLoadedScenes()
        {
            var session = CreateSession(addPrimaryObservation: true);
            var wall = AddWallSurface(session);
            AddWallPlacement(session, wall, "hero-bookcase:0", "bookcase-double", DecorAssetType.Prop, ContactRequirement.WallBacked, new Vector3(0f, 1.4f, 3.65f), new Vector3(1.8f, 2.8f, 0.45f));
            var target = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            target.name = "Room Target";
            target.transform.position = new Vector3(0f, 1.6f, 0f);
            target.transform.localScale = Vector3.one * 2f;
            SetUnlitColor(target, Color.green);
            var baseline = RoomCaptureService.CaptureSet(session, NewDirectory());

            var foreignScene = EditorSceneManager.NewPreviewScene();
            previewScenes.Add(foreignScene);
            var occluder = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            occluder.name = "Foreign Scene Occluder";
            occluder.transform.position = new Vector3(0f, 1.6f, -2.5f);
            occluder.transform.localScale = new Vector3(6f, 6f, 0.2f);
            SceneManager.MoveGameObjectToScene(occluder, foreignScene);
            SetUnlitColor(occluder, Color.red);

            var withForeignScene = RoomCaptureService.CaptureSet(session, NewDirectory());
            Assert.That(withForeignScene.entries.Select(value => value.viewId), Is.EqualTo(baseline.entries.Select(value => value.viewId)));
            Assert.That(withForeignScene.entries.Select(value => value.contentHash), Is.EqualTo(baseline.entries.Select(value => value.contentHash)),
                "Adding an occluder in another loaded scene must not change room or wall-contact captures.");
        }

        private PreviewSession CreateSession(bool addPrimaryObservation)
        {
            var root = Track(new GameObject("Capture Test Room"));
            var bounds = root.AddComponent<BoxCollider>();
            bounds.isTrigger = true;
            bounds.center = new Vector3(0f, 2f, 0f);
            bounds.size = new Vector3(8f, 4f, 8f);
            var room = root.AddComponent<ConceptRoom>();
            room.Configure(bounds, Array.Empty<Collider>());

            if (addPrimaryObservation)
            {
                var observationObject = new GameObject("Primary Entrance");
                observationObject.transform.SetParent(root.transform, false);
                observationObject.transform.localPosition = new Vector3(0f, 1.6f, -3.5f);
                observationObject.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
                observationObject.AddComponent<RoomObservationPoint>();
                room.RefreshChildren();
            }

            var catalog = Track(ScriptableObject.CreateInstance<DecorCatalog>());
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(room, null, catalog, 17, 0.5f, Array.Empty<CompositionElement>());
            return new PreviewSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Plan = plan,
                ManifestHash = "room-hash",
                GeometryProfileHash = "geometry-hash"
            };
        }

        private RoomSurface AddWallSurface(PreviewSession session)
        {
            var wallObject = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            wallObject.name = "North Wall Surface";
            wallObject.transform.SetParent(session.Plan.Room.transform, false);
            wallObject.transform.position = new Vector3(0f, 2f, 3.9f);
            wallObject.transform.localScale = new Vector3(7f, 4f, 0.2f);
            var collider = wallObject.GetComponent<BoxCollider>();
            var surface = wallObject.AddComponent<RoomSurface>();
            surface.Configure("wall-north", RoomSurfaceType.Wall, collider, new Vector3(0f, 2f, 3.8f), Vector3.back, Vector3.right, new Vector2(7f, 4f), true);
            session.Plan.Room.RefreshChildren();
            return surface;
        }

        private void AddWallPlacement(
            PreviewSession session,
            RoomSurface surface,
            string placementId,
            string assetId,
            DecorAssetType assetType,
            ContactRequirement requirement,
            Vector3 position,
            Vector3 size)
        {
            var descriptor = Track(ScriptableObject.CreateInstance<DecorAssetDescriptor>());
            descriptor.InitializeFromScan(assetId, null, new Bounds(Vector3.zero, size), assetType);
            descriptor.ConfigureMetadata("dungeon-archive", new[] { assetType == DecorAssetType.Light ? DecorRole.LightingCue : DecorRole.Hero }, PlacementSurface.Wall);
            var rules = ContactRules.Defaults(requirement);
            rules.frameId = "back";
            descriptor.ConfigureGeometry(new DecorGeometryProfile
            {
                source = GeometrySource.RendererBounds,
                reviewed = true,
                collisionProxies = new List<OrientedBoxProxy> { new("aggregate", Vector3.zero, size, Quaternion.identity) },
                backContact = new ContactFrame
                {
                    frameId = "back",
                    localPoint = new Vector3(0f, 0f, size.z * 0.5f),
                    localNormal = Vector3.forward,
                    localTangent = Vector3.right,
                    size = new Vector2(size.x, size.y)
                },
                contact = rules,
                contacts = new List<ContactRules> { rules }
            });
            var contactPoint = new Vector3(position.x, position.y, surface.Origin.z);
            session.Placements.Add(new PlacedDecorItem
            {
                placementId = placementId,
                descriptor = descriptor,
                role = assetType == DecorAssetType.Light ? DecorRole.LightingCue : DecorRole.Hero,
                position = position,
                rotation = Quaternion.identity,
                scale = Vector3.one,
                worldBounds = new Bounds(position, size),
                surfaceId = surface.SurfaceId,
                surfaceIds = new List<string> { surface.SurfaceId },
                contactEvidence = new ContactEvidence { surfaceId = surface.SurfaceId, contactPoint = contactPoint, valid = true },
                contactEvidenceSet = new List<ContactEvidence> { new() { surfaceId = surface.SurfaceId, contactPoint = contactPoint, valid = true } }
            });
            var visual = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            visual.name = assetId;
            visual.transform.SetPositionAndRotation(position, Quaternion.identity);
            visual.transform.localScale = size;
        }

        private string NewDirectory()
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, $"../Library/DungeonDecorator/Tests/RoomCapture/{Guid.NewGuid():N}"));
            directories.Add(directory);
            return directory;
        }

        private void SetUnlitColor(GameObject target, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            Assert.That(shader, Is.Not.Null, "An unlit shader is required for the scene-isolation capture test.");
            var material = Track(new Material(shader));
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            target.GetComponent<Renderer>().sharedMaterial = material;
        }

        private T Track<T>(T item) where T : Object
        {
            cleanup.Add(item);
            return item;
        }
    }
}
