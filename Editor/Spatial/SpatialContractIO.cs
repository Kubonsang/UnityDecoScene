using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class SpatialContractIO
    {
        public static IReadOnlyList<string> WriteDrafts(SpatialCalibrationSession session, SpatialCalibrationReport report, SpatialCaptureSet captures)
        {
            ValidateDraftInputs(session, report, captures);
            var prefabPath = AssetDatabase.GetAssetPath(session.Descriptor.Prefab);
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            var directory = DraftDirectory();
            session.DraftPaths.Clear();

            var proposalCapture = ProposalCapture(captures);
            var assetDocument = CreateAssetDocument(session, report, proposalCapture, prefabPath, guid);
            var documents = new List<SpatialContractDocument> { assetDocument };
            if (session.Template == SpatialCalibrationTemplate.SupportedBy && session.TargetPrefab != null)
                documents.Add(CreateInteractionDocument(session, report, proposalCapture, guid));

            var manifest = BuildCaptureManifest(session.SessionId, report, documents);
            SpatialCalibrationCaptureService.FinalizeCaptureSet(captures, manifest);
            FinalizeDraftDocuments(documents, captures.capture_set_hash, manifest);

            var assetDraft = Path.Combine(directory, session.SessionId + ".spatial.json");
            WriteJson(assetDraft, assetDocument);
            session.DraftPaths.Add(assetDraft);

            if (documents.Count == 2)
            {
                var interactionDraft = Path.Combine(directory, session.SessionId + ".interaction.json");
                WriteJson(interactionDraft, documents[1]);
                session.DraftPaths.Add(interactionDraft);
            }
            return session.DraftPaths;
        }

        /// <summary>Writes one SupportedBy interaction draft and no asset geometry draft.</summary>
        public static string WriteInteractionDraft(SpatialCalibrationSession session, SpatialCalibrationReport report, SpatialCaptureSet captures)
        {
            ValidateDraftInputs(session, report, captures);
            if (session.Template != SpatialCalibrationTemplate.SupportedBy || session.TargetPrefab == null)
                throw new InvalidOperationException("Interaction-only drafts require a SupportedBy session with a target prefab.");
            var prefabPath = AssetDatabase.GetAssetPath(session.Descriptor.Prefab);
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            session.DraftPaths.Clear();
            var directory = DraftDirectory();
            var supersededAssetDraft = Path.Combine(directory, session.SessionId + ".spatial.json");
            var document = CreateInteractionDocument(session, report, ProposalCapture(captures), guid);
            var manifest = BuildCaptureManifest(session.SessionId, report, new[] { document });
            SpatialCalibrationCaptureService.FinalizeCaptureSet(captures, manifest);
            FinalizeDraftDocuments(new[] { document }, captures.capture_set_hash, manifest);
            var path = Path.Combine(directory, session.SessionId + ".interaction.json");
            WriteJson(path, document);
            if (File.Exists(supersededAssetDraft)) File.Delete(supersededAssetDraft);
            session.DraftPaths.Add(path);
            return path;
        }

        public static SpatialCaptureManifest BuildCaptureManifest(
            string sessionId,
            SpatialCalibrationReport report,
            IEnumerable<SpatialContractDocument> documents)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("Capture manifest session_id is required.", nameof(sessionId));
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (!string.Equals(sessionId, report.session_id, StringComparison.Ordinal))
                throw new InvalidDataException("Capture manifest session_id does not match the technical report.");
            var proposals = (documents ?? throw new ArgumentNullException(nameof(documents)))
                .Where(value => value != null)
                .Select(value => new SpatialCaptureProposalBinding
                {
                    contract_type = value.contract_type,
                    canonical_identity = CanonicalProposalIdentity(value),
                    proposal_hash = SpatialContractHashUtility.ComputeProposalHash(value)
                })
                .OrderBy(value => (value.contract_type ?? string.Empty) + "\0" + (value.canonical_identity ?? string.Empty), StringComparer.Ordinal)
                .ToList();
            if (proposals.Count == 0) throw new InvalidDataException("Capture manifest requires at least one proposal.");
            return new SpatialCaptureManifest
            {
                schema_version = 1,
                manifest_version = 1,
                session_id = sessionId,
                proposals = proposals,
                technical_report_hash = report.report_hash,
                technical_passed = report.Passed,
                technical_error_count = report.error_count
            };
        }

        public static string CanonicalProposalIdentity(SpatialContractDocument document)
        {
            if (document?.contract_type == "asset" && document.asset != null)
                return (document.asset.asset_guid ?? string.Empty).Trim().ToLowerInvariant();
            if (document?.contract_type == "interaction" && document.interaction != null)
            {
                var value = document.interaction;
                return (value.subject_guid ?? string.Empty).Trim().ToLowerInvariant() + "__" +
                       Utf8Hex(value.target_key) + "__" + Utf8Hex(value.relation);
            }
            throw new InvalidDataException("Proposal identity requires exactly one payload matching contract_type.");
        }

        public static SpatialContractDocument Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Spatial contract not found.", path);
            var json = PreserveNegativeZeroNumbers(File.ReadAllText(path));
            var document = JsonUtility.FromJson<SpatialContractDocument>(json);
            if (document == null || document.contract_version != 1) throw new InvalidDataException("Unsupported spatial contract document.");
            return document;
        }

        /// <summary>
        /// Unity's JsonUtility turns a JSON -0 token into positive float zero. Spatial Contract
        /// hashes are shared with Go, whose canonical encoder preserves IEEE-754 negative zero.
        /// Replace only negative-zero number tokens outside JSON strings with a tiny negative
        /// finite sentinel; the hash canonicalizer rounds it back to -0 and imported geometry sees
        /// a value far below every supported spatial tolerance.
        /// </summary>
        internal static string PreserveNegativeZeroNumbers(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            var rewritten = new StringBuilder(json.Length + 16);
            var inString = false;
            var escaped = false;
            for (var index = 0; index < json.Length; index++)
            {
                var character = json[index];
                if (inString)
                {
                    rewritten.Append(character);
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    rewritten.Append(character);
                    continue;
                }

                if (character == '-' && TryReadNegativeZero(json, index, out var end))
                {
                    rewritten.Append("-1e-30");
                    index = end - 1;
                    continue;
                }
                rewritten.Append(character);
            }
            if (inString || escaped) throw new InvalidDataException("Spatial contract JSON contains an unterminated string.");
            return rewritten.ToString();
        }

        private static bool TryReadNegativeZero(string json, int start, out int end)
        {
            end = start;
            if (start < 0 || start + 1 >= json.Length || json[start] != '-' || json[start + 1] != '0' ||
                !HasValueBoundaryBefore(json, start)) return false;

            var cursor = start + 2;
            if (cursor < json.Length && char.IsDigit(json[cursor])) return false;
            if (cursor < json.Length && json[cursor] == '.')
            {
                cursor++;
                var fractionStart = cursor;
                while (cursor < json.Length && char.IsDigit(json[cursor]))
                {
                    if (json[cursor] != '0') return false;
                    cursor++;
                }
                if (cursor == fractionStart) return false;
            }

            if (cursor < json.Length && (json[cursor] == 'e' || json[cursor] == 'E'))
            {
                cursor++;
                if (cursor < json.Length && (json[cursor] == '+' || json[cursor] == '-')) cursor++;
                var exponentStart = cursor;
                while (cursor < json.Length && char.IsDigit(json[cursor])) cursor++;
                if (cursor == exponentStart) return false;
            }

            if (!HasValueBoundaryAfter(json, cursor)) return false;
            end = cursor;
            return true;
        }

        private static bool HasValueBoundaryBefore(string json, int start)
        {
            for (var index = start - 1; index >= 0; index--)
            {
                if (char.IsWhiteSpace(json[index])) continue;
                return json[index] == '[' || json[index] == ',' || json[index] == ':';
            }
            return true;
        }

        private static bool HasValueBoundaryAfter(string json, int end)
        {
            for (var index = end; index < json.Length; index++)
            {
                if (char.IsWhiteSpace(json[index])) continue;
                return json[index] == ',' || json[index] == ']' || json[index] == '}';
            }
            return true;
        }

        public static string SerializeForStorage(SpatialContractDocument document, bool prettyPrint = true)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (document.contract_type == "asset" && document.asset != null)
                return document.review == null
                    ? JsonUtility.ToJson(new AssetDraftJson(document), prettyPrint)
                    : JsonUtility.ToJson(new ReviewedAssetJson(document), prettyPrint);
            if (document.contract_type == "interaction" && document.interaction != null)
                return document.review == null
                    ? JsonUtility.ToJson(new InteractionDraftJson(document), prettyPrint)
                    : JsonUtility.ToJson(new ReviewedInteractionJson(document), prettyPrint);
            throw new InvalidDataException("Spatial contract must contain exactly one payload matching contract_type.");
        }

        public static bool ApplyApprovedAssetContract(string path, DecorAssetDescriptor descriptor, out string reason)
        {
            reason = null;
            if (descriptor == null || descriptor.Prefab == null) { reason = "Descriptor and prefab are required."; return false; }
            if (!TryCreateApprovedGeometry(path, descriptor, out var profile, out reason)) return false;
            Undo.RecordObject(descriptor, "Import approved spatial contract");
            descriptor.ConfigureGeometry(profile);
            EditorUtility.SetDirty(descriptor);
            AssetDatabase.SaveAssets();
            return true;
        }

        public static bool TryCreateApprovedGeometry(string path, DecorAssetDescriptor descriptor, out DecorGeometryProfile profile, out string reason)
        {
            profile = null;
            reason = null;
            if (descriptor == null || descriptor.Prefab == null) { reason = "Descriptor and prefab are required."; return false; }
            SpatialContractDocument document;
            try { document = Load(path); }
            catch (Exception exception) { reason = exception.Message; return false; }
            if (!document.IsApproved || document.contract_type != "asset" || document.asset == null) { reason = "Only human-approved asset contracts can be imported."; return false; }
            if (!SpatialContractHashUtility.ValidateApproved(document, out reason)) return false;
            var prefabPath = AssetDatabase.GetAssetPath(descriptor.Prefab);
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            var dependencyHash = AssetDatabase.GetAssetDependencyHash(prefabPath).ToString();
            if (!string.Equals(document.asset.asset_guid, guid, StringComparison.OrdinalIgnoreCase)) { reason = "Asset GUID does not match the descriptor prefab."; return false; }
            if (!string.Equals(document.asset.dependency_hash, dependencyHash, StringComparison.Ordinal)) { reason = "Contract is stale because the prefab dependency hash changed."; return false; }
            var verifiedContentHash = SpatialContractHashUtility.ComputeContentHash(document);
            var verifiedReviewJson = JsonUtility.ToJson(document.review, false);
            if (!SpatialContractAuthorityVerifier.Verify(path, verifiedContentHash, out reason)) return false;

            // Consume the exact snapshot that the external ledger verified.
            try { document = Load(path); }
            catch (Exception exception) { reason = "CONTRACT_AUTHORITY_CHANGED " + exception.Message; return false; }
            if (!SpatialContractHashUtility.ValidateApproved(document, out reason) ||
                !string.Equals(SpatialContractHashUtility.ComputeContentHash(document), verifiedContentHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(JsonUtility.ToJson(document.review, false), verifiedReviewJson, StringComparison.Ordinal))
            {
                reason = "CONTRACT_AUTHORITY_CHANGED contract content or review evidence changed during authority verification.";
                return false;
            }
            var latestPrefabPath = AssetDatabase.GetAssetPath(descriptor.Prefab);
            var latestGuid = AssetDatabase.AssetPathToGUID(latestPrefabPath);
            var latestDependencyHash = AssetDatabase.GetAssetDependencyHash(latestPrefabPath).ToString();
            if (!string.Equals(latestPrefabPath, prefabPath, StringComparison.Ordinal) ||
                !string.Equals(latestGuid, guid, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(latestDependencyHash, dependencyHash, StringComparison.Ordinal) ||
                !string.Equals(document.asset.asset_guid, latestGuid, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(document.asset.dependency_hash, latestDependencyHash, StringComparison.Ordinal))
            {
                reason = "CONTRACT_AUTHORITY_CHANGED prefab identity or dependency changed during authority verification.";
                return false;
            }

            var frames = document.asset.frames.ToDictionary(value => value.id, value => value.ToFrame(), StringComparer.Ordinal);
            var importedContacts = document.asset.contacts.Select(ToRules).ToList();
            var primary = importedContacts.FirstOrDefault();
            profile = new DecorGeometryProfile
            {
                source = GeometrySource.Manual,
                collisionProxies = document.asset.collision_proxies.Select(value => value.ToProxy()).ToList(),
                forwardAxis = SpatialContractArrays.Vector(document.asset.forward),
                upAxis = SpatialContractArrays.Vector(document.asset.up),
                pivotOffset = SpatialContractArrays.Vector(document.asset.pivot_offset),
                bottomContact = frames.TryGetValue("bottom", out var bottom) ? bottom : descriptor.Geometry.bottomContact,
                backContact = frames.TryGetValue("back", out var back) ? back : descriptor.Geometry.backContact,
                topContact = frames.TryGetValue("top", out var top) ? top : descriptor.Geometry.topContact,
                contact = primary ?? ContactRules.Defaults(ContactRequirement.FreeStanding),
                contacts = importedContacts,
                inferenceConfidence = 1f,
                reviewed = true,
                dependencyHash = latestDependencyHash
            };
            profile.Normalize();
            return true;
        }

        /// <summary>
        /// Loads an Approved interaction as an authority-bearing catalog binding.
        /// A caller cannot manufacture this provenance from tracked JSON alone:
        /// the external ledger is checked and the exact semantic snapshot is
        /// reloaded before the binding is returned.
        /// </summary>
        public static bool TryLoadApprovedInteractionBinding(
            string path,
            string canonicalSubjectDescriptorId,
            string canonicalTargetDescriptorId,
            out SupportInteractionBinding binding,
            out string reason)
        {
            binding = null;
            reason = null;
            SpatialContractDocument document;
            try { document = Load(path); }
            catch (Exception exception) { reason = exception.Message; return false; }
            if (!document.IsApproved || document.contract_type != "interaction" || document.interaction == null)
            {
                reason = "Only human-approved interaction contracts can be loaded.";
                return false;
            }
            if (!SpatialContractHashUtility.ValidateApproved(document, out reason)) return false;
            var verifiedContentHash = SpatialContractHashUtility.ComputeContentHash(document);
            var verifiedReviewJson = JsonUtility.ToJson(document.review, false);
            if (!SpatialContractAuthorityVerifier.TryVerify(path, verifiedContentHash, out var authority, out reason)) return false;
            if (!string.Equals(authority.contract_type, "interaction", StringComparison.Ordinal) ||
                !string.Equals(authority.subject_guid, document.interaction.subject_guid, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(authority.target_key, document.interaction.target_key, StringComparison.Ordinal) ||
                !IsSha256(authority.subject_geometry_hash) || !IsSha256(authority.target_geometry_hash))
            {
                reason = "CONTRACT_AUTHORITY_REJECTED interaction receipt does not match its verified subject, target, or geometry binding.";
                return false;
            }

            // Consume the exact semantic snapshot that the external ledger
            // verified. Registration separately rechecks this hash so callers
            // cannot mutate the returned document between load and registration.
            try { document = Load(path); }
            catch (Exception exception) { reason = "CONTRACT_AUTHORITY_CHANGED " + exception.Message; return false; }
            if (!document.IsApproved || document.contract_type != "interaction" || document.interaction == null)
            {
                reason = "CONTRACT_AUTHORITY_CHANGED contract type or approval state changed during authority verification.";
                return false;
            }
            if (!SpatialContractHashUtility.ValidateApproved(document, out var validationReason))
            {
                reason = "CONTRACT_AUTHORITY_CHANGED " + validationReason;
                return false;
            }
            if (!string.Equals(
                    SpatialContractHashUtility.ComputeContentHash(document),
                    verifiedContentHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(JsonUtility.ToJson(document.review, false), verifiedReviewJson, StringComparison.Ordinal))
            {
                reason = "CONTRACT_AUTHORITY_CHANGED interaction content or review evidence changed during authority verification.";
                return false;
            }

            binding = SupportInteractionBinding.FromAuthorityVerifiedSnapshot(
                document,
                canonicalSubjectDescriptorId,
                canonicalTargetDescriptorId,
                authority.subject_geometry_hash,
                authority.target_geometry_hash,
                verifiedContentHash);
            return true;
        }

        public static string ApprovedAssetPath(string assetGuid) => $"Assets/SpatialContracts/Assets/{assetGuid}.spatial.json";

        private static bool IsSha256(string value) =>
            !string.IsNullOrWhiteSpace(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

        private static SpatialCaptureSet ProposalCapture(SpatialCaptureSet captures) => new()
        {
            session_id = captures.session_id,
            capture_set_hash = string.Empty
        };

        private static void FinalizeDraftDocuments(
            IEnumerable<SpatialContractDocument> source,
            string captureSetHash,
            SpatialCaptureManifest manifest)
        {
            var documents = source.Where(value => value != null).ToList();
            foreach (var document in documents)
            {
                if (document.contract_type == "asset" && document.asset != null)
                {
                    document.asset.capture_set_hash = captureSetHash;
                    document.asset.geometry_hash = SpatialContractHashUtility.ComputeGeometryHash(document.asset);
                }
                else if (document.contract_type == "interaction" && document.interaction != null)
                {
                    document.interaction.capture_set_hash = captureSetHash;
                    document.interaction.interaction_hash = SpatialContractHashUtility.ComputeInteractionHash(document.interaction);
                }
                else throw new InvalidDataException("Draft contains a payload that does not match contract_type.");
            }

            var actual = documents.Select(value => new SpatialCaptureProposalBinding
                {
                    contract_type = value.contract_type,
                    canonical_identity = CanonicalProposalIdentity(value),
                    proposal_hash = SpatialContractHashUtility.ComputeProposalHash(value)
                })
                .OrderBy(value => value.contract_type + "\0" + value.canonical_identity, StringComparer.Ordinal)
                .ToList();
            var expected = (manifest?.proposals ?? new List<SpatialCaptureProposalBinding>())
                .OrderBy(value => value.contract_type + "\0" + value.canonical_identity, StringComparer.Ordinal)
                .ToList();
            if (actual.Count != expected.Count || actual.Where((value, index) =>
                    !string.Equals(value.contract_type, expected[index].contract_type, StringComparison.Ordinal) ||
                    !string.Equals(value.canonical_identity, expected[index].canonical_identity, StringComparison.Ordinal) ||
                    !string.Equals(value.proposal_hash, expected[index].proposal_hash, StringComparison.OrdinalIgnoreCase)).Any())
                throw new InvalidDataException("Final draft proposal does not match capture-manifest.json.");
        }

        private static string Utf8Hex(string value) => string.Concat(
            Encoding.UTF8.GetBytes(value ?? string.Empty).Select(item => item.ToString("x2")));

        private static string DraftDirectory()
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/DungeonDecorator/SpatialDrafts"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static void ValidateDraftInputs(SpatialCalibrationSession session, SpatialCalibrationReport report, SpatialCaptureSet captures)
        {
            if (session == null || report == null || captures == null)
                throw new ArgumentNullException("Session, report, and captures are required.");
            if (!string.Equals(session.SessionId, report.session_id, StringComparison.Ordinal) ||
                !string.Equals(session.SessionId, captures.session_id, StringComparison.Ordinal))
                throw new InvalidDataException("Session, technical report, and capture set IDs must match.");
        }

        private static SpatialContractDocument CreateAssetDocument(SpatialCalibrationSession session, SpatialCalibrationReport report, SpatialCaptureSet captures, string prefabPath, string guid)
        {
            var frames = new[] { session.Frame("bottom"), session.Frame("back"), session.Frame("top") };
            return new SpatialContractDocument
            {
                contract_type = "asset",
                state = report.Passed ? SpatialContractStates.AwaitingHumanReview : SpatialContractStates.TechnicalFailed,
                asset = new AssetSpatialContractPayload
                {
                    asset_guid = guid,
                    asset_path = prefabPath,
                    dependency_hash = AssetDatabase.GetAssetDependencyHash(prefabPath).ToString(),
                    units = "meter",
                    forward = SpatialContractArrays.Vector(session.Geometry.forwardAxis),
                    up = SpatialContractArrays.Vector(session.Geometry.upAxis),
                    pivot_offset = SpatialContractArrays.Vector(session.Geometry.pivotOffset),
                    collision_proxies = session.Geometry.collisionProxies.Select(SpatialObbContract.FromProxy).ToList(),
                    frames = frames.Select(SpatialContactFrameContract.FromFrame).ToList(),
                    contacts = session.Rules.Select(CloneRule).ToList(),
                    revision = 1,
                    geometry_hash = string.Empty,
                    capture_set_hash = captures.capture_set_hash
                },
                technical = new SpatialTechnicalEvidence { passed = report.Passed, error_count = report.error_count, report_hash = report.report_hash }
            };
        }

        private static SpatialContractDocument CreateInteractionDocument(SpatialCalibrationSession session, SpatialCalibrationReport report, SpatialCaptureSet captures, string subjectGuid)
        {
            var targetPath = AssetDatabase.GetAssetPath(session.TargetPrefab);
            var relativePosition = session.TargetObject.transform.InverseTransformPoint(session.SubjectObject.transform.position);
            var relativeRotation = Quaternion.Inverse(session.TargetObject.transform.rotation) * session.SubjectObject.transform.rotation;
            return new SpatialContractDocument
            {
                contract_type = "interaction",
                state = report.Passed ? SpatialContractStates.AwaitingHumanReview : SpatialContractStates.TechnicalFailed,
                interaction = new InteractionSpatialContractPayload
                {
                    subject_guid = subjectGuid,
                    target_key = "asset:" + AssetDatabase.AssetPathToGUID(targetPath),
                    relation = "SupportedBy",
                    subject_frame = session.SupportedBySubjectFrameId,
                    target_frame = session.SupportedByTargetFrameId,
                    relative_position = SpatialContractArrays.Vector(relativePosition),
                    relative_rotation = SpatialContractArrays.Quaternion(relativeRotation),
                    position_tolerance = new[] { 0.2f, 0.01f, 0.2f },
                    angle_tolerance = 180f,
                    collision_policy = "contact-only",
                    revision = 1,
                    interaction_hash = string.Empty,
                    capture_set_hash = captures.capture_set_hash
                },
                technical = new SpatialTechnicalEvidence { passed = report.Passed, error_count = report.error_count, report_hash = report.report_hash }
            };
        }

        private static SpatialContactRuleContract CloneRule(SpatialContactRuleContract source) => new()
        {
            id = source.id,
            kind = source.kind,
            frame_id = source.frame_id,
            target = source.target,
            minimum_gap = source.minimum_gap,
            maximum_gap = source.maximum_gap,
            maximum_penetration = source.maximum_penetration,
            minimum_support = source.minimum_support,
            direction_alignment = source.direction_alignment
        };

        private static ContactRules ToRules(SpatialContactRuleContract source)
        {
            if (source == null) return ContactRules.Defaults(ContactRequirement.FreeStanding);
            Enum.TryParse(source.kind, out ContactRequirement requirement);
            return new ContactRules
            {
                ruleId = source.id,
                frameId = source.frame_id,
                requirement = requirement,
                minimumGap = source.minimum_gap,
                maximumGap = source.maximum_gap,
                maximumPenetration = source.maximum_penetration,
                minimumSupportCoverage = source.minimum_support
            };
        }

        private static void WriteJson(string path, SpatialContractDocument document)
        {
            File.WriteAllText(path, SerializeForStorage(document) + Environment.NewLine, new UTF8Encoding(false));
        }

        [Serializable]
        private sealed class AssetDraftJson
        {
            public int contract_version;
            public string contract_type;
            public string state;
            public AssetSpatialContractPayload asset;
            public SpatialTechnicalEvidence technical;
            public AssetDraftJson(SpatialContractDocument value)
            {
                contract_version = value.contract_version; contract_type = value.contract_type;
                state = value.state; asset = value.asset; technical = value.technical;
            }
        }

        [Serializable]
        private sealed class ReviewedAssetJson
        {
            public int contract_version;
            public string contract_type;
            public string state;
            public AssetSpatialContractPayload asset;
            public SpatialTechnicalEvidence technical;
            public SpatialHumanReview review;
            public ReviewedAssetJson(SpatialContractDocument value)
            {
                contract_version = value.contract_version; contract_type = value.contract_type;
                state = value.state; asset = value.asset; technical = value.technical; review = value.review;
            }
        }

        [Serializable]
        private sealed class InteractionDraftJson
        {
            public int contract_version;
            public string contract_type;
            public string state;
            public InteractionSpatialContractPayload interaction;
            public SpatialTechnicalEvidence technical;
            public InteractionDraftJson(SpatialContractDocument value)
            {
                contract_version = value.contract_version; contract_type = value.contract_type;
                state = value.state; interaction = value.interaction; technical = value.technical;
            }
        }

        [Serializable]
        private sealed class ReviewedInteractionJson
        {
            public int contract_version;
            public string contract_type;
            public string state;
            public InteractionSpatialContractPayload interaction;
            public SpatialTechnicalEvidence technical;
            public SpatialHumanReview review;
            public ReviewedInteractionJson(SpatialContractDocument value)
            {
                contract_version = value.contract_version; contract_type = value.contract_type;
                state = value.state; interaction = value.interaction; technical = value.technical; review = value.review;
            }
        }
    }
}
