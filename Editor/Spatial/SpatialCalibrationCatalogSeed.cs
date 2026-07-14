using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    [Serializable]
    public sealed class SpatialCalibrationCatalogSeedRequest
    {
        public int schemaVersion = 1;
        public string descriptorFolder = "Assets/SpatialCalibration/Descriptors";
        public List<SpatialCalibrationCatalogSeedItem> items = new();
    }

    [Serializable]
    public sealed class SpatialCalibrationCatalogSeedItem
    {
        public string assetPath;
        public string template = nameof(SpatialCalibrationTemplate.FloorSupported);
        public string role = nameof(DecorRole.Clutter);
    }

    public static class SpatialCalibrationCatalogSeed
    {
        public const string RequestRelativePath = "Library/DungeonDecorator/CalibrationWorkflow/catalog-seed.json";

        public static int ImportIfPresent()
        {
            var requestPath = ProjectPath(RequestRelativePath);
            if (!File.Exists(requestPath)) return 0;

            var request = JsonUtility.FromJson<SpatialCalibrationCatalogSeedRequest>(File.ReadAllText(requestPath));
            if (request == null || request.schemaVersion != 1 || request.items == null)
                throw new InvalidDataException("Unsupported calibration catalog seed request.");
            var descriptorFolder = NormalizeAssetPath(request.descriptorFolder);
            EnsureFolder(descriptorFolder);

            var imported = 0;
            foreach (var item in request.items)
            {
                var assetPath = NormalizeAssetPath(item.assetPath);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null) throw new FileNotFoundException("Calibration seed asset was not found.", assetPath);
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (string.IsNullOrWhiteSpace(guid)) throw new InvalidDataException($"Asset GUID is missing: {assetPath}");
                var descriptorPath = $"{descriptorFolder}/{Sanitize(prefab.name)}_{guid.Substring(0, 8)}.asset";
                if (AssetDatabase.LoadAssetAtPath<DecorAssetDescriptor>(descriptorPath) != null) continue;

                var template = ParseTemplate(item.template);
                var role = Enum.TryParse<DecorRole>(item.role, true, out var parsedRole) ? parsedRole : DecorRole.Clutter;
                var surface = template == SpatialCalibrationTemplate.WallMounted ? PlacementSurface.Wall : PlacementSurface.Floor;
                var requirement = template switch
                {
                    SpatialCalibrationTemplate.WallMounted => ContactRequirement.WallMounted,
                    SpatialCalibrationTemplate.WallBackedFloorSupported => ContactRequirement.WallBacked,
                    _ => ContactRequirement.FloorSupported
                };

                var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
                descriptor.InitializeFromScan(guid, prefab, DecorAssetScanner.CalculateLocalBounds(prefab), DecorAssetType.Prop);
                descriptor.ConfigureMetadata("kaykit-dungeon", new[] { role }, surface, isReviewed: false);
                var geometry = DecorAssetScanner.BuildGeometryProfile(prefab, assetPath);
                geometry.contact = ContactRules.Defaults(requirement);
                geometry.reviewed = false;
                geometry.Normalize();
                descriptor.ConfigureGeometry(geometry);
                AssetDatabase.CreateAsset(descriptor, descriptorPath);
                EditorUtility.SetDirty(descriptor);
                imported++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            File.Delete(requestPath);
            return imported;
        }

        public static SpatialCalibrationTemplate ParseTemplate(string value)
        {
            if (!Enum.TryParse<SpatialCalibrationTemplate>(value, true, out var template))
                throw new InvalidDataException($"Unsupported calibration template: {value}");
            return template;
        }

        private static string NormalizeAssetPath(string value)
        {
            var path = (value ?? string.Empty).Replace('\\', '/').Trim();
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || path.Contains("../", StringComparison.Ordinal))
                throw new InvalidDataException($"Calibration seed path must stay under Assets: {value}");
            return path.TrimEnd('/');
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            var name = Path.GetFileName(folder);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
                throw new InvalidDataException($"Descriptor folder is invalid: {folder}");
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static string Sanitize(string value) => Path.GetInvalidFileNameChars()
            .Aggregate(value, (current, invalid) => current.Replace(invalid, '_'));

        private static string ProjectPath(string relative) => Path.GetFullPath(Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
            relative));
    }
}
