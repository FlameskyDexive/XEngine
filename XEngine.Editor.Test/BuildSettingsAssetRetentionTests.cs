using System.Reflection;
using XEngine.Editor.Build;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class BuildSettingsAssetRetentionTests : EditorTestHarness
{
    public BuildSettingsAssetRetentionTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    // A concrete test subclass would be discovered as a second xengine.desktop pipeline,
    // masking DesktopBuildProfile metadata in unrelated registry/profile tests.
    private static AssetCollector.CollectionResult Collect(BuildSettings settings)
        => (AssetCollector.CollectionResult)typeof(BuildPipeline)
            .GetMethod("CollectAssets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new DesktopBuildPipeline(), [settings, null, false])!;

    private Guid Pipeline(string path)
    {
        var asset = new UniversalRenderPipelineAsset();
        Assets.CreateAsset(asset, path);
        return asset.AssetID;
    }

    private BuildSettings SettingsWithEmptyScene()
    {
        var scene = new Scene();
        scene.Add(new GameObject("Empty"));
        Guid sceneGuid = CreateSceneAsset(scene, "Main.scene");
        var settings = EditorRegistries.GetSettings<BuildSettings>();
        settings.Scenes.Clear();
        settings.Scenes.Add(new SceneBuildEntry { Path = "Main.scene", SceneGuid = sceneGuid, Enabled = true });
        settings.AssetMode = AssetExportMode.DependenciesOnly;
        return settings;
    }

    [Fact]
    public void DependenciesOnly_RetainsExportedSettingsAndTheirTransitiveDependencies()
    {
        BuildSettings settings = SettingsWithEmptyScene();
        Guid graphics = Pipeline("Graphics.pipeline");
        Guid quality = Pipeline("Quality.pipeline");
        Guid dependency = Pipeline("Dependency.pipeline");
        Guid unrelated = Pipeline("Unrelated.pipeline");
        Assets.Dependencies.SetDependencies(graphics, [dependency]);
        EditorRegistries.GetSettings<GraphicsSettings>().DefaultPipeline = new(graphics);
        EditorRegistries.GetSettings<QualitySettingsPanel>().FullPipeline = new(quality);

        var result = Collect(settings);

        Assert.Contains(graphics, result.AllAssets);
        Assert.Contains(quality, result.AllAssets);
        Assert.Contains(dependency, result.AllAssets);
        Assert.DoesNotContain(unrelated, result.AllAssets);
        Assert.Equal("player-settings", result.RetentionReasons[graphics].RootKind);
        Assert.Equal(graphics, result.RetentionReasons[dependency].RootGuid);
        Assert.Single(result.RetentionReasons[dependency].Chain);
    }

    [Fact]
    public void DependenciesOnly_UsesProfileOverrideAndRestoresAuthoredSettings()
    {
        BuildSettings settings = SettingsWithEmptyScene();
        Guid authored = Pipeline("Authored.pipeline");
        Guid overridden = Pipeline("Override.pipeline");
        var graphics = EditorRegistries.GetSettings<GraphicsSettings>();
        graphics.DefaultPipeline = new(authored);
        var handle = BuildProfileRegistry.Create("Mac QA", "xengine.desktop")!.Value;
        handle.Profile.SettingOverrides.Add(new ProfileSettingOverride
        {
            SettingsName = "Graphics", FieldPath = "DefaultPipeline", Value = overridden.ToString(),
        });
        BuildProfileRegistry.Save(handle);
        BuildProfileRegistry.SetActive(settings, handle.Guid, requestRecompile: false);

        var result = Collect(settings);

        Assert.Contains(overridden, result.AllAssets);
        Assert.DoesNotContain(authored, result.AllAssets);
        Assert.Equal(authored, graphics.DefaultPipeline.AssetID);
    }
}
