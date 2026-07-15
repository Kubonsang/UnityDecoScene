using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class SpatialSupportedRegionTests
    {
        [Test]
        public void ReviewedInsetTargetTopFrameDrivesAlignmentAndOutOfBoundsVerdict()
        {
            var subjectPrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var targetPrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var subject = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            var target = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            try
            {
                subject.InitializeFromScan("small-prop", subjectPrefab,
                    new Bounds(Vector3.zero, new Vector3(0.2f, 0.2f, 0.2f)), DecorAssetType.Prop);
                target.InitializeFromScan("inset-table", targetPrefab,
                    new Bounds(Vector3.zero, new Vector3(2f, 1f, 2f)), DecorAssetType.Prop);
                target.ConfigureGeometry(ReviewedTargetGeometry());

                var session = SpatialCalibrationSession.Begin(
                    subject, target, SpatialCalibrationTemplate.SupportedBy, "bottom", "top");
                try
                {
                    session.UpdateContactFrame(
                        "bottom",
                        new Vector3(0f, -0.1f, 0f),
                        Vector3.down,
                        Vector3.right,
                        new Vector2(0.2f, 0.2f));
                    session.AlignSupportedBySubjectFrameToTarget();

                    var expectedOrigin = session.TargetObject.transform.TransformPoint(
                        target.Geometry.topContact.localPoint);
                    var aabbTop = new Vector3(
                        session.TargetWorldBounds().center.x,
                        session.TargetWorldBounds().max.y,
                        session.TargetWorldBounds().center.z);
                    Assert.That(Vector3.Distance(session.TargetSurface().Origin, expectedOrigin), Is.LessThan(1e-5f));
                    Assert.That(Vector3.Distance(expectedOrigin, aabbTop), Is.GreaterThan(0.1f),
                        "The fixture must prove that reviewed geometry, not renderer AABB, owns the support region.");
                    Assert.That(session.TargetSurface().Size.x, Is.EqualTo(0.5f).Within(1e-5f));
                    Assert.That(session.TargetSurface().Size.y, Is.EqualTo(0.4f).Within(1e-5f));

                    var passed = SpatialCalibrationValidator.Validate(session);
                    Assert.That(passed.error_count, Is.Zero, string.Join("\n", passed.errors));

                    session.SubjectObject.transform.position += session.TargetSurface().Tangent * 0.4f;
                    var outside = SpatialCalibrationValidator.Validate(session);
                    Assert.That(outside.error_count, Is.GreaterThan(0));
                    Assert.That(outside.contacts.Single().support, Is.LessThan(0.6f));
                    Assert.That(outside.errors.Any(value => value.Contains("support")), Is.True);
                }
                finally
                {
                    session.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(subjectPrefab);
                Object.DestroyImmediate(targetPrefab);
            }
        }

        [Test]
        public void TopFrameEditorUsesGeometryTopContactAndResetRestoresAutomaticDraft()
        {
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            try
            {
                descriptor.InitializeFromScan("editable-top", prefab,
                    new Bounds(Vector3.zero, Vector3.one), DecorAssetType.Prop);
                var session = SpatialCalibrationSession.Begin(
                    descriptor, (GameObject)null, SpatialCalibrationTemplate.FloorSupported);
                try
                {
                    var automaticPoint = session.Geometry.topContact.localPoint;
                    session.UpdateContactFrame(
                        "top", new Vector3(0.2f, 0.35f, -0.1f), Vector3.up, Vector3.forward,
                        new Vector2(0.45f, 0.3f));

                    Assert.That(session.Frame("top"), Is.SameAs(session.Geometry.topContact));
                    Assert.That(session.Frame("top").localPoint, Is.EqualTo(new Vector3(0.2f, 0.35f, -0.1f)));
                    Assert.That(session.Frame("top").size, Is.EqualTo(new Vector2(0.45f, 0.3f)));

                    session.ResetContactFrame("top");
                    Assert.That(session.Frame("top").localPoint, Is.EqualTo(automaticPoint));
                }
                finally
                {
                    session.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(descriptor);
                Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void OppositeBackFrameBecomesWorldUpStackSupportWithoutMutatingDescriptor()
        {
            var subjectPrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var targetPrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var subject = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            var target = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            try
            {
                subject.InitializeFromScan("stacked-book", subjectPrefab,
                    new Bounds(Vector3.zero, new Vector3(0.3f, 0.5f, 0.1f)), DecorAssetType.Prop);
                target.InitializeFromScan("support-book", targetPrefab,
                    new Bounds(Vector3.zero, new Vector3(0.4f, 0.6f, 0.1f)), DecorAssetType.Prop);
                target.ConfigureGeometry(ReviewedBookGeometry());
                var approvedTopNormal = target.Geometry.topContact.localNormal;
                var approvedTopPoint = target.Geometry.topContact.localPoint;

                var session = SpatialCalibrationSession.Begin(
                    subject, target, SpatialCalibrationTemplate.SupportedBy, "back", "top");
                try
                {
                    session.UpdateContactFrame(
                        "back",
                        new Vector3(0f, 0f, -0.05f),
                        Vector3.back,
                        Vector3.right,
                        new Vector2(0.3f, 0.5f));
                    session.ConfigureTargetOppositeFrame("back", "top");
                    session.TargetObject.transform.SetPositionAndRotation(
                        Vector3.zero,
                        ContactFrameSupportRotation(session.TargetFrame("back")));
                    session.AlignSupportedBySubjectFrameToTarget();

                    Assert.That(Vector3.Dot(session.TargetSurface("top").Normal, Vector3.up),
                        Is.GreaterThan(0.999f));
                    Assert.That(session.TargetFrame("top").localPoint.z, Is.EqualTo(0.05f).Within(1e-5f));
                    var report = SpatialCalibrationValidator.Validate(session);
                    Assert.That(report.error_count, Is.Zero, string.Join("\n", report.errors));

                    Assert.That(target.Geometry.topContact.localNormal, Is.EqualTo(approvedTopNormal));
                    Assert.That(target.Geometry.topContact.localPoint, Is.EqualTo(approvedTopPoint));
                }
                finally
                {
                    session.Dispose();
                }
            }
            finally
            {
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(subjectPrefab);
                Object.DestroyImmediate(targetPrefab);
            }
        }

        private static DecorGeometryProfile ReviewedTargetGeometry()
        {
            var geometry = new DecorGeometryProfile
            {
                source = GeometrySource.Collider,
                reviewed = true,
                collisionProxies =
                {
                    new OrientedBoxProxy("table", Vector3.zero, new Vector3(2f, 1f, 2f), Quaternion.identity)
                },
                topContact = new ContactFrame
                {
                    frameId = "top",
                    localPoint = new Vector3(0.35f, 0.35f, -0.2f),
                    localNormal = Vector3.up,
                    localTangent = Vector3.right,
                    size = new Vector2(0.5f, 0.4f)
                }
            };
            geometry.Normalize();
            return geometry;
        }

        private static DecorGeometryProfile ReviewedBookGeometry()
        {
            var geometry = new DecorGeometryProfile
            {
                source = GeometrySource.Collider,
                reviewed = true,
                collisionProxies =
                {
                    new OrientedBoxProxy("book", Vector3.zero, new Vector3(0.4f, 0.6f, 0.1f), Quaternion.identity)
                },
                backContact = new ContactFrame
                {
                    frameId = "back",
                    localPoint = new Vector3(0f, 0f, -0.05f),
                    localNormal = Vector3.back,
                    localTangent = Vector3.right,
                    size = new Vector2(0.36f, 0.5f)
                },
                topContact = new ContactFrame
                {
                    frameId = "top",
                    localPoint = new Vector3(0f, 0.3f, 0f),
                    localNormal = Vector3.up,
                    localTangent = Vector3.right,
                    size = new Vector2(0.36f, 0.08f)
                }
            };
            geometry.Normalize();
            return geometry;
        }

        private static Quaternion ContactFrameSupportRotation(ContactFrame frame)
        {
            var normalAlignment = Quaternion.FromToRotation(frame.localNormal.normalized, Vector3.down);
            var tangentAfterNormal = Vector3.ProjectOnPlane(
                normalAlignment * frame.localTangent,
                Vector3.up).normalized;
            var tangentAlignment = tangentAfterNormal.sqrMagnitude > 0.000001f
                ? Quaternion.AngleAxis(
                    Vector3.SignedAngle(tangentAfterNormal, Vector3.right, Vector3.up),
                    Vector3.up)
                : Quaternion.identity;
            return (tangentAlignment * normalAlignment).normalized;
        }
    }
}
