// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class UniversalProjectTemplateTests : EditorTestHarness
{
    public UniversalProjectTemplateTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    [Fact]
    public void Create_SeedsUniversalPipelineAssets()
    {
        Assert.True(File.Exists(AssetAbsolutePath(UniversalProjectTemplate.RendererPath)));
        Assert.True(File.Exists(AssetAbsolutePath(UniversalProjectTemplate.PipelinePath)));
        Assert.True(File.Exists(AssetAbsolutePath(UniversalProjectTemplate.GlobalSettingsPath)));
        Assert.True(File.Exists(AssetAbsolutePath(UniversalProjectTemplate.VolumeProfilePath)));
        Assert.True(File.Exists(AssetAbsolutePath(UniversalProjectTemplate.SampleScenePath)));
        Assert.True(File.Exists(Path.Combine(Project.ProjectSettingsPath, "Graphics.yaml")));
        Assert.True(File.Exists(Path.Combine(Project.ProjectSettingsPath, "Quality.yaml")));

        // Unity 6.7 naming: Settings/Pipeline/New Universal Render Pipeline Asset(_Renderer).
        Assert.EndsWith(
            "Settings/Pipeline/New Universal Render Pipeline Asset_Renderer.renderer",
            UniversalProjectTemplate.RendererPath.Replace('\\', '/'));
        Assert.EndsWith(
            "Settings/Pipeline/New Universal Render Pipeline Asset.pipeline",
            UniversalProjectTemplate.PipelinePath.Replace('\\', '/'));
    }

    [Fact]
    public void Create_WiresGraphicsSettingsToPipelineAsset()
    {
        GraphicsSettings graphics = EditorRegistries.GetSettings<GraphicsSettings>();
        Assert.NotEqual(Guid.Empty, graphics.DefaultPipeline.AssetID);
        Assert.NotEqual(Guid.Empty, graphics.PipelineGlobalSettings.AssetID);

        Guid pipelineGuid = Assets.PathToGuid(UniversalProjectTemplate.PipelinePath);
        Assert.Equal(pipelineGuid, graphics.DefaultPipeline.AssetID);
        Assert.Equal(
            Assets.PathToGuid(UniversalProjectTemplate.GlobalSettingsPath),
            graphics.PipelineGlobalSettings.AssetID);

        var pipeline = Assert.IsType<UniversalRenderPipelineAsset>(AssetDatabase.Get(pipelineGuid));
        Assert.True(pipeline.Hdr);
        Assert.NotEqual(Guid.Empty, pipeline.Renderer.AssetID);
        Assert.Equal(Assets.PathToGuid(UniversalProjectTemplate.RendererPath), pipeline.Renderer.AssetID);

        Assert.Same(pipeline, UniversalRenderPipeline.Default.Asset);
        Assert.NotNull(pipeline.Renderer.Res);
    }

    [Fact]
    public void Create_SeedsQualityFullTierOverride()
    {
        QualitySettingsPanel quality = EditorRegistries.GetSettings<QualitySettingsPanel>();
        Guid pipelineGuid = Assets.PathToGuid(UniversalProjectTemplate.PipelinePath);

        // Top tier carries the graphics default asset; Preview stays empty (falls back).
        Assert.Equal(pipelineGuid, quality.FullPipeline.AssetID);
        Assert.Equal(Guid.Empty, quality.PreviewPipeline.AssetID);
        Assert.Same(
            AssetDatabase.Get(pipelineGuid),
            QualitySettings.PipelineOverrideFor(QualitySettings.QualityTier.Full));
        Assert.Null(QualitySettings.PipelineOverrideFor(QualitySettings.QualityTier.Preview));
    }

    [Fact]
    public void CreateUrpAssetPair_CreatesLinkedPairWithUnityNames()
    {
        var created = UniversalProjectTemplate.CreateUrpAssetPair(
            Project, "Settings", "New Universal Render Pipeline Asset");

        Assert.NotNull(created);
        Assert.Equal(
            "Settings/New Universal Render Pipeline Asset.pipeline",
            created!.Value.PipelinePath.Replace('\\', '/'));
        Assert.Equal(
            "Settings/New Universal Render Pipeline Asset_Renderer.renderer",
            created.Value.RendererPath.Replace('\\', '/'));
        Assert.True(File.Exists(AssetAbsolutePath(created.Value.PipelinePath)));
        Assert.True(File.Exists(AssetAbsolutePath(created.Value.RendererPath)));

        // The pair is linked by GUID: the pipeline text references the renderer's meta GUID.
        string pipelineText = File.ReadAllText(AssetAbsolutePath(created.Value.PipelinePath));
        string rendererMetaText = File.ReadAllText(AssetAbsolutePath(created.Value.RendererPath) + ".meta");
        string guidLine = rendererMetaText.Split('\n')
            .First(line => line.TrimStart().StartsWith("\"guid\"", StringComparison.Ordinal));
        string rendererGuid = guidLine.Split('"')[^2];
        Assert.NotEqual(string.Empty, rendererGuid);
        Assert.Contains(rendererGuid, pipelineText, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleScene_HasUrpCameraLightAndVolume()
    {
        Guid sceneGuid = Assets.PathToGuid(UniversalProjectTemplate.SampleScenePath);
        var scene = Assert.IsType<Scene>(AssetDatabase.Get(sceneGuid));

        GameObject? cameraGo = null;
        GameObject? lightGo = null;
        GameObject? volumeGo = null;
        foreach (GameObject go in scene.AllObjects)
        {
            if (go.Name == "Main Camera") cameraGo = go;
            else if (go.Name == "Directional Light") lightGo = go;
            else if (go.Name == "Global Volume") volumeGo = go;
        }

        Assert.NotNull(cameraGo);
        Camera camera = Assert.IsType<Camera>(cameraGo!.GetComponent<Camera>());
        Assert.True(camera.HDR);
        UniversalAdditionalCameraData cameraData = Assert.IsType<UniversalAdditionalCameraData>(
            cameraGo.GetComponent<UniversalAdditionalCameraData>());
        Assert.True(cameraData.RenderPostProcessing);
        Assert.NotNull(cameraGo.GetComponent<AudioListener>());

        Assert.NotNull(lightGo);
        Assert.NotNull(lightGo!.GetComponent<DirectionalLight>());
        Assert.NotNull(lightGo.GetComponent<UniversalAdditionalLightData>());

        Assert.NotNull(volumeGo);
        Volume volume = Assert.IsType<Volume>(volumeGo!.GetComponent<Volume>());
        Assert.True(volume.IsGlobal);
        Assert.Equal(Assets.PathToGuid(UniversalProjectTemplate.VolumeProfilePath), volume.Profile.AssetID);

        var profile = Assert.IsType<VolumeProfile>(
            AssetDatabase.Get(Assets.PathToGuid(UniversalProjectTemplate.VolumeProfilePath)));
        Assert.True(profile.TryGet(out TonemapVolumeComponent tonemap));
        Assert.True(tonemap.OverrideType);
    }

    [Fact]
    public void EmptySceneFactory_AttachesUrpComponents()
    {
        using Scene scene = UniversalProjectTemplate.CreateEmptyScene(Guid.Empty, "Untitled Scene");
        GameObject? cameraGo = null;
        foreach (GameObject go in scene.AllObjects)
        {
            if (go.Name == "Main Camera")
                cameraGo = go;
        }

        Assert.NotNull(cameraGo);
        Assert.NotNull(cameraGo!.GetComponent<UniversalAdditionalCameraData>());
        Assert.NotNull(cameraGo.GetComponent<AudioListener>());
    }
}
