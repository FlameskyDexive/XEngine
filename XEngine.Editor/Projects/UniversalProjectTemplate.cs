// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Echo;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.Projects;

/// <summary>
/// Seeds a new project with Unity-style Universal pipeline defaults: <c>.pipeline</c> /
/// <c>.renderer</c> / volume profile, GraphicsSettings assignment, and an empty SampleScene
/// (Main Camera + Directional Light + Global Volume).
/// </summary>
public static class UniversalProjectTemplate
{
    public const string SettingsFolder = "Settings";
    /// <summary>Unity 6.7 naming: assets live in a Pipeline subfolder with the Create-menu names.</summary>
    public const string PipelineFolder = "Settings/Pipeline";
    public const string PipelineBaseName = "New Universal Render Pipeline Asset";
    public const string RendererPath = PipelineFolder + "/" + PipelineBaseName + "_Renderer.renderer";
    public const string PipelinePath = PipelineFolder + "/" + PipelineBaseName + ".pipeline";
    /// <summary>Project-level URP global settings at the Assets root, like Unity 6.7.</summary>
    public const string GlobalSettingsPath = "UniversalRenderPipelineGlobalSettings.asset";
    public const string VolumeProfilePath = "Settings/DefaultVolumeProfile.volumeprofile";
    public const string SampleScenePath = "Scenes/SampleScene.scene";

    public static void Seed(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        Guid rendererGuid = Guid.NewGuid();
        Guid pipelineGuid = Guid.NewGuid();
        Guid globalSettingsGuid = Guid.NewGuid();
        Guid volumeGuid = Guid.NewGuid();
        Guid sceneGuid = Guid.NewGuid();

        WriteRenderer(project, RendererPath, rendererGuid);
        WritePipeline(project, PipelinePath, pipelineGuid, rendererGuid);
        WriteGlobalSettings(project, globalSettingsGuid);
        WriteVolumeProfile(project, volumeGuid);
        WriteSampleScene(project, sceneGuid, volumeGuid);
        WriteProjectSettings(project, pipelineGuid, globalSettingsGuid, sceneGuid);
    }

    /// <summary>
    /// Creates a linked URP asset pair (Unity 6.7 Create menu "URP Asset (with Universal Renderer)"):
    /// <c>{baseName}.pipeline</c> referencing <c>{baseName}_Renderer.renderer</c> in the given
    /// folder. Does NOT touch Graphics/Quality settings — Unity doesn't either.
    /// </summary>
    /// <returns>Assets-relative paths (pipeline, renderer) or null when the folder is invalid.</returns>
    public static (string PipelinePath, string RendererPath)? CreateUrpAssetPair(Project project, string relativeFolder, string baseName)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(baseName))
            return null;

        string folder = relativeFolder.Replace('\\', '/');
        string absFolder = Path.Combine(project.AssetsPath, folder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(absFolder);

        string pipelineFile = Utils.UniqueNames.ForFile(absFolder, baseName, ".pipeline");
        string rendererFile = Utils.UniqueNames.ForFile(absFolder, baseName + "_Renderer", ".renderer");

        string ToAssetPath(string file) => string.IsNullOrEmpty(folder) ? file : folder + "/" + file;

        Guid rendererGuid = Guid.NewGuid();
        Guid pipelineGuid = Guid.NewGuid();
        WriteRenderer(project, ToAssetPath(rendererFile), rendererGuid);
        WritePipeline(project, ToAssetPath(pipelineFile), pipelineGuid, rendererGuid);
        return (ToAssetPath(pipelineFile), ToAssetPath(rendererFile));
    }

    /// <summary>
    /// Empty Universal scene: Main Camera (URP camera data + AudioListener), Directional Light
    /// (URP light data), and a global Volume. No primitives — matches Unity's URP empty template.
    /// </summary>
    public static Scene CreateEmptyScene(Guid volumeProfileGuid, string name = "Sample Scene")
    {
        var scene = new Scene { Name = name };

        var camGo = new GameObject("Main Camera");
        camGo.Tag = "Main Camera";
        camGo.Transform.Position = new Float3(0, 1, -10);
        camGo.Transform.LocalEulerAngles = new Float3(0, 0, 0);
        Camera cam = camGo.AddComponent<Camera>();
        cam.Depth = -1;
        cam.HDR = true;
        UniversalAdditionalCameraData cameraData = UniversalAdditionalCameraData.GetOrAdd(cam);
        cameraData.RenderPostProcessing = true;
        cameraData.RenderShadows = true;
        camGo.AddComponent<AudioListener>();
        scene.Add(camGo);

        var lightGo = new GameObject("Directional Light");
        lightGo.Transform.LocalEulerAngles = new Float3(-45, 45, 0);
        DirectionalLight light = lightGo.AddComponent<DirectionalLight>();
        light.Intensity = 1f;
        UniversalAdditionalLightData.GetOrAdd(light);
        scene.Add(lightGo);

        var volumeGo = new GameObject("Global Volume");
        Volume volume = volumeGo.AddComponent<Volume>();
        volume.IsGlobal = true;
        volume.Weight = 1f;
        if (volumeProfileGuid != Guid.Empty)
            volume.Profile = new AssetRef<VolumeProfile>(volumeProfileGuid);
        scene.Add(volumeGo);

        return scene;
    }

    private static void WriteRenderer(Project project, string relativePath, Guid guid)
    {
        var renderer = new UniversalRenderData
        {
            Name = Path.GetFileNameWithoutExtension(relativePath),
            RenderingPath = RenderingPath.Forward,
            Kind = RendererKind.Universal,
        };
        WriteEchoAsset(project, relativePath, renderer, guid, "ScriptableObjectImporter", 1);
        renderer.Dispose();
    }

    private static void WritePipeline(Project project, string relativePath, Guid guid, Guid rendererGuid)
    {
        var pipeline = new UniversalRenderPipelineAsset
        {
            Name = Path.GetFileNameWithoutExtension(relativePath),
            Hdr = true,
            RenderScale = 1f,
            ShadowsEnabled = true,
            ShadowAtlasSize = 4096,
            ShadowDistance = 100f,
            Renderer = new AssetRef<UniversalRenderData>(rendererGuid),
        };
        WriteEchoAsset(project, relativePath, pipeline, guid, "ScriptableObjectImporter", 1);
        pipeline.Dispose();
    }

    private static void WriteGlobalSettings(Project project, Guid guid)
    {
        var settings = new UniversalRenderPipelineGlobalSettings
        {
            Name = Path.GetFileNameWithoutExtension(GlobalSettingsPath),
        };
        WriteEchoAsset(project, GlobalSettingsPath, settings, guid, "ScriptableObjectImporter", 1);
        settings.Dispose();
    }

    private static void WriteVolumeProfile(Project project, Guid guid)
    {
        var profile = new VolumeProfile { Name = "Default Volume Profile" };
        profile.Components.Add(new TonemapVolumeComponent
        {
            OverrideType = true,
            Type = (int)TonemapperType.AgX,
        });
        WriteEchoAsset(project, VolumeProfilePath, profile, guid, "ScriptableObjectImporter", 1);
        profile.Dispose();
    }

    private static void WriteSampleScene(Project project, Guid guid, Guid volumeGuid)
    {
        using Scene scene = CreateEmptyScene(volumeGuid);
        WriteEchoAsset(project, SampleScenePath, scene, guid, "SceneImporter", 3);
    }

    private static void WriteProjectSettings(Project project, Guid pipelineGuid, Guid globalSettingsGuid, Guid sceneGuid)
    {
        Directory.CreateDirectory(project.ProjectSettingsPath);

        var graphics = new GraphicsSettings
        {
            DefaultPipeline = new AssetRef<UniversalRenderPipelineAsset>(pipelineGuid),
            PipelineGlobalSettings = new AssetRef<UniversalRenderPipelineGlobalSettings>(globalSettingsGuid),
        };
        WriteSettingsYaml(project, "Graphics", graphics);

        // Unity 6.7 quality-level customRenderPipeline: the top tier carries the same asset the
        // Graphics default points at; Preview stays empty (falls back to the Graphics default).
        var quality = new QualitySettingsPanel
        {
            FullPipeline = new AssetRef<UniversalRenderPipelineAsset>(pipelineGuid),
        };
        WriteSettingsYaml(project, "Quality", quality);

        var general = new GeneralSettings
        {
            ProductName = project.Name,
        };
        WriteSettingsYaml(project, "General", general);
        ProjectEngineVersion.Write(project.RootPath, XEngine.Editor.Core.EditorApplication.CanonicalEngineVersion);

        // Seed the Library last-scene cache so the first launch reopens the sample scene. This is
        // session state, not a setting — kept out of ProjectSettings/General.yaml on purpose.
        Directory.CreateDirectory(project.LibraryPath);
        File.WriteAllText(Path.Combine(project.LibraryPath, "LastScene.txt"), SampleScenePath);

        var build = new BuildSettings();
        build.Scenes.Add(new SceneBuildEntry
        {
            Path = SampleScenePath,
            SceneGuid = sceneGuid,
            Enabled = true,
        });
        WriteSettingsYaml(project, "Build", build);
    }

    private static void WriteEchoAsset(Project project, string relativePath, EngineObject obj, Guid guid,
        string importerType, int importerVersion)
    {
        string absolute = Path.Combine(project.AssetsPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        obj.AssetPath = relativePath.Replace('\\', '/');
        EchoObject echo = Serializer.Serialize(typeof(object), obj);
        File.WriteAllText(absolute, echo.WriteToString());

        var meta = new MetaFileData
        {
            Guid = guid,
            ImporterType = importerType,
            ImporterVersion = importerVersion,
        };
        MetaFile.Write(MetaFile.GetMetaPath(absolute), meta);
    }

    private static void WriteSettingsYaml(Project project, string name, ProjectSettingsBase settings)
    {
        EchoObject echo = Serializer.Serialize(settings, TypeMode.Auto);
        File.WriteAllText(Path.Combine(project.ProjectSettingsPath, name + ".yaml"), echo.WriteToYaml());
    }
}
