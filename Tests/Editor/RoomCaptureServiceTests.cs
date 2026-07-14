using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;
using Object = UnityEngine.Object;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class RoomCaptureServiceTests
    {
        private readonly List<Object> cleanup = new();
        private readonly List<string> directories = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in cleanup.Where(item => item != null)) Object.DestroyImmediate(item);
            foreach (var directory in directories.Where(Directory.Exists)) Directory.Delete(directory, true);
            cleanup.Clear();
            directories.Clear();
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

        private string NewDirectory()
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, $"../Library/DungeonDecorator/Tests/RoomCapture/{Guid.NewGuid():N}"));
            directories.Add(directory);
            return directory;
        }

        private T Track<T>(T item) where T : Object
        {
            cleanup.Add(item);
            return item;
        }
    }
}
