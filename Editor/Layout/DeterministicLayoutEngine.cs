using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class DeterministicLayoutEngine
    {
        private const int CandidateAttempts = 72;

        public static LayoutResult Generate(RoomCompositionPlan plan, IReadOnlyList<PlacedDecorItem> lockedPlacements = null)
        {
            return Generate(new LayoutRequest(plan, lockedPlacements));
        }

        public static LayoutResult Generate(LayoutRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var plan = request.Plan;
            var lockedPlacements = request.LockedPlacements;
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (plan.Room == null || plan.Room.AuthoringBounds == null) throw new InvalidOperationException("The composition plan needs a room with authoring bounds.");
            if (plan.Catalog == null) throw new InvalidOperationException("The composition plan needs a decor catalog.");

            var result = new LayoutResult();
            if (lockedPlacements != null)
            {
                foreach (var locked in lockedPlacements.Where(item => item != null && item.locked))
                {
                    result.Placements.Add(ClonePlacement(locked));
                }
            }

            var random = new System.Random(plan.Seed);
            var orderedElements = plan.Elements
                .Where(element => element != null)
                .OrderBy(element => RoleOrder(element.role))
                .ThenBy(element => element.elementId, StringComparer.Ordinal)
                .ToList();
            var activeStyleSet = ResolveStyleSet(plan, orderedElements);

            foreach (var element in orderedElements)
            {
                var descriptor = plan.Catalog.Find(element.descriptorId);
                if (descriptor == null || descriptor.Prefab == null)
                {
                    AddGap(result, element, activeStyleSet, "The requested descriptor or prefab is missing.", 0);
                    continue;
                }

                if (!descriptor.HasRole(element.role))
                {
                    AddGap(result, element, activeStyleSet, $"The asset is not reviewed for the {element.role} role.", 0);
                    continue;
                }

                if (descriptor.Geometry == null || !descriptor.Geometry.IsUsable)
                {
                    AddGap(result, element, activeStyleSet, "GEOMETRY_UNREVIEWED: the asset geometry profile must be reviewed before placement.", 0);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(activeStyleSet) && !string.Equals(descriptor.StyleSet, activeStyleSet, StringComparison.OrdinalIgnoreCase))
                {
                    AddGap(result, element, activeStyleSet, $"Style set '{descriptor.StyleSet}' does not match '{activeStyleSet}'.", 0);
                    continue;
                }

                var densityMultiplier = element.role is DecorRole.Clutter or DecorRole.DecalCue
                    ? Mathf.Lerp(0.5f, 1.5f, plan.Density)
                    : 1f;
                var requestedCount = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(1, element.count) * densityMultiplier), 1, descriptor.MaximumInstancesPerRoom);
                for (var instanceIndex = 0; instanceIndex < requestedCount; instanceIndex++)
                {
                    if (result.Placements.Any(item => item.elementId == element.elementId && item.instanceIndex == instanceIndex && item.locked))
                        continue;

                    var placed = TryPlace(plan, request.AuthoringContext, element, descriptor, instanceIndex, random, result.Placements);
                    if (placed != null) result.Placements.Add(placed);
                    else AddGap(result, element, activeStyleSet, "No collision-free candidate satisfied the room and composition constraints.", 0, 1);
                }
            }

            return result;
        }

        private static PlacedDecorItem TryPlace(RoomCompositionPlan plan, RoomAuthoringContext authoringContext, CompositionElement element, DecorAssetDescriptor descriptor, int instanceIndex, System.Random random, IReadOnlyList<PlacedDecorItem> placed)
        {
            Candidate? best = null;
            for (var attempt = 0; attempt < CandidateAttempts; attempt++)
            {
                if (!TryCreateCandidate(plan.Room, authoringContext, element, descriptor, instanceIndex, attempt, random, placed, out var candidate)) continue;
                if (!IsCandidateValid(plan.Room, authoringContext, descriptor, element, candidate, placed)) continue;

                candidate.Score = ScoreCandidate(plan.Room, element, candidate, placed);
                if (!best.HasValue || candidate.Score > best.Value.Score) best = candidate;
            }

            if (!best.HasValue) return null;
            var chosen = best.Value;
            return new PlacedDecorItem
            {
                placementId = $"{element.elementId}:{instanceIndex}",
                elementId = element.elementId,
                instanceIndex = instanceIndex,
                role = element.role,
                relation = element.relation,
                descriptor = descriptor,
                position = chosen.Position,
                rotation = chosen.Rotation,
                scale = chosen.Scale,
                worldBounds = chosen.Bounds,
                surfaceId = chosen.Surface != null ? chosen.Surface.SurfaceId : string.Empty,
                contactEvidence = chosen.Contact,
                surfaceIds = chosen.Surfaces.Select(value => value.SurfaceId).ToList(),
                contactEvidenceSet = chosen.Contacts.ToList(),
                locked = element.locked
            };
        }

        private static bool TryCreateCandidate(ConceptRoom room, RoomAuthoringContext authoringContext, CompositionElement element, DecorAssetDescriptor descriptor, int instanceIndex, int attempt, System.Random random, IReadOnlyList<PlacedDecorItem> placed, out Candidate candidate)
        {
            candidate = default;
            var box = room.AuthoringBounds;
            var scalar = Mathf.Lerp(descriptor.MinimumScale, descriptor.MaximumScale, (float)random.NextDouble());
            var scale = Vector3.one * scalar;
            var yaw = ResolveYaw(descriptor, random);
            var localPoint = SampleLocalPoint(box, element.preferredZone, descriptor.Surface, random);
            var rotation = box.transform.rotation * Quaternion.Euler(0f, yaw, 0f);
            var worldPoint = box.transform.TransformPoint(localPoint);

            var anchor = FindAnchor(element, placed);
            var usesRelationAnchor = anchor != null && element.relation is CompositionRelation.Surrounds or CompositionRelation.Supports or CompositionRelation.ScatteredNear or CompositionRelation.Faces;
            if (usesRelationAnchor)
            {
                var angle = (instanceIndex * 137.5f + attempt * 47f + (float)random.NextDouble() * 30f) * Mathf.Deg2Rad;
                var distance = Mathf.Max(0.2f, element.spacing) * Mathf.Lerp(0.75f, 1.25f, (float)random.NextDouble());
                worldPoint = anchor.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
                if (element.relation == CompositionRelation.Faces)
                {
                    var direction = anchor.position - worldPoint;
                    direction.y = 0f;
                    if (direction.sqrMagnitude > 0.001f) rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
                }
            }

            var profile = descriptor.Geometry;
            var rules = profile.EffectiveContacts
                .Where(value => value != null && value.requirement != ContactRequirement.FreeStanding)
                .OrderBy(ContactOrder)
                .ToArray();
            var targetSurfaces = new List<RoomSurface>();
            var contacts = new List<ContactEvidence>();
            if (rules.Length > 0)
            {
                var primary = rules[0];
                var surfaceType = SurfaceTypeFor(primary.requirement);
                var surfaces = ReviewedSurfaces(room, authoringContext, surfaceType, element.preferredSurfaceId);
                if (surfaces.Length == 0) return false;
                var targetSurface = surfaces[(attempt + instanceIndex) % surfaces.Length];
                var frame = profile.FrameFor(primary);
                rotation = SpatialGeometryUtility.AlignContactFrame(profile, primary, targetSurface);
                if (surfaceType == RoomSurfaceType.Floor) rotation = Quaternion.AngleAxis(yaw, targetSurface.Normal) * rotation;
                var edgePadding = Mathf.Max(0.25f, descriptor.Clearance);
                var halfWidth = Mathf.Max(0f, targetSurface.Size.x * 0.5f - frame.size.x * scalar * 0.5f - edgePadding);
                var halfHeight = Mathf.Max(0f, targetSurface.Size.y * 0.5f - frame.size.y * scalar * 0.5f - edgePadding);
                var horizontal = usesRelationAnchor
                    ? Mathf.Clamp(Vector3.Dot(worldPoint - targetSurface.Origin, targetSurface.Tangent), -halfWidth, halfWidth)
                    : Mathf.Lerp(-halfWidth, halfWidth, (float)random.NextDouble());
                var vertical = usesRelationAnchor
                    ? Mathf.Clamp(Vector3.Dot(worldPoint - targetSurface.Origin, targetSurface.Bitangent), -halfHeight, halfHeight)
                    : Mathf.Lerp(-halfHeight, halfHeight, (float)random.NextDouble());
                var surfacePoint = targetSurface.Point(horizontal, vertical);
                var targetGap = TargetGap(primary);
                worldPoint = SpatialGeometryUtility.PlaceContactAtSurface(profile, frame, targetSurface, surfacePoint, rotation, scale, targetGap);
                targetSurfaces.Add(targetSurface);

                for (var ruleIndex = 1; ruleIndex < rules.Length; ruleIndex++)
                {
                    var secondary = rules[ruleIndex];
                    if (!TryAttachSecondary(room, authoringContext, descriptor, secondary, rotation, scale, targetSurfaces, ref worldPoint, out var secondarySurface)) return false;
                    targetSurfaces.Add(secondarySurface);
                }

                for (var ruleIndex = 0; ruleIndex < rules.Length; ruleIndex++)
                {
                    var evidence = SpatialGeometryUtility.EvaluateContact(descriptor, rules[ruleIndex], worldPoint, rotation, scale, targetSurfaces[ruleIndex]);
                    if (!evidence.valid) return false;
                    contacts.Add(evidence);
                }
            }

            var boxes = SpatialGeometryUtility.BuildWorldObbs(descriptor, worldPoint, rotation, scale);
            var bounds = SpatialGeometryUtility.CombinedAabb(boxes);
            candidate = new Candidate(worldPoint, rotation, scale, bounds, targetSurfaces, contacts, 0f);
            return true;
        }

        private static bool TryAttachSecondary(ConceptRoom room, RoomAuthoringContext authoringContext, DecorAssetDescriptor descriptor, ContactRules rules, Quaternion rotation, Vector3 scale, IReadOnlyList<RoomSurface> existingSurfaces, ref Vector3 position, out RoomSurface targetSurface)
        {
            targetSurface = null;
            var frame = descriptor.Geometry.FrameFor(rules);
            foreach (var candidateSurface in ReviewedSurfaces(room, authoringContext, SurfaceTypeFor(rules.requirement)))
            {
                var contactPoint = position + rotation * Vector3.Scale(frame.localPoint, scale);
                var signedDistance = Vector3.Dot(contactPoint - candidateSurface.Origin, candidateSurface.Normal);
                var adjusted = position + candidateSurface.Normal * (TargetGap(rules) - signedDistance);
                var evidence = SpatialGeometryUtility.EvaluateContact(descriptor, rules, adjusted, rotation, scale, candidateSurface);
                if (!evidence.valid) continue;

                var previousStillValid = true;
                var effectiveRules = descriptor.Geometry.EffectiveContacts
                    .Where(value => value != null && value.requirement != ContactRequirement.FreeStanding)
                    .OrderBy(ContactOrder)
                    .ToArray();
                for (var index = 0; index < existingSurfaces.Count; index++)
                {
                    if (index >= effectiveRules.Length || !SpatialGeometryUtility.EvaluateContact(descriptor, effectiveRules[index], adjusted, rotation, scale, existingSurfaces[index]).valid)
                    {
                        previousStillValid = false;
                        break;
                    }
                }
                if (!previousStillValid) continue;
                position = adjusted;
                targetSurface = candidateSurface;
                return true;
            }
            return false;
        }

        private static RoomSurface[] ReviewedSurfaces(ConceptRoom room, RoomAuthoringContext authoringContext, RoomSurfaceType type, string preferredSurfaceId = null)
        {
            var contextSurfaces = authoringContext?.surfaces?.Where(value => value != null).ToArray();
            var surfaces = contextSurfaces != null && contextSurfaces.Length > 0
                ? contextSurfaces.Where(value => value.Reviewed && value.Supported && value.SurfaceType == type)
                    .OrderBy(value => value.SurfaceId, StringComparer.Ordinal).ToArray()
                : room.GetReviewedSurfaces(type).OrderBy(value => value.SurfaceId, StringComparer.Ordinal).ToArray();
            if (string.IsNullOrWhiteSpace(preferredSurfaceId)) return surfaces;
            return surfaces.Where(value => string.Equals(value.SurfaceId, preferredSurfaceId, StringComparison.Ordinal)).ToArray();
        }

        private static RoomSurfaceType SurfaceTypeFor(ContactRequirement requirement) => requirement switch
        {
            ContactRequirement.WallBacked or ContactRequirement.WallMounted => RoomSurfaceType.Wall,
            ContactRequirement.CeilingMounted => RoomSurfaceType.Ceiling,
            _ => RoomSurfaceType.Floor
        };

        private static int ContactOrder(ContactRules rules) => rules.requirement switch
        {
            ContactRequirement.WallBacked or ContactRequirement.WallMounted => 0,
            ContactRequirement.FloorSupported => 1,
            ContactRequirement.CeilingMounted => 2,
            _ => 3
        };

        private static float TargetGap(ContactRules rules) => rules.requirement == ContactRequirement.FloorSupported
            ? rules.minimumGap
            : (rules.minimumGap + rules.maximumGap) * 0.5f;

        private static Vector3 SampleLocalPoint(BoxCollider box, PreferredZone zone, PlacementSurface surface, System.Random random)
        {
            var min = box.center - box.size * 0.5f;
            var max = box.center + box.size * 0.5f;
            float Range(float a, float b) => Mathf.Lerp(a, b, (float)random.NextDouble());

            var x = Range(min.x, max.x);
            var z = Range(min.z, max.z);
            switch (zone)
            {
                case PreferredZone.Focal:
                    x = Range(box.center.x - box.size.x * 0.18f, box.center.x + box.size.x * 0.18f);
                    z = Range(box.center.z, box.center.z + box.size.z * 0.3f);
                    break;
                case PreferredZone.Center:
                    x = Range(box.center.x - box.size.x * 0.25f, box.center.x + box.size.x * 0.25f);
                    z = Range(box.center.z - box.size.z * 0.25f, box.center.z + box.size.z * 0.25f);
                    break;
                case PreferredZone.Perimeter:
                    SetPerimeter(ref x, ref z, min, max, random);
                    break;
                case PreferredZone.Corner:
                    x = random.Next(0, 2) == 0 ? Range(min.x, min.x + box.size.x * 0.18f) : Range(max.x - box.size.x * 0.18f, max.x);
                    z = random.Next(0, 2) == 0 ? Range(min.z, min.z + box.size.z * 0.18f) : Range(max.z - box.size.z * 0.18f, max.z);
                    break;
            }

            if (surface == PlacementSurface.Wall) SetPerimeter(ref x, ref z, min, max, random);
            var y = surface == PlacementSurface.Ceiling ? max.y : surface == PlacementSurface.Wall ? Range(min.y + box.size.y * 0.2f, max.y - box.size.y * 0.2f) : min.y;
            return new Vector3(x, y, z);
        }

        private static void SetPerimeter(ref float x, ref float z, Vector3 min, Vector3 max, System.Random random)
        {
            switch (random.Next(0, 4))
            {
                case 0: x = min.x; break;
                case 1: x = max.x; break;
                case 2: z = min.z; break;
                default: z = max.z; break;
            }
        }

        private static Quaternion ResolveWallRotation(BoxCollider box, Vector3 localPoint)
        {
            var local = localPoint - box.center;
            var normalizedX = Mathf.Abs(local.x) / Mathf.Max(0.001f, box.size.x * 0.5f);
            var normalizedZ = Mathf.Abs(local.z) / Mathf.Max(0.001f, box.size.z * 0.5f);
            Vector3 localInward;
            if (normalizedX > normalizedZ) localInward = local.x < 0f ? Vector3.right : Vector3.left;
            else localInward = local.z < 0f ? Vector3.forward : Vector3.back;
            return Quaternion.LookRotation(box.transform.TransformDirection(localInward), box.transform.up);
        }

        private static bool IsCandidateValid(ConceptRoom room, RoomAuthoringContext authoringContext, DecorAssetDescriptor descriptor, CompositionElement element, Candidate candidate, IReadOnlyList<PlacedDecorItem> placed)
        {
            if (!room.ContainsBounds(candidate.Bounds, candidate.Surface != null)) return false;

            foreach (var zone in room.KeepClearZones)
            {
                if (zone != null && zone.WorldBounds.Intersects(candidate.Bounds)) return false;
            }

            foreach (var existing in placed)
            {
                if (existing == null) continue;
                var padding = Mathf.Max(descriptor.Clearance, existing.descriptor != null ? existing.descriptor.Clearance : 0f);
                var candidateBoxes = SpatialGeometryUtility.BuildWorldObbs(descriptor, candidate.Position, candidate.Rotation, candidate.Scale, padding);
                var existingBoxes = SpatialGeometryUtility.BuildWorldObbs(existing.descriptor, existing.position, existing.rotation, existing.scale, padding);
                if (SpatialGeometryUtility.Intersects(existingBoxes, candidateBoxes)) return false;
                if (descriptor.ForbiddenAssetIds.Contains(existing.descriptor != null ? existing.descriptor.AssetId : string.Empty)) return false;
                if (existing.descriptor != null && existing.descriptor.ForbiddenAssetIds.Contains(descriptor.AssetId)) return false;
            }

            if (IntersectsRoomGeometry(authoringContext, descriptor, candidate)) return false;

            if (element.relation == CompositionRelation.Avoids)
            {
                var anchor = FindAnchor(element, placed);
                if (anchor != null && Vector3.Distance(anchor.position, candidate.Position) < Mathf.Max(0.1f, element.spacing)) return false;
            }

            return true;
        }

        private static bool IntersectsRoomGeometry(RoomAuthoringContext context, DecorAssetDescriptor descriptor, Candidate candidate)
        {
            if (context?.obstacles == null) return false;
            var candidateBoxes = SpatialGeometryUtility.BuildWorldObbs(descriptor, candidate.Position, candidate.Rotation, candidate.Scale);
            foreach (var obstacle in context.obstacles
                         .Where(value => value != null && value.policy != RoomObstaclePolicy.Ignore)
                         .OrderBy(value => value.stableId ?? string.Empty, StringComparer.Ordinal))
            {
                // Unknown fixed geometry is a hard blocker: guessing around an unreviewed wall or
                // pillar would make the deterministic preview appear safer than it is.
                if (!obstacle.IsUsable) return true;
                if (IsCandidateContactTarget(candidate, obstacle)) continue;
                if (SpatialGeometryUtility.Intersects(candidateBoxes, SpatialGeometryUtility.BuildWorldObbs(obstacle))) return true;
            }
            return false;
        }

        private static bool IsCandidateContactTarget(Candidate candidate, RoomObstacleProxy obstacle)
        {
            return obstacle.policy == RoomObstaclePolicy.ContactSurface &&
                   !string.IsNullOrWhiteSpace(obstacle.contactSurfaceId) &&
                   candidate.Surfaces.Any(surface => surface != null &&
                       string.Equals(surface.SurfaceId, obstacle.contactSurfaceId, StringComparison.Ordinal));
        }

        private static float ScoreCandidate(ConceptRoom room, CompositionElement element, Candidate candidate, IReadOnlyList<PlacedDecorItem> placed)
        {
            var score = 1f;
            var box = room.AuthoringBounds;
            var local = box.transform.InverseTransformPoint(candidate.Bounds.center) - box.center;
            var normalizedX = Mathf.Abs(local.x) / Mathf.Max(0.001f, box.size.x * 0.5f);
            var normalizedZ = Mathf.Abs(local.z) / Mathf.Max(0.001f, box.size.z * 0.5f);

            score += element.preferredZone switch
            {
                PreferredZone.Center => (1f - Mathf.Max(normalizedX, normalizedZ)) * 2f,
                PreferredZone.Focal => (1f - Mathf.Max(normalizedX, normalizedZ)) * 1.5f,
                PreferredZone.Perimeter => Mathf.Max(normalizedX, normalizedZ) * 2f,
                PreferredZone.Corner => Mathf.Min(normalizedX, normalizedZ) * 2f,
                _ => 0.5f
            };

            var anchor = FindAnchor(element, placed);
            if (anchor != null)
            {
                var distance = Vector3.Distance(anchor.position, candidate.Position);
                var target = Mathf.Max(0.2f, element.spacing);
                score += Mathf.Max(0f, 2f - Mathf.Abs(distance - target));
            }

            foreach (var observation in room.ObservationPoints)
            {
                if (observation == null) continue;
                var direction = candidate.Bounds.center - observation.transform.position;
                var dot = Vector3.Dot(observation.transform.forward, direction.normalized);
                if (element.role == DecorRole.Hero) score += Mathf.Max(0f, dot) * (observation.Primary ? 4f : 2f);
                else if (dot > 0.85f) score -= candidate.Bounds.extents.magnitude * 0.1f;
            }

            return score;
        }

        private static float ResolveYaw(DecorAssetDescriptor descriptor, System.Random random)
        {
            if ((descriptor.Rotation & RotationMode.RandomYaw) != 0) return (float)random.NextDouble() * 360f;
            if ((descriptor.Rotation & RotationMode.QuarterTurns) != 0) return random.Next(0, 4) * 90f;
            return 0f;
        }

        private static PlacedDecorItem FindAnchor(CompositionElement element, IReadOnlyList<PlacedDecorItem> placed)
        {
            if (string.IsNullOrWhiteSpace(element.anchorElementId)) return null;
            return placed.FirstOrDefault(item => item != null && item.elementId == element.anchorElementId);
        }

        private static string ResolveStyleSet(RoomCompositionPlan plan, IEnumerable<CompositionElement> elements)
        {
            foreach (var element in elements.OrderBy(value => value.role == DecorRole.Hero ? 0 : 1))
            {
                var descriptor = plan.Catalog.Find(element.descriptorId);
                if (descriptor != null && !string.IsNullOrWhiteSpace(descriptor.StyleSet)) return descriptor.StyleSet;
            }
            return string.Empty;
        }

        private static int RoleOrder(DecorRole role) => role switch
        {
            DecorRole.Hero => 0,
            DecorRole.Support => 1,
            DecorRole.StoryEvidence => 2,
            DecorRole.LightingCue => 3,
            DecorRole.DecalCue => 4,
            _ => 5
        };

        private static void AddGap(LayoutResult result, CompositionElement element, string styleSet, string reason, int available, int requestedOverride = -1)
        {
            result.AssetGaps.gaps.Add(new AssetGap
            {
                role = element.role,
                styleSet = styleSet,
                reason = reason,
                requestedCount = requestedOverride >= 0 ? requestedOverride : Mathf.Max(1, element.count),
                availableCount = available
            });
        }

        private static PlacedDecorItem ClonePlacement(PlacedDecorItem source) => new()
        {
            placementId = source.placementId,
            elementId = source.elementId,
            instanceIndex = source.instanceIndex,
            role = source.role,
            relation = source.relation,
            descriptor = source.descriptor,
            position = source.position,
            rotation = source.rotation,
            scale = source.scale,
            worldBounds = source.worldBounds,
            surfaceId = source.surfaceId,
            contactEvidence = source.contactEvidence,
            surfaceIds = source.surfaceIds != null ? new List<string>(source.surfaceIds) : new List<string>(),
            contactEvidenceSet = source.contactEvidenceSet != null ? new List<ContactEvidence>(source.contactEvidenceSet) : new List<ContactEvidence>(),
            locked = source.locked
        };

        private struct Candidate
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public Bounds Bounds;
            public RoomSurface Surface;
            public ContactEvidence Contact;
            public IReadOnlyList<RoomSurface> Surfaces;
            public IReadOnlyList<ContactEvidence> Contacts;
            public float Score;

            public Candidate(Vector3 position, Quaternion rotation, Vector3 scale, Bounds bounds, IReadOnlyList<RoomSurface> surfaces, IReadOnlyList<ContactEvidence> contacts, float score)
            {
                Position = position;
                Rotation = rotation;
                Scale = scale;
                Bounds = bounds;
                Surfaces = surfaces ?? Array.Empty<RoomSurface>();
                Contacts = contacts ?? Array.Empty<ContactEvidence>();
                Surface = Surfaces.Count > 0 ? Surfaces[0] : null;
                Contact = Contacts.Count > 0 ? Contacts[0] : null;
                Score = score;
            }
        }
    }
}
