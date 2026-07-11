using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GNF.DungeonGen.EditorTools
{
    /// <summary>
    /// Builds a single, authored "abandoned ritual archive" room from the existing
    /// Shrine showcase shell and KayKit Dungeon Remastered assets.
    /// </summary>
    public static class RitualArchiveShowcaseBuilder
    {
        private const string SourceScene = "Assets/Scenes/Shrine_Showcase.unity";
        private const string OutputScene = "Assets/Scenes/RitualArchive_Showcase.unity";
        private const string ModelRoot = "Assets/KayKit_DungeonRemastered_1.1_SOURCE/Assets/fbx(unity)/";
        private const string ArtRootName = "Ritual Archive Art Direction";
        private const string CapturePath = "RoomDecorArtifacts/RitualArchive_Showcase.png";

        private static readonly List<PlacedProp> FloorProps = new();

        [MenuItem("GNF/Scenes/Build Ritual Archive Showcase")]
        public static void BuildFromMenu()
        {
            BuildAndCapture();
            Debug.Log($"[RitualArchive] Built {OutputScene} and {CapturePath}");
        }

        // Unity command-line entry point.
        public static void BuildAndCapture()
        {
            FloorProps.Clear();
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SourceScene) == null)
                throw new FileNotFoundException("Source showcase scene is missing.", SourceScene);

            var scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
            ClearGeneratedDecorationGroups();
            RemoveExistingArtDirectionRoot();

            var artRoot = new GameObject(ArtRootName).transform;
            var heroRoot = CreateGroup("01_Hero_FocalTable", artRoot);
            var supportRoot = CreateGroup("02_SupportingArchive", artRoot);
            var storyRoot = CreateGroup("03_StoryEvidence", artRoot);
            var wallRoot = CreateGroup("04_WallRhythm", artRoot);
            var lightRoot = CreateGroup("05_Lighting", artRoot);
            CreateConceptNote(artRoot);

            BuildHero(heroRoot);
            BuildSupportingArchive(supportRoot);
            BuildStoryEvidence(storyRoot);
            BuildWallRhythm(wallRoot);
            ConfigureLighting(lightRoot);
            PrepareArtRootForRendering(artRoot);
            var camera = ConfigurePresentationCamera();

            ValidateRoomLayout();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, OutputScene))
                throw new InvalidOperationException($"Could not save {OutputScene}");
            AssetDatabase.SaveAssets();
            Capture(camera);
        }

        private static void BuildHero(Transform root)
        {
            var table = PlaceFloor("table_round_large.fbx", "Hero_RitualTable", new Vector3(18f, 0f, 18f), 15f, root, "hero");
            var tableTop = RendererBounds(table).max.y + 0.025f;

            PlaceOnSurface("candle_triple.fbx", "Hero_CandleCrown", new Vector3(18f, tableTop, 18f), -15f, root);
            PlaceOnSurface("book_brown.fbx", "Hero_OpenRecord_A", new Vector3(17.15f, tableTop, 18.15f), -25f, root);
            PlaceOnSurface("book_tan.fbx", "Hero_OpenRecord_B", new Vector3(18.75f, tableTop, 17.75f), 32f, root);
            PlaceOnSurface("bottle_A_labeled_brown.fbx", "Hero_SealedVial", new Vector3(18.35f, tableTop, 18.8f), 8f, root);

            PlaceFloor("candle_thin_lit.fbx", "Ritual_Candle_W", new Vector3(14f, 0f, 18f), 0f, root, "ritual-ring");
            PlaceFloor("candle_thin_lit.fbx", "Ritual_Candle_E", new Vector3(22f, 0f, 18f), 0f, root, "ritual-ring");
            PlaceFloor("candle_thin_lit.fbx", "Ritual_Candle_S", new Vector3(18f, 0f, 14f), 0f, root, "ritual-ring");
            PlaceFloor("candle_thin_lit.fbx", "Ritual_Candle_N", new Vector3(18f, 0f, 22f), 0f, root, "ritual-ring");
        }

        private static void BuildSupportingArchive(Transform root)
        {
            PlaceFloor("bookcase_double_decoratedA.fbx", "Archive_Back_Left", new Vector3(11f, 0f, 29.5f), 180f, root, "archive");
            PlaceFloor("bookcase_double_decoratedB.fbx", "Archive_Back_Right", new Vector3(25f, 0f, 29.5f), 180f, root, "archive");
            PlaceFloor("bookcase_single_decoratedA.fbx", "Archive_West", new Vector3(5.65f, 0f, 14f), 90f, root, "archive");
            PlaceFloor("bookcase_single_decoratedB.fbx", "Archive_East", new Vector3(30.35f, 0f, 23f), 270f, root, "archive");

            PlaceFloor("pillar_decorated.fbx", "Archive_Pillar_SW", new Vector3(10f, 0f, 10f), 0f, root, "pillar");
            PlaceFloor("pillar_decorated.fbx", "Archive_Pillar_SE", new Vector3(26f, 0f, 10f), 90f, root, "pillar");
            PlaceFloor("pillar_decorated.fbx", "Archive_Pillar_NW", new Vector3(10f, 0f, 26f), 180f, root, "pillar");
            PlaceFloor("pillar_decorated.fbx", "Archive_Pillar_NE", new Vector3(26f, 0f, 26f), 270f, root, "pillar");
        }

        private static void BuildStoryEvidence(Transform root)
        {
            var brokenTable = PlaceFloor("table_medium_broken.fbx", "Evidence_AbandonedDesk", new Vector3(8.5f, 0f, 21.5f), 72f, root, "story-desk");
            var deskTop = RendererBounds(brokenTable).max.y + 0.02f;
            PlaceOnSurface("book_grey.fbx", "Evidence_GreyLedger", new Vector3(8.15f, deskTop, 21.6f), 48f, root);
            PlaceOnSurface("bottle_C_brown.fbx", "Evidence_EmptyBottle", new Vector3(8.85f, deskTop, 21.25f), -18f, root);

            PlaceFloor("rocks_small.fbx", "Evidence_Collapse", new Vector3(29.0f, 0f, 6.2f), 22f, root, "rubble");
            PlaceFloor("rocks_small.fbx", "Evidence_Debris", new Vector3(23.8f, 0f, 6.2f), 118f, root, "rubble");
            PlaceFloor("candle_melted.fbx", "Evidence_MeltedCandle_A", new Vector3(15.2f, 0f, 15.15f), 0f, root, "ritual-ring");
            PlaceFloor("candle_lit.fbx", "Evidence_MeltedCandle_B", new Vector3(20.85f, 0f, 15.1f), 0f, root, "ritual-ring");
            PlaceFloor("candle_melted.fbx", "Evidence_MeltedCandle_C", new Vector3(15.15f, 0f, 20.85f), 0f, root, "ritual-ring");
            PlaceFloor("candle_lit.fbx", "Evidence_MeltedCandle_D", new Vector3(20.9f, 0f, 20.9f), 0f, root, "ritual-ring");
        }

        private static void BuildWallRhythm(Transform root)
        {
            PlaceWallCentered("banner_patternA_red.fbx", "Banner_Red_Left", new Vector3(12f, 2.25f, 31.58f), 180f, root);
            PlaceWallCentered("banner_patternA_red.fbx", "Banner_Red_Right", new Vector3(24f, 2.25f, 31.58f), 180f, root);
            PlaceWallCentered("shelf_small_candles.fbx", "Archive_CandleShelf", new Vector3(18f, 1.75f, 31.45f), 180f, root);
            PlaceWallCentered("torch_mounted.fbx", "Torch_Back_Left", new Vector3(7.8f, 2.35f, 31.55f), 180f, root);
            PlaceWallCentered("torch_mounted.fbx", "Torch_Back_Right", new Vector3(28.2f, 2.35f, 31.55f), 180f, root);
            PlaceWallCentered("torch_mounted.fbx", "Torch_West", new Vector3(4.45f, 2.35f, 24.5f), 90f, root);
            PlaceWallCentered("torch_mounted.fbx", "Torch_East", new Vector3(31.55f, 2.35f, 11.5f), 270f, root);
        }

        private static void ConfigureLighting(Transform root)
        {
            foreach (var light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (light.type != LightType.Directional) continue;
                light.transform.rotation = Quaternion.Euler(48f, -28f, 0f);
                light.color = new Color(0.56f, 0.61f, 0.78f);
                light.intensity = 0.32f;
                light.shadows = LightShadows.Soft;
            }

            CreatePointLight("Hero_WarmPool", new Vector3(18f, 4.2f, 18f), new Color(1f, 0.39f, 0.16f), 5.2f, 13f, root);
            CreatePointLight("Archive_ColdRim", new Vector3(18f, 4.8f, 27.2f), new Color(0.25f, 0.31f, 0.78f), 2.5f, 11f, root);
            CreatePointLight("TorchGlow_Left", new Vector3(8f, 2.7f, 29.6f), new Color(1f, 0.48f, 0.16f), 2.4f, 7f, root);
            CreatePointLight("TorchGlow_Right", new Vector3(28f, 2.7f, 29.6f), new Color(1f, 0.48f, 0.16f), 2.4f, 7f, root);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.11f, 0.13f, 0.22f);
            RenderSettings.ambientEquatorColor = new Color(0.07f, 0.055f, 0.08f);
            RenderSettings.ambientGroundColor = new Color(0.025f, 0.02f, 0.03f);
            RenderSettings.ambientIntensity = 0.52f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.055f, 0.045f, 0.08f);
            RenderSettings.fogDensity = 0.012f;
        }

        private static Camera ConfigurePresentationCamera()
        {
            var camera = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
            if (camera == null)
            {
                var cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                camera = cameraObject.AddComponent<Camera>();
            }

            camera.name = "RitualArchive_Camera";
            camera.transform.position = new Vector3(18f, 19.5f, -7.5f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(18f, 1.35f, 18f) - camera.transform.position, Vector3.up);
            camera.fieldOfView = 43f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 180f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.025f, 0.02f, 0.04f);
            camera.allowHDR = true;
            camera.cullingMask = ~0;
            camera.useOcclusionCulling = false;
            return camera;
        }

        private static void PrepareArtRootForRendering(Transform artRoot)
        {
            // The showcase scenes reserve layer 31 for authored room dressing.
            // Match that convention and explicitly enable imported model renderers.
            foreach (var child in artRoot.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = 31;

            foreach (var renderer in artRoot.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = true;
                renderer.forceRenderingOff = false;
            }
        }

        private static GameObject PlaceFloor(string assetName, string objectName, Vector3 position, float yaw, Transform parent, string overlapGroup)
        {
            var instance = InstantiateModel(assetName, objectName, parent);
            instance.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            var bounds = RendererBounds(instance);
            instance.transform.position += Vector3.up * (position.y - bounds.min.y);
            FloorProps.Add(new PlacedProp(instance, overlapGroup));
            return instance;
        }

        private static GameObject PlaceOnSurface(string assetName, string objectName, Vector3 position, float yaw, Transform parent)
        {
            var instance = InstantiateModel(assetName, objectName, parent);
            instance.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            var bounds = RendererBounds(instance);
            instance.transform.position += Vector3.up * (position.y - bounds.min.y);
            return instance;
        }

        private static GameObject PlaceWallCentered(string assetName, string objectName, Vector3 boundsCenter, float yaw, Transform parent)
        {
            var instance = InstantiateModel(assetName, objectName, parent);
            instance.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            instance.transform.position += boundsCenter - RendererBounds(instance).center;
            return instance;
        }

        private static GameObject InstantiateModel(string assetName, string objectName, Transform parent)
        {
            var path = ModelRoot + assetName;
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null) throw new FileNotFoundException($"KayKit model is missing: {path}");
            var instance = PrefabUtility.InstantiatePrefab(asset, SceneManager.GetActiveScene()) as GameObject;
            if (instance == null) throw new InvalidOperationException($"Could not instantiate {path}");
            instance.name = objectName;
            instance.transform.SetParent(parent, true);
            return instance;
        }

        private static void CreatePointLight(string name, Vector3 position, Color color, float intensity, float range, Transform parent)
        {
            var lightObject = new GameObject(name);
            lightObject.transform.SetParent(parent, true);
            lightObject.transform.position = position;
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.72f;
        }

        private static void ValidateRoomLayout()
        {
            const float min = 4.15f;
            const float max = 31.85f;
            var errors = new List<string>();
            foreach (var placed in FloorProps)
            {
                var bounds = RendererBounds(placed.GameObject);
                if (bounds.min.x < min || bounds.max.x > max || bounds.min.z < min || bounds.max.z > max)
                    errors.Add($"{placed.GameObject.name} extends outside the room interior: {bounds}");
            }

            for (var i = 0; i < FloorProps.Count; i++)
            for (var j = i + 1; j < FloorProps.Count; j++)
            {
                var a = FloorProps[i];
                var b = FloorProps[j];
                if (a.OverlapGroup == b.OverlapGroup && a.OverlapGroup == "ritual-ring") continue;
                if (FootprintsOverlap(RendererBounds(a.GameObject), RendererBounds(b.GameObject), 0.08f))
                    errors.Add($"Floor prop footprints overlap: {a.GameObject.name} / {b.GameObject.name}");
            }

            // Entrance-facing negative space: preserve the central approach from the south wall.
            var approach = new Bounds(new Vector3(18f, 1f, 9f), new Vector3(5.2f, 2f, 9f));
            foreach (var placed in FloorProps)
            {
                if (RendererBounds(placed.GameObject).Intersects(approach))
                    errors.Add($"{placed.GameObject.name} blocks the entrance-to-hero negative space.");
            }

            if (errors.Count > 0)
                throw new InvalidOperationException("Ritual Archive validation failed:\n" + string.Join("\n", errors));

            Debug.Log($"[RitualArchive] Validation passed: {FloorProps.Count} floor props, zero footprint overlaps, approach kept clear.");
        }

        private static bool FootprintsOverlap(Bounds a, Bounds b, float padding)
        {
            var x = a.min.x + padding < b.max.x && a.max.x - padding > b.min.x;
            var z = a.min.z + padding < b.max.z && a.max.z - padding > b.min.z;
            var y = a.min.y < b.max.y - 0.02f && a.max.y > b.min.y + 0.02f;
            return x && z && y;
        }

        private static Bounds RendererBounds(GameObject gameObject)
        {
            var renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new InvalidOperationException($"{gameObject.name} has no Renderer.");
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static Transform CreateGroup(string name, Transform parent)
        {
            var group = new GameObject(name).transform;
            group.SetParent(parent, false);
            return group;
        }

        private static void ClearGeneratedDecorationGroups()
        {
            var groups = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(transform => transform.name is "04_FloorProps" or "05_WallProps")
                .ToArray();
            foreach (var group in groups)
            {
                for (var index = group.childCount - 1; index >= 0; index--)
                    UnityEngine.Object.DestroyImmediate(group.GetChild(index).gameObject);
            }
        }

        private static void RemoveExistingArtDirectionRoot()
        {
            var existing = GameObject.Find(ArtRootName);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
        }

        private static void CreateConceptNote(Transform parent)
        {
            var note = new GameObject("CONCEPT — Abandoned ritual archive; warm forbidden knowledge against a cold ruined dungeon");
            note.transform.SetParent(parent, false);
        }

        private static void Capture(Camera camera)
        {
            const int width = 1280;
            const int height = 720;
            var captureFullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", CapturePath));
            Directory.CreateDirectory(Path.GetDirectoryName(captureFullPath) ?? "Temp");
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(captureFullPath, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(renderTexture);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private readonly struct PlacedProp
        {
            public readonly GameObject GameObject;
            public readonly string OverlapGroup;

            public PlacedProp(GameObject gameObject, string overlapGroup)
            {
                GameObject = gameObject;
                OverlapGroup = overlapGroup;
            }
        }
    }
}
