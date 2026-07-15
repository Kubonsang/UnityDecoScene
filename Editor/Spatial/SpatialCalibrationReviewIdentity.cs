using System;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class SpatialCalibrationReviewIdentity
    {
        public static string ComputeInteractionSignature(InteractionSpatialContractPayload interaction)
        {
            if (interaction == null) throw new ArgumentNullException(nameof(interaction));
            var signature = new InteractionSpatialContractPayload
            {
                subject_guid = string.Empty,
                target_key = interaction.target_key,
                relation = interaction.relation,
                subject_frame = interaction.subject_frame,
                target_frame = interaction.target_frame,
                relative_position = Clone(interaction.relative_position),
                relative_rotation = Clone(interaction.relative_rotation),
                position_tolerance = Clone(interaction.position_tolerance),
                angle_tolerance = interaction.angle_tolerance,
                collision_policy = interaction.collision_policy,
                // Contract revision is lifecycle metadata, not a spatial pose discriminator.
                revision = 1,
                interaction_hash = string.Empty,
                capture_set_hash = string.Empty
            };
            return SpatialContractHashUtility.ComputeInteractionHash(signature);
        }

        public static string BuildInteractionReviewGroupKey(string geometryFamilyHash, InteractionSpatialContractPayload interaction)
        {
            if (string.IsNullOrWhiteSpace(geometryFamilyHash)) throw new ArgumentException("Geometry family hash is required.", nameof(geometryFamilyHash));
            return "interaction|" + geometryFamilyHash.Trim() + "|" + ComputeInteractionSignature(interaction);
        }

        private static float[] Clone(float[] value) => value == null ? Array.Empty<float>() : (float[])value.Clone();
    }
}
