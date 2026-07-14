using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityDecoScene.DungeonDecorator.Editor;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class RoomReviewHashTests
    {
        [Test]
        public void Sha256MatchesKnownVector()
        {
            Assert.That(RoomReviewHashUtility.Sha256("abc"), Is.EqualTo(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        }

        [Test]
        public void InputHashUsesOrdinalCanonicalItemOrder()
        {
            var first = Inputs(
                Item("pillar-b", RoomReviewChangeScope.Composition, "transform-b"),
                Item("Pillar-A", RoomReviewChangeScope.Composition, "transform-a"),
                Item("pillar-a", RoomReviewChangeScope.Asset, "asset-a"));
            var reordered = Inputs(
                Item("pillar-a", RoomReviewChangeScope.Asset, "asset-a"),
                Item("Pillar-A", RoomReviewChangeScope.Composition, "transform-a"),
                Item("pillar-b", RoomReviewChangeScope.Composition, "transform-b"));

            Assert.That(RoomReviewHashUtility.ComputeInputHash(first), Is.EqualTo(RoomReviewHashUtility.ComputeInputHash(reordered)));

            reordered.items[1].id = "pillar-A";
            Assert.That(RoomReviewHashUtility.ComputeInputHash(first), Is.Not.EqualTo(RoomReviewHashUtility.ComputeInputHash(reordered)),
                "Ordinal comparison must remain case-sensitive.");
        }

        [Test]
        public void FloatHashQuantizesToOneTenThousandth()
        {
            var baseline = RoomReviewHashUtility.HashQuantized(new[] { 1f, -2f, 0f });
            var belowHalfQuantum = RoomReviewHashUtility.HashQuantized(new[] { 1.00004f, -2.00004f, -0f });
            var nextQuantum = RoomReviewHashUtility.HashQuantized(new[] { 1.0001f, -2f, 0f });

            Assert.That(belowHalfQuantum, Is.EqualTo(baseline));
            Assert.That(nextQuantum, Is.Not.EqualTo(baseline));
            Assert.Throws<ArgumentOutOfRangeException>(() => RoomReviewHashUtility.Quantize(float.NaN));
        }

        [Test]
        public void DiffIsOrderIndependentAndReportsOnlyChangedScopesAndIds()
        {
            var previous = Inputs(
                Item("corner-pillar", RoomReviewChangeScope.Placement, "old-transform"),
                Item("hero-table", RoomReviewChangeScope.Placement, "same-transform"),
                Item("pillar-prefab", RoomReviewChangeScope.Asset, "old-dependency"));
            var current = Inputs(
                Item("pillar-prefab", RoomReviewChangeScope.Asset, "new-dependency"),
                Item("hero-table", RoomReviewChangeScope.Placement, "same-transform"),
                Item("corner-pillar", RoomReviewChangeScope.Placement, "new-transform"));

            var changes = RoomReviewHashUtility.Diff(previous, current);

            Assert.That(changes.scope, Is.EqualTo(RoomReviewChangeScope.Placement | RoomReviewChangeScope.Asset));
            Assert.That(changes.affectedIds, Is.EqualTo(new[] { "corner-pillar", "pillar-prefab" }));

            previous.items.Reverse();
            current.items.Reverse();
            Assert.That(RoomReviewHashUtility.Diff(previous, previous).HasChanges, Is.False);
            Assert.That(RoomReviewHashUtility.Diff(current, current).affectedIds, Is.Empty);
        }

        [Test]
        public void DiffSeparatesScalarInputCategories()
        {
            var previous = Inputs();
            var current = Inputs();
            current.conceptHash = "concept-v2";
            current.validationRulesHash = "rules-v2";
            current.presentationHash = "presentation-v2";

            var changes = RoomReviewHashUtility.Diff(previous, current);

            Assert.That(changes.scope, Is.EqualTo(RoomReviewChangeScope.Concept | RoomReviewChangeScope.ValidationRules | RoomReviewChangeScope.Presentation));
            Assert.That(changes.affectedIds, Is.Empty);
        }

        [Test]
        public void ApprovalMustMatchAllCurrentEvidenceHashes()
        {
            var inputs = Inputs();
            var inputHash = RoomReviewHashUtility.ComputeInputHash(inputs);
            var run = ApprovedRun(inputs, inputHash);

            Assert.That(RoomReviewHashUtility.IsCurrentApproval(run), Is.True);
            Assert.That(RoomReviewHashUtility.BuildAgentBrief(run).nextAction, Is.EqualTo(RoomReviewNextActions.ApplyAvailable));

            run.capture.captureSetHash = "capture-v2";
            Assert.That(RoomReviewHashUtility.IsCurrentApproval(run), Is.False);
            Assert.That(RoomReviewHashUtility.BuildAgentBrief(run).stale, Is.True);
            Assert.That(RoomReviewHashUtility.BuildAgentBrief(run).nextAction, Is.EqualTo(RoomReviewNextActions.RunReview));
        }

        [Test]
        public void CompactAgentBriefOmitsHeavyInputsPathsMessagesAndComments()
        {
            var inputs = Inputs(Item("corner-pillar", RoomReviewChangeScope.Placement, new string('x', 500)));
            inputs.roomShellHash = new string('r', 500);
            var run = ApprovedRun(inputs, RoomReviewHashUtility.ComputeInputHash(inputs));
            run.status = RoomReviewStates.AwaitingHumanReview;
            run.decision = new RoomReviewDecision { comment = new string('c', 1000) };
            run.capture.views[0].imagePath = "C:/very/large/private/path/front.png";
            run.changes = new RoomReviewChangeSet
            {
                scope = RoomReviewChangeScope.Placement,
                affectedIds = new[] { "z-pillar", "a-pillar", "a-pillar" }
            };
            run.technicalErrorCodes = new[] { "OBB_OVERLAP", "CONTACT_GAP", "OBB_OVERLAP" };

            var json = RoomReviewHashUtility.ToCompactAgentBriefJson(run);

            Assert.That(json.Length, Is.LessThan(2048));
            Assert.That(json, Does.Not.Contain("private/path"));
            Assert.That(json, Does.Not.Contain(new string('c', 20)));
            Assert.That(json, Does.Not.Contain(new string('r', 20)));
            Assert.That(json, Does.Not.Contain(new string('x', 20)));
            var brief = RoomReviewHashUtility.BuildAgentBrief(run);
            Assert.That(brief.affectedIds, Is.EqualTo(new[] { "a-pillar", "z-pillar" }));
            Assert.That(brief.technicalErrorCodes, Is.EqualTo(new[] { "CONTACT_GAP", "OBB_OVERLAP" }));
            Assert.That(brief.requiredViewCount, Is.EqualTo(1));
            Assert.That(brief.nextAction, Is.EqualTo(RoomReviewNextActions.OpenReview));
        }

        [Test]
        public void StalePriorApprovalWithFreshCapturesRoutesToHumanReviewInsteadOfLoopingAgent()
        {
            var inputs = Inputs();
            var run = ApprovedRun(inputs, RoomReviewHashUtility.ComputeInputHash(inputs));
            run.status = RoomReviewStates.Stale;
            run.inputHash = "new-input";

            var brief = RoomReviewHashUtility.BuildAgentBrief(run);

            Assert.That(brief.stale, Is.True);
            Assert.That(brief.nextAction, Is.EqualTo(RoomReviewNextActions.OpenReview));
        }

        private static RoomReviewInputs Inputs(params RoomReviewInputItem[] items) => new()
        {
            targetId = "room-01",
            roomShellHash = "room-v1",
            compositionHash = "composition-v1",
            placementHash = "placement-v1",
            assetDependencyHash = "assets-v1",
            conceptHash = "concept-v1",
            validationRulesHash = "rules-v1",
            captureProfileHash = "capture-profile-v1",
            presentationHash = "presentation-v1",
            items = new List<RoomReviewInputItem>(items)
        };

        private static RoomReviewInputItem Item(string id, RoomReviewChangeScope scope, string hash) => new()
        {
            id = id,
            scope = scope,
            hash = hash
        };

        private static RoomReviewRun ApprovedRun(RoomReviewInputs inputs, string inputHash) => new()
        {
            runId = "run-01",
            targetId = inputs.targetId,
            targetName = "Dungeon Room",
            status = RoomReviewStates.Approved,
            inputHash = inputHash,
            inputs = inputs,
            technicalReportHash = "report-v1",
            capture = new RoomReviewCapture
            {
                captureSetHash = "capture-v1",
                views = new List<RoomReviewCaptureView>
                {
                    new() { id = "front", contentHash = "front-v1", imagePath = "front.png", required = true },
                    new() { id = "debug", contentHash = "debug-v1", imagePath = "debug.png", required = false }
                }
            },
            decision = new RoomReviewDecision
            {
                value = RoomReviewDecisions.Approved,
                reviewer = "local-user",
                inputHash = inputHash,
                technicalReportHash = "report-v1",
                captureSetHash = "capture-v1"
            }
        };
    }
}
