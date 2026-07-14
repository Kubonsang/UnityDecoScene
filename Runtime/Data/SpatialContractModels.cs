using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    public static class SpatialContractStates
    {
        public const string Draft = "Draft";
        public const string TechnicalPassed = "TechnicalPassed";
        public const string AwaitingHumanReview = "AwaitingHumanReview";
        public const string Approved = "Approved";
        public const string TechnicalFailed = "TechnicalFailed";
        public const string RevisionRequested = "RevisionRequested";
        public const string UnableToJudge = "UnableToJudge";
        public const string Stale = "Stale";
    }

    public enum SpatialCalibrationTemplate
    {
        WallMounted,
        WallBackedFloorSupported,
        FloorSupported,
        SupportedBy
    }

    [Serializable]
    public sealed class SpatialObbContract
    {
        public string id;
        public float[] center = new float[3];
        public float[] size = new float[3] { 1f, 1f, 1f };
        public float[] rotation = new float[4] { 0f, 0f, 0f, 1f };

        public static SpatialObbContract FromProxy(OrientedBoxProxy proxy) => new()
        {
            id = proxy.proxyId,
            center = SpatialContractArrays.Vector(proxy.localCenter),
            size = SpatialContractArrays.Vector(proxy.size),
            rotation = SpatialContractArrays.Quaternion(proxy.localRotation)
        };

        public OrientedBoxProxy ToProxy() => new(id, SpatialContractArrays.Vector(center), SpatialContractArrays.Vector(size), SpatialContractArrays.Quaternion(rotation));
    }

    [Serializable]
    public sealed class SpatialContactFrameContract
    {
        public string id;
        public float[] point = new float[3];
        public float[] normal = new float[3];
        public float[] tangent = new float[3];
        public float[] size = new float[2] { 1f, 1f };

        public static SpatialContactFrameContract FromFrame(ContactFrame frame) => new()
        {
            id = frame.frameId,
            point = SpatialContractArrays.Vector(frame.localPoint),
            normal = SpatialContractArrays.Vector(frame.localNormal),
            tangent = SpatialContractArrays.Vector(frame.localTangent),
            size = new[] { frame.size.x, frame.size.y }
        };

        public ContactFrame ToFrame() => new()
        {
            frameId = id,
            localPoint = SpatialContractArrays.Vector(point),
            localNormal = SpatialContractArrays.Vector(normal),
            localTangent = SpatialContractArrays.Vector(tangent),
            size = size != null && size.Length == 2 ? new Vector2(size[0], size[1]) : Vector2.one
        };
    }

    [Serializable]
    public sealed class SpatialContactRuleContract
    {
        public string id;
        public string kind;
        public string frame_id;
        public string target;
        public float minimum_gap;
        public float maximum_gap = 0.01f;
        public float maximum_penetration;
        public float minimum_support = 0.6f;
        public float direction_alignment = 0.95f;
    }

    [Serializable]
    public sealed class AssetSpatialContractPayload
    {
        public string asset_guid;
        public string asset_path;
        public string dependency_hash;
        public string units = "meter";
        public float[] forward = new float[3] { 0f, 0f, 1f };
        public float[] up = new float[3] { 0f, 1f, 0f };
        public float[] pivot_offset = new float[3];
        public List<SpatialObbContract> collision_proxies = new();
        public List<SpatialObbContract> clearance_proxies = new();
        public List<SpatialContactFrameContract> frames = new();
        public List<SpatialContactRuleContract> contacts = new();
        public int revision = 1;
        public string geometry_hash;
        public string capture_set_hash;
    }

    [Serializable]
    public sealed class InteractionSpatialContractPayload
    {
        public string subject_guid;
        public string target_key;
        public string relation = "SupportedBy";
        public string subject_frame = "bottom";
        public string target_frame = "top";
        public float[] relative_position = new float[3];
        public float[] relative_rotation = new float[4] { 0f, 0f, 0f, 1f };
        public float[] position_tolerance = new float[3] { 0.2f, 0.01f, 0.2f };
        public float angle_tolerance = 180f;
        public string collision_policy = "contact-only";
        public int revision = 1;
        public string interaction_hash;
        public string capture_set_hash;
    }

    [Serializable]
    public sealed class SpatialTechnicalEvidence
    {
        public bool passed;
        public int error_count;
        public string report_hash;
    }

    [Serializable]
    public sealed class SpatialHumanReview
    {
        public string decision;
        public string contract_hash;
        public string capture_set_hash;
        public string reviewer;
        public List<string> issue_types = new();
        public string comment;
        public int revision = 1;
    }

    [Serializable]
    public sealed class SpatialContractDocument
    {
        public int contract_version = 1;
        public string contract_type = "asset";
        public string state = SpatialContractStates.Draft;
        public AssetSpatialContractPayload asset;
        public InteractionSpatialContractPayload interaction;
        public SpatialTechnicalEvidence technical;
        public SpatialHumanReview review;

        public bool IsApproved => state == SpatialContractStates.Approved && review != null && review.decision == SpatialContractStates.Approved;
    }

    [Serializable]
    public sealed class SpatialContactEvidence
    {
        public string rule_id;
        public string target;
        public float gap;
        public float penetration;
        public float support;
        public float direction_alignment;
        public float[] contact_point = new float[3];
        public bool valid;
    }

    [Serializable]
    public sealed class SpatialCalibrationReport
    {
        public string session_id;
        public string status = SpatialContractStates.Draft;
        public int error_count;
        public List<string> errors = new();
        public List<SpatialContactEvidence> contacts = new();
        public string report_hash;

        public bool Passed => error_count == 0;
    }

    [Serializable]
    public sealed class SpatialCaptureSet
    {
        public string session_id;
        public string capture_set_hash;
        public List<string> raw_paths = new();
        public List<string> evidence_paths = new();
        public string report_path;
    }

    public static class SpatialContractArrays
    {
        public static float[] Vector(Vector3 value) => new[] { value.x, value.y, value.z };
        public static Vector3 Vector(float[] value) => value != null && value.Length == 3 ? new Vector3(value[0], value[1], value[2]) : Vector3.zero;
        public static float[] Quaternion(Quaternion value) => new[] { value.x, value.y, value.z, value.w };
        public static Quaternion Quaternion(float[] value) => value != null && value.Length == 4 ? new Quaternion(value[0], value[1], value[2], value[3]) : UnityEngine.Quaternion.identity;
    }
}
