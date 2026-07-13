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
        public void ReviewTemplateReceivesEmbeddedWorkflowWithoutRuntimePrompting()
        {
            var state = new SpatialCalibrationWorkflowState { workflowId = "abc", createdUtc = "now" };
            var html = SpatialCalibrationWorkflow.RenderReviewHtml(state, "<script>const state=__WORKFLOW_JSON__;</script><i>__GENERATED_UTC__</i>");
            Assert.That(html, Does.Contain("\"workflowId\":\"abc\""));
            Assert.That(html, Does.Not.Contain("__WORKFLOW_JSON__"));
            Assert.That(html, Does.Not.Contain("__GENERATED_UTC__"));
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
            Assert.That(template, Does.Contain("승인 예정"));
            Assert.That(template, Does.Contain("수정 필요"));
            Assert.That(template, Does.Contain("미판정"));
            Assert.That(template, Does.Contain("decisions.get(value.id)!=='Approved'"));
            Assert.That(template, Does.Contain("awaitingGroups.flatMap(group=>group.items.map"));
            Assert.That(template, Does.Contain("동일 공간 모델"));
            Assert.That(template, Does.Contain("/api/spatial/workflow/review-batch"));
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
    }
}
