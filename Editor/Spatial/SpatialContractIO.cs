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
            if (session == null || report == null || captures == null) throw new ArgumentNullException("Session, report, and captures are required.");
            var prefabPath = AssetDatabase.GetAssetPath(session.Descriptor.Prefab);
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Library/DungeonDecorator/SpatialDrafts"));
            Directory.CreateDirectory(directory);
            session.DraftPaths.Clear();

            var assetDocument = CreateAssetDocument(session, report, captures, prefabPath, guid);
            var assetDraft = Path.Combine(directory, session.SessionId + ".spatial.json");
            WriteJson(assetDraft, assetDocument);
            session.DraftPaths.Add(assetDraft);

            if (session.Template == SpatialCalibrationTemplate.SupportedBy && session.TargetPrefab != null)
            {
                var interaction = CreateInteractionDocument(session, report, captures, guid);
                var interactionDraft = Path.Combine(directory, session.SessionId + ".interaction.json");
                WriteJson(interactionDraft, interaction);
                session.DraftPaths.Add(interactionDraft);
            }
            return session.DraftPaths;
        }

        public static SpatialContractDocument Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("Spatial contract not found.", path);
            var document = JsonUtility.FromJson<SpatialContractDocument>(File.ReadAllText(path));
            if (document == null || document.contract_version != 1) throw new InvalidDataException("Unsupported spatial contract document.");
            return document;
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
                    subject_frame = "bottom",
                    target_frame = "top",
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
