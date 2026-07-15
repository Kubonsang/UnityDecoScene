using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityDecoScene.DungeonDecorator.Editor
{
    public static class DecorAssetScanner
    {
        public static IReadOnlyList<DecorAssetDescriptor> ScanFolder(string prefabFolder, string descriptorFolder, DecorCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (!AssetDatabase.IsValidFolder(prefabFolder)) throw new ArgumentException($"Invalid prefab folder: {prefabFolder}");

            EnsureFolder(descriptorFolder);
            var descriptors = new List<DecorAssetDescriptor>();
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { prefabFolder });

            foreach (var guid in guids.OrderBy(value => value, StringComparer.Ordinal))
            {
                var prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) continue;

                var descriptorPath = $"{descriptorFolder}/{SanitizeFileName(prefab.name)}_{guid[..8]}.asset";
                var descriptor = AssetDatabase.LoadAssetAtPath<DecorAssetDescriptor>(descriptorPath);
                var dependencyHash = AssetDatabase.GetAssetDependencyHash(prefabPath).ToString();
                if (descriptor == null)
                {
                    descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
                    descriptor.InitializeFromScan(guid, prefab, CalculateLocalBounds(prefab), DetectAssetType(prefab));
                    AssetDatabase.CreateAsset(descriptor, descriptorPath);
                    descriptor.ConfigureGeometry(BuildGeometryProfile(prefab, prefabPath));
                }
                else if (descriptor.Geometry == null ||
                         descriptor.Geometry.collisionProxies == null ||
                         descriptor.Geometry.collisionProxies.Count == 0 ||
                         !string.Equals(descriptor.Geometry.dependencyHash, dependencyHash, StringComparison.Ordinal))
                {
                    descriptor.RefreshScanData(guid, prefab, CalculateLocalBounds(prefab), DetectAssetType(prefab));
                    descriptor.ConfigureGeometry(BuildGeometryProfile(prefab, prefabPath));
                }

                descriptors.Add(descriptor);
                EditorUtility.SetDirty(descriptor);
            }

            catalog.ReplaceAll(descriptors);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return descriptors;
        }

        public static Bounds CalculateLocalBounds(GameObject prefab)
        {
            var root = LoadAnalysisRoot(prefab, out var prefabContents);
            try
            {
                return CalculateBoundsFromRoot(root);
            }
            finally
            {
                UnloadAnalysisRoot(root, prefabContents);
            }
        }

        public static DecorGeometryProfile BuildGeometryProfile(GameObject prefab, string prefabPath = null)
        {
            var path = string.IsNullOrWhiteSpace(prefabPath) ? AssetDatabase.GetAssetPath(prefab) : prefabPath;
            var root = LoadAnalysisRoot(prefab, out var prefabContents);
            try
            {
                var proxies = new List<OrientedBoxProxy>();
                var colliders = root.GetComponentsInChildren<Collider>(true);
                if (colliders.Length > 0)
                {
                    foreach (var collider in colliders.OrderBy(value => HierarchyPath(root.transform, value.transform), StringComparer.Ordinal))
                        proxies.Add(ColliderProxy(root.transform, collider, proxies.Count));
                }
                else
                {
                    foreach (var renderer in root.GetComponentsInChildren<Renderer>(true).OrderBy(value => HierarchyPath(root.transform, value.transform), StringComparer.Ordinal))
                        proxies.Add(RendererProxy(root.transform, renderer, proxies.Count));
                }

                var aggregate = CalculateBoundsFromRoot(root);
                var profile = new DecorGeometryProfile
                {
                    source = colliders.Length > 0 ? GeometrySource.Collider : GeometrySource.RendererBounds,
                    collisionProxies = proxies,
                    forwardAxis = Vector3.forward,
                    upAxis = Vector3.up,
                    pivotOffset = aggregate.center,
                    bottomContact = new ContactFrame { frameId = "bottom", localPoint = new Vector3(aggregate.center.x, aggregate.min.y, aggregate.center.z), localNormal = Vector3.down, localTangent = Vector3.right, size = new Vector2(aggregate.size.x, aggregate.size.z) },
                    backContact = new ContactFrame { frameId = "back", localPoint = new Vector3(aggregate.center.x, aggregate.center.y, aggregate.min.z), localNormal = Vector3.back, localTangent = Vector3.right, size = new Vector2(aggregate.size.x, aggregate.size.y) },
                    topContact = new ContactFrame { frameId = "top", localPoint = new Vector3(aggregate.center.x, aggregate.max.y, aggregate.center.z), localNormal = Vector3.up, localTangent = Vector3.right, size = new Vector2(aggregate.size.x, aggregate.size.z) },
                    contact = ContactRules.Defaults(ContactRequirement.FloorSupported),
                    inferenceConfidence = colliders.Length > 0 ? 0.85f : 0.6f,
                    reviewed = false,
                    dependencyHash = AssetDatabase.GetAssetDependencyHash(path).ToString()
                };
                profile.Normalize();
                return profile;
            }
            finally
            {
                UnloadAnalysisRoot(root, prefabContents);
            }
        }

        private static GameObject LoadAnalysisRoot(GameObject prefab, out bool prefabContents)
        {
            var path = AssetDatabase.GetAssetPath(prefab);
            prefabContents = string.Equals(Path.GetExtension(path), ".prefab", StringComparison.OrdinalIgnoreCase);
            if (prefabContents) return PrefabUtility.LoadPrefabContents(path);

            var instance = UnityEngine.Object.Instantiate(prefab);
            instance.name = prefab.name;
            instance.hideFlags = HideFlags.HideAndDontSave;
            foreach (var transform in instance.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags = HideFlags.HideAndDontSave;
            Physics.SyncTransforms();
            return instance;
        }

        private static void UnloadAnalysisRoot(GameObject root, bool prefabContents)
        {
            if (root == null) return;
            if (prefabContents) PrefabUtility.UnloadPrefabContents(root);
            else UnityEngine.Object.DestroyImmediate(root);
        }

        private static Bounds CalculateBoundsFromRoot(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var colliders = root.GetComponentsInChildren<Collider>(true);
            var hasBounds = false;
            var worldBounds = new Bounds(root.transform.position, Vector3.zero);

            foreach (var renderer in renderers)
            {
                if (!hasBounds)
                {
                    worldBounds = renderer.bounds;
                    hasBounds = true;
                }
                else worldBounds.Encapsulate(renderer.bounds);
            }

            if (!hasBounds)
            {
                foreach (var collider in colliders)
                {
                    if (!hasBounds)
                    {
                        worldBounds = collider.bounds;
                        hasBounds = true;
                    }
                    else worldBounds.Encapsulate(collider.bounds);
                }
            }

            if (!hasBounds) return new Bounds(Vector3.zero, Vector3.one);
            var localCenter = root.transform.InverseTransformPoint(worldBounds.center);
            var localSize = root.transform.InverseTransformVector(worldBounds.size);
            return new Bounds(localCenter, Abs(localSize));
        }

        private static OrientedBoxProxy ColliderProxy(Transform root, Collider collider, int index)
        {
            if (collider is BoxCollider box)
            {
                var center = root.InverseTransformPoint(box.transform.TransformPoint(box.center));
                var scale = box.transform.lossyScale;
                var size = Vector3.Scale(box.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                var rotation = Quaternion.Inverse(root.rotation) * box.transform.rotation;
                return new OrientedBoxProxy($"collider-{index}", center, size, rotation);
            }
            var bounds = collider.bounds;
            return new OrientedBoxProxy($"collider-{index}", root.InverseTransformPoint(bounds.center), Abs(root.InverseTransformVector(bounds.size)), Quaternion.identity);
        }

        private static OrientedBoxProxy RendererProxy(Transform root, Renderer renderer, int index)
        {
            var bounds = renderer.localBounds;
            var center = root.InverseTransformPoint(renderer.transform.TransformPoint(bounds.center));
            var scale = renderer.transform.lossyScale;
            var size = Vector3.Scale(bounds.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            var rotation = Quaternion.Inverse(root.rotation) * renderer.transform.rotation;
            return new OrientedBoxProxy($"renderer-{index}", center, size, rotation);
        }

        private static Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        private static string HierarchyPath(Transform root, Transform value)
        {
            var parts = new Stack<string>();
            for (var current = value; current != null && current != root; current = current.parent) parts.Push(current.name);
            return string.Join("/", parts);
        }

        private static DecorAssetType DetectAssetType(GameObject prefab)
        {
            if (prefab.GetComponentInChildren<Light>(true) != null) return DecorAssetType.Light;
            var components = prefab.GetComponentsInChildren<Component>(true);
            if (components.Any(component => component != null && component.GetType().FullName == "UnityEngine.Rendering.Universal.DecalProjector"))
                return DecorAssetType.Decal;
            return DecorAssetType.Prop;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var normalized = folder.Replace('\\', '/').TrimEnd('/');
            var parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            var name = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException($"Descriptor folder must be inside Assets: {folder}");
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            return value;
        }
    }
}
