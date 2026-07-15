using System;
using System.Collections.Generic;
using System.Linq;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    internal sealed class SurfaceArrangementPlacementAnalysis
    {
        public readonly HashSet<PlacedDecorItem> CompatiblePlacements = new();
        public readonly List<ValidationIssue> Issues = new();
    }

    /// <summary>
    /// Structural rules shared by preview lock preservation and final validation. These rules do
    /// not replace OBB/contact validation; they prove that an arrangement placement still belongs
    /// to the current spec and that its support chain terminates at the resolver's target root.
    /// </summary>
    internal static class SurfaceArrangementPlacementRules
    {
        public static IReadOnlyDictionary<string, int> ResolveCounts(SurfaceArrangementSpec spec)
        {
            var members = (spec?.members ?? new List<SurfaceArrangementMemberSpec>())
                .Where(value => value != null)
                .OrderBy(value => value.descriptor_id?.Trim() ?? string.Empty, StringComparer.Ordinal)
                .ToArray();
            var result = members.ToDictionary(
                value => value.descriptor_id.Trim(),
                value => value.minimum_count,
                StringComparer.Ordinal);
            var available = members.Sum(value => value.maximum_count - value.minimum_count);
            var desired = UnityEngine.Mathf.Clamp(UnityEngine.Mathf.RoundToInt(available * spec.Amount), 0, available);
            for (var slot = 0; slot < desired; slot++)
            {
                var selected = members
                    .Where(value => value.selection_weight > 0f && result[value.descriptor_id.Trim()] < value.maximum_count)
                    .OrderByDescending(value => value.selection_weight /
                                                (result[value.descriptor_id.Trim()] - value.minimum_count + 1f))
                    .ThenBy(value => value.descriptor_id?.Trim() ?? string.Empty, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (selected == null) break;
                result[selected.descriptor_id.Trim()]++;
            }
            return result;
        }

        public static SurfaceArrangementPlacementAnalysis Analyze(
            RoomCompositionPlan plan,
            IReadOnlyList<PlacedDecorItem> placements)
        {
            var analysis = new SurfaceArrangementPlacementAnalysis();
            if (plan == null) return analysis;
            var all = (placements ?? Array.Empty<PlacedDecorItem>()).Where(value => value != null).ToArray();
            var arrangementPlacements = all.Where(value => !string.IsNullOrWhiteSpace(value.arrangementId)).ToArray();
            var specGroups = plan.SurfaceArrangements.Where(value => value != null)
                .GroupBy(value => value.arrangement_id?.Trim() ?? string.Empty, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.ToArray(), StringComparer.Ordinal);

            foreach (var unknown in arrangementPlacements
                         .Where(value => !specGroups.ContainsKey(value.arrangementId.Trim()))
                         .OrderBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal))
            {
                Add(analysis, SurfaceArrangementErrorCodes.NoFit,
                    $"Placement '{unknown.placementId}' belongs to deleted arrangement '{unknown.arrangementId}'.",
                    unknown);
            }

            foreach (var pair in specGroups.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                var specId = pair.Key;
                var items = arrangementPlacements
                    .Where(value => string.Equals(value.arrangementId?.Trim(), specId, StringComparison.Ordinal))
                    .OrderBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(value => value.instanceIndex)
                    .ToArray();
                if (pair.Value.Length != 1)
                {
                    foreach (var item in items)
                        Add(analysis, SurfaceArrangementErrorCodes.NoFit,
                            $"Arrangement id '{specId}' is duplicated in the composition plan.", item);
                    continue;
                }

                var spec = pair.Value[0];
                if (!SurfaceArrangementSpecUtility.Validate(spec, out var invalid))
                {
                    if (items.Length == 0)
                        analysis.Issues.Add(new ValidationIssue(SurfaceArrangementErrorCodes.NoFit,
                            ValidationSeverity.Error, $"Arrangement '{specId}' is invalid: {invalid}", specId));
                    foreach (var item in items)
                        Add(analysis, SurfaceArrangementErrorCodes.NoFit,
                            $"Arrangement '{specId}' is invalid: {invalid}", item);
                    continue;
                }

                AnalyzeSpec(analysis, spec, items, all);
            }
            return analysis;
        }

        private static void AnalyzeSpec(
            SurfaceArrangementPlacementAnalysis analysis,
            SurfaceArrangementSpec spec,
            IReadOnlyList<PlacedDecorItem> items,
            IReadOnlyList<PlacedDecorItem> all)
        {
            var counts = ResolveCounts(spec);
            var minimums = spec.members.ToDictionary(
                value => value.descriptor_id.Trim(),
                value => value.minimum_count,
                StringComparer.Ordinal);
            var target = all.Where(value => string.IsNullOrWhiteSpace(value.arrangementId) &&
                                            string.Equals(value.elementId, spec.target_element_id, StringComparison.Ordinal))
                .OrderBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal)
                .FirstOrDefault();
            if (target == null)
            {
                analysis.Issues.Add(new ValidationIssue(SurfaceArrangementErrorCodes.SupportRegionInvalid,
                    ValidationSeverity.Error,
                    $"Arrangement '{spec.arrangement_id}' has no target root for element '{spec.target_element_id}'.",
                    spec.arrangement_id, spec.target_element_id));
            }

            var invalid = new HashSet<PlacedDecorItem>();
            var byId = all.Where(value => !string.IsNullOrWhiteSpace(value.placementId))
                .GroupBy(value => value.placementId, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.ToArray(), StringComparer.Ordinal);
            var duplicateSlots = items.Where(value => value.descriptor != null)
                .GroupBy(value => (value.descriptor.AssetId, value.instanceIndex))
                .Where(value => value.Count() > 1)
                .ToArray();
            foreach (var duplicate in duplicateSlots)
            foreach (var item in duplicate)
            {
                invalid.Add(item);
                Add(analysis, SurfaceArrangementErrorCodes.NoFit,
                    $"Arrangement '{spec.arrangement_id}' contains duplicate slot '{duplicate.Key.AssetId}:{duplicate.Key.instanceIndex}'.",
                    item);
            }

            foreach (var item in items)
            {
                var descriptorId = item.descriptor?.AssetId?.Trim();
                if (string.IsNullOrWhiteSpace(descriptorId) || !counts.TryGetValue(descriptorId, out var resolved))
                {
                    invalid.Add(item);
                    Add(analysis, SurfaceArrangementErrorCodes.NoFit,
                        $"Placement '{item.placementId}' uses a descriptor that is not in arrangement '{spec.arrangement_id}'.",
                        item);
                    continue;
                }
                var expectedId = $"arrangement:{spec.arrangement_id}:{descriptorId}:{item.instanceIndex}";
                if (item.instanceIndex < 0 || item.instanceIndex >= resolved ||
                    !string.Equals(item.placementId, expectedId, StringComparison.Ordinal))
                {
                    invalid.Add(item);
                    Add(analysis, SurfaceArrangementErrorCodes.NoFit,
                        $"Placement '{item.placementId}' exceeds the resolved '{descriptorId}' count ({resolved}) or has a stale slot id.",
                        item);
                }
                if (item.stackLevel < 1 || item.stackLevel > spec.MaxStackHeight)
                {
                    invalid.Add(item);
                    Add(analysis, SurfaceArrangementErrorCodes.StackSupportInsufficient,
                        $"Placement '{item.placementId}' has invalid stack level {item.stackLevel}.", item);
                }
            }

            foreach (var descriptorId in counts.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                var actual = items.Count(value => string.Equals(value.descriptor?.AssetId?.Trim(), descriptorId, StringComparison.Ordinal));
                if (actual < minimums[descriptorId])
                    analysis.Issues.Add(new ValidationIssue(SurfaceArrangementErrorCodes.NoFit,
                        ValidationSeverity.Error,
                        $"Arrangement '{spec.arrangement_id}' resolved '{descriptorId}' to {counts[descriptorId]} but only {actual} placements exist; minimum is {minimums[descriptorId]}.",
                        spec.arrangement_id, descriptorId));
                else if (actual > counts[descriptorId])
                    analysis.Issues.Add(new ValidationIssue(SurfaceArrangementErrorCodes.NoFit,
                        ValidationSeverity.Error,
                        $"Arrangement '{spec.arrangement_id}' has {actual} '{descriptorId}' placements; resolved maximum is {counts[descriptorId]}.",
                        spec.arrangement_id, descriptorId));
            }

            var states = new Dictionary<PlacedDecorItem, int>();
            bool ReachesTarget(PlacedDecorItem item)
            {
                if (invalid.Contains(item) || target == null) return false;
                if (states.TryGetValue(item, out var state))
                {
                    if (state == 1)
                    {
                        invalid.Add(item);
                        Add(analysis, SurfaceArrangementErrorCodes.StackSupportInsufficient,
                            $"Arrangement '{spec.arrangement_id}' contains a cyclic stack support graph.", item);
                        return false;
                    }
                    return state == 2;
                }
                states[item] = 1;
                if (string.IsNullOrWhiteSpace(item.supportPlacementId) ||
                    !byId.TryGetValue(item.supportPlacementId, out var supports) || supports.Length != 1)
                {
                    invalid.Add(item);
                    Add(analysis, SurfaceArrangementErrorCodes.SupportRegionInvalid,
                        $"Placement '{item.placementId}' has a missing or ambiguous support '{item.supportPlacementId}'.", item);
                    states[item] = 3;
                    return false;
                }

                var support = supports[0];
                if (ReferenceEquals(support, target))
                {
                    if (item.stackLevel != 1)
                    {
                        invalid.Add(item);
                        Add(analysis, SurfaceArrangementErrorCodes.StackSupportInsufficient,
                            $"Placement '{item.placementId}' is directly supported by the target root but is not stack level 1.", item, target);
                        states[item] = 3;
                        return false;
                    }
                    states[item] = 2;
                    return true;
                }

                if (string.IsNullOrWhiteSpace(support.arrangementId) ||
                    !string.Equals(support.arrangementId, spec.arrangement_id, StringComparison.Ordinal))
                {
                    invalid.Add(item);
                    Add(analysis, SurfaceArrangementErrorCodes.SupportRegionInvalid,
                        $"Placement '{item.placementId}' does not terminate at target root '{target.placementId}'.", item, support);
                    states[item] = 3;
                    return false;
                }
                if (item.stackLevel != support.stackLevel + 1 || !ReachesTarget(support))
                {
                    invalid.Add(item);
                    Add(analysis, SurfaceArrangementErrorCodes.StackSupportInsufficient,
                        $"Placement '{item.placementId}' has an invalid same-arrangement support chain.", item, support);
                    states[item] = 3;
                    return false;
                }
                states[item] = 2;
                return true;
            }

            foreach (var item in items) ReachesTarget(item);
            foreach (var item in items.Where(value => !invalid.Contains(value) && states.TryGetValue(value, out var state) && state == 2))
                analysis.CompatiblePlacements.Add(item);
        }

        private static void Add(
            SurfaceArrangementPlacementAnalysis analysis,
            string code,
            string message,
            params PlacedDecorItem[] placements)
        {
            analysis.Issues.Add(new ValidationIssue(code, ValidationSeverity.Error, message,
                placements.Where(value => value != null)
                    .Select(value => value.placementId)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
        }
    }
}
