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

            var assetDocument = CreateAssetDocument(session, report, captures, prefabPath, guid);
            var assetDraft = Path.Combine(directory, session.SessionId + ".spatial.json");
            WriteJson(assetDraft, assetDocument);
            session.DraftPaths.Add(assetDraft);

            if (session.Template == SpatialCalibrationTemplate.SupportedBy && session.TargetPrefab != null)
            {
                var interactionDraft = WriteInteractionDraftCore(session, report, captures, guid, directory);
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
            if (File.Exists(supersededAssetDraft)) File.Delete(supersededAssetDraft);
            var path = WriteInteractionDraftCore(session, report, captures, guid, directory);
            session.DraftPaths.Add(path);
            return path;
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
                dependencyHash = dependencyHash
            };
            profile.Normalize();
            return true;
        }

        public static string ApprovedAssetPath(string assetGuid) => $"Assets/SpatialContracts/Assets/{assetGuid}.spatial.json";

        private static string WriteInteractionDraftCore(
            SpatialCalibrationSession session,
            SpatialCalibrationReport report,
            SpatialCaptureSet captures,
            string subjectGuid,
            string directory)
        {
            var document = CreateInteractionDocument(session, report, captures, subjectGuid);
            var path = Path.Combine(directory, session.SessionId + ".interaction.json");
            WriteJson(path, document);
            return path;
        }

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
