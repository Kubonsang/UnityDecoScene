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
    public sealed class SurfaceArrangementCompositionEditorTests
    {
        private readonly List<Object> cleanup = new();

        [TearDown]
        public void TearDown()
        {
            foreach (var item in cleanup.Where(value => value != null)) Object.DestroyImmediate(item);
            cleanup.Clear();
        }

        [Test]
        public void PresetEditUpdatesFourSlidersAndCanBeUndone()
        {
            var plan = CreatePlan();

            SurfaceArrangementCompositionEditing.ApplyPreset(plan, 0, SurfaceArrangementPreset.Neat);

            var edited = plan.SurfaceArrangements[0];
            Assert.That(edited.Preset, Is.EqualTo(SurfaceArrangementPreset.Neat));
            Assert.That(edited.amount, Is.EqualTo(0.45f).Within(0.0001f));
            Assert.That(edited.orderliness, Is.EqualTo(0.90f).Within(0.0001f));
            Assert.That(edited.grouping, Is.EqualTo(0.55f).Within(0.0001f));
            Assert.That(edited.stacking, Is.EqualTo(0.55f).Within(0.0001f));
            Assert.That(edited.spec_hash, Is.EqualTo(SurfaceArrangementSpecUtility.ComputeSpecHash(edited)));

            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();

            Assert.That(plan.SurfaceArrangements[0].Preset, Is.EqualTo(SurfaceArrangementPreset.InUse));
            Assert.That(plan.SurfaceArrangements[0].orderliness, Is.EqualTo(0.45f).Within(0.0001f));
        }

        [Test]
        public void SliderAndMemberEditsAreClampedAndRefreshSpecHash()
        {
            var plan = CreatePlan();

            SurfaceArrangementCompositionEditing.SetSlider(plan, 0, SurfaceArrangementSlider.Stacking, 4f);
            SurfaceArrangementCompositionEditing.SetMemberCounts(plan, 0, 0, -2, 20);

            var edited = plan.SurfaceArrangements[0];
            Assert.That(edited.stacking, Is.EqualTo(1f));
            Assert.That(edited.members[0].minimum_count, Is.EqualTo(1), "A one-member arrangement must retain at least one required item.");
            Assert.That(edited.members[0].maximum_count, Is.EqualTo(12));
            Assert.That(edited.spec_hash, Is.EqualTo(SurfaceArrangementSpecUtility.ComputeSpecHash(edited)));

            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
        }

        [Test]
        public void ArrangementRegenerationFreezeExcludesOnlyRequestedArrangement()
        {
            var basePlacement = Placement("table", null, false);
            var firstArrangement = Placement("books-a", "books", true);
            var secondArrangement = Placement("papers-a", "papers", false);

            var frozen = RoomPreviewManager.BuildArrangementRegenerationLocks(
                new[] { basePlacement, firstArrangement, secondArrangement }, "books");

            Assert.That(frozen.Select(value => value.placementId), Is.EquivalentTo(new[] { "table", "papers-a" }));
            Assert.That(frozen.All(value => value.locked), Is.True, "Unrelated results must be temporarily frozen during the reroll.");
            Assert.That(secondArrangement.locked, Is.False, "Building regeneration locks must not mutate the current preview state.");
            Assert.That(frozen.Single(value => value.placementId == "papers-a"), Is.Not.SameAs(secondArrangement));
        }

        private RoomCompositionPlan CreatePlan()
        {
            var plan = Track(ScriptableObject.CreateInstance<RoomCompositionPlan>());
            plan.Configure(null, null, null, 20260715, 0.42f, Array.Empty<CompositionElement>(), new[]
            {
                new SurfaceArrangementSpec
                {
                    arrangement_id = "archive-table",
                    target_element_id = "reading-table",
                    target_frame_id = "top",
                    preset = "InUse",
                    amount = 0.55f,
                    orderliness = 0.45f,
                    grouping = 0.75f,
                    stacking = 0.55f,
                    edge_margin = 0.08f,
                    max_stack_height = 3,
                    members = new List<SurfaceArrangementMemberSpec>
                    {
                        new()
                        {
                            descriptor_id = "book-brown",
                            minimum_count = 2,
                            maximum_count = 3,
                            selection_weight = 1f,
                            affinity_group = "books"
                        }
                    }
                }
            });
            return plan;
        }

        private static PlacedDecorItem Placement(string id, string arrangementId, bool locked) => new()
        {
            placementId = id,
            elementId = id,
            arrangementId = arrangementId,
            locked = locked,
            surfaceIds = new List<string>(),
            contactEvidenceSet = new List<ContactEvidence>()
        };

        private T Track<T>(T item) where T : Object
        {
            cleanup.Add(item);
            return item;
        }
    }
}
