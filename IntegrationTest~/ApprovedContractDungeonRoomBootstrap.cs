using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityDecoScene.DungeonDecorator;
using UnityDecoScene.DungeonDecorator.Editor;

public static class ApprovedContractDungeonRoomBootstrap
{
    private const string DataRoot = "Assets/ApprovedContractRoomPreview";
    private const string ScenePath = "Assets/Scenes/ApprovedContract_DungeonRoom.unity";
    private const string OutputRelative = "Library/DungeonDecorator/ApprovedRoomPreview";
    private const string StyleSet = "kaykit-dungeon";
    private const int ExpectedPlacements = 9;

    [MenuItem("Tools/Concept Room Decorator/Build Approved Contract Dungeon Preview")]
    public static void BuildFromMenu()
    {
        Build();
        Application.OpenURL(new Uri(Path.Combine(OutputDirectory(), "review.html")).AbsoluteUri);
    }

    public static void BuildForBatch()
    {
        try
        {
            Build();
            Debug.Log("[ApprovedRoomPreview] COMPLETE");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    private static void Build()
    {
        RoomPreviewManager.DiscardPreview();
        if (AssetDatabase.IsValidFolder(DataRoot)) AssetDatabase.DeleteAsset(DataRoot);
        EnsureFolder(DataRoot);
        EnsureFolder($"{DataRoot}/Data");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "ApprovedContract_DungeonRoom";
        var room = BuildRoomShell();
        RoomSurfaceScanner.Scan(room);
        RoomSurfaceScanner.SetReviewed(room, true);
        room.RefreshChildren();

        var descriptors = new Dictionary<string, DecorAssetDescriptor>(StringComparer.Ordinal)
        {
            ["bookcase"] = CreateApprovedDescriptor("bookcase_double_decoratedA", DecorRole.Support, PlacementSurface.Wall, "archive", "duty-ledgers"),
            ["table"] = CreateApprovedDescriptor("table_long_decorated_A", DecorRole.Hero, PlacementSurface.Floor, "watch-room", "warden-table"),
            ["chair"] = CreateApprovedDescriptor("chair", DecorRole.Support, PlacementSurface.Floor, "archive", "used-seat"),
            ["chest"] = CreateApprovedDescriptor("chest_large", DecorRole.StoryEvidence, PlacementSurface.Floor, "storage", "sealed-records"),
            ["barrels"] = CreateApprovedDescriptor("barrel_small_stack", DecorRole.Clutter, PlacementSurface.Floor, "storage", "provisions"),
            ["banner"] = CreateApprovedDescriptor("banner_patternA_red", DecorRole.StoryEvidence, PlacementSurface.Wall, "warden", "red-banner"),
            ["torch"] = CreateApprovedDescriptor("torch_mounted", DecorRole.LightingCue, PlacementSurface.Wall, "warden", "torch")
        };

        var catalog = ScriptableObject.CreateInstance<DecorCatalog>();
        catalog.name = "Approved Contract Dungeon Catalog";
        catalog.ReplaceAll(descriptors.Values);
        AssetDatabase.CreateAsset(catalog, $"{DataRoot}/Data/ApprovedContractDungeonCatalog.asset");

        var brief = ScriptableObject.CreateInstance<RoomConceptBrief>();
        brief.name = "Forgotten Warden Watch Room";
        AssetDatabase.CreateAsset(brief, $"{DataRoot}/Data/ForgottenWardenWatchRoom.asset");
        AssetDatabase.SaveAssets();

        var elements = new[]
        {
            Element("01-warden-table", descriptors["table"], DecorRole.Hero, PreferredZone.Focal, 1),
            Element("02-duty-ledgers", descriptors["bookcase"], DecorRole.Support, PreferredZone.Perimeter, 1, preferredSurfaceId: "wall-north"),
            Element("03-used-chairs", descriptors["chair"], DecorRole.Support, PreferredZone.Center, 2, CompositionRelation.Surrounds, "01-warden-table", 1.65f),
            Element("04-sealed-records", descriptors["chest"], DecorRole.StoryEvidence, PreferredZone.Corner, 1),
            Element("05-warden-banner", descriptors["banner"], DecorRole.StoryEvidence, PreferredZone.Perimeter, 1, preferredSurfaceId: "wall-north"),
            Element("06-west-torch", descriptors["torch"], DecorRole.LightingCue, PreferredZone.Perimeter, 1, preferredSurfaceId: "wall-west"),
            Element("07-east-torch", descriptors["torch"], DecorRole.LightingCue, PreferredZone.Perimeter, 1, preferredSurfaceId: "wall-east"),
            Element("08-provisions", descriptors["barrels"], DecorRole.Clutter, PreferredZone.Corner, 1)
        };

        PreviewSession preview = null;
        ValidationReport report = null;
        RoomCompositionPlan chosenPlan = null;
        for (var offset = 0; offset < 32; offset++)
        {
            var plan = ScriptableObject.CreateInstance<RoomCompositionPlan>();
            plan.name = "Approved Contract Dungeon Preview Plan";
            plan.Configure(room, brief, catalog, 240714 + offset, 0.35f, elements);
            preview = RoomPreviewManager.GeneratePreview(plan, false);
            report = RoomPreviewManager.ValidateCurrent();
            if (preview.Placements.Count == ExpectedPlacements && (preview.AssetGaps?.gaps.Count ?? 0) == 0 && report.ErrorCount == 0)
            {
                chosenPlan = plan;
                break;
            }
            RoomPreviewManager.DiscardPreview();
            UnityEngine.Object.DestroyImmediate(plan);
        }

        if (preview == null || report == null || chosenPlan == null)
            throw new InvalidOperationException("No deterministic seed produced a complete, technically valid room preview.");

        AddPreviewTorchLights(preview);
        ConfigurePresentationLighting();
        EditorSceneManager.SaveScene(scene, ScenePath);
        var captures = RoomCaptureService.CaptureAll(preview);
        WriteReview(preview, report, captures);
        Selection.activeGameObject = preview.Root;
        Debug.Log($"[ApprovedRoomPreview] seed={chosenPlan.Seed} placements={preview.Placements.Count} errors={report.ErrorCount} review={Path.Combine(OutputDirectory(), "review.html")}");
    }

    private static ConceptRoom BuildRoomShell()
    {
        var wallModel = FindModel("wall");
        var wallBounds = DecorAssetScanner.CalculateLocalBounds(wallModel);
        var segment = Mathf.Max(wallBounds.size.x, wallBounds.size.z);
        var width = segment * 5f;
        var depth = segment * 5f;
        var height = Mathf.Max(4f, wallBounds.size.y);

        var root = new GameObject("Forgotten Warden Watch Room");
        var shell = new GameObject("KayKit Dungeon Shell - One Entrance").transform;
        shell.SetParent(root.transform, false);

        var authoring = root.AddComponent<BoxCollider>();
        authoring.isTrigger = true;
        authoring.center = new Vector3(0f, height * 0.5f, 0f);
        authoring.size = new Vector3(width, height, depth);

        var floorObject = new GameObject("Reviewed Flat Floor Collider");
        floorObject.transform.SetParent(root.transform, false);
        floorObject.transform.localPosition = new Vector3(0f, -0.1f, 0f);
        var floor = floorObject.AddComponent<BoxCollider>();
        floor.size = new Vector3(width, 0.2f, depth);

        BuildUniformFloor(shell, width, depth);
        BuildStraightWalls(shell, width, depth, segment);
        BuildCornerPillars(shell, width, depth);

        var observationObject = new GameObject("Single Entrance Observation");
        observationObject.transform.SetParent(root.transform, false);
        observationObject.transform.position = new Vector3(0f, 1.65f, -depth * 0.5f + 0.35f);
        observationObject.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 1.55f, depth * 0.22f) - observationObject.transform.position, Vector3.up);
        var observation = observationObject.AddComponent<RoomObservationPoint>();
        var serializedObservation = new SerializedObject(observation);
        serializedObservation.FindProperty("label").stringValue = "South entrance";
        serializedObservation.FindProperty("fieldOfView").floatValue = 88f;
        serializedObservation.FindProperty("primary").boolValue = true;
        serializedObservation.ApplyModifiedPropertiesWithoutUndo();

        var clearObject = new GameObject("Entrance Sightline - Keep Clear");
        clearObject.transform.SetParent(root.transform, false);
        clearObject.transform.localPosition = new Vector3(0f, 1f, -depth * 0.28f);
        var clearCollider = clearObject.AddComponent<BoxCollider>();
        clearCollider.isTrigger = true;
        clearCollider.size = new Vector3(2.2f, 2f, depth * 0.42f);
        var clear = clearObject.AddComponent<KeepClearZone>();
        var serializedClear = new SerializedObject(clear);
        serializedClear.FindProperty("reason").stringValue = "single entrance and hero sightline";
        serializedClear.FindProperty("volume").objectReferenceValue = clearCollider;
        serializedClear.ApplyModifiedPropertiesWithoutUndo();

        var room = root.AddComponent<ConceptRoom>();
        room.Configure(authoring, new Collider[] { floor });
        return room;
    }

    private static void BuildUniformFloor(Transform parent, float width, float depth)
    {
        var model = FindModel("floor_tile_large");
        var bounds = DecorAssetScanner.CalculateLocalBounds(model);
        var cellX = Mathf.Max(0.1f, bounds.size.x);
        var cellZ = Mathf.Max(0.1f, bounds.size.z);
        var countX = Mathf.RoundToInt(width / cellX);
        var countZ = Mathf.RoundToInt(depth / cellZ);
        for (var z = 0; z < countZ; z++)
        for (var x = 0; x < countX; x++)
        {
            var center = new Vector3(-width * 0.5f + cellX * (x + 0.5f), 0f, -depth * 0.5f + cellZ * (z + 0.5f));
            var instance = InstantiateStructure(model, $"Floor {x}-{z}", parent, Quaternion.identity);
            instance.transform.position = new Vector3(center.x - bounds.center.x, -bounds.max.y, center.z - bounds.center.z);
        }
    }

    private static void BuildStraightWalls(Transform parent, float width, float depth, float segment)
    {
        for (var index = 0; index < 5; index++)
        {
            var x = -width * 0.5f + segment * (index + 0.5f);
            PlaceWall(parent, "wall", $"North Wall {index}", new Vector3(x, 0f, depth * 0.5f), Quaternion.identity, WallSide.North);
            var southAsset = index == 2 ? "wall_doorway" : "wall";
            PlaceWall(parent, southAsset, index == 2 ? "South Wall - Single Entrance" : $"South Wall {index}", new Vector3(x, 0f, -depth * 0.5f), Quaternion.Euler(0f, 180f, 0f), WallSide.South);
            var z = -depth * 0.5f + segment * (index + 0.5f);
            PlaceWall(parent, "wall", $"East Wall {index}", new Vector3(width * 0.5f, 0f, z), Quaternion.Euler(0f, 90f, 0f), WallSide.East);
            PlaceWall(parent, "wall", $"West Wall {index}", new Vector3(-width * 0.5f, 0f, z), Quaternion.Euler(0f, -90f, 0f), WallSide.West);
        }
    }

    private static void BuildCornerPillars(Transform parent, float width, float depth)
    {
        var pillar = FindModel("pillar_decorated");
        foreach (var position in new[]
                 {
                     new Vector3(-width * 0.5f, 0f, -depth * 0.5f), new Vector3(width * 0.5f, 0f, -depth * 0.5f),
                     new Vector3(-width * 0.5f, 0f, depth * 0.5f), new Vector3(width * 0.5f, 0f, depth * 0.5f)
                 })
        {
            var instance = InstantiateStructure(pillar, "Dungeon Corner Pillar", parent, Quaternion.identity);
            var bounds = RendererBounds(instance);
            instance.transform.position += position - new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
        }
    }

    private static DecorAssetDescriptor CreateApprovedDescriptor(string assetName, DecorRole role, PlacementSurface surface, params string[] motifs)
    {
        var prefab = FindModel(assetName);
        var prefabPath = AssetDatabase.GetAssetPath(prefab);
        var guid = AssetDatabase.AssetPathToGUID(prefabPath);
        var descriptor = ScriptableObject.CreateInstance<DecorAssetDescriptor>();
        descriptor.name = assetName;
        descriptor.InitializeFromScan(guid, prefab, DecorAssetScanner.CalculateLocalBounds(prefab), DecorAssetType.Prop);
        descriptor.ConfigureMetadata(StyleSet, new[] { role }, surface, motifs, true);
        var descriptorPath = $"{DataRoot}/Data/{assetName}.asset";
        AssetDatabase.CreateAsset(descriptor, descriptorPath);
        var contractPath = SpatialContractIO.ApprovedAssetPath(guid);
        if (!SpatialContractIO.ApplyApprovedAssetContract(contractPath, descriptor, out var reason))
            throw new InvalidOperationException($"Approved spatial contract could not be imported for {assetName}: {reason}");
        return descriptor;
    }

    private static CompositionElement Element(string id, DecorAssetDescriptor descriptor, DecorRole role, PreferredZone zone, int count,
        CompositionRelation relation = CompositionRelation.Independent, string anchor = null, float spacing = 1f, string preferredSurfaceId = null) => new()
    {
        elementId = id,
        descriptorId = descriptor.AssetId,
        role = role,
        preferredZone = zone,
        preferredSurfaceId = preferredSurfaceId,
        count = count,
        relation = relation,
        anchorElementId = anchor,
        spacing = spacing
    };

    private static void AddPreviewTorchLights(PreviewSession preview)
    {
        foreach (var placement in preview.Placements.Where(value => value.role == DecorRole.LightingCue && value.previewObject != null))
        {
            var lightObject = new GameObject($"{placement.previewObject.name} Warm Light") { hideFlags = HideFlags.DontSaveInEditor };
            lightObject.transform.SetParent(preview.Root.transform, true);
            lightObject.transform.position = placement.worldBounds.center + Vector3.up * 0.2f + placement.rotation * Vector3.forward * 0.2f;
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.48f, 0.18f);
            light.intensity = 85f;
            light.range = 3.4f;
            light.shadows = LightShadows.Soft;
        }
    }

    private static void ConfigurePresentationLighting()
    {
        var key = new GameObject("Soft Moonlight").AddComponent<Light>();
        key.type = LightType.Directional;
        key.transform.rotation = Quaternion.Euler(48f, -32f, 0f);
        key.color = new Color(0.38f, 0.46f, 0.68f);
        key.intensity = 0.7f;
        key.shadows = LightShadows.Soft;

        var fill = new GameObject("Warm Archive Fill").AddComponent<Light>();
        fill.type = LightType.Point;
        fill.transform.position = new Vector3(0f, 2.8f, 2.1f);
        fill.color = new Color(1f, 0.5f, 0.23f);
        fill.intensity = 95f;
        fill.range = 5.5f;
        fill.shadows = LightShadows.Soft;

        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.09f, 0.12f, 0.2f);
        RenderSettings.ambientEquatorColor = new Color(0.06f, 0.05f, 0.07f);
        RenderSettings.ambientGroundColor = new Color(0.025f, 0.02f, 0.025f);
        RenderSettings.ambientIntensity = 0.7f;
    }

    private static void WriteReview(PreviewSession preview, ValidationReport report, IReadOnlyList<string> captures)
    {
        var output = OutputDirectory();
        Directory.CreateDirectory(output);
        var copied = new List<string>();
        foreach (var capture in captures)
        {
            var destination = Path.Combine(output, Path.GetFileName(capture));
            File.Copy(capture, destination, true);
            copied.Add(Path.GetFileName(destination));
        }

        var placementRows = new StringBuilder();
        foreach (var placement in preview.Placements.OrderBy(value => value.elementId, StringComparer.Ordinal))
        {
            var contacts = placement.contactEvidenceSet != null && placement.contactEvidenceSet.Count > 0
                ? string.Join("<br>", placement.contactEvidenceSet.Select(value => $"{value.surfaceId}: gap {value.gap.ToString("0.000", CultureInfo.InvariantCulture)}m / support {(value.supportCoverage * 100f).ToString("0", CultureInfo.InvariantCulture)}%"))
                : "free-standing";
            placementRows.Append($"<tr><td>{placement.descriptor.Prefab.name}</td><td>{placement.role}</td><td>{contacts}</td></tr>");
        }

        var cards = string.Join(Environment.NewLine, copied.Select(path => $"<figure><img src=\"{path}\" alt=\"{path}\"><figcaption>{path}</figcaption></figure>"));
        var html = $@"<!doctype html><html lang='ko'><head><meta charset='utf-8'><title>승인 계약 던전 방 검수</title><style>
body{{margin:0;background:#11141b;color:#ece7dc;font:16px/1.55 system-ui,sans-serif}}main{{max-width:1320px;margin:auto;padding:32px}}h1{{margin:0 0 8px}}.status{{display:inline-block;background:#173d2b;color:#8ff0b5;padding:8px 12px;border-radius:999px;font-weight:800}}.note{{background:#222735;border-left:4px solid #d49b45;padding:14px 18px;margin:18px 0}}.grid{{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:18px}}figure{{margin:0;background:#1b202b;border:1px solid #343b4c;border-radius:14px;overflow:hidden}}img{{display:block;width:100%}}figcaption{{padding:10px 14px;color:#bfc7d8}}table{{width:100%;border-collapse:collapse;margin-top:22px;background:#171b24}}th,td{{border:1px solid #333b4b;padding:10px;text-align:left}}th{{color:#e8b86b}}@media(max-width:800px){{.grid{{grid-template-columns:1fr}}}}</style></head><body><main>
<span class='status'>기술 검사 통과 · 오류 {report.ErrorCount}개</span><h1>Forgotten Warden Watch Room</h1><p>승인된 Spatial Contract만 사용한 비저장 배치 프리뷰입니다. 보급품이 남은 감시대장의 당직실이라는 콘셉트로, 단일 입구·균일한 바닥·의도적인 진입 여백을 사용했습니다.</p>
<div class='note'><strong>이번에 확인할 것</strong><br>책장이 바닥과 벽에 동시에 자연스럽게 붙는지, 배너와 횃불이 벽/모서리에 끼지 않는지, 식량이 놓인 테이블·의자·장부 책장이 하나의 당직 공간으로 읽히는지 확인해 주세요.</div>
<div class='grid'>{cards}</div><table><thead><tr><th>에셋</th><th>역할</th><th>접촉 증거</th></tr></thead><tbody>{placementRows}</tbody></table>
<p>판정은 Codex 대화에서 <strong>승인</strong> 또는 <strong>수정 필요 + 이유</strong>로 알려 주세요. 이 페이지 자체는 씬에 Apply하지 않습니다.</p>
</main></body></html>";
        File.WriteAllText(Path.Combine(output, "review.html"), html, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "technical-report.txt"), $"seed={preview.Plan.Seed}{Environment.NewLine}placements={preview.Placements.Count}{Environment.NewLine}errors={report.ErrorCount}{Environment.NewLine}manifestHash={preview.ManifestHash}{Environment.NewLine}geometryProfileHash={preview.GeometryProfileHash}{Environment.NewLine}", new UTF8Encoding(false));
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
        var instance = PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene()) as GameObject;
        if (instance == null) throw new InvalidOperationException($"Could not instantiate KayKit structure: {prefab.name}");
        instance.name = name;
        instance.transform.SetParent(parent, true);
        instance.transform.SetPositionAndRotation(Vector3.zero, rotation);
        return instance;
    }

    private static Bounds RendererBounds(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) throw new InvalidOperationException($"{root.name} has no renderer.");
        var bounds = renderers[0].bounds;
        for (var index = 1; index < renderers.Length; index++) bounds.Encapsulate(renderers[index].bounds);
        return bounds;
    }

    private static GameObject FindModel(string exactName)
    {
        var paths = AssetDatabase.FindAssets($"{exactName} t:GameObject")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(path => Path.GetFileNameWithoutExtension(path).Equals(exactName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0) throw new FileNotFoundException($"KayKit model not found: {exactName}");
        return AssetDatabase.LoadAssetAtPath<GameObject>(paths[0]);
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    private static string OutputDirectory() => Path.GetFullPath(Path.Combine(Application.dataPath, "..", OutputRelative));

    private enum WallSide { North, South, East, West }
}
