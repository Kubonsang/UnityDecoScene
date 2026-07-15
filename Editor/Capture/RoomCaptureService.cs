using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class RoomCaptureService
    {
        private const int CaptureSize = 768;
        private const int ManifestVersion = 1;
        private const string ManifestFileName = "capture-manifest.json";

        // TestPlay intentionally launches Unity with -nographics. Keep capture-set,
        // cache, and hashing tests independent from the graphics device while the
        // real pixel-isolation test still exercises Render in a graphics-enabled run.
        internal static Func<string, string, string> RenderOverrideForTests { get; set; }

        public static IReadOnlyList<string> CaptureAll(PreviewSession session)
        {
            ValidateSession(session);
            var room = session.Plan.Room;
            var captureScene = ResolveCaptureScene(session);
            var bounds = room.AuthoringBounds.bounds;
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, $"../Library/DungeonDecorator/Captures/{session.SessionId}"));
            Directory.CreateDirectory(directory);
            session.CapturePaths.Clear();

            var topPosition = bounds.center + Vector3.up * (bounds.extents.y + Mathf.Max(bounds.size.x, bounds.size.z) + 2f);
            session.CapturePaths.Add(Render(directory, "top", topPosition, Quaternion.LookRotation(Vector3.down, Vector3.forward), true, Mathf.Max(bounds.extents.x, bounds.extents.z) * 1.15f, 60f, captureScene));

            var observationIndex = 0;
            foreach (var observation in room.ObservationPoints)
            {
                if (observation == null) continue;
                session.CapturePaths.Add(Render(directory, $"observation-{observationIndex++}", observation.transform.position, observation.transform.rotation, false, 5f, observation.FieldOfView, captureScene));
            }

            var height = bounds.min.y + bounds.size.y * 0.65f;
            var insetX = bounds.size.x * 0.08f;
            var insetZ = bounds.size.z * 0.08f;
            var cornerA = new Vector3(bounds.min.x + insetX, height, bounds.min.z + insetZ);
            var cornerB = new Vector3(bounds.max.x - insetX, height, bounds.max.z - insetZ);
            var floorFocus = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.3f, bounds.center.z);
            session.CapturePaths.Add(Render(directory, "corner-a", cornerA, Quaternion.LookRotation((floorFocus - cornerA).normalized, Vector3.up), false, 5f, 58f, captureScene));
            session.CapturePaths.Add(Render(directory, "corner-b", cornerB, Quaternion.LookRotation((floorFocus - cornerB).normalized, Vector3.up), false, 5f, 58f, captureScene));
            foreach (var view in BuildWallContactViews(session))
                session.CapturePaths.Add(Render(directory, view.Id, view.Position, view.Rotation, view.Orthographic, view.OrthographicSize, view.FieldOfView, captureScene));
            foreach (var view in BuildSurfaceArrangementViews(session))
                session.CapturePaths.Add(Render(directory, view.Id, view.Position, view.Rotation, view.Orthographic, view.OrthographicSize, view.FieldOfView, captureScene));
            return session.CapturePaths;
        }

        /// <summary>
        /// Captures the four stable room-review views plus deterministic wall-contact side close-ups
        /// for eligible placements, then writes a cache manifest. This overload deliberately renders
        /// a fresh set; use the input-key overload for verified cache reuse.
        /// </summary>
        public static RoomCaptureSet CaptureSet(PreviewSession session, string outputDirectory)
        {
            ValidateSession(session);
            var inputKey = ComputeDefaultInputKey(session);
            return CaptureSetCore(session, outputDirectory, inputKey, false);
        }

        /// <summary>
        /// Reuses a prior set only when its manifest has the same input key and every PNG still
        /// matches its recorded SHA-256. A cache hit performs no rendering.
        /// </summary>
        public static RoomCaptureSet CaptureSet(PreviewSession session, string outputDirectory, string inputKey)
        {
            ValidateSession(session);
            if (string.IsNullOrWhiteSpace(inputKey)) throw new ArgumentException("A non-empty capture input key is required.", nameof(inputKey));
            return CaptureSetCore(session, outputDirectory, inputKey, true);
        }

        public static string ComputeCaptureSetHash(IEnumerable<RoomCaptureEntry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var canonical = new StringBuilder();
            foreach (var entry in entries.Where(value => value != null).OrderBy(value => value.viewId, StringComparer.Ordinal))
                canonical.Append(entry.viewId ?? string.Empty).Append('\n').Append(entry.contentHash ?? string.Empty).Append('\n');
            return Sha256(Encoding.UTF8.GetBytes(canonical.ToString()));
        }

        private static RoomCaptureSet CaptureSetCore(PreviewSession session, string outputDirectory, string inputKey, bool allowCache)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
            var directory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(directory);
            var views = BuildCaptureViews(session);

            if (allowCache && TryReadCachedSet(directory, inputKey, views, out var cached))
            {
                PublishPaths(session, cached);
                return cached;
            }

            var result = new RoomCaptureSet { inputKey = inputKey, renderedCount = views.Count, reusedFromCache = false };
            var captureScene = ResolveCaptureScene(session);
            foreach (var view in views)
            {
                var path = Render(directory, view.Id, view.Position, view.Rotation, view.Orthographic, view.OrthographicSize, view.FieldOfView, captureScene);
                result.entries.Add(new RoomCaptureEntry(view.Id, path, HashFile(path)));
            }
            result.captureSetHash = ComputeCaptureSetHash(result.entries);
            WriteManifest(directory, result);
            PublishPaths(session, result);
            return result;
        }

        private static IReadOnlyList<CaptureView> BuildFixedViews(ConceptRoom room)
        {
            var bounds = room.AuthoringBounds.bounds;
            var maximumSpan = Mathf.Max(bounds.size.x, bounds.size.z);
            var topPosition = bounds.center + Vector3.up * (bounds.extents.y + maximumSpan + 2f);
            var views = new List<CaptureView>(4)
            {
                new(RoomCaptureViewIds.Top, topPosition, Quaternion.LookRotation(Vector3.down, Vector3.forward), true, Mathf.Max(bounds.extents.x, bounds.extents.z) * 1.15f, 60f)
            };

            var primary = room.ObservationPoints
                .Where(point => point != null && point.Primary)
                .OrderBy(point => point.Label ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(point => HierarchyPath(point.transform), StringComparer.Ordinal)
                .FirstOrDefault();
            if (primary != null)
            {
                views.Add(new CaptureView(RoomCaptureViewIds.PrimaryObservation, primary.transform.position, primary.transform.rotation, false, 5f, primary.FieldOfView));
            }
            else
            {
                var fallbackPosition = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.65f, bounds.min.z + bounds.size.z * 0.08f);
                var fallbackFocus = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.3f, bounds.center.z);
                views.Add(new CaptureView(RoomCaptureViewIds.PrimaryObservation, fallbackPosition, LookAt(fallbackPosition, fallbackFocus), false, 5f, 60f));
            }

            var height = bounds.min.y + bounds.size.y * 0.65f;
            var insetX = bounds.size.x * 0.08f;
            var insetZ = bounds.size.z * 0.08f;
            var cornerA = new Vector3(bounds.min.x + insetX, height, bounds.min.z + insetZ);
            var cornerB = new Vector3(bounds.max.x - insetX, height, bounds.max.z - insetZ);
            var floorFocus = new Vector3(bounds.center.x, bounds.min.y + bounds.size.y * 0.3f, bounds.center.z);
            views.Add(new CaptureView(RoomCaptureViewIds.CornerA, cornerA, LookAt(cornerA, floorFocus), false, 5f, 58f));
            views.Add(new CaptureView(RoomCaptureViewIds.CornerB, cornerB, LookAt(cornerB, floorFocus), false, 5f, 58f));
            return views;
        }

        private static IReadOnlyList<CaptureView> BuildCaptureViews(PreviewSession session)
        {
            var views = new List<CaptureView>(BuildFixedViews(session.Plan.Room));
            views.AddRange(BuildWallContactViews(session));
            views.AddRange(BuildSurfaceArrangementViews(session));
            return views;
        }

        internal static string[] SurfaceArrangementViewIds(string arrangementId)
        {
            var source = string.IsNullOrWhiteSpace(arrangementId) ? "arrangement" : arrangementId.Trim();
            var slug = new string(source.ToLowerInvariant()
                .Select(value => char.IsLetterOrDigit(value) ? value : '-')
                .ToArray()).Trim('-');
            while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(slug)) slug = "arrangement";
            if (slug.Length > 18) slug = slug.Substring(0, 18).TrimEnd('-');
            var suffix = Sha256(Encoding.UTF8.GetBytes(source)).Substring(0, 8);
            var prefix = RoomCaptureViewIds.SurfaceArrangementPrefix + slug + "-" + suffix;
            return new[]
            {
                prefix + RoomCaptureViewIds.ArrangementOverviewSuffix,
                prefix + RoomCaptureViewIds.ArrangementTopSuffix,
                prefix + RoomCaptureViewIds.ArrangementSideSuffix,
                prefix + RoomCaptureViewIds.ArrangementContactSuffix
            };
        }

        private static IEnumerable<CaptureView> BuildSurfaceArrangementViews(PreviewSession session)
        {
            var specs = session.Plan?.SurfaceArrangements;
            if (specs == null) yield break;
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in specs.Where(value => value != null)
                         .OrderBy(value => value.arrangement_id ?? string.Empty, StringComparer.Ordinal))
            {
                var target = session.Placements.Where(value => value != null &&
                                                               string.Equals(value.elementId, spec.target_element_id, StringComparison.Ordinal))
                    .OrderBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal)
                    .FirstOrDefault();
                var members = session.Placements.Where(value => value != null &&
                                                                 string.Equals(value.arrangementId, spec.arrangement_id, StringComparison.Ordinal))
                    .OrderBy(value => value.stackLevel)
                    .ThenBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal)
                    .ToArray();
                if (target == null || members.Length == 0 ||
                    !TryArrangementSurface(target, spec.TargetFrameId, out var surface)) continue;

                var bounds = ResolvePlacementBounds(target);
                foreach (var member in members) bounds.Encapsulate(ResolvePlacementBounds(member));
                var focus = bounds.center;
                focus.y = Mathf.Lerp(surface.Origin.y, bounds.max.y, 0.45f);
                var planarSpan = Mathf.Max(surface.Size.x, surface.Size.y, bounds.size.x, bounds.size.z);
                var distance = Mathf.Max(1.15f, planarSpan * 0.95f);
                var ids = SurfaceArrangementViewIds(spec.arrangement_id);
                if (ids.Any(id => !emitted.Add(id))) continue;

                var diagonal = (surface.Tangent - surface.Bitangent + surface.Normal * 0.85f).normalized;
                var overviewPosition = focus + diagonal * distance;
                var overviewUp = Vector3.ProjectOnPlane(surface.Normal, focus - overviewPosition).normalized;
                if (overviewUp.sqrMagnitude < 0.000001f) overviewUp = Vector3.up;
                yield return new CaptureView(ids[0], overviewPosition,
                    Quaternion.LookRotation((focus - overviewPosition).normalized, overviewUp), false, 5f, 48f);

                var topPosition = focus + surface.Normal * (distance + Mathf.Max(0.75f, bounds.size.y));
                var topUp = Vector3.ProjectOnPlane(surface.Bitangent, -surface.Normal).normalized;
                if (topUp.sqrMagnitude < 0.000001f) topUp = Vector3.forward;
                yield return new CaptureView(ids[1], topPosition,
                    Quaternion.LookRotation(-surface.Normal, topUp), true,
                    Mathf.Max(0.35f, Mathf.Max(surface.Size.x, surface.Size.y) * 0.62f), 45f);

                var sidePosition = focus + surface.Tangent * (distance + 0.35f) + surface.Normal * Mathf.Max(0.08f, bounds.size.y * 0.08f);
                var sideDirection = focus - sidePosition;
                var sideUp = Vector3.ProjectOnPlane(surface.Normal, sideDirection).normalized;
                if (sideUp.sqrMagnitude < 0.000001f) sideUp = Vector3.up;
                yield return new CaptureView(ids[2], sidePosition,
                    Quaternion.LookRotation(sideDirection.normalized, sideUp), true,
                    Mathf.Max(0.35f, bounds.extents.y * 1.25f), 42f);

                var contactMember = members.OrderBy(value => value.contactEvidence?.supportCoverage ?? 0f)
                    .ThenByDescending(value => value.stackLevel)
                    .ThenBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal)
                    .First();
                var memberBounds = ResolvePlacementBounds(contactMember);
                var contactPoint = contactMember.contactEvidence?.contactPoint ?? new Vector3(memberBounds.center.x, memberBounds.min.y, memberBounds.center.z);
                var contactDistance = Mathf.Max(0.55f, memberBounds.extents.magnitude * 1.8f);
                var contactPosition = contactPoint + surface.Tangent * contactDistance + surface.Normal * Mathf.Max(0.04f, memberBounds.extents.y * 0.2f);
                var contactDirection = contactPoint - contactPosition;
                var contactUp = Vector3.ProjectOnPlane(surface.Normal, contactDirection).normalized;
                if (contactUp.sqrMagnitude < 0.000001f) contactUp = Vector3.up;
                yield return new CaptureView(ids[3], contactPosition,
                    Quaternion.LookRotation(contactDirection.normalized, contactUp), true,
                    Mathf.Clamp(memberBounds.extents.magnitude * 1.15f, 0.18f, 0.8f), 38f);
            }
        }

        private static bool TryArrangementSurface(PlacedDecorItem target, string frameId, out ArrangementSurface surface)
        {
            surface = default;
            if (target?.descriptor?.Geometry == null) return false;
            var frame = target.descriptor.Geometry.Frame(frameId);
            if (frame == null) return false;
            var normal = (target.rotation * frame.localNormal).normalized;
            if (normal.sqrMagnitude < 0.9f || Vector3.Dot(normal, Vector3.up) < 0.95f) return false;
            var localTangent = frame.localTangent.sqrMagnitude < 0.001f ? Vector3.right : frame.localTangent.normalized;
            var localBitangent = Vector3.Cross(frame.localNormal.normalized, localTangent).normalized;
            var tangentVector = target.rotation * Vector3.Scale(localTangent * frame.size.x, target.scale);
            var bitangentVector = target.rotation * Vector3.Scale(localBitangent * frame.size.y, target.scale);
            var tangent = Vector3.ProjectOnPlane(tangentVector, normal).normalized;
            var bitangent = Vector3.ProjectOnPlane(bitangentVector, normal).normalized;
            if (tangent.sqrMagnitude < 0.9f || bitangent.sqrMagnitude < 0.9f) return false;
            surface = new ArrangementSurface(
                target.position + target.rotation * Vector3.Scale(frame.localPoint, target.scale),
                normal,
                tangent,
                bitangent,
                new Vector2(tangentVector.magnitude, bitangentVector.magnitude));
            return surface.Size.x > 0.001f && surface.Size.y > 0.001f;
        }

        private static IEnumerable<CaptureView> BuildWallContactViews(PreviewSession session)
        {
            var emittedViewIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var placement in session.Placements
                         .Where(value => value?.descriptor?.Geometry != null)
                         .OrderBy(value => value.placementId ?? string.Empty, StringComparer.Ordinal)
                         .ThenBy(value => value.descriptor.AssetId ?? string.Empty, StringComparer.Ordinal)
                         .ThenBy(value => value.instanceIndex))
            {
                var effectiveRules = placement.descriptor.Geometry.EffectiveContacts
                    .Where(value => value != null && value.requirement != ContactRequirement.FreeStanding)
                    .ToArray();
                var wallRuleIndex = Array.FindIndex(effectiveRules, value =>
                    value.requirement == ContactRequirement.WallBacked ||
                    value.requirement == ContactRequirement.WallMounted && IsMountedLight(placement.descriptor));
                if (wallRuleIndex < 0) continue;

                var surfaceId = ResolveSurfaceId(placement, wallRuleIndex);
                var surface = ResolveSurface(session, surfaceId);
                if (surface == null || surface.SurfaceType != RoomSurfaceType.Wall) continue;

                var contactPoint = ResolveContactPoint(placement, effectiveRules[wallRuleIndex], wallRuleIndex);
                var bounds = ResolvePlacementBounds(placement);
                var tangent = CanonicalDirection(surface.Tangent, Vector3.right);
                var tangentExtent = Vector3.Dot(
                    new Vector3(Mathf.Abs(tangent.x), Mathf.Abs(tangent.y), Mathf.Abs(tangent.z)),
                    bounds.extents);
                var distance = Mathf.Max(1.25f, tangentExtent + 0.75f);
                var outwardOffset = surface.Normal * Mathf.Clamp(bounds.extents.magnitude * 0.12f, 0.05f, 0.25f);
                var position = contactPoint + tangent * distance + outwardOffset;
                var direction = contactPoint - position;
                var up = Vector3.ProjectOnPlane(Vector3.up, direction).normalized;
                if (up.sqrMagnitude < 0.000001f) up = CanonicalDirection(surface.Bitangent, Vector3.up);
                var orthographicSize = Mathf.Max(0.35f, bounds.extents.magnitude * 1.15f);
                var id = ContactViewId(placement);
                if (!emittedViewIds.Add(id)) continue;
                yield return new CaptureView(id, position, Quaternion.LookRotation(direction.normalized, up), true, orthographicSize, 42f);
            }
        }

        private static bool IsMountedLight(DecorAssetDescriptor descriptor)
        {
            if (descriptor.AssetType == DecorAssetType.Light) return true;
            var id = descriptor.AssetId ?? string.Empty;
            var prefabName = descriptor.Prefab != null ? descriptor.Prefab.name : string.Empty;
            return id.IndexOf("torch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   prefabName.IndexOf("torch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveSurfaceId(PlacedDecorItem placement, int ruleIndex)
        {
            if (placement.surfaceIds != null && ruleIndex >= 0 && ruleIndex < placement.surfaceIds.Count)
                return placement.surfaceIds[ruleIndex];
            return ruleIndex == 0 ? placement.surfaceId : string.Empty;
        }

        private static RoomSurface ResolveSurface(PreviewSession session, string surfaceId)
        {
            if (string.IsNullOrWhiteSpace(surfaceId)) return null;
            var contextSurfaces = session.AuthoringContext?.surfaces;
            if (contextSurfaces != null && contextSurfaces.Count > 0)
                return contextSurfaces.FirstOrDefault(value => value != null && string.Equals(value.SurfaceId, surfaceId, StringComparison.Ordinal));
            return session.Plan.Room.Surfaces.FirstOrDefault(value => value != null && string.Equals(value.SurfaceId, surfaceId, StringComparison.Ordinal));
        }

        private static Vector3 ResolveContactPoint(PlacedDecorItem placement, ContactRules rules, int ruleIndex)
        {
            if (placement.contactEvidenceSet != null && ruleIndex >= 0 && ruleIndex < placement.contactEvidenceSet.Count)
            {
                var evidence = placement.contactEvidenceSet[ruleIndex];
                if (evidence != null) return evidence.contactPoint;
            }
            if (ruleIndex == 0 && placement.contactEvidence != null) return placement.contactEvidence.contactPoint;
            var frame = placement.descriptor.Geometry.FrameFor(rules);
            return frame != null
                ? placement.position + placement.rotation * Vector3.Scale(frame.localPoint, placement.scale)
                : placement.worldBounds.center;
        }

        private static Bounds ResolvePlacementBounds(PlacedDecorItem placement)
        {
            if (placement.worldBounds.size.sqrMagnitude > 0.000001f) return placement.worldBounds;
            var boxes = SpatialGeometryUtility.BuildWorldObbs(placement.descriptor, placement.position, placement.rotation, placement.scale);
            var bounds = SpatialGeometryUtility.CombinedAabb(boxes);
            return bounds.size.sqrMagnitude > 0.000001f ? bounds : new Bounds(placement.position, Vector3.one);
        }

        private static string ContactViewId(PlacedDecorItem placement)
        {
            var label = string.IsNullOrWhiteSpace(placement.placementId)
                ? $"{placement.descriptor.AssetId}:{placement.instanceIndex}"
                : placement.placementId;
            var identity = string.Join("|", label, placement.descriptor.AssetId ?? string.Empty, placement.instanceIndex.ToString(CultureInfo.InvariantCulture));
            var slug = new string(label.ToLowerInvariant()
                .Select(value => char.IsLetterOrDigit(value) ? value : '-')
                .ToArray()).Trim('-');
            while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(slug)) slug = "placement";
            if (slug.Length > 40) slug = slug.Substring(0, 40).TrimEnd('-');
            var suffix = Sha256(Encoding.UTF8.GetBytes(identity)).Substring(0, 8);
            return RoomCaptureViewIds.WallContactSidePrefix + slug + "-" + suffix;
        }

        private static Vector3 CanonicalDirection(Vector3 direction, Vector3 fallback)
        {
            if (direction.sqrMagnitude < 0.000001f) direction = fallback;
            direction.Normalize();
            var flip = Mathf.Abs(direction.x) > 0.000001f ? direction.x < 0f :
                Mathf.Abs(direction.y) > 0.000001f ? direction.y < 0f : direction.z < 0f;
            return flip ? -direction : direction;
        }

        private static bool TryReadCachedSet(string directory, string inputKey, IReadOnlyList<CaptureView> views, out RoomCaptureSet set)
        {
            set = null;
            var manifestPath = Path.Combine(directory, ManifestFileName);
            if (!File.Exists(manifestPath)) return false;

            CaptureManifest manifest;
            try { manifest = JsonUtility.FromJson<CaptureManifest>(File.ReadAllText(manifestPath, Encoding.UTF8)); }
            catch { return false; }
            if (manifest == null || manifest.version != ManifestVersion || !string.Equals(manifest.inputKey, inputKey, StringComparison.Ordinal)) return false;
            if (manifest.entries == null || manifest.entries.Count != views.Count) return false;

            var expected = views.Select((view, index) => new { view.Id, Index = index })
                .ToDictionary(value => value.Id, value => value.Index, StringComparer.Ordinal);
            var entries = new List<RoomCaptureEntry>(views.Count);
            foreach (var cached in manifest.entries)
            {
                if (cached == null || string.IsNullOrWhiteSpace(cached.viewId) || !expected.ContainsKey(cached.viewId)) return false;
                var expectedFileName = cached.viewId + ".png";
                if (!string.Equals(cached.fileName, expectedFileName, StringComparison.Ordinal)) return false;
                var path = Path.GetFullPath(Path.Combine(directory, cached.fileName));
                if (!File.Exists(path)) return false;
                var actualHash = HashFile(path);
                if (!string.Equals(actualHash, cached.contentHash, StringComparison.Ordinal)) return false;
                entries.Add(new RoomCaptureEntry(cached.viewId, path, actualHash));
            }

            if (entries.Select(entry => entry.viewId).Distinct(StringComparer.Ordinal).Count() != views.Count) return false;
            var captureSetHash = ComputeCaptureSetHash(entries);
            if (!string.Equals(captureSetHash, manifest.captureSetHash, StringComparison.Ordinal)) return false;
            set = new RoomCaptureSet
            {
                inputKey = inputKey,
                captureSetHash = captureSetHash,
                entries = entries.OrderBy(entry => expected[entry.viewId]).ToList(),
                renderedCount = 0,
                reusedFromCache = true
            };
            return true;
        }

        private static void WriteManifest(string directory, RoomCaptureSet set)
        {
            var manifest = new CaptureManifest
            {
                version = ManifestVersion,
                inputKey = set.inputKey,
                captureSetHash = set.captureSetHash,
                entries = set.entries.Select(entry => new CaptureManifestEntry
                {
                    viewId = entry.viewId,
                    fileName = Path.GetFileName(entry.path),
                    contentHash = entry.contentHash
                }).ToList()
            };
            File.WriteAllText(Path.Combine(directory, ManifestFileName), JsonUtility.ToJson(manifest, true), new UTF8Encoding(false));
        }

        private static void PublishPaths(PreviewSession session, RoomCaptureSet set)
        {
            session.CapturePaths.Clear();
            session.CapturePaths.AddRange(set.entries.Select(entry => entry.path));
        }

        private static string ComputeDefaultInputKey(PreviewSession session)
        {
            var canonical = new StringBuilder();
            canonical.Append(session.ManifestHash ?? string.Empty).Append('|')
                .Append(session.GeometryProfileHash ?? string.Empty).Append('|')
                .Append(session.AuthoringSourceHash ?? string.Empty).Append('|')
                .Append(session.ObstacleGeometryHash ?? string.Empty).Append('|')
                .Append(session.Plan.Seed).Append('|');
            foreach (var placement in session.Placements.Where(value => value != null).OrderBy(value => value.placementId, StringComparer.Ordinal))
            {
                canonical.Append(placement.placementId).Append(':')
                    .Append(placement.descriptor != null ? placement.descriptor.AssetId : string.Empty).Append(':');
                Append(canonical, placement.position);
                Append(canonical, Canonical(placement.rotation));
                Append(canonical, placement.scale);
                canonical.Append('|');
            }
            foreach (var view in BuildCaptureViews(session))
            {
                canonical.Append(view.Id).Append(':');
                Append(canonical, view.Position);
                Append(canonical, Canonical(view.Rotation));
                canonical.Append(':').Append(Quantize(view.FieldOfView)).Append(':').Append(Quantize(view.OrthographicSize)).Append('|');
            }
            return Sha256(Encoding.UTF8.GetBytes(canonical.ToString()));
        }

        private static void Append(StringBuilder builder, Vector3 value) => builder.Append(Quantize(value.x)).Append(',').Append(Quantize(value.y)).Append(',').Append(Quantize(value.z)).Append(';');
        private static void Append(StringBuilder builder, Quaternion value) => builder.Append(Quantize(value.x)).Append(',').Append(Quantize(value.y)).Append(',').Append(Quantize(value.z)).Append(',').Append(Quantize(value.w)).Append(';');
        private static int Quantize(float value) => Mathf.RoundToInt(value * 10000f);

        private static Quaternion Canonical(Quaternion value)
        {
            value = value.normalized;
            if (value.w < 0f || (Mathf.Approximately(value.w, 0f) && (value.x < 0f || (Mathf.Approximately(value.x, 0f) && (value.y < 0f || (Mathf.Approximately(value.y, 0f) && value.z < 0f))))))
                value = new Quaternion(-value.x, -value.y, -value.z, -value.w);
            return value;
        }

        private static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(stream));
        }

        private static string Sha256(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(bytes));
        }

        private static string Hex(IEnumerable<byte> bytes) => string.Concat(bytes.Select(value => value.ToString("x2")));

        private static Quaternion LookAt(Vector3 position, Vector3 target)
        {
            var direction = target - position;
            return direction.sqrMagnitude > 0.000001f ? Quaternion.LookRotation(direction.normalized, Vector3.up) : Quaternion.identity;
        }

        private static string HierarchyPath(Transform value)
        {
            if (value == null) return string.Empty;
            var names = new Stack<string>();
            while (value != null)
            {
                names.Push(value.name);
                value = value.parent;
            }
            return string.Join("/", names);
        }

        private static void ValidateSession(PreviewSession session)
        {
            if (session?.Plan?.Room == null || session.Plan.Room.AuthoringBounds == null)
                throw new InvalidOperationException("An active room preview with authoring bounds is required.");
        }

        private static Scene ResolveCaptureScene(PreviewSession session)
        {
            if (session.Root != null && session.Root.scene.IsValid()) return session.Root.scene;
            return session.Plan.Room.gameObject.scene;
        }

        private static string Render(string directory, string name, Vector3 position, Quaternion rotation, bool orthographic, float orthographicSize, float fieldOfView, Scene captureScene)
        {
            var renderOverride = RenderOverrideForTests;
            if (renderOverride != null) return renderOverride(directory, name);

            var previousActiveScene = SceneManager.GetActiveScene();
            var restoreActiveScene = captureScene.IsValid() && captureScene.isLoaded &&
                                     previousActiveScene.IsValid() && previousActiveScene.isLoaded &&
                                     previousActiveScene != captureScene &&
                                     SceneManager.SetActiveScene(captureScene);
            var cameraObject = new GameObject("Concept Room Capture Camera") { hideFlags = HideFlags.HideAndDontSave };
            if (captureScene.IsValid()) SceneManager.MoveGameObjectToScene(cameraObject, captureScene);
            var camera = cameraObject.AddComponent<Camera>();
            var previousSceneCullingMask = camera.overrideSceneCullingMask;
            if (captureScene.IsValid()) camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(captureScene);
            camera.transform.SetPositionAndRotation(position, rotation);
            camera.orthographic = orthographic;
            camera.orthographicSize = Mathf.Max(0.1f, orthographicSize);
            camera.fieldOfView = fieldOfView;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.008f, 0.009f, 0.012f, 1f);
            camera.allowHDR = true;

            var renderTexture = RenderTexture.GetTemporary(CaptureSize, CaptureSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var texture = new Texture2D(CaptureSize, CaptureSize, TextureFormat.RGB24, false, false);
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, CaptureSize, CaptureSize), 0, 0, false);
                texture.Apply(false, false);
                var path = Path.Combine(directory, $"{name}.png");
                File.WriteAllBytes(path, texture.EncodeToPNG());
                return path;
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                camera.overrideSceneCullingMask = previousSceneCullingMask;
                RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                if (restoreActiveScene && previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                    SceneManager.SetActiveScene(previousActiveScene);
            }
        }

        [Serializable]
        private sealed class CaptureManifest
        {
            public int version;
            public string inputKey;
            public string captureSetHash;
            public List<CaptureManifestEntry> entries = new();
        }

        [Serializable]
        private sealed class CaptureManifestEntry
        {
            public string viewId;
            public string fileName;
            public string contentHash;
        }

        private readonly struct CaptureView
        {
            public readonly string Id;
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly bool Orthographic;
            public readonly float OrthographicSize;
            public readonly float FieldOfView;

            public CaptureView(string id, Vector3 position, Quaternion rotation, bool orthographic, float orthographicSize, float fieldOfView)
            {
                Id = id;
                Position = position;
                Rotation = rotation;
                Orthographic = orthographic;
                OrthographicSize = orthographicSize;
                FieldOfView = fieldOfView;
            }
        }

        private readonly struct ArrangementSurface
        {
            public readonly Vector3 Origin;
            public readonly Vector3 Normal;
            public readonly Vector3 Tangent;
            public readonly Vector3 Bitangent;
            public readonly Vector2 Size;

            public ArrangementSurface(Vector3 origin, Vector3 normal, Vector3 tangent, Vector3 bitangent, Vector2 size)
            {
                Origin = origin;
                Normal = normal;
                Tangent = tangent;
                Bitangent = bitangent;
                Size = size;
            }
        }
    }
}
