using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    internal enum SurfaceArrangementSlider
    {
        Amount,
        Orderliness,
        Grouping,
        Stacking
    }

    /// <summary>
    /// Keeps composition-card mutations in one place so nested arrangement data is always edited
    /// through SerializedObject and participates in Unity Undo.
    /// </summary>
    internal static class SurfaceArrangementCompositionEditing
    {
        private const string ArrangementsProperty = "surfaceArrangements";

        public static bool ApplyPreset(RoomCompositionPlan plan, int arrangementIndex, SurfaceArrangementPreset preset)
        {
            return Edit(plan, arrangementIndex, $"Set Arrangement Preset: {preset}", property =>
            {
                property.FindPropertyRelative("preset").stringValue = preset.ToString();
                var values = PresetValues(preset);
                property.FindPropertyRelative("amount").floatValue = values.amount;
                property.FindPropertyRelative("orderliness").floatValue = values.orderliness;
                property.FindPropertyRelative("grouping").floatValue = values.grouping;
                property.FindPropertyRelative("stacking").floatValue = values.stacking;
            });
        }

        public static bool SetSlider(RoomCompositionPlan plan, int arrangementIndex, SurfaceArrangementSlider slider, float value)
        {
            var propertyName = slider switch
            {
                SurfaceArrangementSlider.Amount => "amount",
                SurfaceArrangementSlider.Orderliness => "orderliness",
                SurfaceArrangementSlider.Grouping => "grouping",
                SurfaceArrangementSlider.Stacking => "stacking",
                _ => throw new ArgumentOutOfRangeException(nameof(slider), slider, null)
            };
            return Edit(plan, arrangementIndex, $"Edit Arrangement {slider}", property =>
                property.FindPropertyRelative(propertyName).floatValue = Mathf.Clamp01(value));
        }

        public static bool SetMemberCounts(RoomCompositionPlan plan, int arrangementIndex, int memberIndex, int minimum, int maximum)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (arrangementIndex < 0 || arrangementIndex >= plan.SurfaceArrangements.Count)
                throw new ArgumentOutOfRangeException(nameof(arrangementIndex));
            var sourceMembers = plan.SurfaceArrangements[arrangementIndex]?.members;
            if (sourceMembers == null || memberIndex < 0 || memberIndex >= sourceMembers.Count)
                throw new ArgumentOutOfRangeException(nameof(memberIndex));
            var otherMinimum = sourceMembers.Where((_, index) => index != memberIndex).Sum(value => value?.minimum_count ?? 0);
            var otherMaximum = sourceMembers.Where((_, index) => index != memberIndex).Sum(value => value?.maximum_count ?? 0);
            var maximumAvailable = Mathf.Clamp(12 - otherMaximum, 1, 12);
            var minimumRequired = otherMinimum == 0 ? 1 : 0;
            minimum = Mathf.Clamp(minimum, minimumRequired, maximumAvailable);
            maximum = Mathf.Clamp(maximum, Mathf.Max(1, minimum), maximumAvailable);

            return Edit(plan, arrangementIndex, "Edit Arrangement Member Count", property =>
            {
                var members = property.FindPropertyRelative("members");
                if (memberIndex < 0 || memberIndex >= members.arraySize)
                    throw new ArgumentOutOfRangeException(nameof(memberIndex));
                var member = members.GetArrayElementAtIndex(memberIndex);
                member.FindPropertyRelative("minimum_count").intValue = minimum;
                member.FindPropertyRelative("maximum_count").intValue = maximum;
            });
        }

        private static bool Edit(RoomCompositionPlan plan, int arrangementIndex, string undoName, Action<SerializedProperty> mutation)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (mutation == null) throw new ArgumentNullException(nameof(mutation));
            if (arrangementIndex < 0 || arrangementIndex >= plan.SurfaceArrangements.Count)
                throw new ArgumentOutOfRangeException(nameof(arrangementIndex));

            var serialized = new SerializedObject(plan);
            serialized.Update();
            var arrangements = serialized.FindProperty(ArrangementsProperty);
            if (arrangements == null || arrangementIndex >= arrangements.arraySize)
                throw new InvalidOperationException("The composition plan does not expose its Surface Arrangement list.");

            Undo.RecordObject(plan, undoName);
            mutation(arrangements.GetArrayElementAtIndex(arrangementIndex));
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // spec_hash is content-derived. Keeping the serialized value current prevents a UI edit
            // from turning an otherwise valid spec into SUPPORT_CONTRACT_STALE on the next preview.
            serialized.Update();
            arrangements = serialized.FindProperty(ArrangementsProperty);
            var hashProperty = arrangements.GetArrayElementAtIndex(arrangementIndex).FindPropertyRelative("spec_hash");
            hashProperty.stringValue = SurfaceArrangementSpecUtility.ComputeSpecHash(plan.SurfaceArrangements[arrangementIndex]);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(plan);
            return true;
        }

        private static (float amount, float orderliness, float grouping, float stacking) PresetValues(SurfaceArrangementPreset preset)
        {
            return preset switch
            {
                SurfaceArrangementPreset.Neat => (0.45f, 0.90f, 0.55f, 0.55f),
                SurfaceArrangementPreset.Scattered => (0.65f, 0.15f, 0.25f, 0.15f),
                _ => (0.55f, 0.45f, 0.75f, 0.55f)
            };
        }
    }

    internal static class SurfaceArrangementCompositionEditor
    {
        public static void Draw(RoomCompositionPlan plan, Action<string> setStatus)
        {
            if (plan?.SurfaceArrangements == null || plan.SurfaceArrangements.Count == 0)
            {
                EditorGUILayout.HelpBox("No tabletop or support-surface arrangements are defined in this plan.", MessageType.Info);
                return;
            }

            var proposal = SafeLoadProposal();
            EditorGUILayout.LabelField($"Surface Arrangements ({plan.SurfaceArrangements.Count})", EditorStyles.boldLabel);
            for (var index = 0; index < plan.SurfaceArrangements.Count; index++)
            {
                var spec = plan.SurfaceArrangements[index];
                if (spec == null) continue;
                DrawCard(plan, index, spec, proposal, setStatus);
            }
        }

        private static void DrawCard(
            RoomCompositionPlan plan,
            int index,
            SurfaceArrangementSpec spec,
            SurfaceArrangementProposal proposal,
            Action<string> setStatus)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(spec.arrangement_id) ? $"Arrangement {index + 1}" : spec.arrangement_id,
                    EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField("Support Object", spec.target_element_id ?? string.Empty);
                    EditorGUILayout.TextField("Support Frame", spec.TargetFrameId);
                }

                DrawPreset(plan, index, spec);
                DrawSlider(plan, index, spec, SurfaceArrangementSlider.Amount, "Amount", spec.Amount);
                DrawSlider(plan, index, spec, SurfaceArrangementSlider.Orderliness, "Orderliness", spec.Orderliness);
                DrawSlider(plan, index, spec, SurfaceArrangementSlider.Grouping, "Grouping", spec.Grouping);
                DrawSlider(plan, index, spec, SurfaceArrangementSlider.Stacking, "Stacking", spec.Stacking);
                EditorGUILayout.LabelField($"Edge margin: {spec.EdgeMargin * 100f:0.#} cm    Max stack: {spec.MaxStackHeight}    Seed offset: {spec.SeedOffset}",
                    EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.Space(3f);
                EditorGUILayout.LabelField("Members", EditorStyles.miniBoldLabel);
                DrawMembers(plan, index, spec);

                if (!SurfaceArrangementSpecUtility.Validate(spec, out var invalid))
                    EditorGUILayout.HelpBox(invalid, MessageType.Error);

                if (proposal != null && string.Equals(proposal.arrangementId, spec.arrangement_id, StringComparison.Ordinal))
                    DrawProposalDifference(spec, proposal);

                using (new EditorGUI.DisabledScope(!CanRegenerate(plan, spec)))
                {
                    if (GUILayout.Button("Regenerate This Arrangement Only"))
                    {
                        try
                        {
                            var session = RoomPreviewManager.RegenerateArrangement(spec.arrangement_id);
                            var count = session.Placements.Count(value => string.Equals(value.arrangementId, spec.arrangement_id, StringComparison.Ordinal));
                            setStatus?.Invoke($"Regenerated '{spec.arrangement_id}' only: {count} props. Other placements and lock choices were preserved.");
                        }
                        catch (Exception exception)
                        {
                            setStatus?.Invoke(exception.Message);
                            Debug.LogException(exception);
                        }
                    }
                }
                if (RoomPreviewManager.Current == null)
                    EditorGUILayout.LabelField("Generate the room preview once before regenerating only this arrangement.", EditorStyles.wordWrappedMiniLabel);
            }
        }

        private static void DrawPreset(RoomCompositionPlan plan, int index, SurfaceArrangementSpec spec)
        {
            EditorGUI.BeginChangeCheck();
            var value = (SurfaceArrangementPreset)EditorGUILayout.EnumPopup("Preset", spec.Preset);
            if (EditorGUI.EndChangeCheck()) SurfaceArrangementCompositionEditing.ApplyPreset(plan, index, value);
        }

        private static void DrawSlider(
            RoomCompositionPlan plan,
            int index,
            SurfaceArrangementSpec spec,
            SurfaceArrangementSlider slider,
            string label,
            float value)
        {
            EditorGUI.BeginChangeCheck();
            var edited = EditorGUILayout.Slider(label, value, 0f, 1f);
            if (EditorGUI.EndChangeCheck()) SurfaceArrangementCompositionEditing.SetSlider(plan, index, slider, edited);
        }

        private static void DrawMembers(RoomCompositionPlan plan, int arrangementIndex, SurfaceArrangementSpec spec)
        {
            var members = spec.members ?? new List<SurfaceArrangementMemberSpec>();
            for (var memberIndex = 0; memberIndex < members.Count; memberIndex++)
            {
                var member = members[memberIndex];
                if (member == null) continue;
                using (new EditorGUILayout.HorizontalScope())
                {
                    var descriptor = plan.Catalog != null ? plan.Catalog.Find(member.descriptor_id) : null;
                    EditorGUILayout.LabelField(descriptor != null ? descriptor.name : member.descriptor_id ?? "Missing descriptor", GUILayout.MinWidth(130f));
                    EditorGUILayout.LabelField("Min", GUILayout.Width(24f));
                    var minimum = EditorGUILayout.IntField(member.minimum_count, GUILayout.Width(34f));
                    EditorGUILayout.LabelField("Max", GUILayout.Width(27f));
                    var maximum = EditorGUILayout.IntField(member.maximum_count, GUILayout.Width(34f));
                    EditorGUILayout.LabelField($"{member.affinity_group}  w {member.selection_weight:0.##}", EditorStyles.miniLabel, GUILayout.MinWidth(85f));
                    if (minimum != member.minimum_count || maximum != member.maximum_count)
                        SurfaceArrangementCompositionEditing.SetMemberCounts(plan, arrangementIndex, memberIndex, minimum, maximum);
                }
            }
        }

        private static void DrawProposalDifference(SurfaceArrangementSpec spec, SurfaceArrangementProposal proposal)
        {
            EditorGUILayout.Space(3f);
            EditorGUILayout.LabelField("AI suggestion vs your values", EditorStyles.miniBoldLabel);
            DrawTextDifference("Support", proposal.targetElementId, spec.target_element_id);
            DrawTextDifference("Frame", proposal.targetFrameId, spec.TargetFrameId);
            EditorGUILayout.LabelField($"Preset - AI: {proposal.preset} | User: {spec.preset}{Changed(proposal.preset, spec.preset)}", EditorStyles.wordWrappedMiniLabel);
            DrawDifference("Amount", proposal.amount, spec.Amount);
            DrawDifference("Orderliness", proposal.orderliness, spec.Orderliness);
            DrawDifference("Grouping", proposal.grouping, spec.Grouping);
            DrawDifference("Stacking", proposal.stacking, spec.Stacking);
            DrawDifference("Edge margin (m)", proposal.edgeMargin, spec.EdgeMargin);
            EditorGUILayout.LabelField($"Max stack - AI: {proposal.maxStackHeight} | User: {spec.MaxStackHeight}{Changed(proposal.maxStackHeight.ToString(), spec.MaxStackHeight.ToString())}", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField($"Seed offset - AI: {proposal.seedOffset} | User: {spec.SeedOffset}{Changed(proposal.seedOffset.ToString(), spec.SeedOffset.ToString())}", EditorStyles.wordWrappedMiniLabel);
            DrawProposalMembers(spec, proposal.membersJson);
            if (!string.IsNullOrWhiteSpace(proposal.rationale))
                EditorGUILayout.HelpBox(proposal.rationale, MessageType.Info);
        }

        private static void DrawTextDifference(string label, string proposed, string current)
        {
            proposed ??= string.Empty;
            current ??= string.Empty;
            EditorGUILayout.LabelField($"{label} - AI: {proposed} | User: {current}{Changed(proposed, current)}", EditorStyles.wordWrappedMiniLabel);
        }

        private static void DrawProposalMembers(SurfaceArrangementSpec spec, string membersJson)
        {
            if (string.IsNullOrWhiteSpace(membersJson)) return;
            ProposalMemberEnvelope envelope;
            try { envelope = JsonUtility.FromJson<ProposalMemberEnvelope>($"{{\"items\":{membersJson}}}"); }
            catch { return; }
            if (envelope?.items == null) return;
            var current = (spec.members ?? new List<SurfaceArrangementMemberSpec>())
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.descriptor_id))
                .GroupBy(value => value.descriptor_id, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.First(), StringComparer.Ordinal);
            var proposedMembers = envelope.items.Where(value => value != null && !string.IsNullOrWhiteSpace(value.descriptorId))
                .OrderBy(value => value.descriptorId, StringComparer.Ordinal).ToArray();
            var proposedIds = new HashSet<string>(proposedMembers.Select(value => value.descriptorId), StringComparer.Ordinal);
            foreach (var proposed in proposedMembers)
            {
                var userRange = current.TryGetValue(proposed.descriptorId, out var user)
                    ? $"{user.minimum_count}-{user.maximum_count}"
                    : "not included";
                var aiRange = $"{proposed.minimumCount}-{proposed.maximumCount}";
                EditorGUILayout.LabelField($"{proposed.descriptorId} - AI: {aiRange} | User: {userRange}{Changed(aiRange, userRange)}",
                    EditorStyles.wordWrappedMiniLabel);
            }
            foreach (var user in current.Where(value => !proposedIds.Contains(value.Key)).OrderBy(value => value.Key, StringComparer.Ordinal))
                EditorGUILayout.LabelField($"{user.Key} - AI: not included | User: {user.Value.minimum_count}-{user.Value.maximum_count} (changed)",
                    EditorStyles.wordWrappedMiniLabel);
        }

        private static void DrawDifference(string label, float proposed, float current)
        {
            var delta = current - proposed;
            var suffix = Mathf.Abs(delta) < 0.0001f
                ? "same"
                : delta.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
            EditorGUILayout.LabelField($"{label} - AI: {proposed:0.00} | User: {current:0.00} ({suffix})", EditorStyles.wordWrappedMiniLabel);
        }

        private static string Changed(string proposed, string current) =>
            string.Equals(proposed, current, StringComparison.Ordinal) ? " (same)" : " (changed)";

        private static bool CanRegenerate(RoomCompositionPlan plan, SurfaceArrangementSpec spec)
        {
            var current = RoomPreviewManager.Current;
            return current != null && current.Plan == plan && !string.IsNullOrWhiteSpace(spec?.arrangement_id);
        }

        private static SurfaceArrangementProposal SafeLoadProposal()
        {
            try { return SurfaceArrangementProposalStore.Load(); }
            catch (Exception exception)
            {
                Debug.LogWarning($"Could not read the current Surface Arrangement proposal: {exception.Message}");
                return null;
            }
        }

        [Serializable]
        private sealed class ProposalMemberEnvelope
        {
            public ProposalMember[] items;
        }

        [Serializable]
        private sealed class ProposalMember
        {
            public string descriptorId;
            public int minimumCount;
            public int maximumCount;
        }
    }
}
