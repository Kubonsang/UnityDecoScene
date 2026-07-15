using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor.Tests
{
    public sealed class SpatialContractTests
    {
        [Test]
        public void ContractDocumentRoundTripsHumanGateFields()
        {
            var document = new SpatialContractDocument
            {
                contract_type = "asset",
                state = SpatialContractStates.AwaitingHumanReview,
                asset = new AssetSpatialContractPayload
                {
                    asset_guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    asset_path = "Assets/banner.prefab",
                    dependency_hash = "hash",
                    capture_set_hash = "capture"
                },
                technical = new SpatialTechnicalEvidence { passed = true, error_count = 0, report_hash = "report" }
            };
            var json = JsonUtility.ToJson(document);
            var loaded = JsonUtility.FromJson<SpatialContractDocument>(json);
            Assert.That(loaded.state, Is.EqualTo(SpatialContractStates.AwaitingHumanReview));
            Assert.That(loaded.technical.passed, Is.True);
            Assert.That(loaded.IsApproved, Is.False);
        }

        [Test]
        public void AssetDraftSerializationOmitsInactiveInteractionAndEmptyReview()
        {
            var document = new SpatialContractDocument
            {
                contract_type = "asset",
                state = SpatialContractStates.AwaitingHumanReview,
                asset = new AssetSpatialContractPayload { asset_guid = "0123456789abcdef0123456789abcdef" },
                technical = new SpatialTechnicalEvidence { passed = true }
            };
            var json = SpatialContractIO.SerializeForStorage(document, false);
            Assert.That(json, Does.Contain("\"asset\":"));
            Assert.That(json, Does.Not.Contain("\"interaction\":"));
            Assert.That(json, Does.Not.Contain("\"review\":"));
        }

        [Test]
        public void FloorCalibrationProducesDeterministicTechnicalEvidence()
        {
            var prefab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            try
            {
                descriptor.InitializeFromScan("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", prefab, new Bounds(Vector3.zero, Vector3.one), DecorAssetType.Prop);
                var session = SpatialCalibrationSession.Begin(descriptor, null, SpatialCalibrationTemplate.FloorSupported);
                try
                {
                    Assert.That(session.FloorFixture, Is.Not.Null);
                    Assert.That(session.WallFixture, Is.Null);
                    var first = SpatialCalibrationValidator.Validate(session);
                    var second = SpatialCalibrationValidator.Validate(session);
                    Assert.That(first.error_count, Is.Zero);
                    Assert.That(SpatialCalibrationCapturePreflight.Inspect(session, first), Is.Empty);
                    Assert.That(first.status, Is.EqualTo(SpatialContractStates.AwaitingHumanReview));
                    Assert.That(second.report_hash, Is.EqualTo(first.report_hash));
                }
                finally { session.Dispose(); }
            }
            finally
            {
                Object.DestroyImmediate(descriptor);
                Object.DestroyImmediate(prefab);
            }
        }

        [Test]
        public void ApprovedContractHashMatchesUnityCtxKnownVector()
        {
            var document = KnownApprovedContract();
            Assert.That(SpatialContractHashUtility.ComputeGeometryHash(document.asset), Is.EqualTo(
                "3100c2d781773f3002be4704bdcba83b9b8d810d937dce1c933db6c0001c150b"));
            Assert.That(SpatialContractHashUtility.ComputeContentHash(document), Is.EqualTo(
                "b4e4805e72353ebcef824c4cdb509c78e7b8ea509e6bd593f7ea0c6f2cb61a5a"));
            Assert.That(SpatialContractHashUtility.ValidateApproved(document, out var reason), Is.True, reason);
        }

        [Test]
        public void NegativeZeroHashMatchesUnityCtxApprovedTableVector()
        {
            var document = KnownApprovedNegativeZeroTableContract();
            Assert.That(System.BitConverter.ToInt32(
                System.BitConverter.GetBytes(document.asset.pivot_offset[0]), 0), Is.LessThan(0),
                "The fixture must retain IEEE-754 negative zero instead of testing ordinary zero.");
            Assert.That(SpatialContractHashUtility.ComputeGeometryHash(document.asset), Is.EqualTo(
                "03ef438f92d4be2408a85f88cecd0f57f4d67ee826dce2b711740bfe19ade10b"));
            Assert.That(SpatialContractHashUtility.ComputeContentHash(document), Is.EqualTo(
                "595585303e6629aa7f9d6755e91d5e2c2aae59f5ac51e6798caaf4b9a4fa447e"));
            Assert.That(SpatialContractHashUtility.ValidateApproved(document, out var reason), Is.True, reason);
        }

        [Test]
        public void Float32BoundaryAndLiteralAmpersandHashesMatchUnityCtxVector()
        {
            var document = KnownApprovedContract();
            document.asset.asset_path = "Assets/Props/A&B.prefab";
            document.asset.pivot_offset[0] = 0.5500005f;
            document.asset.geometry_hash = SpatialContractHashUtility.ComputeGeometryHash(document.asset);

            Assert.That(document.asset.pivot_offset[0], Is.EqualTo(0.5500005f));
            Assert.That(document.asset.geometry_hash, Is.EqualTo(
                "e936d7f62d75993c19bd44ba4bba22dfc9f862c99c85301ccf5e84b51863ad60"));
            Assert.That(SpatialContractHashUtility.ComputeContentHash(document), Is.EqualTo(
                "8118e7ac51b8c8167e3fb1c9102a35f1c8bdf2fb0d2fab534ac15b439caaeeca"));
        }

        [Test]
        public void Utf16OrderingAndLineSeparatorHashMatchesUnityCtxVector()
        {
            var document = KnownApprovedContract();
            document.asset.dependency_hash = "quoted:\"\u2028:literal:\\u2028:\u2029";
            var bmp = document.asset.collision_proxies[0];
            bmp.id = "\ue000";
            var supplementary = new SpatialObbContract
            {
                id = "\U00010000",
                center = new[] { 0.25f, 0.5f, 0f },
                size = new[] { 1f, 1f, 1f },
                rotation = new[] { 0f, 0f, 0f, 1f }
            };
            document.asset.collision_proxies = new List<SpatialObbContract> { bmp, supplementary };

            Assert.That(SpatialContractHashUtility.ComputeGeometryHash(document.asset), Is.EqualTo(
                "1dd4d0e938b2a5bcb8e41ad182ade23b0d2eb8b2dbed3b6118beb4a64df3fae4"));
        }

        [Test]
        public void LoadPreservesNegativeZeroTokenAndIgnoresStringContents()
        {
            var path = Path.Combine(Path.GetTempPath(), $"spatial-negative-zero-{System.Guid.NewGuid():N}.json");
            try
            {
                Assert.That(SpatialContractIO.PreserveNegativeZeroNumbers("{\"label\":\"keep -0 here\",\"value\":-0}"),
                    Is.EqualTo("{\"label\":\"keep -0 here\",\"value\":-1e-30}"));
                Assert.That(SpatialContractIO.PreserveNegativeZeroNumbers("{\"value\":-0.25}"),
                    Is.EqualTo("{\"value\":-0.25}"));
                const string raw =
                    "{\"contract_version\":1,\"contract_type\":\"asset\",\"state\":\"Draft\"," +
                    "\"asset\":{\"asset_guid\":\"negative-zero-load-fixture\",\"pivot_offset\":[-0,0,0]}}";
                File.WriteAllText(path, raw);

                var loaded = SpatialContractIO.Load(path);

                Assert.That(System.BitConverter.ToInt32(
                    System.BitConverter.GetBytes(loaded.asset.pivot_offset[0]), 0), Is.LessThan(0));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void ApprovedContractRejectsGeometryChangedAfterReview()
        {
            var document = KnownApprovedContract();
            document.asset.collision_proxies[0].size[0] = 2f;
            Assert.That(SpatialContractHashUtility.ValidateApproved(document, out var reason), Is.False);
            Assert.That(reason, Does.StartWith("CONTRACT_HASH_MISMATCH"));
        }

        [Test]
        public void ApprovedContractBatchSyncPreviewsAppliesAndBecomesIdempotent()
        {
            const string root = "Assets/__ApprovedContractSyncTests";
            AssetDatabase.DeleteAsset(root);
            Directory.CreateDirectory(Path.GetFullPath(root + "/Contracts"));
            AssetDatabase.Refresh();
            SpatialContractAuthorityVerifier.TestOverride = (_, _) => null;
            try
            {
                var source = new GameObject("Contract Sync Prefab");
                var prefabPath = root + "/sync.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
                Object.DestroyImmediate(source);
                var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
                descriptor.InitializeFromScan("sync", prefab, new Bounds(new Vector3(0f, 0.5f, 0f), Vector3.one), DecorAssetType.Prop);
                AssetDatabase.CreateAsset(descriptor, root + "/sync.asset");
                var catalog = ScriptableObject.CreateInstance<DecorCatalog>();
                catalog.ReplaceAll(new[] { descriptor });
                AssetDatabase.CreateAsset(catalog, root + "/catalog.asset");
                AssetDatabase.SaveAssets();

                var document = KnownApprovedContract();
                document.asset.asset_guid = AssetDatabase.AssetPathToGUID(prefabPath);
                document.asset.asset_path = prefabPath;
                document.asset.dependency_hash = AssetDatabase.GetAssetDependencyHash(prefabPath).ToString();
                document.asset.geometry_hash = SpatialContractHashUtility.ComputeGeometryHash(document.asset);
                document.review.contract_hash = SpatialContractHashUtility.ComputeContentHash(document);
                var contractPath = root + "/Contracts/" + document.asset.asset_guid + ".spatial.json";
                File.WriteAllText(Path.GetFullPath(contractPath), SpatialContractIO.SerializeForStorage(document));
                AssetDatabase.ImportAsset(contractPath, ImportAssetOptions.ForceSynchronousImport);

                var preview = ApprovedContractSyncService.Preview(catalog, root + "/Contracts");
                var previewItem = preview.items.Single();
                Assert.That(previewItem.status, Is.EqualTo(ApprovedContractSyncStatus.Ready), previewItem.message);
                Assert.That(ApprovedContractSyncService.Apply(preview), Is.EqualTo(1));
                Assert.That(descriptor.Geometry.reviewed, Is.True);
                Assert.That(descriptor.Geometry.dependencyHash, Is.EqualTo(document.asset.dependency_hash));

                var repeated = ApprovedContractSyncService.Preview(catalog, root + "/Contracts");
                Assert.That(repeated.items.Single().status, Is.EqualTo(ApprovedContractSyncStatus.Unchanged));
            }
            finally
            {
                SpatialContractAuthorityVerifier.TestOverride = null;
                AssetDatabase.DeleteAsset(root);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void ApprovedAssetImportRejectsPrefabDependencyChangedDuringAuthorityVerification()
        {
            const string root = "Assets/__ApprovedContractDependencyRaceTests";
            AssetDatabase.DeleteAsset(root);
            Directory.CreateDirectory(Path.GetFullPath(root));
            AssetDatabase.Refresh();
            DecorAssetDescriptor descriptor = null;
            try
            {
                var source = new GameObject("Dependency Race Prefab");
                var prefabPath = root + "/race.prefab";
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, prefabPath);
                Object.DestroyImmediate(source);
                descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
                descriptor.InitializeFromScan("dependency-race", prefab, new Bounds(Vector3.zero, Vector3.one), DecorAssetType.Prop);

                var document = KnownApprovedContract();
                document.asset.asset_guid = AssetDatabase.AssetPathToGUID(prefabPath);
                document.asset.asset_path = prefabPath;
                document.asset.dependency_hash = AssetDatabase.GetAssetDependencyHash(prefabPath).ToString();
                document.asset.geometry_hash = SpatialContractHashUtility.ComputeGeometryHash(document.asset);
                document.review.contract_hash = SpatialContractHashUtility.ComputeContentHash(document);
                var contractPath = Path.GetFullPath(root + "/race.spatial.json");
                File.WriteAllText(contractPath, SpatialContractIO.SerializeForStorage(document));

                SpatialContractAuthorityVerifier.TestOverride = (_, _) =>
                {
                    var contents = PrefabUtility.LoadPrefabContents(prefabPath);
                    contents.AddComponent<BoxCollider>();
                    PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
                    PrefabUtility.UnloadPrefabContents(contents);
                    AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
                    return null;
                };

                Assert.That(SpatialContractIO.TryCreateApprovedGeometry(
                    contractPath, descriptor, out _, out var reason), Is.False);
                Assert.That(reason, Does.StartWith("CONTRACT_AUTHORITY_CHANGED prefab identity or dependency changed"));
            }
            finally
            {
                SpatialContractAuthorityVerifier.TestOverride = null;
                if (descriptor != null) Object.DestroyImmediate(descriptor);
                AssetDatabase.DeleteAsset(root);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void InteractionCatalogRejectsSelfAssertedApprovedBinding()
        {
            const string subjectGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string targetGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var document = KnownApprovedInteractionContract(subjectGuid, targetGuid);
            var catalog = new SupportContractCatalog();
            catalog.RegisterAsset(new SupportAssetIdentity("book", subjectGuid, "book-geometry", "book-geometry"));
            catalog.RegisterAsset(new SupportAssetIdentity("table", targetGuid, "table-geometry", "table-geometry"));

            var binding = new SupportInteractionBinding(
                document, "book", "table", "book-geometry", "table-geometry");

            Assert.That(catalog.RegisterApprovedInteraction(binding, out var reason), Is.False);
            Assert.That(reason, Does.StartWith("CONTRACT_AUTHORITY_REQUIRED"));
        }

        [Test]
        public void ApprovedInteractionLoaderCarriesAuthorityAndCatalogClonesPose()
        {
            const string subjectGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string targetGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var subjectGeometryHash = new string('c', 64);
            var targetGeometryHash = new string('d', 64);
            var document = KnownApprovedInteractionContract(subjectGuid, targetGuid);
            var path = Path.Combine(Path.GetTempPath(), $"approved-interaction-{System.Guid.NewGuid():N}.interaction.json");
            File.WriteAllText(path, SpatialContractIO.SerializeForStorage(document));
            SpatialContractAuthorityVerifier.TestEvidenceOverride = (actualPath, expectedHash) =>
            {
                Assert.That(actualPath, Is.EqualTo(Path.GetFullPath(path)));
                Assert.That(expectedHash, Is.EqualTo(SpatialContractHashUtility.ComputeContentHash(document)));
                return new SpatialContractAuthorityEvidence
                {
                    authorized = true,
                    contract_hash = expectedHash,
                    contract_type = "interaction",
                    subject_guid = subjectGuid,
                    target_key = "asset:" + targetGuid,
                    subject_geometry_hash = subjectGeometryHash,
                    target_geometry_hash = targetGeometryHash
                };
            };
            try
            {
                Assert.That(SpatialContractIO.TryLoadApprovedInteractionBinding(
                    path, "book", "table",
                    out var binding, out var loadReason), Is.True, loadReason);
                Assert.That(binding.AuthorityVerified, Is.True);
                Assert.That(binding.SubjectGeometryHash, Is.EqualTo(subjectGeometryHash));
                Assert.That(binding.TargetGeometryHash, Is.EqualTo(targetGeometryHash));

                var catalog = new SupportContractCatalog();
                catalog.RegisterAsset(new SupportAssetIdentity("book", subjectGuid, subjectGeometryHash, subjectGeometryHash));
                catalog.RegisterAsset(new SupportAssetIdentity("table", targetGuid, targetGeometryHash, targetGeometryHash));
                Assert.That(catalog.RegisterApprovedInteraction(binding, out var registerReason), Is.True, registerReason);
                Assert.That(catalog.TryResolve("book", "table", out var before, out var code), Is.True, code);
                var approvedX = before.Interaction.relative_position[0];

                binding.Document.review.comment = "tampered after verification";
                Assert.That(catalog.RegisterApprovedInteraction(binding, out registerReason), Is.False);
                Assert.That(registerReason, Does.StartWith("CONTRACT_AUTHORITY_CHANGED"));
                binding.Document.interaction.relative_position[0] = approvedX + 2f;

                Assert.That(catalog.TryResolve("book", "table", out var after, out code), Is.True, code);
                Assert.That(after.Interaction.relative_position[0], Is.EqualTo(approvedX),
                    "Catalog registration must snapshot the authority-verified pose instead of retaining a mutable document reference.");
            }
            finally
            {
                SpatialContractAuthorityVerifier.TestEvidenceOverride = null;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void ApprovedInteractionLoaderRejectsAuthorityVerificationRace()
        {
            const string subjectGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string targetGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var document = KnownApprovedInteractionContract(subjectGuid, targetGuid);
            var path = Path.Combine(Path.GetTempPath(), $"stale-interaction-{System.Guid.NewGuid():N}.interaction.json");
            File.WriteAllText(path, SpatialContractIO.SerializeForStorage(document));
            SpatialContractAuthorityVerifier.TestEvidenceOverride = (_, expectedHash) =>
            {
                var replacement = KnownApprovedInteractionContract(subjectGuid, targetGuid);
                replacement.review.reviewer = "other-user";
                replacement.review.contract_hash = SpatialContractHashUtility.ComputeContentHash(replacement);
                File.WriteAllText(path, SpatialContractIO.SerializeForStorage(replacement));
                return new SpatialContractAuthorityEvidence
                {
                    authorized = true,
                    contract_hash = expectedHash,
                    contract_type = "interaction",
                    subject_guid = subjectGuid,
                    target_key = "asset:" + targetGuid,
                    subject_geometry_hash = new string('c', 64),
                    target_geometry_hash = new string('d', 64)
                };
            };
            try
            {
                Assert.That(SpatialContractIO.TryLoadApprovedInteractionBinding(
                    path, "book", "table",
                    out _, out var reason), Is.False);
                Assert.That(reason, Does.StartWith("CONTRACT_AUTHORITY_CHANGED"));
            }
            finally
            {
                SpatialContractAuthorityVerifier.TestEvidenceOverride = null;
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void SupportedByFlatFrameUsesThinnestObbFaceAndPersistsPoseIntent()
        {
            var subject = new GameObject("Flat Book Root");
            var subjectMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
            subjectMesh.transform.SetParent(subject.transform, false);
            subjectMesh.transform.localScale = new Vector3(0.2f, 0.5f, 0.355f);
            var target = new GameObject("Table Root");
            var targetMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
            targetMesh.transform.SetParent(target.transform, false);
            targetMesh.transform.localScale = new Vector3(2f, 0.5f, 1.5f);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            IReadOnlyList<string> paths = null;
            SpatialCaptureSet captures = null;
            var subjectPrefabPath = $"Assets/__spatial-subject-{System.Guid.NewGuid():N}.prefab";
            var targetPrefabPath = $"Assets/__spatial-target-{System.Guid.NewGuid():N}.prefab";
            try
            {
                var subjectPrefab = PrefabUtility.SaveAsPrefabAsset(subject, subjectPrefabPath);
                var targetPrefab = PrefabUtility.SaveAsPrefabAsset(target, targetPrefabPath);
                descriptor.InitializeFromScan("flat-book", subjectPrefab,
                    new Bounds(Vector3.zero, new Vector3(0.2f, 0.5f, 0.355f)), DecorAssetType.Prop);
                var session = SpatialCalibrationSession.Begin(
                    descriptor, targetPrefab, SpatialCalibrationTemplate.SupportedBy, "flat", "top");
                try
                {
                    Assert.That(session.Rules.Single().frame_id, Is.EqualTo("flat"));
                    Assert.That(Vector3.Dot(
                        session.SubjectObject.transform.TransformDirection(session.Frame("flat").localNormal).normalized,
                        Vector3.down), Is.GreaterThan(0.999f));
                    Assert.That(session.SubjectObject.GetComponentInChildren<Renderer>().bounds.size.y,
                        Is.EqualTo(0.2f).Within(1e-4f),
                        "Flat intent must put the reviewed compound OBB's thinnest axis in world Y.");
                    var report = SpatialCalibrationValidator.Validate(session);
                    Assert.That(report.error_count, Is.Zero, string.Join("\n", report.errors));
                    captures = CreateCaptureEvidence(session.SessionId, report);
                    paths = SpatialContractIO.WriteDrafts(session, report, captures);
                    var interaction = SpatialContractIO.Load(paths.Single(path => path.EndsWith(".interaction.json")));
                    Assert.That(interaction.interaction.subject_frame, Is.EqualTo("flat"));
                    Assert.That(interaction.interaction.target_frame, Is.EqualTo("top"));

                    session.ConfigureSupportedByFrames("bottom", "top");
                    Assert.That(session.Rules.Single().frame_id, Is.EqualTo("bottom"));
                    Assert.That(session.LastReport, Is.Null);
                }
                finally { session.Dispose(); }
            }
            finally
            {
                foreach (var path in paths ?? System.Array.Empty<string>()) File.Delete(path);
                DeleteCaptureEvidence(captures);
                AssetDatabase.DeleteAsset(subjectPrefabPath);
                AssetDatabase.DeleteAsset(targetPrefabPath);
                Object.DestroyImmediate(descriptor);
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void InteractionOnlyWriterCreatesNoAssetGeometryDraft()
        {
            var subject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
            string path = null;
            string supersededAssetPath = null;
            SpatialCaptureSet captures = null;
            var subjectPrefabPath = $"Assets/__spatial-subject-{System.Guid.NewGuid():N}.prefab";
            var targetPrefabPath = $"Assets/__spatial-target-{System.Guid.NewGuid():N}.prefab";
            try
            {
                var subjectPrefab = PrefabUtility.SaveAsPrefabAsset(subject, subjectPrefabPath);
                var targetPrefab = PrefabUtility.SaveAsPrefabAsset(target, targetPrefabPath);
                descriptor.InitializeFromScan("interaction-only", subjectPrefab,
                    new Bounds(Vector3.zero, Vector3.one), DecorAssetType.Prop);
                var session = SpatialCalibrationSession.Begin(
                    descriptor, targetPrefab, SpatialCalibrationTemplate.SupportedBy, "bottom", "top");
                try
                {
                    var report = SpatialCalibrationValidator.Validate(session);
                    Assert.That(report.error_count, Is.Zero, string.Join("\n", report.errors));
                    captures = CreateCaptureEvidence(session.SessionId, report);
                    var previousDrafts = SpatialContractIO.WriteDrafts(session, report, captures);
                    supersededAssetPath = previousDrafts.Single(value => value.EndsWith(".spatial.json", System.StringComparison.OrdinalIgnoreCase));
                    Assert.That(File.Exists(supersededAssetPath), Is.True, "The reuse case must begin with an older asset draft.");

                    path = SpatialContractIO.WriteInteractionDraft(session, report, captures);

                    Assert.That(session.DraftPaths, Is.EqualTo(new[] { path }));
                    Assert.That(path, Does.EndWith(".interaction.json"));
                    Assert.That(File.Exists(supersededAssetPath), Is.False, "Interaction-only rewrite must retire the same-session asset draft.");
                    Assert.That(SpatialContractIO.Load(path).contract_type, Is.EqualTo("interaction"));
                }
                finally { session.Dispose(); }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(path)) File.Delete(path);
                if (!string.IsNullOrWhiteSpace(supersededAssetPath)) File.Delete(supersededAssetPath);
                DeleteCaptureEvidence(captures);
                AssetDatabase.DeleteAsset(subjectPrefabPath);
                AssetDatabase.DeleteAsset(targetPrefabPath);
                Object.DestroyImmediate(descriptor);
                Object.DestroyImmediate(subject);
                Object.DestroyImmediate(target);
            }
        }

        private static SpatialCaptureSet CreateCaptureEvidence(string sessionId, SpatialCalibrationReport report)
        {
            var directory = Path.Combine(Path.GetTempPath(), "spatial-contract-capture-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var rawNames = new[] { "front.png", "side.png", "top.png", "contact.png" };
            var evidenceNames = new[] { "front-evidence.png", "side-evidence.png", "top-evidence.png", "contact-evidence.png" };
            foreach (var name in rawNames.Concat(evidenceNames))
                File.WriteAllBytes(Path.Combine(directory, name), new byte[] { 1, 2, 3, (byte)name.Length });
            var reportPath = Path.Combine(directory, "technical-report.json");
            File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
            return new SpatialCaptureSet
            {
                session_id = sessionId,
                raw_paths = rawNames.Select(name => Path.Combine(directory, name)).ToList(),
                evidence_paths = evidenceNames.Select(name => Path.Combine(directory, name)).ToList(),
                report_path = reportPath,
                manifest_path = Path.Combine(directory, "capture-manifest.json")
            };
        }

        private static void DeleteCaptureEvidence(SpatialCaptureSet captures)
        {
            if (string.IsNullOrWhiteSpace(captures?.report_path)) return;
            var directory = Path.GetDirectoryName(captures.report_path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static SpatialContractDocument KnownApprovedContract()
        {
            var asset = new AssetSpatialContractPayload
            {
                asset_guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                asset_path = "Assets/Test.prefab",
                dependency_hash = "dep",
                units = "meter",
                forward = new[] { 0f, 0f, 1f },
                up = new[] { 0f, 1f, 0f },
                pivot_offset = new[] { 0f, 0.5f, 0f },
                collision_proxies = new List<SpatialObbContract>
                {
                    new() { id = "box", center = new[] { 0f, 0.5f, 0f }, size = new[] { 1f, 1f, 1f }, rotation = new[] { 0f, 0f, 0f, 1f } }
                },
                frames = new List<SpatialContactFrameContract>
                {
                    new() { id = "back", point = new[] { 0f, 0.5f, -0.5f }, normal = new[] { 0f, 0f, -1f }, tangent = new[] { 1f, 0f, 0f }, size = new[] { 1f, 1f } },
                    new() { id = "bottom", point = new[] { 0f, 0f, 0f }, normal = new[] { 0f, -1f, 0f }, tangent = new[] { 1f, 0f, 0f }, size = new[] { 1f, 1f } }
                },
                contacts = new List<SpatialContactRuleContract>
                {
                    new() { id = "floor", kind = "FloorSupported", frame_id = "bottom", target = "surface:floor", minimum_gap = 0f, maximum_gap = 0.01f, maximum_penetration = 0f, minimum_support = 0.6f, direction_alignment = 0.95f }
                },
                revision = 1,
                geometry_hash = "3100c2d781773f3002be4704bdcba83b9b8d810d937dce1c933db6c0001c150b",
                capture_set_hash = "capture"
            };
            return new SpatialContractDocument
            {
                contract_version = 1,
                contract_type = "asset",
                state = SpatialContractStates.Approved,
                asset = asset,
                technical = new SpatialTechnicalEvidence { passed = true, error_count = 0, report_hash = "report" },
                review = new SpatialHumanReview
                {
                    decision = SpatialContractStates.Approved,
                    contract_hash = "b4e4805e72353ebcef824c4cdb509c78e7b8ea509e6bd593f7ea0c6f2cb61a5a",
                    capture_set_hash = "capture",
                    reviewer = "local-user",
                    revision = 1
                }
            };
        }

        private static SpatialContractDocument KnownApprovedInteractionContract(string subjectGuid, string targetGuid)
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
                    relative_position = new[] { 0.1f, 1f, 0.2f },
                    relative_rotation = new[] { -0.7071068f, 0f, 0f, 0.7071068f },
                    position_tolerance = new[] { 0.2f, 0.01f, 0.2f },
                    angle_tolerance = 180f,
                    collision_policy = "contact-only",
                    revision = 1,
                    capture_set_hash = "capture"
                },
                technical = new SpatialTechnicalEvidence { passed = true, error_count = 0, report_hash = "report" },
                review = new SpatialHumanReview
                {
                    decision = SpatialContractStates.Approved,
                    capture_set_hash = "capture",
                    reviewer = "local-user",
                    revision = 1
                }
            };
            document.interaction.interaction_hash = SpatialContractHashUtility.ComputeInteractionHash(document.interaction);
            document.review.contract_hash = SpatialContractHashUtility.ComputeContentHash(document);
            return document;
        }

        private static SpatialContractDocument KnownApprovedNegativeZeroTableContract()
        {
            var negativeZero = System.BitConverter.ToSingle(new byte[] { 0, 0, 0, 128 }, 0);
            var asset = new AssetSpatialContractPayload
            {
                asset_guid = "a3fa97880303a42f48b512df88e92628",
                asset_path = "Assets/KayKit_DungeonRemastered_1.1_SOURCE/Assets/fbx(unity)/table_medium.fbx",
                dependency_hash = "283f72daa9e5a3be1a1ef522e9b5d527",
                units = "meter",
                forward = new[] { 0f, 0f, 1f },
                up = new[] { 0f, 1f, 0f },
                pivot_offset = new[] { negativeZero, 0.5f, 0f },
                collision_proxies = new List<SpatialObbContract>
                {
                    new()
                    {
                        id = "renderer-0",
                        center = new[] { negativeZero, 0.5f, 0f },
                        size = new[] { 2f, 1f, 2f },
                        rotation = new[] { 0f, 0f, 0f, 1f }
                    }
                },
                frames = new List<SpatialContactFrameContract>
                {
                    new()
                    {
                        id = "back",
                        point = new[] { negativeZero, 0.5f, -1f },
                        normal = new[] { 0f, 0f, -1f },
                        tangent = new[] { 1f, 0f, 0f },
                        size = new[] { 2f, 1.000001f }
                    },
                    new()
                    {
                        id = "bottom",
                        point = new[] { negativeZero, negativeZero, 0f },
                        normal = new[] { 0f, -1f, 0f },
                        tangent = new[] { 1f, 0f, 0f },
                        size = new[] { 2f, 2f }
                    },
                    new()
                    {
                        id = "top",
                        point = new[] { negativeZero, 1f, 0f },
                        normal = new[] { 0f, 1f, 0f },
                        tangent = new[] { 1f, 0f, 0f },
                        size = new[] { 2f, 2f }
                    }
                },
                contacts = new List<SpatialContactRuleContract>
                {
                    new()
                    {
                        id = "floor",
                        kind = "FloorSupported",
                        frame_id = "bottom",
                        target = "surface:floor",
                        minimum_gap = 0f,
                        maximum_gap = 0.01f,
                        maximum_penetration = 0f,
                        minimum_support = 0.6f,
                        direction_alignment = 0.95f
                    }
                },
                revision = 1,
                geometry_hash = "03ef438f92d4be2408a85f88cecd0f57f4d67ee826dce2b711740bfe19ade10b",
                capture_set_hash = "f30da4a06f6652089688e95fbe19919f8b42783bb446f9874486ad91aebfe2a4"
            };
            return new SpatialContractDocument
            {
                contract_version = 1,
                contract_type = "asset",
                state = SpatialContractStates.Approved,
                asset = asset,
                technical = new SpatialTechnicalEvidence
                {
                    passed = true,
                    error_count = 0,
                    report_hash = "685b1cfc6d78dc1e9fab3abca0c9fd947d5e75d89e0045a069be3651b8119bb7"
                },
                review = new SpatialHumanReview
                {
                    decision = SpatialContractStates.Approved,
                    contract_hash = "595585303e6629aa7f9d6755e91d5e2c2aae59f5ac51e6798caaf4b9a4fa447e",
                    capture_set_hash = "f30da4a06f6652089688e95fbe19919f8b42783bb446f9874486ad91aebfe2a4",
                    reviewer = "local-user",
                    revision = 1
                }
            };
        }
    }
}
