using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator
{
    [CreateAssetMenu(fileName = "DecorAssetDescriptor", menuName = "Concept Room Decorator/Decor Asset Descriptor")]
    public sealed class DecorAssetDescriptor : ScriptableObject
    {
        [SerializeField] private string assetId;
        [SerializeField] private GameObject prefab;
        [SerializeField] private DecorAssetType assetType = DecorAssetType.Prop;
        [SerializeField] private List<DecorRole> roles = new() { DecorRole.Clutter };
        [SerializeField] private PlacementSurface placementSurface = PlacementSurface.Floor;
        [SerializeField] private string styleSet = "default";
        [SerializeField] private List<string> themes = new();
        [SerializeField] private List<string> factions = new();
        [SerializeField] private List<string> eras = new();
        [SerializeField] private List<string> materials = new();
        [SerializeField] private List<string> motifs = new();
        [Range(0.1f, 10f)] [SerializeField] private float visualWeight = 1f;
        [Min(0f)] [SerializeField] private float clearance = 0.1f;
        [Min(0.01f)] [SerializeField] private float minimumScale = 1f;
        [Min(0.01f)] [SerializeField] private float maximumScale = 1f;
        [SerializeField] private RotationMode rotationMode = RotationMode.QuarterTurns;
        [SerializeField] private Vector3 localBoundsCenter;
        [SerializeField] private Vector3 localBoundsSize = Vector3.one;
        [SerializeField] private List<string> forbiddenAssetIds = new();
        [Min(1)] [SerializeField] private int maximumInstancesPerRoom = 20;
        [Min(0f)] [SerializeField] private float minimumLightIntensity;
        [Min(0f)] [SerializeField] private float maximumLightIntensity = 100000f;
        [Min(0f)] [SerializeField] private float maximumLightRange = 30f;
        [SerializeField] private bool reviewed;
        [SerializeField] private DecorGeometryProfile geometryProfile = new();

        public string AssetId => assetId;
        public GameObject Prefab => prefab;
        public DecorAssetType AssetType => assetType;
        public IReadOnlyList<DecorRole> Roles => roles;
        public PlacementSurface Surface => placementSurface;
        public string StyleSet => styleSet;
        public IReadOnlyList<string> Themes => themes;
        public IReadOnlyList<string> Factions => factions;
        public IReadOnlyList<string> Eras => eras;
        public IReadOnlyList<string> Materials => materials;
        public IReadOnlyList<string> Motifs => motifs;
        public float VisualWeight => visualWeight;
        public float Clearance => clearance;
        public float MinimumScale => minimumScale;
        public float MaximumScale => Mathf.Max(minimumScale, maximumScale);
        public RotationMode Rotation => rotationMode;
        public Vector3 LocalBoundsCenter => localBoundsCenter;
        public Vector3 LocalBoundsSize => localBoundsSize;
        public IReadOnlyList<string> ForbiddenAssetIds => forbiddenAssetIds;
        public int MaximumInstancesPerRoom => maximumInstancesPerRoom;
        public float MinimumLightIntensity => minimumLightIntensity;
        public float MaximumLightIntensity => Mathf.Max(minimumLightIntensity, maximumLightIntensity);
        public float MaximumLightRange => maximumLightRange;
        public bool Reviewed => reviewed;
        public DecorGeometryProfile Geometry => geometryProfile;

        public bool HasRole(DecorRole role) => roles.Contains(role);

        public void InitializeFromScan(string id, GameObject sourcePrefab, Bounds localBounds, DecorAssetType type)
        {
            assetId = id;
            prefab = sourcePrefab;
            localBoundsCenter = localBounds.center;
            localBoundsSize = localBounds.size;
            assetType = type;
            roles = new List<DecorRole> { type == DecorAssetType.Light ? DecorRole.LightingCue : type == DecorAssetType.Decal ? DecorRole.DecalCue : DecorRole.Clutter };
            placementSurface = type == DecorAssetType.Decal ? PlacementSurface.Wall : PlacementSurface.Floor;
            reviewed = false;
            geometryProfile = CreateBoundsGeometry(localBounds, placementSurface, false);
        }

        public void ConfigureMetadata(string targetStyleSet, IEnumerable<DecorRole> allowedRoles, PlacementSurface surface, IEnumerable<string> assetMotifs = null, bool isReviewed = true)
        {
            styleSet = string.IsNullOrWhiteSpace(targetStyleSet) ? "default" : targetStyleSet;
            roles = allowedRoles != null ? new List<DecorRole>(allowedRoles) : new List<DecorRole> { DecorRole.Clutter };
            placementSurface = surface;
            motifs = assetMotifs != null ? new List<string>(assetMotifs) : new List<string>();
            reviewed = isReviewed;
            geometryProfile ??= CreateBoundsGeometry(new Bounds(localBoundsCenter, localBoundsSize), surface, isReviewed);
            geometryProfile.reviewed = isReviewed;
            geometryProfile.contact = ContactRules.Defaults(surface switch
            {
                PlacementSurface.Wall => ContactRequirement.WallMounted,
                PlacementSurface.Ceiling => ContactRequirement.CeilingMounted,
                _ => ContactRequirement.FloorSupported
            });
            geometryProfile.Normalize();
        }

        public void RefreshScanData(string id, GameObject sourcePrefab, Bounds localBounds, DecorAssetType type)
        {
            assetId = id;
            prefab = sourcePrefab;
            localBoundsCenter = localBounds.center;
            localBoundsSize = localBounds.size;
            assetType = type;
            geometryProfile ??= CreateBoundsGeometry(localBounds, placementSurface, false);
        }

        public void ConfigureGeometry(DecorGeometryProfile profile)
        {
            geometryProfile = profile ?? throw new ArgumentNullException(nameof(profile));
            geometryProfile.Normalize();
        }

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(assetId))
            {
                assetId = Guid.NewGuid().ToString("N");
            }

            minimumScale = Mathf.Max(0.01f, minimumScale);
            maximumScale = Mathf.Max(minimumScale, maximumScale);
            maximumInstancesPerRoom = Mathf.Max(1, maximumInstancesPerRoom);
            maximumLightIntensity = Mathf.Max(minimumLightIntensity, maximumLightIntensity);
            maximumLightRange = Mathf.Max(0f, maximumLightRange);
            localBoundsSize = new Vector3(
                Mathf.Max(0.001f, localBoundsSize.x),
                Mathf.Max(0.001f, localBoundsSize.y),
                Mathf.Max(0.001f, localBoundsSize.z));
            geometryProfile ??= CreateBoundsGeometry(new Bounds(localBoundsCenter, localBoundsSize), placementSurface, false);
            geometryProfile.Normalize();
        }

        private static DecorGeometryProfile CreateBoundsGeometry(Bounds bounds, PlacementSurface surface, bool isReviewed)
        {
            var requirement = surface switch
            {
                PlacementSurface.Wall => ContactRequirement.WallMounted,
                PlacementSurface.Ceiling => ContactRequirement.CeilingMounted,
                _ => ContactRequirement.FloorSupported
            };
            return new DecorGeometryProfile
            {
                source = GeometrySource.RendererBounds,
                collisionProxies = new List<OrientedBoxProxy> { new("aggregate", bounds.center, bounds.size, Quaternion.identity) },
                pivotOffset = bounds.center,
                bottomContact = new ContactFrame { frameId = "bottom", localPoint = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z), localNormal = Vector3.down, localTangent = Vector3.right, size = new Vector2(bounds.size.x, bounds.size.z) },
                backContact = new ContactFrame { frameId = "back", localPoint = new Vector3(bounds.center.x, bounds.center.y, bounds.min.z), localNormal = Vector3.back, localTangent = Vector3.right, size = new Vector2(bounds.size.x, bounds.size.y) },
                contact = ContactRules.Defaults(requirement),
                inferenceConfidence = 0.5f,
                reviewed = isReviewed
            };
        }
    }
}
