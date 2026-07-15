using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class SpatialCalibrationWorkflowTests
    {
        [Test]
        public void ApprovedBookcaseDefaultAvoidsRepeatedRelationInference()
        {
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            try
            {
                descriptor.InitializeFromScan("bookcase_double_decoratedA", prefab, new Bounds(Vector3.zero, Vector3.one), DecorAssetType.Prop);
                var template = SpatialCalibrationWorkflow.InferTemplate(descriptor, out var source);
                Assert.That(template, Is.EqualTo(SpatialCalibrationTemplate.WallBackedFloorSupported));
                Assert.That(source, Is.EqualTo("ApprovedDefault"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(descriptor);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void UnsupportedCeilingRelationStaysHumanReviewedInsteadOfGuessed()
        {
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            try
            {
                descriptor.InitializeFromScan("ceiling_unknown", prefab, new Bounds(Vector3.zero, Vector3.one), DecorAssetType.Prop);
                descriptor.ConfigureMetadata("default", new[] { DecorRole.Clutter }, PlacementSurface.Ceiling);
                var template = SpatialCalibrationWorkflow.InferTemplate(descriptor, out var source);
                Assert.That(template, Is.Null);
                Assert.That(source, Is.EqualTo("NeedsHumanRelation"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(descriptor);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void AgentBriefContainsOnlyNextActionAndBlockers()
        {
            var state = new SpatialCalibrationWorkflowState
            {
                workflowId = "workflow",
                status = SpatialCalibrationWorkflowStates.AwaitingHumanReview,
                items = new List<SpatialCalibrationWorkflowItem>
                {
                    new() { assetId = "ready", status = SpatialCalibrationWorkflowStates.AwaitingHumanReview, captureDirectory = "large/path/not-needed" },
                    new() { assetId = "bad", status = SpatialCalibrationWorkflowStates.TechnicalFailed, errors = new[] { "OBB_OVERLAP: details", "OBB_OVERLAP: duplicate" } }
                }
            };
            var brief = SpatialCalibrationWorkflow.BuildAgentBrief(state);
            var json = JsonUtility.ToJson(brief);
            Assert.That(brief.nextAction, Is.EqualTo("WAIT_FOR_HUMAN_REVIEW"));
            Assert.That(brief.blockers, Has.Count.EqualTo(1));
            Assert.That(brief.blockers[0].errorCodes, Is.EqualTo(new[] { "OBB_OVERLAP" }));
            Assert.That(json, Does.Not.Contain("large/path/not-needed"));
        }

        [Test]
        public void MixedHumanDecisionsKeepExplicitStatusAndCounts()
        {
            var state = new SpatialCalibrationWorkflowState
            {
                workflowId = "mixed",
                items = new List<SpatialCalibrationWorkflowItem>
                {
                    new() { assetId = "approved", status = SpatialCalibrationWorkflowStates.Approved },
                    new() { assetId = "revise", status = SpatialCalibrationWorkflowStates.RevisionRequested,
                        revisionIssueCodes = new[] { SpatialArrangementRevisionIssues.TooNeat } },
                    new() { assetId = "recapture", status = SpatialCalibrationWorkflowStates.UnableToJudge }
                }
            };

            var brief = SpatialCalibrationWorkflow.BuildAgentBrief(state);

            Assert.That(SpatialCalibrationWorkflow.SummarizeStatus(state.items),
                Is.EqualTo(SpatialCalibrationWorkflowStates.RevisionRequested));
            Assert.That(brief.status, Is.EqualTo(SpatialCalibrationWorkflowStates.RevisionRequested));
            Assert.That(brief.approved, Is.EqualTo(1));
            Assert.That(brief.revisionRequested, Is.EqualTo(1));
            Assert.That(brief.unableToJudge, Is.EqualTo(1));
            Assert.That(brief.nextAction, Is.EqualTo("REVISE_REQUESTED_ITEMS"));
        }

        [Test]
        public void RerunPreservesRevisionHistoryButClearsActiveReviewIdentity()
        {
            var item = new SpatialCalibrationWorkflowItem
            {
                status = SpatialCalibrationWorkflowStates.RevisionRequested,
                reviewer = "local-user",
                reviewedUtc = "2026-07-15T00:00:00Z",
                comment = "책이 지나치게 가지런합니다.",
                revisionIssueCodes = new[] { SpatialArrangementRevisionIssues.TooNeat },
                captureHash = "old-capture"
            };

            SpatialCalibrationWorkflow.ResetEvidenceForRerun(item);

            Assert.That(item.revisionComment, Is.EqualTo("책이 지나치게 가지런합니다."));
            Assert.That(item.revisionIssueCodes, Is.EqualTo(new[] { SpatialArrangementRevisionIssues.TooNeat }));
            Assert.That(item.reviewer, Is.Empty);
            Assert.That(item.reviewedUtc, Is.Empty);
            Assert.That(item.comment, Is.Empty);
            Assert.That(item.captureHash, Is.Empty);
        }

        [Test]
        public void ReviewTemplateReceivesEmbeddedWorkflowWithoutRuntimePrompting()
        {
            var state = new SpatialCalibrationWorkflowState { workflowId = "abc", createdUtc = "now" };
            var html = SpatialCalibrationWorkflow.RenderReviewHtml(state, "<script>const state=__WORKFLOW_JSON__;</script><i>__GENERATED_UTC__</i>");
            Assert.That(html, Does.Contain("\"workflowId\":\"abc\""));
            Assert.That(html, Does.Not.Contain("__WORKFLOW_JSON__"));
            Assert.That(html, Does.Not.Contain("__GENERATED_UTC__"));
        }

        [Test]
        public void ReviewHtmlRoundTripsKoreanFeedbackAsUtf8WithoutBom()
        {
            var state = new SpatialCalibrationWorkflowState
            {
                workflowId = "utf8",
                items = new List<SpatialCalibrationWorkflowItem>
                {
                    new()
                    {
                        assetId = "book_brown",
                        displayName = "갈색 책",
                        status = SpatialCalibrationWorkflowStates.RevisionRequested,
                        revisionComment = "쌓임이 부자연스러워요.",
                        revisionIssueCodes = new[] { SpatialArrangementRevisionIssues.UnnaturalStack }
                    }
                }
            };
            var html = SpatialCalibrationWorkflow.RenderReviewHtml(state,
                "<meta charset=\"utf-8\"><script>const state=__WORKFLOW_JSON__;</script>__GENERATED_UTC__");
            var path = System.IO.Path.GetTempFileName();
            try
            {
                System.IO.File.WriteAllText(path, html, new System.Text.UTF8Encoding(false));
                var bytes = System.IO.File.ReadAllBytes(path);
                var loaded = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
                Assert.That(bytes.Take(3), Is.Not.EqualTo(new byte[] { 0xef, 0xbb, 0xbf }));
                Assert.That(loaded, Does.Contain("갈색 책"));
                Assert.That(loaded, Does.Contain("쌓임이 부자연스러워요."));
            }
            finally { System.IO.File.Delete(path); }
        }

        [Test]
        public void MissingUnityCtxConfigurationReturnsEmptyInsteadOfGuessing()
        {
            var value = SpatialCalibrationWorkflow.ResolveUnityCtxBinary();
            Assert.That(value, Is.Not.Null);
            if (!string.IsNullOrWhiteSpace(value)) Assert.That(System.IO.File.Exists(value), Is.True);
        }

        [Test]
        public void ReviewTemplateSeparatesViewChecksFromExplicitDecisionAndBatchSave()
        {
            var template = System.IO.File.ReadAllText(
                "Packages/com.unitydecoscene.dungeon-decorator/Editor/Spatial/Templates/CalibrationReviewTemplate.html");
            Assert.That(template, Does.Contain("data-reviewed"));
            Assert.That(template, Does.Contain("체크만으로 승인되지 않습니다"));
            Assert.That(template, Does.Contain("수정 필요 선택 시 필수"));
            Assert.That(template, Does.Contain("수정할 문제를 선택하거나 이유를 적어주세요."));
            Assert.That(template, Does.Contain("승인 예정"));
            Assert.That(template, Does.Contain("수정 필요"));
            Assert.That(template, Does.Contain("미판정"));
            Assert.That(template, Does.Contain("decisions.get(value.id)!=='Approved'"));
            Assert.That(template, Does.Contain("awaitingGroups.flatMap(group=>group.items.map"));
            Assert.That(template, Does.Contain("동일 공간 모델"));
            Assert.That(template, Does.Contain("/api/spatial/workflow/review-batch"));
            Assert.That(template, Does.Contain("ARRANGEMENT_TOO_EMPTY"));
            Assert.That(template, Does.Contain("쌓임이 부자연스러움"));
            Assert.That(template, Does.Contain("판단 불가"));
            Assert.That(template, Does.Contain("이전 수정 요청"));
        }

        [TestCase("FloorSupported", SpatialCalibrationTemplate.FloorSupported)]
        [TestCase("wallbackedfloorsupported", SpatialCalibrationTemplate.WallBackedFloorSupported)]
        [TestCase("WallMounted", SpatialCalibrationTemplate.WallMounted)]
        public void CatalogSeedParsesOnlyExplicitRelationshipTemplates(
            string value,
            SpatialCalibrationTemplate expected)
        {
            Assert.That(SpatialCalibrationCatalogSeed.ParseTemplate(value), Is.EqualTo(expected));
        }

        [Test]
        public void CatalogSeedRejectsUnknownRelationshipTemplate()
        {
            Assert.Throws<System.IO.InvalidDataException>(() =>
                SpatialCalibrationCatalogSeed.ParseTemplate("ProbablyOnAWall"));
        }

        [Test]
        public void GeometryFamilyHashUsesSpatialEnvelopeInsteadOfRenderMaterial()
        {
            var first = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var second = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var firstMaterial = new Material(Shader.Find("Standard"));
            var secondMaterial = new Material(Shader.Find("Standard"));
            try
            {
                first.GetComponent<Renderer>().sharedMaterial = firstMaterial;
                second.GetComponent<Renderer>().sharedMaterial = secondMaterial;
                firstMaterial.color = Color.red;
                secondMaterial.color = Color.blue;
                Assert.That(SpatialGeometryFamilyHasher.Compute(first),
                    Is.EqualTo(SpatialGeometryFamilyHasher.Compute(second)));
                second.transform.localScale = new Vector3(2f, 1f, 1f);
                Assert.That(SpatialGeometryFamilyHasher.Compute(first),
                    Is.Not.EqualTo(SpatialGeometryFamilyHasher.Compute(second)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(firstMaterial);
                UnityEngine.Object.DestroyImmediate(secondMaterial);
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void NextBatchLimitCountsGeometryFamiliesAndKeepsTheirVariantsTogether()
        {
            var items = new List<SpatialCalibrationWorkflowItem>
            {
                new() { id = "banner-red", geometryFamilyHash = "banner", template = "WallMounted", status = SpatialCalibrationWorkflowStates.Pending },
                new() { id = "banner-blue", geometryFamilyHash = "banner", template = "WallMounted", status = SpatialCalibrationWorkflowStates.Pending },
                new() { id = "chair", geometryFamilyHash = "chair", template = "FloorSupported", status = SpatialCalibrationWorkflowStates.Pending }
            };

            var selected = SpatialCalibrationWorkflow.SelectBatchCandidates(items, 1);

            Assert.That(selected.Select(item => item.id), Is.EqualTo(new[] { "banner-red", "banner-blue" }));
        }

        [Test]
        public void SameEnvelopeWithDifferentRelationshipStaysInSeparateBatchFamily()
        {
            var items = new List<SpatialCalibrationWorkflowItem>
            {
                new() { id = "wall", geometryFamilyHash = "box", template = "WallMounted", status = SpatialCalibrationWorkflowStates.Pending },
                new() { id = "floor", geometryFamilyHash = "box", template = "FloorSupported", status = SpatialCalibrationWorkflowStates.Pending }
            };

            var selected = SpatialCalibrationWorkflow.SelectBatchCandidates(items, 1);

            Assert.That(selected.Select(item => item.id), Is.EqualTo(new[] { "wall" }));
        }

        [Test]
        public void SameGeometryFamilyDifferentSupportedBySignaturesHaveIndependentReviewGroups()
        {
            var table = new InteractionSpatialContractPayload
            {
                subject_guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                target_key = "asset:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                relation = "SupportedBy",
                subject_frame = "back",
                target_frame = "top",
                relative_position = new[] { 0f, 1f, 0f },
                relative_rotation = new[] { -0.7071068f, 0f, 0f, 0.7071068f }
            };
            var stack = new InteractionSpatialContractPayload
            {
                subject_guid = table.subject_guid,
                target_key = "asset:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                relation = table.relation,
                subject_frame = table.subject_frame,
                target_frame = table.target_frame,
                relative_position = new[] { 0f, 0.2f, 0f },
                relative_rotation = (float[])table.relative_rotation.Clone()
            };
            var materialVariant = new InteractionSpatialContractPayload
            {
                subject_guid = "cccccccccccccccccccccccccccccccc",
                target_key = table.target_key,
                relation = table.relation,
                subject_frame = table.subject_frame,
                target_frame = table.target_frame,
                relative_position = (float[])table.relative_position.Clone(),
                relative_rotation = (float[])table.relative_rotation.Clone(),
                revision = 7
            };

            var tableGroup = SpatialCalibrationReviewIdentity.BuildInteractionReviewGroupKey("book-family", table);
            var stackGroup = SpatialCalibrationReviewIdentity.BuildInteractionReviewGroupKey("book-family", stack);
            var variantGroup = SpatialCalibrationReviewIdentity.BuildInteractionReviewGroupKey("book-family", materialVariant);

            Assert.That(stackGroup, Is.Not.EqualTo(tableGroup));
            Assert.That(variantGroup, Is.EqualTo(tableGroup), "Subject GUID must not split material-only geometry aliases.");
        }

        [Test]
        public void ReviewTemplatePrefersExplicitInteractionReviewGroupKey()
        {
            var template = System.IO.File.ReadAllText(
                "Packages/com.unitydecoscene.dungeon-decorator/Editor/Spatial/Templates/CalibrationReviewTemplate.html");
            Assert.That(template, Does.Contain("item.reviewGroupKey"));
            Assert.That(template, Does.Contain("reviewKind(item)==='interaction'"));
        }

        [Test]
        public void ArrangementProposalLivesOnlyInEditorSessionState()
        {
            SurfaceArrangementProposalStore.Clear();
            try
            {
                var stored = SurfaceArrangementProposalStore.Submit(new SurfaceArrangementProposal
                {
                    arrangementId = "archive-table",
                    targetElementId = "support-10-reading-table",
                    preset = "InUse",
                    membersJson = "[{\"descriptorId\":\"book_brown\",\"minimumCount\":2,\"maximumCount\":3}]",
                    rationale = "사용 중인 기록 보관소처럼 보이게"
                });

                var loaded = SurfaceArrangementProposalStore.Load();
                Assert.That(stored.suggestedUtc, Is.Not.Empty);
                Assert.That(loaded.arrangementId, Is.EqualTo("archive-table"));
                Assert.That(loaded.rationale, Is.EqualTo("사용 중인 기록 보관소처럼 보이게"));
                Assert.That(loaded.targetFrameId, Is.EqualTo("top"));
            }
            finally { SurfaceArrangementProposalStore.Clear(); }
        }

        [Test]
        public void ArrangementProposalRejectsUnsupportedOrUnsafeValues()
        {
            Assert.Throws<ArgumentException>(() => SurfaceArrangementProposalStore.Submit(new SurfaceArrangementProposal
            {
                arrangementId = "bad",
                targetElementId = "table",
                preset = "LearnedTheme",
                amount = 0.5f,
                orderliness = 0.5f,
                grouping = 0.5f,
                stacking = 0.5f
            }));
            Assert.Throws<ArgumentOutOfRangeException>(() => SurfaceArrangementProposalStore.Submit(new SurfaceArrangementProposal
            {
                arrangementId = "bad",
                targetElementId = "table",
                preset = "InUse",
                amount = 1.2f,
                orderliness = 0.5f,
                grouping = 0.5f,
                stacking = 0.5f
            }));
        }
    }
}
