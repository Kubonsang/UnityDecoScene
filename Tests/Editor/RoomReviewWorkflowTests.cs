using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;
using Object = UnityEngine.Object;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class RoomReviewWorkflowTests
    {
        private readonly List<Object> cleanup = new();
        private readonly HashSet<string> reviewDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> assetCleanup = new();

        [TearDown]
        public void TearDown()
        {
            RoomPreviewManager.DiscardPreview();
            foreach (var value in cleanup.Where(value => value != null)) Object.DestroyImmediate(value);
            foreach (var path in assetCleanup) AssetDatabase.DeleteAsset(path);
            foreach (var directory in reviewDirectories.Where(Directory.Exists)) Directory.Delete(directory, true);
            if (File.Exists(RoomReviewWorkflow.CurrentPath)) File.Delete(RoomReviewWorkflow.CurrentPath);
            cleanup.Clear();
            reviewDirectories.Clear();
            assetCleanup.Clear();
        }

        [Test]
        public void ManualPreviewMoveChangesOnlyPlacementFingerprint()
        {
            var preview = RoomPreviewManager.GeneratePreview(CreatePlan(1));
            var before = RoomReviewSnapshotService.Capture(preview);

            preview.Placements[0].previewObject.transform.position += Vector3.right * 0.25f;
            var after = RoomReviewSnapshotService.Capture(preview);
            var changes = RoomReviewHashUtility.Diff(before, after);

            Assert.That(after.placementHash, Is.Not.EqualTo(before.placementHash));
            Assert.That(changes.scope, Is.EqualTo(RoomReviewChangeScope.Placement));
            Assert.That(changes.affectedIds, Does.Contain($"placement:{preview.Placements[0].placementId}"));
        }

        [Test]
        public void DisplayNamesAndCommentsDoNotInvalidateButShellAndLightingDo()
        {
            var preview = RoomPreviewManager.GeneratePreview(CreatePlan(1));
            var room = preview.Plan.Room;
            var baseline = RoomReviewSnapshotService.Capture(preview);

            room.name = "Renamed Only For Display";
            var renamed = RoomReviewSnapshotService.Capture(preview);
            Assert.That(RoomReviewHashUtility.ComputeInputHash(renamed), Is.EqualTo(RoomReviewHashUtility.ComputeInputHash(baseline)));

            room.AuthoringBounds.size += Vector3.right * 0.5f;
            var resized = RoomReviewSnapshotService.Capture(preview);
            Assert.That(resized.roomShellHash, Is.Not.EqualTo(baseline.roomShellHash));

            room.AuthoringBounds.size -= Vector3.right * 0.5f;
            var lightObject = Track(new GameObject("Presentation Light"));
            var light = lightObject.AddComponent<Light>();
            light.intensity = 1f;
            var lit = RoomReviewSnapshotService.Capture(preview);
            light.intensity = 2f;
            var relit = RoomReviewSnapshotService.Capture(preview);
            Assert.That(relit.presentationHash, Is.Not.EqualTo(lit.presentationHash));
        }

        [Test]
        public void TechnicalFailureStopsBeforeHumanCapture()
        {
            var preview = RoomPreviewManager.GeneratePreview(CreatePlan(2));
            var first = preview.Placements[0];
            var second = preview.Placements[1];
            second.previewObject.transform.SetPositionAndRotation(first.previewObject.transform.position, first.previewObject.transform.rotation);
            second.previewObject.transform.localScale = first.previewObject.transform.localScale;

            var run = RoomReviewWorkflow.Prepare(preview, false);
            TrackReviewDirectory(run.targetId);

            Assert.That(run.status, Is.EqualTo(RoomReviewStates.TechnicalFailed));
            Assert.That(run.technicalErrorCodes, Does.Contain("OBB_OVERLAP"));
            Assert.That(run.capture.views, Is.Empty);
            var captureDirectory = Path.Combine(Path.GetDirectoryName(RoomReviewWorkflow.StatePathFor(run.targetId))!, "runs", run.runId, "captures");
            Assert.That(Directory.Exists(captureDirectory), Is.False);
        }

        [Test]
        public void SameEvidenceReusesHumanApprovalAndPlacementChangeMakesItStale()
        {
            var preview = RoomPreviewManager.GeneratePreview(CreatePlan(1));
            RoomReviewTestCache.Seed(preview);
            var first = RoomReviewWorkflow.Prepare(preview, false);
            TrackReviewDirectory(first.targetId);
            Assert.That(first.status, Is.EqualTo(RoomReviewStates.AwaitingHumanReview));
            Approve(first, "Looks natural.");

            preview.Plan.Room.name = "Name Changed Without Spatial Meaning";
            var unchanged = RoomReviewWorkflow.Prepare(preview, false);
            Assert.That(unchanged.status, Is.EqualTo(RoomReviewStates.Approved));
            Assert.That(RoomReviewHashUtility.IsCurrentApproval(unchanged), Is.True);
            Assert.That(unchanged.changes.HasChanges, Is.False);
            Assert.That(unchanged.decision.comment, Is.EqualTo("Looks natural."));

            preview.Placements[0].previewObject.transform.position += Vector3.right * 0.2f;
            RoomReviewTestCache.Seed(preview);
            var changed = RoomReviewWorkflow.Prepare(preview, false);
            Assert.That(changed.status, Is.EqualTo(RoomReviewStates.Stale));
            Assert.That(RoomReviewHashUtility.IsCurrentApproval(changed), Is.False);
            Assert.That((changed.changes.scope & RoomReviewChangeScope.Placement) != 0, Is.True);
        }

        private RoomCompositionPlan CreatePlan(int count)
        {
            var root = Track(new GameObject("Review Test Room"));
            var authoring = root.AddComponent<BoxCollider>();
            authoring.isTrigger = true;
            authoring.center = new Vector3(0f, 2f, 0f);
            authoring.size = new Vector3(8f, 4f, 8f);

            var floorObject = Track(new GameObject("Floor"));
            floorObject.transform.SetParent(root.transform, false);
            floorObject.transform.localPosition = new Vector3(0f, -0.1f, 0f);
            var floor = floorObject.AddComponent<BoxCollider>();
            floor.size = new Vector3(8f, 0.2f, 8f);
            var room = root.AddComponent<ConceptRoom>();
            room.Configure(authoring, new Collider[] { floor });
            var surfaceObject = Track(new GameObject("Floor Surface"));
            surfaceObject.transform.SetParent(root.transform, false);
            var surface = surfaceObject.AddComponent<RoomSurface>();
            surface.Configure("floor-main", RoomSurfaceType.Floor, floor, Vector3.zero, Vector3.up, Vector3.right, new Vector2(8f, 8f), true);
            room.RefreshChildren();

            var source = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            source.name = "Review Prop";
            source.transform.localScale = Vector3.one * 0.5f;
            var prefabPath = $"Assets/__RoomReviewWorkflowTest_{Guid.NewGuid():N}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
            assetCleanup.Add(prefabPath);
            var descriptor = Track(ScriptableObject.CreateInstance<DecorAssetDescriptor>());
            descriptor.InitializeFromScan("review-prop", prefab, new Bounds(new Vector3(0f, 0.25f, 0f), Vector3.one * 0.5f), DecorAssetType.Prop);
            descriptor.ConfigureMetadata("dungeon", new[] { DecorRole.Hero }, PlacementSurface.Floor, new[] { "review-prop" });
            var catalog = Track(ScriptableObject.CreateInstance<DecorCatalog>());
            catalog.ReplaceAll(new[] { descriptor });
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(room, null, catalog, 417, 0.45f, new[]
            {
                new CompositionElement
                {
                    elementId = "hero",
                    descriptorId = descriptor.AssetId,
                    role = DecorRole.Hero,
                    count = count,
                    preferredZone = PreferredZone.Center,
                    spacing = 1.5f
                }
            });
            return plan;
        }

        private void Approve(RoomReviewRun run, string comment)
        {
            run.status = RoomReviewStates.Approved;
            run.decision = new RoomReviewDecision
            {
                value = RoomReviewDecisions.Approved,
                reviewer = "test-human",
                reviewedUtc = DateTime.UtcNow.ToString("O"),
                issueCodes = Array.Empty<string>(),
                comment = comment,
                inputHash = run.inputHash,
                technicalReportHash = run.technicalReportHash,
                captureSetHash = run.capture.captureSetHash
            };
            File.WriteAllText(RoomReviewWorkflow.StatePathFor(run.targetId), JsonUtility.ToJson(run, true));
        }

        private void TrackReviewDirectory(string targetId)
        {
            var directory = Path.GetDirectoryName(RoomReviewWorkflow.StatePathFor(targetId));
            if (!string.IsNullOrWhiteSpace(directory)) reviewDirectories.Add(directory);
        }

        private T Track<T>(T value) where T : Object
        {
            cleanup.Add(value);
            return value;
        }
    }
}
