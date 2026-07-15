using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class PreviewValidationService
    {
        public const string ValidationVersion = "room-validator-4-surface-arrangement";

        public static ValidationReport Validate(PreviewSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var report = new ValidationReport
            {
                sessionId = session.SessionId,
                manifestHash = session.ManifestHash,
                geometryProfileHash = session.GeometryProfileHash,
                validationVersion = ValidationVersion,
                seed = session.Plan != null ? session.Plan.Seed : 0,
                visualScores = session.LastValidation?.visualScores ?? new VisualQualityScores()
            };
            var room = session.Plan != null ? session.Plan.Room : null;
            var brief = session.Plan != null ? session.Plan.ConceptBrief : null;

            if (room == null || room.AuthoringBounds == null)
            {
                report.issues.Add(new ValidationIssue("ROOM_MISSING", ValidationSeverity.Error, "The preview has no valid ConceptRoom authoring bounds."));
                session.LastValidation = FinalizeReport(report, session);
                return session.LastValidation;
            }

            ValidateAuthoringContext(session, report);

            for (var i = 0; i < session.Placements.Count; i++)
            {
                var item = session.Placements[i];
                if (item == null || item.descriptor == null)
                {
                    report.issues.Add(new ValidationIssue("ASSET_MISSING", ValidationSeverity.Error, "A placement has no decor descriptor.", item?.placementId));
                    continue;
                }

                if (!room.ContainsBounds(item.worldBounds, (item.surfaceIds?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(item.surfaceId)))
                    report.issues.Add(new ValidationIssue("OUTSIDE_ROOM", ValidationSeverity.Error, $"{item.descriptor.name} extends outside the room bounds.", item.placementId));

                if (!IsFinite(item.position) || !IsFinite(item.scale) || item.scale.x <= 0f || item.scale.y <= 0f || item.scale.z <= 0f)
                    report.issues.Add(new ValidationIssue("INVALID_TRANSFORM", ValidationSeverity.Error, $"{item.descriptor.name} has an invalid transform.", item.placementId));

                if (item.descriptor.Geometry == null || !item.descriptor.Geometry.IsUsable)
                    report.issues.Add(new ValidationIssue("GEOMETRY_UNREVIEWED", ValidationSeverity.Error, $"{item.descriptor.name} has no reviewed geometry profile.", item.placementId));

                ValidateContact(session, room, item, report);
                ValidateRoomObstacles(session, item, report);

                foreach (var zone in room.KeepClearZones)
                {
                    if (zone != null && zone.WorldBounds.Intersects(item.worldBounds))
                        report.issues.Add(new ValidationIssue("KEEP_CLEAR", ValidationSeverity.Error, $"{item.descriptor.name} intersects '{zone.Reason}'.", item.placementId));
                }

                for (var j = i + 1; j < session.Placements.Count; j++)
                {
                    var other = session.Placements[j];
                    if (other == null || other.descriptor == null) continue;
                    if (item.descriptor.Geometry == null || other.descriptor.Geometry == null) continue;
                    var arrangementPair = !string.IsNullOrWhiteSpace(item.arrangementId) ||
                                          !string.IsNullOrWhiteSpace(other.arrangementId);
                    var padding = arrangementPair ? 0f : Mathf.Max(item.descriptor.Clearance, other.descriptor.Clearance);
                    var left = SpatialGeometryUtility.BuildWorldObbs(item.descriptor, item.position, item.rotation, item.scale, padding);
                    var right = SpatialGeometryUtility.BuildWorldObbs(other.descriptor, other.position, other.rotation, other.scale, padding);
                    if (SpatialGeometryUtility.Intersects(left, right))
                        report.issues.Add(new ValidationIssue(
                            arrangementPair ? SurfaceArrangementErrorCodes.Overlap : "OBB_OVERLAP",
                            ValidationSeverity.Error,
                            $"{item.descriptor.name} overlaps {other.descriptor.name}.",
                            item.placementId,
                            other.placementId));
                }
            }

            ValidateStyle(session.Placements, report);
            ValidateMotifs(session.Placements, brief, report);
            ValidateRoles(session.Placements, report);
            ValidateHeroVisibility(session, report);
            ValidateAssetTypeRules(session, report);

            if (session.AssetGaps != null)
            {
                foreach (var gap in session.AssetGaps.gaps)
                    report.issues.Add(new ValidationIssue(
                        string.IsNullOrWhiteSpace(gap.code) ? "ASSET_GAP" : gap.code,
                        ValidationSeverity.Error,
                        $"{gap.role}: {gap.reason}",
                        gap.targetId));
            }

            session.LastValidation = FinalizeReport(report, session);
            return session.LastValidation;
        }

        private static ValidationReport FinalizeReport(ValidationReport report, PreviewSession session)
        {
            report.issues = report.issues
                .OrderBy(value => value.code ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(value => value.severity)
                .ThenBy(value => string.Join("|", (value.elementIds ?? new List<string>()).OrderBy(id => id, StringComparer.Ordinal)), StringComparer.Ordinal)
                .ToList();
            var canonical = new StringBuilder()
                .Append(report.validationVersion).Append('|')
                .Append(report.manifestHash).Append('|')
                .Append(report.geometryProfileHash).Append('|')
                .Append(session?.AuthoringSourceHash).Append('|')
                .Append(session?.ObstacleGeometryHash).Append('|')
                .Append(report.seed);
            foreach (var issue in report.issues)
            {
                canonical.Append('\n').Append(issue.code).Append('|').Append((int)issue.severity).Append('|');
                foreach (var id in (issue.elementIds ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal))
                    canonical.Append(id).Append(',');
            }
            using var sha = SHA256.Create();
            report.reportHash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString())).Select(value => value.ToString("x2")));
            return report;
        }

        private static void ValidateAuthoringContext(PreviewSession session, ValidationReport report)
        {
            var context = session.AuthoringContext;
            if (context == null) return;

            if (!string.Equals(session.AuthoringSourceHash ?? string.Empty, context.sourceHash ?? string.Empty, StringComparison.Ordinal))
            {
                report.issues.Add(new ValidationIssue(RoomAuthoringErrorCodes.SourceStale, ValidationSeverity.Error,
                    "The authoring context source hash changed after this preview was generated."));
            }

            var currentObstacleHash = context.ComputeObstacleHash();
            if (!string.IsNullOrWhiteSpace(session.ObstacleGeometryHash) &&
                !string.Equals(session.ObstacleGeometryHash, currentObstacleHash, StringComparison.Ordinal))
            {
                report.issues.Add(new ValidationIssue(RoomAuthoringErrorCodes.SourceStale, ValidationSeverity.Error,
                    "The room obstacle geometry changed after this preview was generated."));
            }

            if (!context.IsSourceCurrent(out var currentSourceHash))
            {
                report.issues.Add(new ValidationIssue(RoomAuthoringErrorCodes.SourceStale, ValidationSeverity.Error,
                    $"The authoring source changed after this preview was generated (expected '{session.AuthoringSourceHash}', current '{currentSourceHash}')."));
            }

            foreach (var obstacle in (context.obstacles ?? new List<RoomObstacleProxy>())
                         .Where(value => value != null && value.policy != RoomObstaclePolicy.Ignore)
                         .OrderBy(value => value.stableId ?? string.Empty, StringComparer.Ordinal))
            {
                if (obstacle.IsUsable) continue;
                report.issues.Add(new ValidationIssue(RoomAuthoringErrorCodes.GeometryUnreviewed, ValidationSeverity.Error,
                    $"Room obstacle '{ObstacleName(obstacle)}' has no reviewed compound OBB geometry.", obstacle.stableId));
            }
        }

        private static void ValidateRoomObstacles(PreviewSession session, PlacedDecorItem item, ValidationReport report)
        {
            var context = session.AuthoringContext;
            if (context?.obstacles == null || item?.descriptor?.Geometry == null) return;
            var itemBoxes = SpatialGeometryUtility.BuildWorldObbs(item.descriptor, item.position, item.rotation, item.scale);
            foreach (var obstacle in context.obstacles
                         .Where(value => value != null && value.policy != RoomObstaclePolicy.Ignore && value.IsUsable)
                         .OrderBy(value => value.stableId ?? string.Empty, StringComparer.Ordinal))
            {
                if (IsPlacementContactTarget(item, obstacle)) continue;
                var obstacleBoxes = SpatialGeometryUtility.BuildWorldObbs(obstacle);
                if (!SpatialGeometryUtility.Intersects(itemBoxes, obstacleBoxes)) continue;
                report.issues.Add(new ValidationIssue(RoomAuthoringErrorCodes.GeometryOverlap, ValidationSeverity.Error,
                    $"{item.descriptor.name} overlaps room geometry '{ObstacleName(obstacle)}'.", item.placementId, obstacle.stableId));
            }
        }

        private static bool IsPlacementContactTarget(PlacedDecorItem item, RoomObstacleProxy obstacle)
        {
            if (obstacle.policy != RoomObstaclePolicy.ContactSurface || string.IsNullOrWhiteSpace(obstacle.contactSurfaceId)) return false;
            if (item.surfaceIds != null && item.surfaceIds.Contains(obstacle.contactSurfaceId, StringComparer.Ordinal)) return true;
            return string.Equals(item.surfaceId, obstacle.contactSurfaceId, StringComparison.Ordinal);
        }

        private static string ObstacleName(RoomObstacleProxy obstacle) =>
            !string.IsNullOrWhiteSpace(obstacle.label) ? obstacle.label : obstacle.stableId ?? "unnamed";

        private static void ValidateContact(PreviewSession session, ConceptRoom room, PlacedDecorItem item, ValidationReport report)
        {
            if (!string.IsNullOrWhiteSpace(item.arrangementId))
            {
                ValidateArrangementContact(session, item, report);
                return;
            }
            var profile = item.descriptor.Geometry;
            if (profile == null) return;
            var rules = profile.EffectiveContacts
                .Where(value => value != null && value.requirement != ContactRequirement.FreeStanding)
                .OrderBy(ContactOrder)
                .ToArray();
            if (rules.Length == 0) return;
            var surfaceIds = item.surfaceIds != null && item.surfaceIds.Count > 0
                ? item.surfaceIds
                : new List<string> { item.surfaceId };
            item.contactEvidenceSet ??= new List<ContactEvidence>();
            item.contactEvidenceSet.Clear();

            for (var index = 0; index < rules.Length; index++)
            {
                var rule = rules[index];
                var surfaceId = index < surfaceIds.Count ? surfaceIds[index] : string.Empty;
                var contextSurfaces = session.AuthoringContext?.surfaces;
                var surface = contextSurfaces != null && contextSurfaces.Count > 0
                    ? contextSurfaces.FirstOrDefault(value => value != null && value.SurfaceId == surfaceId)
                    : room.Surfaces.FirstOrDefault(value => value != null && value.SurfaceId == surfaceId);
                if (surface == null || !surface.Reviewed)
                {
                    report.issues.Add(new ValidationIssue("SURFACE_UNREVIEWED", ValidationSeverity.Error, $"{item.descriptor.name} is missing a reviewed {rule.requirement} surface.", item.placementId));
                    continue;
                }
                if (!surface.Supported)
                {
                    report.issues.Add(new ValidationIssue("UNSUPPORTED_SURFACE", ValidationSeverity.Error, surface.UnsupportedReason, item.placementId));
                    continue;
                }

                var evidence = SpatialGeometryUtility.EvaluateContact(item.descriptor, rule, item.position, item.rotation, item.scale, surface);
                item.contactEvidenceSet.Add(evidence);
                if (index == 0)
                {
                    item.surfaceId = surface.SurfaceId;
                    item.contactEvidence = evidence;
                }
                if (evidence.penetration > rule.maximumPenetration + 0.000001f)
                    report.issues.Add(new ValidationIssue("SURFACE_PENETRATION", ValidationSeverity.Error, $"{item.descriptor.name} penetrates '{surface.SurfaceId}' by {evidence.penetration:0.####}m.", item.placementId));
                if (evidence.gap < rule.minimumGap - 0.000001f || evidence.gap > rule.maximumGap + 0.000001f)
                    report.issues.Add(new ValidationIssue("CONTACT_GAP", ValidationSeverity.Error, $"{item.descriptor.name} {rule.requirement} gap to '{surface.SurfaceId}' is {evidence.gap:0.####}m; expected {rule.minimumGap:0.####}-{rule.maximumGap:0.####}m.", item.placementId));
                if (evidence.supportCoverage < rule.minimumSupportCoverage - 0.000001f)
                    report.issues.Add(new ValidationIssue("INSUFFICIENT_SUPPORT", ValidationSeverity.Error, $"{item.descriptor.name} {rule.requirement} coverage is {evidence.supportCoverage:P0}; expected at least {rule.minimumSupportCoverage:P0}.", item.placementId));
                if (evidence.directionAlignment < 0.95f)
                    report.issues.Add(new ValidationIssue("CONTACT_DIRECTION", ValidationSeverity.Error, $"{item.descriptor.name} {rule.requirement} face does not oppose '{surface.SurfaceId}'.", item.placementId));
            }
        }

        private static void ValidateArrangementContact(PreviewSession session, PlacedDecorItem item, ValidationReport report)
        {
            var spec = session.Plan?.SurfaceArrangements?.FirstOrDefault(value => value != null &&
                string.Equals(value.arrangement_id, item.arrangementId, StringComparison.Ordinal));
            var support = session.Placements.FirstOrDefault(value => value != null &&
                string.Equals(value.placementId, item.supportPlacementId, StringComparison.Ordinal));
            var code = SurfaceArrangementPacker.ValidatePlacement(session.Request, spec, item, support, out var evidence);
            item.contactEvidence = evidence;
            item.contactEvidenceSet ??= new List<ContactEvidence>();
            item.contactEvidenceSet.Clear();
            item.contactEvidenceSet.Add(evidence);
            item.surfaceId = evidence.surfaceId;
            item.surfaceIds ??= new List<string>();
            item.surfaceIds.Clear();
            if (!string.IsNullOrWhiteSpace(evidence.surfaceId)) item.surfaceIds.Add(evidence.surfaceId);
            if (string.IsNullOrWhiteSpace(code)) return;
            report.issues.Add(new ValidationIssue(
                code,
                ValidationSeverity.Error,
                $"{item.descriptor.name} does not satisfy arrangement '{item.arrangementId}' support rules.",
                item.placementId,
                support?.placementId));
        }

        private static void ValidateStyle(IEnumerable<PlacedDecorItem> placements, ValidationReport report)
        {
            var styleSets = placements
                .Where(item => item?.descriptor != null && !string.IsNullOrWhiteSpace(item.descriptor.StyleSet))
                .Select(item => item.descriptor.StyleSet)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (styleSets.Length > 1)
                report.issues.Add(new ValidationIssue("STYLE_MIX", ValidationSeverity.Error, $"The room mixes style sets: {string.Join(", ", styleSets)}."));
        }

        private static void ValidateMotifs(IEnumerable<PlacedDecorItem> placements, RoomConceptBrief brief, ValidationReport report)
        {
            if (brief == null) return;
            var motifs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var placement in placements)
            {
                if (placement?.descriptor == null) continue;
                foreach (var motif in placement.descriptor.Motifs.Where(value => !string.IsNullOrWhiteSpace(value))) motifs.Add(motif.Trim());
            }

            foreach (var required in brief.RequiredMotifs.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (!motifs.Contains(required.Trim()))
                    report.issues.Add(new ValidationIssue("MOTIF_REQUIRED", ValidationSeverity.Error, $"Required motif '{required}' is missing."));
            }

            foreach (var forbidden in brief.ForbiddenMotifs.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                if (motifs.Contains(forbidden.Trim()))
                    report.issues.Add(new ValidationIssue("MOTIF_FORBIDDEN", ValidationSeverity.Error, $"Forbidden motif '{forbidden}' is present."));
            }
        }

        private static void ValidateRoles(IEnumerable<PlacedDecorItem> placements, ValidationReport report)
        {
            var items = placements.Where(item => item != null).ToArray();
            if (!items.Any(item => item.role == DecorRole.Hero))
                report.issues.Add(new ValidationIssue("HERO_MISSING", ValidationSeverity.Error, "The composition has no Hero element."));
            if (!items.Any(item => item.role == DecorRole.Support || item.role == DecorRole.StoryEvidence))
                report.issues.Add(new ValidationIssue("STORY_SUPPORT_MISSING", ValidationSeverity.Warning, "The Hero has no supporting or story-evidence elements."));
        }

        private static void ValidateHeroVisibility(PreviewSession session, ValidationReport report)
        {
            var room = session.Plan.Room;
            var hero = session.Placements.FirstOrDefault(item => item != null && item.role == DecorRole.Hero);
            if (hero == null || room.ObservationPoints.Count == 0) return;

            var visibleFromPrimary = false;
            foreach (var observation in room.ObservationPoints.Where(point => point != null && point.Primary))
            {
                var direction = hero.worldBounds.center - observation.transform.position;
                var halfFov = observation.FieldOfView * 0.5f;
                if (Vector3.Angle(observation.transform.forward, direction) <= halfFov)
                {
                    visibleFromPrimary = true;
                    break;
                }
            }

            if (!visibleFromPrimary)
                report.issues.Add(new ValidationIssue("HERO_NOT_VISIBLE", ValidationSeverity.Error, "The Hero is outside every primary observation point's field of view.", hero.placementId));
        }

        private static void ValidateAssetTypeRules(PreviewSession session, ValidationReport report)
        {
            foreach (var group in session.Placements.Where(item => item?.descriptor != null).GroupBy(item => item.descriptor))
            {
                if (group.Count() > group.Key.MaximumInstancesPerRoom)
                    report.issues.Add(new ValidationIssue("INSTANCE_LIMIT", ValidationSeverity.Error, $"{group.Key.name} exceeds its per-room instance limit of {group.Key.MaximumInstancesPerRoom}."));
            }

            foreach (var item in session.Placements.Where(value => value?.descriptor != null))
            {
                if (item.descriptor.AssetType == DecorAssetType.Light)
                {
                    var lights = item.previewObject != null ? item.previewObject.GetComponentsInChildren<Light>(true) : Array.Empty<Light>();
                    if (lights.Length == 0)
                    {
                        report.issues.Add(new ValidationIssue("LIGHT_COMPONENT_MISSING", ValidationSeverity.Error, $"{item.descriptor.name} is classified as a light but has no Light component.", item.placementId));
                        continue;
                    }
                    foreach (var light in lights)
                    {
                        if (light.intensity < item.descriptor.MinimumLightIntensity || light.intensity > item.descriptor.MaximumLightIntensity)
                            report.issues.Add(new ValidationIssue("LIGHT_INTENSITY", ValidationSeverity.Error, $"{item.descriptor.name} light intensity is outside its descriptor range.", item.placementId));
                        if (item.descriptor.MaximumLightRange > 0f && light.range > item.descriptor.MaximumLightRange)
                            report.issues.Add(new ValidationIssue("LIGHT_RANGE", ValidationSeverity.Error, $"{item.descriptor.name} light range exceeds {item.descriptor.MaximumLightRange:0.##}m.", item.placementId));
                    }
                }
                else if (item.descriptor.AssetType == DecorAssetType.Decal)
                {
                    var hasProjector = item.previewObject != null && item.previewObject.GetComponentsInChildren<Component>(true)
                        .Any(component => component != null && component.GetType().FullName == "UnityEngine.Rendering.Universal.DecalProjector");
                    if (!hasProjector)
                        report.issues.Add(new ValidationIssue("DECAL_PROJECTOR_MISSING", ValidationSeverity.Error, $"{item.descriptor.name} is classified as a decal but has no URP DecalProjector.", item.placementId));
                    if (item.descriptor.Surface == PlacementSurface.Any)
                        report.issues.Add(new ValidationIssue("DECAL_SURFACE_AMBIGUOUS", ValidationSeverity.Error, $"{item.descriptor.name} must declare Floor, Wall, or Ceiling placement.", item.placementId));
                }
            }
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static int ContactOrder(ContactRules rules) => rules.requirement switch
        {
            ContactRequirement.WallBacked or ContactRequirement.WallMounted => 0,
            ContactRequirement.FloorSupported => 1,
            ContactRequirement.CeilingMounted => 2,
            _ => 3
        };
    }
}
