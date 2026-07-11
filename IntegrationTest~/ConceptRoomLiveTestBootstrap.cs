using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityDecoScene.DungeonDecorator;
using UnityDecoScene.DungeonDecorator.Editor;

[InitializeOnLoad]
public static class ConceptRoomKayKitShellBootstrap
{
    private const string TestRoot = "Assets/DecoratorV02Test";
    private const string ScenePath = "Assets/Scenes/DecoratorV02_Test.unity";
    private static readonly string MarkerPath = Path.GetFullPath("Library/DungeonDecorator/TestRuns/bootstrap-v02-kaykit-shell.done");

    static ConceptRoomKayKitShellBootstrap()
    {
        EditorApplication.delayCall += RunOnce;
    }

    [MenuItem("Tools/Concept Room Decorator/Run KayKit Dungeon Shell Test")]
    public static void RunFromMenu()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        RunOnce();
    }

    private static void RunOnce()
    {
        if (File.Exists(MarkerPath)) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += RunOnce;
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath));
        try
        {
            BuildTest();
            File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            File.WriteAllText(Path.ChangeExtension(MarkerPath, ".failed.txt"), exception.ToString());
        }
    }

    private static void BuildTest()
    {
        RoomPreviewManager.DiscardPreview();
        if (AssetDatabase.IsValidFolder(TestRoot)) AssetDatabase.DeleteAsset(TestRoot);
        EnsureFolder(TestRoot);
        EnsureFolder($"{TestRoot}/Data");
        EnsureFolder($"{TestRoot}/Materials");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "DecoratorV02_Test";
        var room = BuildRoom();
        RoomSurfaceScanner.Scan(room);
        RoomSurfaceScanner.SetReviewed(room, true);
        room.RefreshChildren();

        var descriptors = new List<DecorAssetDescriptor>
        {
            CreateDescriptor("bookcase_double_decoratedA", DecorRole.Hero, PlacementSurface.Wall, ContactRequirement.WallBacked, "archive", "hero-bookcase"),
            CreateDescriptor("banner_patternA_red", DecorRole.StoryEvidence, PlacementSurface.Wall, ContactRequirement.WallMounted, "warden", "red-banner"),
            CreateDescriptor("table_long_decorated_A", DecorRole.Support, PlacementSurface.Floor, ContactRequirement.FloorSupported, "archive", "work-table"),
            CreateDescriptor("chair", DecorRole.Support, PlacementSurface.Floor, ContactRequirement.FloorSupported, "archive", "chair"),
            CreateDescriptor("chest_large", DecorRole.StoryEvidence, PlacementSurface.Floor, ContactRequirement.FloorSupported, "storage", "sealed-chest"),
            CreateDescriptor("barrel_small_stack", DecorRole.Clutter, PlacementSurface.Floor, ContactRequirement.FloorSupported, "storage", "provisions")
        };

        var catalog = ScriptableObject.CreateInstance<DecorCatalog>();
        catalog.name = "KayKit Contact Test Catalog";
        catalog.ReplaceAll(descriptors);
        AssetDatabase.CreateAsset(catalog, $"{TestRoot}/Data/KayKitContactCatalog.asset");

        var brief = ScriptableObject.CreateInstance<RoomConceptBrief>();
        brief.name = "Forgotten Warden Archive";
        AssetDatabase.CreateAsset(brief, $"{TestRoot}/Data/ForgottenWardenArchive.asset");

        var elements = new[]
        {
            Element("hero-bookcase", descriptors[0], DecorRole.Hero, PreferredZone.Focal, 1),
            Element("red-banner", descriptors[1], DecorRole.StoryEvidence, PreferredZone.Perimeter, 1),
            Element("work-table", descriptors[2], DecorRole.Support, PreferredZone.Center, 1),
            Element("chairs", descriptors[3], DecorRole.Support, PreferredZone.Center, 2, CompositionRelation.Surrounds, "work-table", 1.4f),
            Element("sealed-chest", descriptors[4], DecorRole.StoryEvidence, PreferredZone.Corner, 1),
            Element("provisions", descriptors[5], DecorRole.Clutter, PreferredZone.Corner, 2)
        };
        var plan = ScriptableObject.CreateInstance<RoomCompositionPlan>();
        plan.name = "KayKit Contact Test Plan";
        plan.Configure(room, brief, catalog, 240711, 0.45f, elements);
        AssetDatabase.CreateAsset(plan, $"{TestRoot}/Data/KayKitContactPlan.asset");

        AssetDatabase.SaveAssets();
        EditorSceneManager.SaveScene(scene, ScenePath);
        var preview = RoomPreviewManager.GeneratePreview(plan, false);
        var report = RoomPreviewManager.ValidateCurrent();
        var captures = RoomCaptureService.CaptureAll(preview);
        WriteReport(preview, report, captures);

        Selection.activeGameObject = preview.Root;
        if (SceneView.lastActiveSceneView != null)
            SceneView.lastActiveSceneView.LookAt(new Vector3(0f, 1.6f, 0.5f), Quaternion.Euler(18f, 205f, 0f), 9f);
        EditorApplication.ExecuteMenuItem("Window/Concept Room Decorator");
        Debug.Log($"[DecoratorV02Test] COMPLETE placements={preview.Placements.Count} gaps={preview.AssetGaps?.gaps.Count ?? 0} errors={report.ErrorCount} captures={captures.Count} report={ReportPath()}");
    }

    private static ConceptRoom BuildRoom()
    {
        var root = new GameObject("Forgotten Warden Archive - Contact Test");
        var structure = new GameObject("KayKit Dungeon Shell").transform;
        structure.SetParent(root.transform, false);
        var wallModel = FindModel("wall");
        var wallBounds = DecorAssetScanner.CalculateLocalBounds(wallModel);
        var segmentLength = Mathf.Max(wallBounds.size.x, wallBounds.size.z);
        var roomWidth = segmentLength * 6f;
        var roomDepth = segmentLength * 5f;
        var roomHeight = Mathf.Max(4f, wallBounds.size.y);

        var bounds = root.AddComponent<BoxCollider>();
        bounds.isTrigger = true;
        bounds.center = new Vector3(0f, roomHeight * 0.5f, 0f);
        bounds.size = new Vector3(roomWidth, roomHeight, roomDepth);

        var floorColliderObject = new GameObject("Reviewed Flat Floor Collider");
        floorColliderObject.transform.SetParent(root.transform, false);
        floorColliderObject.transform.localPosition = new Vector3(0f, -0.1f, 0f);
        var floorCollider = floorColliderObject.AddComponent<BoxCollider>();
        floorCollider.size = new Vector3(roomWidth, 0.2f, roomDepth);
        BuildKayKitFloor(structure, roomWidth, roomDepth);
        BuildKayKitWalls(structure, roomWidth, roomDepth, segmentLength);
        BuildCornerPillars(structure, roomWidth, roomDepth);

        var observationObject = new GameObject("Entrance Observation");
        observationObject.transform.SetParent(root.transform, false);
        observationObject.transform.position = new Vector3(0f, 1.65f, -roomDepth * 0.5f + 0.3f);
        observationObject.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.6f, roomDepth * 0.12f) - observationObject.transform.position, Vector3.up);
        var observation = observationObject.AddComponent<RoomObservationPoint>();
        var serializedObservation = new SerializedObject(observation);
        serializedObservation.FindProperty("fieldOfView").floatValue = 105f;
        serializedObservation.FindProperty("primary").boolValue = true;
        serializedObservation.ApplyModifiedPropertiesWithoutUndo();

        var key = new GameObject("Key Light").AddComponent<Light>();
        key.transform.SetParent(root.transform, false);
        key.transform.position = new Vector3(-roomWidth * 0.23f, roomHeight * 0.72f, -roomDepth * 0.1f);
        key.type = LightType.Point;
        key.color = new Color(1f, 0.61f, 0.32f);
        key.intensity = 280f;
        key.range = 7f;
        var fill = new GameObject("Cool Fill").AddComponent<Light>();
        fill.transform.SetParent(root.transform, false);
        fill.transform.position = new Vector3(roomWidth * 0.3f, roomHeight * 0.65f, roomDepth * 0.25f);
        fill.type = LightType.Point;
        fill.color = new Color(0.35f, 0.48f, 0.7f);
        fill.intensity = 110f;
        fill.range = 5.5f;

        var room = root.AddComponent<ConceptRoom>();
        room.Configure(bounds, new Collider[] { floorCollider });
        return room;
    }

    private static void BuildKayKitFloor(Transform parent, float roomWidth, float roomDepth)
    {
        var baseModel = FindModel("floor_tile_large");
        var baseBounds = DecorAssetScanner.CalculateLocalBounds(baseModel);
        var cellX = Mathf.Max(0.1f, baseBounds.size.x);
        var cellZ = Mathf.Max(0.1f, baseBounds.size.z);
        var countX = Mathf.Max(1, Mathf.RoundToInt(roomWidth / cellX));
        var countZ = Mathf.Max(1, Mathf.RoundToInt(roomDepth / cellZ));
        var variants = new[] { "floor_tile_large", "floor_tile_large", "floor_tile_large_rocks", "floor_tile_large", "floor_tile_big_grate" };
        for (var z = 0; z < countZ; z++)
        for (var x = 0; x < countX; x++)
        {
            var variant = variants[(x * 3 + z * 5) % variants.Length];
            var model = FindModel(variant);
            var localBounds = DecorAssetScanner.CalculateLocalBounds(model);
            var center = new Vector3(-roomWidth * 0.5f + cellX * (x + 0.5f), 0f, -roomDepth * 0.5f + cellZ * (z + 0.5f));
            var instance = InstantiateStructure(model, $"Floor {x}-{z} ({variant})", parent, Quaternion.identity);
            instance.transform.position = new Vector3(center.x - localBounds.center.x, -localBounds.max.y, center.z - localBounds.center.z);
        }
    }

    private static void BuildKayKitWalls(Transform parent, float roomWidth, float roomDepth, float segmentLength)
    {
        var north = new[] { "wall", "wall_cracked", "wall_inset_shelves_decoratedA", "wall", "wall_broken", "wall" };
        var south = new[] { "wall", "wall", "wall_doorway", "wall_doorway", "wall_cracked", "wall" };
        var sides = new[] { "wall", "wall_inset_candles", "wall_cracked", "wall", "wall_broken" };
        for (var index = 0; index < north.Length; index++)
        {
            var x = -roomWidth * 0.5f + segmentLength * (index + 0.5f);
            PlaceWall(parent, north[index], $"North Wall {index}", new Vector3(x, 0f, roomDepth * 0.5f), Quaternion.identity, WallSide.North);
            PlaceWall(parent, south[index], $"South Wall {index}", new Vector3(-x, 0f, -roomDepth * 0.5f), Quaternion.Euler(0f, 180f, 0f), WallSide.South);
        }
        for (var index = 0; index < sides.Length; index++)
        {
            var z = -roomDepth * 0.5f + segmentLength * (index + 0.5f);
            PlaceWall(parent, sides[index], $"East Wall {index}", new Vector3(roomWidth * 0.5f, 0f, z), Quaternion.Euler(0f, 90f, 0f), WallSide.East);
            PlaceWall(parent, sides[(index + 2) % sides.Length], $"West Wall {index}", new Vector3(-roomWidth * 0.5f, 0f, -z), Quaternion.Euler(0f, -90f, 0f), WallSide.West);
        }
    }

    private static void BuildCornerPillars(Transform parent, float roomWidth, float roomDepth)
    {
        var pillar = FindModel("pillar_decorated");
        foreach (var position in new[]
                 {
                     new Vector3(-roomWidth * 0.5f, 0f, -roomDepth * 0.5f),
                     new Vector3(roomWidth * 0.5f, 0f, -roomDepth * 0.5f),
                     new Vector3(-roomWidth * 0.5f, 0f, roomDepth * 0.5f),
                     new Vector3(roomWidth * 0.5f, 0f, roomDepth * 0.5f)
                 })
        {
            var instance = InstantiateStructure(pillar, "Dungeon Corner Pillar", parent, Quaternion.identity);
            var worldBounds = RendererBounds(instance);
            instance.transform.position += position - new Vector3(worldBounds.center.x, worldBounds.min.y, worldBounds.center.z);
        }
    }

    private static void PlaceWall(Transform parent, string assetName, string objectName, Vector3 anchor, Quaternion rotation, WallSide side)
    {
        var instance = InstantiateStructure(FindModel(assetName), objectName, parent, rotation);
        var bounds = RendererBounds(instance);
        var translation = new Vector3(anchor.x - bounds.center.x, -bounds.min.y, anchor.z - bounds.center.z);
        switch (side)
        {
            case WallSide.North: translation.z = anchor.z - bounds.min.z; break;
            case WallSide.South: translation.z = anchor.z - bounds.max.z; break;
            case WallSide.East: translation.x = anchor.x - bounds.min.x; break;
            case WallSide.West: translation.x = anchor.x - bounds.max.x; break;
        }
        instance.transform.position += translation;
    }

    private static GameObject InstantiateStructure(GameObject prefab, string name, Transform parent, Quaternion rotation)
    {
        var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
        if (instance == null) throw new InvalidOperationException($"Could not instantiate KayKit structure: {prefab.name}");
        instance.name = name;
        instance.transform.SetParent(parent, true);
        instance.transform.SetPositionAndRotation(Vector3.zero, rotation);
        return instance;
    }

    private static Bounds RendererBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(root.transform.position, Vector3.one);
        var bounds = renderers[0].bounds;
        for (var index = 1; index < renderers.Length; index++) bounds.Encapsulate(renderers[index].bounds);
        return bounds;
    }

    private enum WallSide { North, South, East, West }

    private static DecorAssetDescriptor CreateDescriptor(string assetName, DecorRole role, PlacementSurface surface, ContactRequirement requirement, params string[] motifs)
    {
        var prefab = FindModel(assetName);
        var path = AssetDatabase.GetAssetPath(prefab);
        var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
        descriptor.name = assetName;
        descriptor.InitializeFromScan(AssetDatabase.AssetPathToGUID(path), prefab, DecorAssetScanner.CalculateLocalBounds(prefab), DecorAssetType.Prop);
        descriptor.ConfigureMetadata("kaykit-dungeon-remastered", new[] { role }, surface, motifs, true);
        var geometry = DecorAssetScanner.BuildGeometryProfile(prefab, path);
        geometry.contact = ContactRules.Defaults(requirement);
        geometry.reviewed = true;
        geometry.Normalize();
        descriptor.ConfigureGeometry(geometry);
        AssetDatabase.CreateAsset(descriptor, $"{TestRoot}/Data/{assetName}.asset");
        return descriptor;
    }

    private static GameObject FindModel(string exactName)
    {
        var candidates = AssetDatabase.FindAssets($"{exactName} t:GameObject")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => Path.GetFileNameWithoutExtension(path).Equals(exactName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException($"KayKit model not found: {exactName}");
        return AssetDatabase.LoadAssetAtPath<GameObject>(candidates[0]);
    }

    private static CompositionElement Element(string id, DecorAssetDescriptor descriptor, DecorRole role, PreferredZone zone, int count, CompositionRelation relation = CompositionRelation.Independent, string anchor = null, float spacing = 1f) => new()
    {
        elementId = id,
        descriptorId = descriptor.AssetId,
        role = role,
        preferredZone = zone,
        count = count,
        relation = relation,
        anchorElementId = anchor,
        spacing = spacing
    };

    private static GameObject Cube(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = position;
        cube.transform.localScale = scale;
        cube.GetComponent<Renderer>().sharedMaterial = material;
        return cube;
    }

    private static Material CreateMaterial(string name, Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        var material = new Material(shader) { name = name };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        else material.color = color;
        AssetDatabase.CreateAsset(material, $"{TestRoot}/Materials/{name}.mat");
        return material;
    }

    private static void WriteReport(PreviewSession preview, ValidationReport report, IReadOnlyList<string> captures)
    {
        var directory = Path.GetDirectoryName(ReportPath());
        Directory.CreateDirectory(directory);
        var lines = new List<string>
        {
            "Concept Room Decorator v0.2 live test",
            $"scene={ScenePath}",
            $"manifestHash={preview.ManifestHash}",
            $"geometryProfileHash={preview.GeometryProfileHash}",
            $"seed={preview.Plan.Seed}",
            $"placements={preview.Placements.Count}",
            $"assetGaps={preview.AssetGaps?.gaps.Count ?? 0}",
            $"technicalErrors={report.ErrorCount}"
        };
        foreach (var placement in preview.Placements)
            lines.Add($"placement={placement.descriptor.name}|role={placement.role}|surface={placement.surfaceId}|gap={placement.contactEvidence?.gap ?? -1f:0.0000}|penetration={placement.contactEvidence?.penetration ?? -1f:0.0000}|support={placement.contactEvidence?.supportCoverage ?? -1f:0.000}");
        foreach (var issue in report.issues)
            lines.Add($"issue={issue.severity}|{issue.code}|{issue.message}");
        foreach (var capture in captures) lines.Add($"capture={capture}");
        File.WriteAllLines(ReportPath(), lines);
    }

    private static string ReportPath() => Path.GetFullPath("Library/DungeonDecorator/TestRuns/latest.txt");

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
