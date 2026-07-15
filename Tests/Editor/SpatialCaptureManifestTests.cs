using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityDecoScene.DungeonDecorator.Editor;

namespace UnityDecoScene.DungeonDecorator.Tests
{
    public sealed class SpatialCaptureManifestTests
    {
        private const string AssetProposalHash = "e0aa2b5c8d00adc5fa14e38daf4146a7b55a6c29b7425616e6cbba8267d6f7a6";
        private const string InteractionProposalHash = "107410253dace1b16fa54ea18a02b679b20bba595b590631127044e257427a90";

        [Test]
        public void ProposalHashesMatchUnityCtxGoldenVectors()
        {
            var asset = AssetGolden();
            var interaction = InteractionGolden();

            Assert.That(SpatialContractHashUtility.ComputeProposalHash(asset), Is.EqualTo(AssetProposalHash));
            Assert.That(SpatialContractHashUtility.ComputeProposalHash(interaction), Is.EqualTo(InteractionProposalHash));

            asset.state = SpatialContractStates.RevisionRequested;
            asset.asset.geometry_hash = new string('f', 64);
            asset.asset.capture_set_hash = "another-capture";
            asset.technical.report_hash = "another-report";
            asset.review = new SpatialHumanReview { decision = SpatialContractStates.Approved, reviewer = "local-user" };
            Assert.That(SpatialContractHashUtility.ComputeProposalHash(asset), Is.EqualTo(AssetProposalHash),
                "Review state, evidence, capture hash, and embedded payload hash are excluded.");

            asset.asset.collision_proxies[0].size[0] += 0.25f;
            Assert.That(SpatialContractHashUtility.ComputeProposalHash(asset), Is.Not.EqualTo(AssetProposalHash),
                "Actual geometry changes must invalidate the proposal binding.");
        }

        [Test]
        public void ManifestContainsBothProposalsInStableOrderAndParticipatesInCaptureHash()
        {
            var directory = Path.Combine(Path.GetTempPath(), "spatial-capture-manifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var report = new SpatialCalibrationReport
                {
                    session_id = "manifest-session",
                    status = SpatialContractStates.AwaitingHumanReview,
                    error_count = 0,
                    report_hash = new string('a', 64)
                };
                var forward = SpatialContractIO.BuildCaptureManifest(
                    report.session_id, report, new[] { InteractionGolden(), AssetGolden() });
                var reverse = SpatialContractIO.BuildCaptureManifest(
                    report.session_id, report, new[] { AssetGolden(), InteractionGolden() });

                Assert.That(forward.proposals.Count, Is.EqualTo(2));
                Assert.That(forward.proposals.Select(value => value.contract_type), Is.EqualTo(new[] { "asset", "interaction" }));
                Assert.That(forward.proposals.Select(value => value.proposal_hash),
                    Is.EqualTo(reverse.proposals.Select(value => value.proposal_hash)));

                var captures = CreateEvidence(directory, report);
                SpatialCalibrationCaptureService.FinalizeCaptureSet(captures, forward);
                Assert.That(captures.capture_set_hash, Is.EqualTo(HashEvidenceByOrdinalFilename(captures)));
                var firstHash = captures.capture_set_hash;

                forward.proposals[0].proposal_hash = new string('b', 64);
                SpatialCalibrationCaptureService.FinalizeCaptureSet(captures, forward);
                Assert.That(captures.capture_set_hash, Is.Not.EqualTo(firstHash),
                    "Changing only capture-manifest.json must change the capture-set hash.");
                Assert.That(captures.capture_set_hash, Is.EqualTo(HashEvidenceByOrdinalFilename(captures)));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static SpatialContractDocument AssetGolden() => new()
        {
            contract_version = 1,
            contract_type = "asset",
            state = SpatialContractStates.AwaitingHumanReview,
            asset = new AssetSpatialContractPayload
            {
                asset_guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                asset_path = "Assets/KayKit/banner.prefab",
                dependency_hash = "dependency-v1",
                units = "meter",
                forward = new[] { 0f, 0f, 1f },
                up = new[] { 0f, 1f, 0f },
                pivot_offset = new[] { 0f, 0f, 0f },
                collision_proxies = new List<SpatialObbContract>
                {
                    new() { id = "banner", center = new[] { 0f, 1f, 0f }, size = new[] { 1f, 2f, 0.05f }, rotation = new[] { 0f, 0f, 0f, 1f } }
                },
                frames = new List<SpatialContactFrameContract>
                {
                    new() { id = "back", point = new[] { 0f, 1f, -0.025f }, normal = new[] { 0f, 0f, -1f }, tangent = new[] { 1f, 0f, 0f }, size = new[] { 1f, 2f } }
                },
                contacts = new List<SpatialContactRuleContract>
                {
                    new() { id = "wall", kind = "WallMounted", frame_id = "back", target = "surface:wall", minimum_gap = 0.005f, maximum_gap = 0.01f, maximum_penetration = 0f, minimum_support = 0.6f, direction_alignment = 0.95f }
                },
                revision = 1,
                geometry_hash = "6df415e66995809273aa4b7c65c98093ff6e78b9ced217f72725adef5c679e15",
                capture_set_hash = "capture-banner"
            },
            technical = new SpatialTechnicalEvidence { passed = true, error_count = 0, report_hash = "report-banner" }
        };

        private static SpatialContractDocument InteractionGolden() => new()
        {
            contract_version = 1,
            contract_type = "interaction",
            state = SpatialContractStates.AwaitingHumanReview,
            interaction = new InteractionSpatialContractPayload
            {
                subject_guid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                target_key = "asset:cccccccccccccccccccccccccccccccc",
                relation = "SupportedBy",
                subject_frame = "bottom",
                target_frame = "top",
                relative_position = new[] { 0f, 0f, 0f },
                relative_rotation = new[] { 0f, 0f, 0f, 1f },
                position_tolerance = new[] { 0.1f, 0.01f, 0.1f },
                angle_tolerance = 10f,
                collision_policy = "contact-only",
                revision = 1,
                interaction_hash = "d747a5154d463b62eaed9e8734f9f70378d0fa91dec96b6fb29876e99c8ea1a1",
                capture_set_hash = "capture-interaction"
            },
            technical = new SpatialTechnicalEvidence { passed = true, error_count = 0, report_hash = "report-interaction" }
        };

        private static SpatialCaptureSet CreateEvidence(string directory, SpatialCalibrationReport report)
        {
            var raw = new[] { "front.png", "side.png", "top.png", "contact.png" };
            var evidence = new[] { "front-evidence.png", "side-evidence.png", "top-evidence.png", "contact-evidence.png" };
            foreach (var name in raw.Concat(evidence))
                File.WriteAllBytes(Path.Combine(directory, name), Encoding.UTF8.GetBytes("evidence:" + name));
            var reportPath = Path.Combine(directory, "technical-report.json");
            File.WriteAllText(reportPath, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            return new SpatialCaptureSet
            {
                session_id = report.session_id,
                raw_paths = raw.Select(name => Path.Combine(directory, name)).ToList(),
                evidence_paths = evidence.Select(name => Path.Combine(directory, name)).ToList(),
                report_path = reportPath,
                manifest_path = Path.Combine(directory, "capture-manifest.json")
            };
        }

        private static string HashEvidenceByOrdinalFilename(SpatialCaptureSet captures)
        {
            using var sha = SHA256.Create();
            var paths = captures.raw_paths.Concat(captures.evidence_paths)
                .Append(captures.report_path).Append(captures.manifest_path)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal);
            foreach (var path in paths)
            {
                var bytes = File.ReadAllBytes(path);
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return string.Concat(sha.Hash.Select(value => value.ToString("x2")));
        }
    }
}
