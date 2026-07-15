using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class SurfaceArrangementPacker
    {
        private const int CandidateAttempts = 48;
        private const float ContactEpsilon = 0.0001f;

        public static void Append(LayoutRequest request, LayoutResult result)
        {
            if (request?.Plan?.SurfaceArrangements == null || result == null) return;
            foreach (var spec in request.Plan.SurfaceArrangements.Where(value => value != null)
                         .OrderBy(value => value.arrangement_id ?? string.Empty, StringComparer.Ordinal))
                AppendArrangement(request, result, spec);
        }

        internal static string ValidatePlacement(
            LayoutRequest request,
            SurfaceArrangementSpec spec,
            PlacedDecorItem item,
            PlacedDecorItem support,
            out ContactEvidence evidence)
        {
            evidence = new ContactEvidence { valid = false };
            if (request?.SupportContracts == null)
                return SurfaceArrangementErrorCodes.SupportContractMissing;
            if (spec == null || item?.descriptor?.Geometry == null || support?.descriptor?.Geometry == null)
                return SurfaceArrangementErrorCodes.SupportRegionInvalid;
            if (!request.SupportContracts.TryResolve(item.descriptor.AssetId, support.descriptor.AssetId, out var contract, out var contractCode))
                return contractCode ?? SurfaceArrangementErrorCodes.SupportContractMissing;
            if (!TrySupportSurface(support, contract.Interaction.target_frame, out var surface))
                return SurfaceArrangementErrorCodes.SupportRegionInvalid;
            var subjectFrame = item.descriptor.Geometry.Frame(contract.Interaction.subject_frame);
            if (subjectFrame == null) return SurfaceArrangementErrorCodes.SupportRegionInvalid;

            var contactPoint = item.position + item.rotation * Vector3.Scale(subjectFrame.localPoint, item.scale);
            var signedDistance = Vector3.Dot(contactPoint - surface.Origin, surface.Normal);
            var gap = Mathf.Max(0f, signedDistance);
            var penetration = Mathf.Max(0f, -signedDistance);
            var alignment = Vector3.Dot((item.rotation * subjectFrame.localNormal).normalized, -surface.Normal);
            var x = Vector3.Dot(contactPoint - surface.Origin, surface.Tangent);
            var y = Vector3.Dot(contactPoint - surface.Origin, surface.Bitangent);
            Footprint(subjectFrame, item.rotation, item.scale, surface, out var halfWidth, out var halfHeight);
            var coverage = Coverage(halfWidth, halfHeight, x, y, surface.Size);
            evidence = new ContactEvidence
            {
                surfaceId = "asset:" + support.placementId,
                gap = gap,
                penetration = penetration,
                supportCoverage = coverage,
                directionAlignment = alignment,
                contactPoint = contactPoint,
                valid = false
            };

            var expectedLevel = support.arrangementId == spec.arrangement_id ? support.stackLevel + 1 : 1;
            if (item.stackLevel != expectedLevel || expectedLevel > spec.MaxStackHeight)
                return SurfaceArrangementErrorCodes.StackSupportInsufficient;
            if (penetration > ContactEpsilon)
                return SurfaceArrangementErrorCodes.Overlap;
            if (gap > 0.01f + ContactEpsilon || alignment < 0.95f)
                return SurfaceArrangementErrorCodes.SupportRegionInvalid;

            // The authored edge margin belongs to the root support surface. A stack layer instead
            // proves safety through the stricter 75% footprint coverage rule below; applying the
            // table margin to an equal-sized book would make every valid vertical stack impossible.
            var margin = support.arrangementId == spec.arrangement_id
                ? 0f
                : Mathf.Max(0.04f, Mathf.Max(spec.EdgeMargin, item.descriptor.Clearance));
            var rangeX = surface.Size.x * 0.5f - margin - halfWidth;
            var rangeY = surface.Size.y * 0.5f - margin - halfHeight;
            if (rangeX < -ContactEpsilon || rangeY < -ContactEpsilon ||
                Mathf.Abs(x) > rangeX + ContactEpsilon || Mathf.Abs(y) > rangeY + ContactEpsilon)
                return SurfaceArrangementErrorCodes.OutOfBounds;

            var minimumSupport = support.arrangementId == spec.arrangement_id ? 0.75f : 0.90f;
            if (coverage + ContactEpsilon < minimumSupport)
                return support.arrangementId == spec.arrangement_id
                    ? SurfaceArrangementErrorCodes.StackSupportInsufficient
                    : SurfaceArrangementErrorCodes.OutOfBounds;
            evidence.valid = true;
            return null;
        }

        private static void AppendArrangement(LayoutRequest request, LayoutResult result, SurfaceArrangementSpec spec)
        {
            if (!SurfaceArrangementSpecUtility.Validate(spec, out var invalid))
            {
                Gap(result, spec, SurfaceArrangementErrorCodes.NoFit, invalid, 1);
                return;
            }
            var target = result.Placements.Where(value => value != null && value.elementId == spec.target_element_id)
                .OrderBy(value => value.placementId, StringComparer.Ordinal).FirstOrDefault();
            if (target?.descriptor == null || !TrySupportSurface(target, spec.target_frame_id, out _))
            {
                Gap(result, spec, SurfaceArrangementErrorCodes.SupportRegionInvalid, "target placement or reviewed support region is missing", 1);
                return;
            }
            if (request.SupportContracts == null)
            {
                Gap(result, spec, SurfaceArrangementErrorCodes.SupportContractMissing, "support contract catalog is missing", MinimumTotal(spec));
                return;
            }

            var tasks = new List<ItemTask>();
            var resolvedCounts = ResolveCounts(spec);
            foreach (var member in spec.members.OrderBy(value => value.descriptor_id, StringComparer.Ordinal))
            {
                var descriptor = request.Plan.Catalog.Find(member.descriptor_id);
                if (descriptor?.Prefab == null || descriptor.Geometry == null || !descriptor.Geometry.IsUsable)
                {
                    Gap(result, spec, SurfaceArrangementErrorCodes.SupportContractMissing, $"descriptor '{member.descriptor_id}' is missing or unreviewed", member.minimum_count);
                    return;
                }
                if (!request.SupportContracts.TryResolve(descriptor.AssetId, target.descriptor.AssetId, out var baseContract, out var code))
                {
                    Gap(result, spec, code, $"{descriptor.AssetId} -> {target.descriptor.AssetId}", member.minimum_count);
                    return;
                }
                var count = resolvedCounts[member];
                var frame = descriptor.Geometry.Frame(baseContract.Interaction.subject_frame);
                var area = frame == null ? 0f : frame.size.x * frame.size.y;
                for (var index = 0; index < count; index++)
                    tasks.Add(new ItemTask(member, descriptor, index, index < member.minimum_count, area));
            }

            tasks = tasks.OrderByDescending(value => value.Area)
                .ThenBy(value => value.Descriptor.AssetId, StringComparer.Ordinal)
                .ThenBy(value => value.InstanceIndex).ToList();
            var seed = StableSeed(request.Plan.Seed, spec.arrangement_id, spec.seed_offset);
            for (var taskIndex = 0; taskIndex < tasks.Count; taskIndex++)
            {
                var task = tasks[taskIndex];
                var taskPlacementId = PlacementId(spec, task);
                if (result.Placements.Any(value => value != null && value.locked &&
                                                   string.Equals(value.placementId, taskPlacementId, StringComparison.Ordinal)))
                    continue;
                var supports = new List<PlacedDecorItem> { target };
                supports.AddRange(result.Placements.Where(value => value != null && value.arrangementId == spec.arrangement_id && value.stackLevel < spec.max_stack_height)
                    .OrderBy(value => value.placementId, StringComparer.Ordinal));
                ArrangementCandidate? best = null;
                var sawOverlap = false;
                var sawBounds = false;
                var sawStackSupport = false;
                string supportContractIssue = null;
                foreach (var support in supports)
                {
                    if (!request.SupportContracts.TryResolve(task.Descriptor.AssetId, support.descriptor.AssetId, out var contract, out var supportCode))
                    {
                        if (support != target) supportContractIssue = supportCode ?? SurfaceArrangementErrorCodes.SupportContractMissing;
                        continue;
                    }
                    if (!TrySupportSurface(support, contract.Interaction.target_frame, out var surface)) continue;
                    var stacked = support != target;
                    if (stacked && support.stackLevel >= spec.max_stack_height) continue;
                    for (var attempt = 0; attempt < CandidateAttempts; attempt++)
                    {
                        if (!TryCandidate(spec, task, taskIndex, tasks.Count, support, surface, contract.Interaction, seed, attempt, out var candidate))
                        {
                            if (stacked) sawStackSupport = true;
                            continue;
                        }
                        if (!InsideRoom(request, candidate)) { sawBounds = true; continue; }
                        if (Overlaps(request, result.Placements, candidate)) { sawOverlap = true; continue; }
                        candidate.Score = Score(spec, task, candidate, result.Placements);
                        if (!best.HasValue || candidate.Score > best.Value.Score) best = candidate;
                    }
                }
                if (!best.HasValue)
                {
                    if (!task.Required) continue;
                    var code = sawOverlap
                        ? SurfaceArrangementErrorCodes.Overlap
                        : sawBounds
                            ? SurfaceArrangementErrorCodes.OutOfBounds
                            : !string.IsNullOrWhiteSpace(supportContractIssue)
                                ? supportContractIssue
                                : sawStackSupport
                                    ? SurfaceArrangementErrorCodes.StackSupportInsufficient
                                    : SurfaceArrangementErrorCodes.NoFit;
                    Gap(result, spec, code, $"required {task.Descriptor.AssetId} instance {task.InstanceIndex} did not fit", 1);
                    continue;
                }
                result.Placements.Add(CreatePlacement(spec, task, best.Value));
            }
        }

        private static bool TryCandidate(
            SurfaceArrangementSpec spec,
            ItemTask task,
            int taskIndex,
            int taskCount,
            PlacedDecorItem support,
            SupportSurface surface,
            InteractionSpatialContractPayload interaction,
            int seed,
            int attempt,
            out ArrangementCandidate candidate)
        {
            candidate = default;
            var subjectFrame = task.Descriptor.Geometry.Frame(interaction.subject_frame);
            if (subjectFrame == null || !string.Equals(interaction.target_frame, surface.FrameId, StringComparison.OrdinalIgnoreCase)) return false;
            var scaleValue = Mathf.Clamp(1f, task.Descriptor.MinimumScale, task.Descriptor.MaximumScale);
            var scale = Vector3.one * scaleValue;
            var relativeRotation = SpatialContractArrays.Quaternion(interaction.relative_rotation);
            var baseRotation = support.rotation * relativeRotation;
            var yawLimit = Mathf.Clamp(interaction.angle_tolerance, 0f, 180f);
            var randomYaw = (Unit(seed, taskIndex, attempt, 2) * 2f - 1f) * yawLimit;
            var yaw = randomYaw * (1f - spec.Orderliness);
            var rotation = Quaternion.AngleAxis(yaw, surface.Normal) * baseRotation;
            var subjectNormal = (rotation * subjectFrame.localNormal).normalized;
            if (Vector3.Dot(subjectNormal, -surface.Normal) < 0.95f) return false;

            Footprint(subjectFrame, rotation, scale, surface, out var halfWidth, out var halfHeight);
            var margin = support.arrangementId == spec.arrangement_id
                ? 0f
                : Mathf.Max(0.04f, Mathf.Max(spec.EdgeMargin, task.Descriptor.Clearance));
            var rangeX = surface.Size.x * 0.5f - margin - halfWidth;
            var rangeY = surface.Size.y * 0.5f - margin - halfHeight;
            if (rangeX < -ContactEpsilon || rangeY < -ContactEpsilon) return false;
            rangeX = Mathf.Max(0f, rangeX); rangeY = Mathf.Max(0f, rangeY);

            var columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(taskCount)));
            var row = taskIndex / columns;
            var column = taskIndex % columns;
            var rows = Mathf.Max(1, Mathf.CeilToInt(taskCount / (float)columns));
            var gridX = columns == 1 ? 0f : Mathf.Lerp(-rangeX, rangeX, column / (float)(columns - 1));
            var gridY = rows == 1 ? 0f : Mathf.Lerp(-rangeY, rangeY, row / (float)(rows - 1));
            var randomX = Mathf.Lerp(-rangeX, rangeX, Unit(seed, taskIndex, attempt, 0));
            var randomY = Mathf.Lerp(-rangeY, rangeY, Unit(seed, taskIndex, attempt, 1));
            var groupX = Mathf.Lerp(-rangeX, rangeX, Unit(seed, StableText(task.Member.affinity_group), 0, 0));
            var groupY = Mathf.Lerp(-rangeY, rangeY, Unit(seed, StableText(task.Member.affinity_group), 0, 1));
            var x = Mathf.Lerp(randomX, gridX, spec.Orderliness);
            var y = Mathf.Lerp(randomY, gridY, spec.Orderliness);
            x = Mathf.Lerp(x, groupX, spec.Grouping * 0.7f);
            y = Mathf.Lerp(y, groupY, spec.Grouping * 0.7f);

            var relativePosition = SpatialContractArrays.Vector(interaction.relative_position);
            var approvedContact = relativePosition + relativeRotation * Vector3.Scale(subjectFrame.localPoint, scale);
            var inverseSupportRotation = Quaternion.Inverse(support.rotation);
            var localOrigin = DivideScale(inverseSupportRotation * (surface.Origin - support.position), support.scale);
            var localNormal = Vector3.Scale(inverseSupportRotation * surface.Normal, support.scale).normalized;
            var signedApprovedGap = Vector3.Dot(approvedContact - localOrigin, localNormal);
            if (signedApprovedGap < -ContactEpsilon || signedApprovedGap > 0.01f + ContactEpsilon) return false;
            var gap = Mathf.Max(0f, signedApprovedGap);
            var contactPoint = surface.Origin + surface.Tangent * x + surface.Bitangent * y;
            var position = contactPoint + surface.Normal * gap - rotation * Vector3.Scale(subjectFrame.localPoint, scale);
            var boxes = SpatialGeometryUtility.BuildWorldObbs(task.Descriptor, position, rotation, scale);
            var bounds = SpatialGeometryUtility.CombinedAabb(boxes);
            var coverage = Coverage(halfWidth, halfHeight, x, y, surface.Size);
            var minimumSupport = support.arrangementId == spec.arrangement_id ? 0.75f : 0.90f;
            if (coverage + ContactEpsilon < minimumSupport || gap < -ContactEpsilon || gap > 0.01f + ContactEpsilon) return false;
            candidate = new ArrangementCandidate(
                position,
                rotation,
                scale,
                bounds,
                boxes,
                support,
                contactPoint + surface.Normal * gap,
                gap,
                coverage,
                x,
                y,
                Mathf.Clamp01(Mathf.Abs(yaw) / 180f));
            return true;
        }

        private static bool TrySupportSurface(PlacedDecorItem placement, string frameId, out SupportSurface surface)
        {
            surface = default;
            if (placement?.descriptor?.Geometry == null || !string.Equals(frameId, "top", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrWhiteSpace(placement.arrangementId))
            {
                var frame = placement.descriptor.Geometry.Frame(frameId);
                if (frame == null) return false;
                var normal = (placement.rotation * frame.localNormal).normalized;
                if (Vector3.Dot(normal, Vector3.up) < 0.95f) return false;
                var localTangent = frame.localTangent.sqrMagnitude < 0.001f ? Vector3.right : frame.localTangent.normalized;
                var localBitangent = Vector3.Cross(frame.localNormal.normalized, localTangent).normalized;
                var tangentVector = placement.rotation * Vector3.Scale(localTangent * frame.size.x, placement.scale);
                var bitangentVector = placement.rotation * Vector3.Scale(localBitangent * frame.size.y, placement.scale);
                var tangent = Vector3.ProjectOnPlane(tangentVector, normal).normalized;
                var bitangent = Vector3.ProjectOnPlane(bitangentVector, normal).normalized;
                if (tangent.sqrMagnitude < 0.9f || bitangent.sqrMagnitude < 0.9f) return false;
                surface = new SupportSurface(
                    frameId,
                    placement.position + placement.rotation * Vector3.Scale(frame.localPoint, placement.scale),
                    normal,
                    tangent,
                    bitangent,
                    new Vector2(tangentVector.magnitude, bitangentVector.magnitude));
                return surface.Size.x > 0.001f && surface.Size.y > 0.001f;
            }
            WorldObb? best = null;
            var bestHeight = float.NegativeInfinity;
            var bestArea = 0f;
            foreach (var box in SpatialGeometryUtility.BuildWorldObbs(placement.descriptor, placement.position, placement.rotation, placement.scale))
            for (var axisIndex = 0; axisIndex < 3; axisIndex++)
            {
                var axis = box.Axis(axisIndex);
                var alignment = Mathf.Abs(Vector3.Dot(axis, Vector3.up));
                if (alignment < 0.95f) continue;
                var sign = Vector3.Dot(axis, Vector3.up) >= 0f ? 1f : -1f;
                var height = (box.Center + axis * sign * box.Extent(axisIndex)).y;
                var other = Enumerable.Range(0, 3).Where(value => value != axisIndex).ToArray();
                var area = box.Extent(other[0]) * box.Extent(other[1]) * 4f;
                if (height > bestHeight + 0.005f || Mathf.Abs(height - bestHeight) <= 0.005f && area > bestArea)
                { best = box; bestHeight = height; bestArea = area; }
            }
            if (!best.HasValue) return false;
            var chosen = best.Value;
            var verticalAxis = Enumerable.Range(0, 3).OrderByDescending(value => Mathf.Abs(Vector3.Dot(chosen.Axis(value), Vector3.up))).First();
            var planar = Enumerable.Range(0, 3).Where(value => value != verticalAxis).ToArray();
            var tangent = chosen.Axis(planar[0]);
            tangent = Vector3.ProjectOnPlane(tangent, Vector3.up).normalized;
            if (tangent.sqrMagnitude < 0.9f) return false;
            var bitangent = Vector3.Cross(Vector3.up, tangent).normalized;
            var extentA = chosen.Extent(planar[0]);
            var extentB = chosen.Extent(planar[1]);
            var signUp = Vector3.Dot(chosen.Axis(verticalAxis), Vector3.up) >= 0f ? 1f : -1f;
            var origin = chosen.Center + chosen.Axis(verticalAxis) * signUp * chosen.Extent(verticalAxis);
            surface = new SupportSurface("top", origin, Vector3.up, tangent, bitangent, new Vector2(extentA * 2f, extentB * 2f));
            return surface.Size.x > 0.001f && surface.Size.y > 0.001f;
        }

        private static bool InsideRoom(LayoutRequest request, ArrangementCandidate candidate)
        {
            if (!request.Plan.Room.ContainsBounds(candidate.Bounds)) return false;
            if (request.Plan.Room.KeepClearZones.Any(zone => zone != null && zone.WorldBounds.Intersects(candidate.Bounds))) return false;
            if (request.AuthoringContext?.obstacles == null) return true;
            foreach (var obstacle in request.AuthoringContext.obstacles.Where(value => value != null && value.policy != RoomObstaclePolicy.Ignore))
            {
                if (!obstacle.IsUsable) return false;
                if (SpatialGeometryUtility.Intersects(candidate.Boxes, SpatialGeometryUtility.BuildWorldObbs(obstacle))) return false;
            }
            return true;
        }

        private static bool Overlaps(LayoutRequest request, IReadOnlyList<PlacedDecorItem> placements, ArrangementCandidate candidate)
        {
            foreach (var existing in placements)
            {
                if (existing?.descriptor == null) continue;
                var boxes = SpatialGeometryUtility.BuildWorldObbs(existing.descriptor, existing.position, existing.rotation, existing.scale);
                if (SpatialGeometryUtility.Intersects(candidate.Boxes, boxes)) return true;
            }
            return false;
        }

        private static float Score(SurfaceArrangementSpec spec, ItemTask task, ArrangementCandidate candidate, IReadOnlyList<PlacedDecorItem> placements)
        {
            var alignment = 1f - candidate.RotationVariation;
            var sameGroup = placements.Where(value => value?.descriptor != null &&
                                                       value.arrangementId == spec.arrangement_id &&
                                                       value.affinityGroup == task.Member.affinity_group).ToArray();
            var nearest = sameGroup.Length == 0 ? 1f : sameGroup.Min(value => Vector3.Distance(value.position, candidate.Position));
            var grouping = 1f / (1f + nearest);
            var stacking = candidate.Support.arrangementId == spec.arrangement_id ? 1f : 0f;
            var balance = 1f / (1f + Mathf.Abs(candidate.X) + Mathf.Abs(candidate.Y));
            return 2f * spec.Amount + 2f * spec.Orderliness * alignment + 1.5f * spec.Orderliness * Mathf.Clamp01(nearest) +
                   1.5f * (1f - spec.Orderliness) * candidate.RotationVariation + 2f * spec.Grouping * grouping +
                   2f * spec.Stacking * stacking + 0.75f * balance;
        }

        private static PlacedDecorItem CreatePlacement(SurfaceArrangementSpec spec, ItemTask task, ArrangementCandidate candidate)
        {
            var level = candidate.Support.arrangementId == spec.arrangement_id ? candidate.Support.stackLevel + 1 : 1;
            var evidence = new ContactEvidence
            {
                surfaceId = "asset:" + candidate.Support.placementId,
                gap = candidate.Gap,
                penetration = 0f,
                supportCoverage = candidate.Coverage,
                directionAlignment = 1f,
                contactPoint = candidate.ContactPoint,
                valid = true
            };
            return new PlacedDecorItem
            {
                placementId = PlacementId(spec, task),
                elementId = $"arrangement:{spec.arrangement_id}",
                instanceIndex = task.InstanceIndex,
                role = ResolveRole(task.Descriptor),
                relation = CompositionRelation.Supports,
                descriptor = task.Descriptor,
                position = candidate.Position,
                rotation = candidate.Rotation,
                scale = candidate.Scale,
                worldBounds = candidate.Bounds,
                surfaceId = evidence.surfaceId,
                contactEvidence = evidence,
                surfaceIds = new List<string> { evidence.surfaceId },
                contactEvidenceSet = new List<ContactEvidence> { evidence },
                arrangementId = spec.arrangement_id,
                affinityGroup = task.Member.affinity_group,
                supportPlacementId = candidate.Support.placementId,
                stackLevel = level
            };
        }

        private static DecorRole ResolveRole(DecorAssetDescriptor descriptor)
        {
            if (descriptor.HasRole(DecorRole.StoryEvidence)) return DecorRole.StoryEvidence;
            if (descriptor.HasRole(DecorRole.Clutter)) return DecorRole.Clutter;
            if (descriptor.HasRole(DecorRole.Support)) return DecorRole.Support;
            return descriptor.Roles.Count > 0 ? descriptor.Roles[0] : DecorRole.Clutter;
        }

        private static Dictionary<SurfaceArrangementMemberSpec, int> ResolveCounts(SurfaceArrangementSpec spec)
        {
            var members = spec.members.Where(value => value != null)
                .OrderBy(value => value.descriptor_id, StringComparer.Ordinal).ToArray();
            var result = members.ToDictionary(value => value, value => value.minimum_count);
            var available = members.Sum(value => value.maximum_count - value.minimum_count);
            var desired = Mathf.Clamp(Mathf.RoundToInt(available * spec.Amount), 0, available);
            for (var slot = 0; slot < desired; slot++)
            {
                var selected = members
                    .Where(value => value.selection_weight > 0f && result[value] < value.maximum_count)
                    .OrderByDescending(value => value.selection_weight / (result[value] - value.minimum_count + 1f))
                    .ThenBy(value => value.descriptor_id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (selected == null) break;
                result[selected]++;
            }
            return result;
        }

        private static string PlacementId(SurfaceArrangementSpec spec, ItemTask task) =>
            $"arrangement:{spec.arrangement_id}:{task.Descriptor.AssetId}:{task.InstanceIndex}";

        private static void Footprint(ContactFrame frame, Quaternion rotation, Vector3 scale, SupportSurface surface, out float halfWidth, out float halfHeight)
        {
            var tangent = frame.localTangent.sqrMagnitude < 0.001f ? Vector3.right : frame.localTangent.normalized;
            var normal = frame.localNormal.sqrMagnitude < 0.001f ? Vector3.down : frame.localNormal.normalized;
            var bitangent = Vector3.Cross(normal, tangent).normalized;
            var worldTangent = rotation * Vector3.Scale(tangent * frame.size.x * 0.5f, scale);
            var worldBitangent = rotation * Vector3.Scale(bitangent * frame.size.y * 0.5f, scale);
            halfWidth = Mathf.Abs(Vector3.Dot(worldTangent, surface.Tangent)) + Mathf.Abs(Vector3.Dot(worldBitangent, surface.Tangent));
            halfHeight = Mathf.Abs(Vector3.Dot(worldTangent, surface.Bitangent)) + Mathf.Abs(Vector3.Dot(worldBitangent, surface.Bitangent));
        }

        private static float Coverage(float halfWidth, float halfHeight, float x, float y, Vector2 target)
        {
            var width = halfWidth * 2f; var height = halfHeight * 2f;
            if (width <= ContactEpsilon || height <= ContactEpsilon) return 0f;
            var overlapX = Mathf.Max(0f, Mathf.Min(x + halfWidth, target.x * 0.5f) - Mathf.Max(x - halfWidth, -target.x * 0.5f));
            var overlapY = Mathf.Max(0f, Mathf.Min(y + halfHeight, target.y * 0.5f) - Mathf.Max(y - halfHeight, -target.y * 0.5f));
            return Mathf.Clamp01(overlapX * overlapY / (width * height));
        }

        private static Vector3 DivideScale(Vector3 value, Vector3 scale) => new(
            Mathf.Abs(scale.x) < ContactEpsilon ? 0f : value.x / scale.x,
            Mathf.Abs(scale.y) < ContactEpsilon ? 0f : value.y / scale.y,
            Mathf.Abs(scale.z) < ContactEpsilon ? 0f : value.z / scale.z);

        private static int MinimumTotal(SurfaceArrangementSpec spec) => spec?.members?.Sum(value => value?.minimum_count ?? 0) ?? 0;
        private static void Gap(LayoutResult result, SurfaceArrangementSpec spec, string code, string message, int count) => result.AssetGaps.gaps.Add(new AssetGap
        {
            code = code,
            targetId = spec?.arrangement_id,
            role = DecorRole.Clutter,
            reason = $"{code}: arrangement '{spec?.arrangement_id}' {message}",
            requestedCount = Mathf.Max(1, count),
            availableCount = 0
        });

        private static int StableSeed(int planSeed, string arrangementId, long offset)
        {
            unchecked
            {
                var foldedOffset = (int)offset ^ (int)(offset >> 32);
                return planSeed * 486187739 ^ StableText(arrangementId) ^ foldedOffset * 16777619;
            }
        }
        private static int StableText(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var item in Encoding.UTF8.GetBytes(value ?? string.Empty)) { hash ^= item; hash *= 16777619; }
                return (int)hash;
            }
        }
        private static float Unit(int seed, int a, int b, int c)
        {
            unchecked
            {
                uint value = (uint)seed ^ (uint)(a * 374761393) ^ (uint)(b * 668265263) ^ (uint)(c * 1442695041);
                value ^= value >> 13; value *= 1274126177; value ^= value >> 16;
                return (value & 0x00ffffff) / 16777216f;
            }
        }

        private readonly struct ItemTask
        {
            public readonly SurfaceArrangementMemberSpec Member; public readonly DecorAssetDescriptor Descriptor;
            public readonly int InstanceIndex; public readonly bool Required; public readonly float Area;
            public ItemTask(SurfaceArrangementMemberSpec member, DecorAssetDescriptor descriptor, int index, bool required, float area)
            { Member = member; Descriptor = descriptor; InstanceIndex = index; Required = required; Area = area; }
        }
        private readonly struct SupportSurface
        {
            public readonly string FrameId; public readonly Vector3 Origin, Normal, Tangent, Bitangent; public readonly Vector2 Size;
            public SupportSurface(string id, Vector3 origin, Vector3 normal, Vector3 tangent, Vector3 bitangent, Vector2 size)
            { FrameId = id; Origin = origin; Normal = normal; Tangent = tangent; Bitangent = bitangent; Size = size; }
        }
        private struct ArrangementCandidate
        {
            public Vector3 Position; public Quaternion Rotation; public Vector3 Scale; public Bounds Bounds;
            public IReadOnlyList<WorldObb> Boxes; public PlacedDecorItem Support; public Vector3 ContactPoint;
            public float Gap, Coverage, X, Y, RotationVariation, Score;
            public ArrangementCandidate(Vector3 position, Quaternion rotation, Vector3 scale, Bounds bounds, IReadOnlyList<WorldObb> boxes, PlacedDecorItem support, Vector3 contactPoint, float gap, float coverage, float x, float y, float rotationVariation)
            { Position = position; Rotation = rotation; Scale = scale; Bounds = bounds; Boxes = boxes; Support = support; ContactPoint = contactPoint; Gap = gap; Coverage = coverage; X = x; Y = y; RotationVariation = rotationVariation; Score = 0f; }
        }
    }
}
