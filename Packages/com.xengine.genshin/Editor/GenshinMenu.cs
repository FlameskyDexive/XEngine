using System.Collections.Generic;
using System;
using System.IO;

using XEngine.Editor;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;


namespace XEngine.Genshin.Editor;

/// <summary>
/// One-click editor workflow for the Genshin combat demo: copy the Unity reference assets into
/// the project (importers parse them on refresh), then build the demo scene from the imported
/// prefab. Designed to be invocable both by a human and by an AI agent via the `runtime_menu`
/// MCP tool.
/// </summary>
public static class GenshinMenu
{
    public const string KeQingPrefabPath = "Unit/KeQing/Prefabs/Girl_Keqing.prefab";

    [MenuItem("Genshin/Copy Unity Unit Assets Into Project")]
    public static void CopyUnityUnitAssets()
    {
        var backend = EditorAssetBackend.Instance;
        if (backend == null)
        {
            Debug.LogError("[Genshin] No asset database — open a project first.");
            return;
        }

        string destination = Path.Combine(Project.Current!.AssetsPath, "Unit");
        GenshinUnityAssetCopier.CopyResult result =
            GenshinUnityAssetCopier.CopyUnits(GenshinUnityAssetCopier.DefaultSourceRoot,
                new[] { "KeQing", "Common" }, destination);
        foreach (string warning in result.Warnings)
            Debug.LogWarning($"[Genshin] {warning}");

        backend.RefreshAll();
        Debug.Log($"[Genshin] Copied {result.FilesCopied} files ({result.TexturesAligned} texture settings aligned) " +
                  $"into Assets/Unit and refreshed the asset database.");
    }

    [MenuItem("Genshin/Build Combat Demo Scene")]
    public static void BuildCombatDemoScene()
    {
        var backend = EditorAssetBackend.Instance;
        if (backend == null) return;

        Guid prefabGuid = backend.GetEntry(KeQingPrefabPath)?.Guid ?? Guid.Empty;
        if (prefabGuid == Guid.Empty)
        {
            Debug.LogError($"[Genshin] '{KeQingPrefabPath}' not imported — run Genshin/Copy Unity Unit Assets first.");
            return;
        }
        var model = AssetDatabase.Get(prefabGuid) as PrefabAsset;
        if (model?.Instantiate() is not { } keQing)
        {
            Debug.LogError($"[Genshin] Failed to instantiate '{KeQingPrefababSafePath()}'.");
            return;
        }

        EditorSceneManager.NewScene();
        Scene scene = Scene.Current!;

        // Reuse the Universal template objects (Main Camera with its UniversalAdditionalCameraData/
        // HDR/AudioListener, Directional Light with its UniversalAdditionalLightData, Global Volume
        // driving ambient/tonemap) instead of building bare ones — the render pipeline expects the
        // Universal plumbing, and cameras/lights without it render unlit.
        GameObject? templateCamera = FindRoot(scene, "Main Camera");
        GameObject? templateLight = FindRoot(scene, "Directional Light");
        if (templateCamera == null || templateLight == null)
        {
            Debug.LogError("[Genshin] Universal template objects missing after NewScene.");
            return;
        }

        // KeQing at the origin, facing +Z (imported verbatim from Unity's left-handed space).
        scene.Add(keQing);

        // Ground plane, 20×20 m, neutral grey Standard material.
        GameObject ground = DefaultModels.Load(DefaultModel.Plane).Instantiate() ?? new GameObject("Ground");
        ground.Name = "Ground";
        ground.Transform.LocalScale = new Float3(20f, 1f, 20f);
        var groundRenderer = ground.GetComponent<MeshRenderer>();
        if (groundRenderer != null)
            groundRenderer.Material = new Material(Shader.LoadDefault(DefaultShader.Standard)) { Name = "GroundGrey" };
        scene.Add(ground);

        // Sun: Genshin-style key light — high angle, slightly warm, from the camera side. XEngine
        // directional lights travel along -forward (verified by GenshinLightingProbeTests), so a
        // negative pitch + positive yaw puts the light behind the camera, lighting the character's
        // camera-facing side.
        templateLight.Name = "Sun";
        templateLight.Transform.LocalEulerAngles = new Float3(-50f, 30f, 0f);
        if (templateLight.GetComponent<DirectionalLight>() is { } sun)
        {
            sun.Intensity = 1.15f;
            sun.Color = new Color(1f, 0.96f, 0.9f, 1f);
        }

        // Camera: third-person framing behind and above the character, looking forward (+Z).
        templateCamera.Transform.LocalPosition = new Float3(0f, 1.6f, -4.5f);
        templateCamera.Transform.LocalEulerAngles = Float3.Zero;
        if (templateCamera.GetComponent<Camera>() is { } camera)
        {
            camera.FieldOfView = 60f;
            camera.ClearFlags = CameraClearFlags.SolidColor;
            camera.ClearColor = new Color(0.32f, 0.47f, 0.65f, 1f);
        }

        EditorSceneManager.SaveAs("Scenes/GenshinCombat.scene");
        Debug.Log($"[Genshin] Built GenshinCombat.scene: {CountNodes(keQing)} KeQing nodes + ground/light/camera.");
    }

    private static GameObject? FindRoot(Scene scene, string name)
    {
        foreach (GameObject root in scene.RootObjects)
            if (root.Name == name)
                return root;
        return null;
    }

    private static string KeQingPrefababSafePath() => KeQingPrefabPath;

    private static int CountNodes(GameObject go)
    {
        int count = 1;
        foreach (GameObject child in go.Children)
            count += CountNodes(child);
        return count;
    }
}
