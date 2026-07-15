using NUnit.Framework;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class ArrangementSupportSurfaceResolverTests
    {
        [Test]
        public void FlatFrameAndOppositeSurfaceUseCompoundObbThinnestAxis()
        {
            var geometry = new DecorGeometryProfile
            {
                source = GeometrySource.Collider,
                reviewed = true,
                collisionProxies =
                {
                    new OrientedBoxProxy("book", Vector3.zero,
                        new Vector3(0.2f, 0.5f, 0.355f), Quaternion.identity)
                }
            };
            geometry.Normalize();

            var flat = geometry.Frame("flat");
            Assert.That(flat, Is.Not.Null);
            Assert.That(Mathf.Abs(Vector3.Dot(flat.localNormal, Vector3.right)), Is.EqualTo(1f).Within(1e-4f));
            Assert.That(flat.size.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(flat.size.y, Is.EqualTo(0.355f).Within(1e-4f));
            Assert.That(ArrangementSupportSurfaceResolver.TryResolveOppositeFrame(
                    geometry, "flat", "top", out var opposite, out var code), Is.True, code);
            Assert.That(Vector3.Dot(opposite.Frame.localNormal, flat.localNormal),
                Is.EqualTo(-1f).Within(1e-4f));
            Assert.That(Vector3.Distance(opposite.Frame.localPoint, flat.localPoint),
                Is.EqualTo(0.2f).Within(1e-4f));
        }

        [Test]
        public void CompoundOppositeBackSurfaceMatchesCalibrationAndRuntimeWithinTolerance()
        {
            var subjectPrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var targetPrefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var subject = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            var target = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            try
            {
                subject.InitializeFromScan("stack-subject", subjectPrefab,
                    new Bounds(Vector3.zero, new Vector3(0.3f, 0.5f, 0.1f)), DecorAssetType.Prop);
                target.InitializeFromScan("compound-support", targetPrefab,
                    new Bounds(Vector3.zero, new Vector3(0.4f, 0.6f, 0.24f)), DecorAssetType.Prop);
                target.ConfigureGeometry(CompoundReviewedBook());
                var originalDescriptor = JsonUtility.ToJson(target.Geometry);

                var session = SpatialCalibrationSession.Begin(
                    subject, target, SpatialCalibrationTemplate.SupportedBy, "back", "top");
                try
                {
                    session.ConfigureTargetOppositeFrame("back", "top");
                    session.TargetObject.transform.SetPositionAndRotation(
                        new Vector3(0.25f, 0.4f, -0.15f),
                        ContactFrameSupportRotation(target.Geometry.backContact));

                    var calibration = session.TargetSurface("top");
                    Assert.That(ArrangementSupportSurfaceResolver.TryResolveOppositeBackSurface(
                            target.Geometry,
                            session.TargetObject.transform.position,
                            session.TargetObject.transform.rotation,
                            session.TargetObject.transform.lossyScale,
                            out var runtime,
                            out var code), Is.True, code);

                    AssertVector(runtime.Origin, calibration.Origin);
                    AssertVector(runtime.Normal, calibration.Normal);
                    AssertVector(runtime.Tangent, calibration.Tangent);
                    AssertVector(runtime.Bitangent, calibration.Bitangent);
                    AssertVector(runtime.Size, calibration.Size);
                    Assert.That(runtime.Size.x, Is.EqualTo(target.Geometry.backContact.size.x).Within(1e-4f));
                    Assert.That(runtime.Size.y, Is.EqualTo(target.Geometry.backContact.size.y).Within(1e-4f));
                    Assert.That(session.TargetFrame("top").localPoint.z, Is.EqualTo(0.12f).Within(1e-4f),
                        "The compound protrusion must move the opposite plane without replacing the reviewed footprint.");
                    Assert.That(JsonUtility.ToJson(target.Geometry), Is.EqualTo(originalDescriptor),
                        "Session-only derivation must not mutate the approved descriptor.");
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
        public void GeometryChangeInvalidatesResolutionAndApprovedSupportBinding()
        {
            const string subjectGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string targetGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var geometry = CompoundReviewedBook();
            Assert.That(ArrangementSupportSurfaceResolver.TryResolveOppositeBackFrame(
                    geometry, "back", "top", out var approved, out var code), Is.True, code);

            geometry.collisionProxies[1].localCenter += Vector3.forward * 0.03f;
            Assert.That(ArrangementSupportSurfaceResolver.TryResolveOppositeBackFrame(
                    geometry, "back", "top", out var changed, out code), Is.True, code);
            Assert.That(changed.GeometryHash, Is.Not.EqualTo(approved.GeometryHash));
            Assert.That(changed.ResolutionHash, Is.Not.EqualTo(approved.ResolutionHash));
            Assert.That(changed.ResolverVersion, Is.EqualTo(ArrangementSupportSurfaceResolver.ResolverVersion));
            Assert.That(changed.ResolverHash, Is.EqualTo(ArrangementSupportSurfaceResolver.ResolverHash));

            var catalog = new SupportContractCatalog();
            catalog.RegisterAsset(new SupportAssetIdentity(
                "subject", subjectGuid, "subject-geometry", "subject-geometry", "subject-family"));
            catalog.RegisterAsset(new SupportAssetIdentity(
                "target", targetGuid, approved.GeometryHash, changed.GeometryHash, "target-family"));
            var document = ApprovedInteraction(subjectGuid, targetGuid);
            Assert.That(catalog.RegisterApprovedInteraction(new SupportInteractionBinding(
                    document, "subject", "target", "subject-geometry", approved.GeometryHash), out var reason),
                Is.True, reason);
            Assert.That(catalog.TryResolve("subject", "target", out _, out code), Is.False);
            Assert.That(code, Is.EqualTo(SurfaceArrangementErrorCodes.SupportContractStale));

            geometry.backContact.localNormal = Vector3.zero;
            Assert.That(ArrangementSupportSurfaceResolver.TryResolveOppositeBackFrame(
                    geometry, "back", "top", out _, out code), Is.False);
            Assert.That(code, Is.EqualTo(SurfaceArrangementErrorCodes.SupportRegionInvalid));
        }

        private static DecorGeometryProfile CompoundReviewedBook()
        {
            var geometry = new DecorGeometryProfile
            {
                source = GeometrySource.Collider,
                reviewed = true,
                dependencyHash = "compound-book-geometry",
                collisionProxies =
                {
                    new OrientedBoxProxy("body", Vector3.zero,
                        new Vector3(0.4f, 0.6f, 0.1f), Quaternion.identity),
                    new OrientedBoxProxy("top-protrusion", new Vector3(0.15f, 0.1f, 0.1f),
                        new Vector3(0.04f, 0.04f, 0.04f), Quaternion.identity)
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

        private static SpatialContractDocument ApprovedInteraction(string subjectGuid, string targetGuid)
        {
            var document = new SpatialContractDocument
            {
                contract_version = 1,
                contract_type = "interaction",
                state = SpatialContractStates.Approved,
                interaction = new InteractionSpatialContractPayload
                {
                    subject_guid = subjectGuid,
                    target_key = "asset:" + targetGuid,
                    relation = "SupportedBy",
                    subject_frame = "back",
                    target_frame = "top",
                    relative_position = new[] { 0f, 0.12f, 0f },
                    relative_rotation = new[] { 0f, 0f, 0f, 1f },
                    position_tolerance = new[] { 0.2f, 0.01f, 0.2f },
                    angle_tolerance = 0f,
                    collision_policy = "contact-only",
                    revision = 1,
                    capture_set_hash = "capture"
                },
                technical = new SpatialTechnicalEvidence
                {
                    passed = true,
                    error_count = 0,
                    report_hash = "report"
                },
                review = new SpatialHumanReview
                {
                    decision = SpatialContractStates.Approved,
                    capture_set_hash = "capture",
                    reviewer = "local-user"
                }
            };
            document.interaction.interaction_hash = SpatialContractHashUtility.ComputeInteractionHash(document.interaction);
            document.review.contract_hash = SpatialContractHashUtility.ComputeContentHash(document);
            return document;
        }

        private static Quaternion ContactFrameSupportRotation(ContactFrame frame)
        {
            var normalAlignment = Quaternion.FromToRotation(frame.localNormal.normalized, Vector3.down);
            var tangentAfterNormal = Vector3.ProjectOnPlane(
                normalAlignment * frame.localTangent, Vector3.up).normalized;
            var tangentAlignment = tangentAfterNormal.sqrMagnitude > 0.000001f
                ? Quaternion.AngleAxis(
                    Vector3.SignedAngle(tangentAfterNormal, Vector3.right, Vector3.up), Vector3.up)
                : Quaternion.identity;
            return (tangentAlignment * normalAlignment).normalized;
        }

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(1e-4f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(1e-4f));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(1e-4f));
        }

        private static void AssertVector(Vector2 actual, Vector2 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(1e-4f));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(1e-4f));
        }
    }
}
