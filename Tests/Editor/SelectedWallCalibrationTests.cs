using NUnit.Framework;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using UnityDecoScene.DungeonDecorator.Editor;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class SelectedWallCalibrationTests
    {
        [Test]
        public void BeginnerWindowLoadsStepByStepUxmlWorkflow()
        {
            var window = ScriptableObject.CreateInstance<SpatialCalibrationWindow>();
            try
            {
                window.CreateGUI();
                Assert.That(window.rootVisualElement.Q<ObjectField>("subject-descriptor-field"), Is.Not.Null);
                Assert.That(window.rootVisualElement.Q<DropdownField>("relationship-field"), Is.Not.Null);
                Assert.That(window.rootVisualElement.Q<Button>("start-button").text, Does.Contain("캘리브레이션"));
                Assert.That(window.rootVisualElement.Q<Button>("start-button").enabledSelf, Is.False);
                Assert.That(window.rootVisualElement.Q<Button>("capture-button").enabledSelf, Is.False);
                Assert.That(window.rootVisualElement.Q<Foldout>("geometry-foldout").value, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void AnalyzeUsesSelectedLocalFaceAndKeepsWallUpWhenFlipped()
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.transform.SetPositionAndRotation(
                    new Vector3(3f, 2f, -4f),
                    Quaternion.Euler(0f, 37f, 0f));
                wall.transform.localScale = new Vector3(4f, 3f, 0.2f);

                var front = SpatialWallSurfaceUtility.Analyze(
                    wall, SpatialWallNormalAxis.LocalForward, false);
                var back = SpatialWallSurfaceUtility.Analyze(
                    wall, SpatialWallNormalAxis.LocalForward, true);

                Assert.That(Vector3.Dot(front.Normal, wall.transform.forward), Is.GreaterThan(0.9999f));
                Assert.That(Vector3.Dot(back.Normal, -wall.transform.forward), Is.GreaterThan(0.9999f));
                Assert.That(Vector3.Dot(front.Bitangent, wall.transform.up), Is.GreaterThan(0.9999f));
                Assert.That(Vector3.Dot(back.Bitangent, wall.transform.up), Is.GreaterThan(0.9999f));
                Assert.That(Vector3.Distance(front.Origin, wall.transform.position + wall.transform.forward * 0.1f), Is.LessThan(0.0001f));
                Assert.That(Vector3.Distance(back.Origin, wall.transform.position - wall.transform.forward * 0.1f), Is.LessThan(0.0001f));
                Assert.That(front.Size.x, Is.EqualTo(4f).Within(0.0001f));
                Assert.That(front.Size.y, Is.EqualTo(3f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(wall);
            }
        }

        [Test]
        public void BatchChoosesBroadWallFaceInsteadOfThinEdge()
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.transform.localScale = new Vector3(5f, 3f, 0.2f);
                var choice = SpatialDungeonCalibrationBatch.ChooseWallFace(wall);
                Assert.That(choice.Axis, Is.EqualTo(SpatialWallNormalAxis.LocalForward));
                Assert.That(choice.Surface.Size.x * choice.Surface.Size.y, Is.EqualTo(15f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(wall);
            }
        }

        [Test]
        public void WallMountedSessionClonesSelectedWallAndLeavesSourceUnchanged()
        {
            var subject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            wall.name = "User Selected Stone Wall";
            wall.transform.SetPositionAndRotation(
                new Vector3(4f, 2.5f, -3f),
                Quaternion.Euler(0f, 25f, 0f));
            wall.transform.localScale = new Vector3(6f, 4f, 0.25f);
            var originalPosition = wall.transform.position;
            var originalRotation = wall.transform.rotation;
            var originalScale = wall.transform.localScale;

            try
            {
                descriptor.InitializeFromScan(
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    subject,
                    new Bounds(Vector3.zero, Vector3.one),
                    DecorAssetType.Prop);

                var session = SpatialCalibrationSession.Begin(
                    descriptor,
                    null,
                    SpatialCalibrationTemplate.WallMounted,
                    wall,
                    SpatialWallNormalAxis.LocalForward,
                    false);
                try
                {
                    var report = SpatialCalibrationValidator.Validate(session);
                    Assert.That(session.SourceWallObject, Is.SameAs(wall));
                    Assert.That(session.WallFixture, Is.Not.SameAs(wall));
                    Assert.That(session.FloorFixture, Is.Null);
                    Assert.That(session.WallFixture.name, Does.Contain(wall.name));
                    Assert.That(session.WallSurface.Normal, Is.EqualTo(Vector3.forward));
                    Assert.That(session.WallSurface.Size.x, Is.EqualTo(6f).Within(0.0001f));
                    Assert.That(session.WallSurface.Size.y, Is.EqualTo(4f).Within(0.0001f));
                    Assert.That(report.error_count, Is.Zero);
                    Assert.That(report.contacts[0].gap, Is.EqualTo(0.0075f).Within(0.0001f));
                }
                finally
                {
                    session.Dispose();
                }

                Assert.That(wall.transform.position, Is.EqualTo(originalPosition));
                Assert.That(wall.transform.rotation, Is.EqualTo(originalRotation));
                Assert.That(wall.transform.localScale, Is.EqualTo(originalScale));
            }
            finally
            {
                Object.DestroyImmediate(descriptor);
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(wall);
            }
        }

        [Test]
        public void CapturePreflightRejectsObbThatCrossesWallBehindValidContactFrame()
        {
            var subject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            try
            {
                wall.transform.localScale = new Vector3(5f, 4f, 0.2f);
                descriptor.InitializeFromScan(
                    "cccccccccccccccccccccccccccccccc",
                    subject,
                    new Bounds(Vector3.zero, Vector3.one),
                    DecorAssetType.Prop);
                var session = SpatialCalibrationSession.Begin(
                    descriptor, null, SpatialCalibrationTemplate.WallMounted,
                    wall, SpatialWallNormalAxis.LocalForward, false);
                try
                {
                    var report = SpatialCalibrationValidator.Validate(session);
                    Assert.That(report.Passed, Is.True);
                    session.Geometry.collisionProxies[0].size.z += 0.1f;
                    var issues = SpatialCalibrationCapturePreflight.Inspect(session, report);
                    Assert.That(issues, Has.Some.StartsWith("CAPTURE_PROXY_SURFACE_INTERSECTION"));
                }
                finally
                {
                    session.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(descriptor);
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(wall);
            }
        }
    }
}
