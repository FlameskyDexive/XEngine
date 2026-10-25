// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// BP2: how a build reaches its platform settings once profiles exist, and how a project authored
/// before profiles is carried over.
/// </summary>
public class BuildProfileResolutionTests : EditorTestHarness
{
    private readonly BuildSettings _settings;

    public BuildProfileResolutionTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
        _settings = EditorRegistries.GetSettings<BuildSettings>();
    }

    [Fact]
    public void GetProfile_ReadsFromTheActiveProfileRatherThanTheLegacyList()
    {
        // The legacy singleton says 1280x720...
        _settings.GetProfile<DesktopBuildProfile>(typeof(DesktopBuildPipeline)).WindowWidth = 1280;

        // ...but the active build profile says 1920, and that is what a build must see.
        BuildProfileHandle handle = BuildProfileRegistry.Create("Windows Release", "xengine.desktop")!.Value;
        ((DesktopBuildProfile)handle.Profile.PlatformSettings!).WindowWidth = 1920;
        BuildProfileRegistry.SetActive(_settings, handle.Guid, requestRecompile: false);

        Assert.Equal(1920, _settings.GetProfile<DesktopBuildProfile>(typeof(DesktopBuildPipeline)).WindowWidth);
        Assert.Equal(1920, ((DesktopBuildProfile)_settings.GetOrCreateProfile(typeof(DesktopBuildPipeline))).WindowWidth);
    }

    [Fact]
    public void GetProfile_FallsBackToTheLegacyListWhenTheActiveProfileTargetsAnotherPlatform()
    {
        _settings.GetProfile<DesktopBuildProfile>(typeof(DesktopBuildPipeline)).WindowWidth = 1024;

        BuildProfileHandle android = BuildProfileRegistry.Create("Android QA", "xengine.android")!.Value;
        BuildProfileRegistry.SetActive(_settings, android.Guid, requestRecompile: false);

        // Asking for desktop settings while an Android profile is active must not hand back the
        // Android object or a freshly defaulted one.
        Assert.Equal(1024, _settings.GetProfile<DesktopBuildProfile>(typeof(DesktopBuildPipeline)).WindowWidth);
    }

    [Fact]
    public void GetProfile_WorksWithoutAnyProfileAsset()
    {
        var standalone = new BuildSettings();
        DesktopBuildProfile profile = standalone.GetProfile<DesktopBuildProfile>(typeof(DesktopBuildPipeline));

        Assert.NotNull(profile);
        Assert.Same(profile, standalone.GetProfile<DesktopBuildProfile>(typeof(DesktopBuildPipeline)));
    }

    [Fact]
    public void ResolvePipeline_StillFollowsTheLegacyPipelineIdMirroredFromTheActiveProfile()
    {
        BuildProfileHandle handle = BuildProfileRegistry.Create("HarmonyOS Store", "xengine.harmonyos")!.Value;
        BuildProfileRegistry.SetActive(_settings, handle.Guid, requestRecompile: false);

        Assert.Equal("xengine.harmonyos", ProjectBuilder.ResolvePipeline(_settings).Id);
    }

    [Fact]
    public void ResolvePipeline_RefusesAnUnknownPipelineInsteadOfSilentlyBuildingDesktop()
    {
        _settings.ActivePipelineId = "xengine.nonexistent";

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectBuilder.ResolvePipeline(_settings));
        Assert.Contains("xengine.nonexistent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_ConvertsLegacyPlatformProfilesAndKeepsTheirAuthoredValues()
    {
        _settings.GetProfile<DesktopBuildProfile>(typeof(DesktopBuildPipeline)).WindowWidth = 800;
        _settings.GetProfile<AndroidBuildProfile>(typeof(AndroidBuildPipeline));
        _settings.ActivePipelineId = "xengine.android";

        int created = BuildProfileMigration.Migrate(_settings);

        Assert.Equal(2, created);
        Assert.Equal(2, BuildProfileRegistry.LoadAll().Count);

        // The previously selected platform stays selected.
        BuildProfileHandle active = BuildProfileRegistry.GetActive(_settings)!.Value;
        Assert.Equal("xengine.android", active.Profile.PipelineId);

        // Authored values survive rather than being reset to pipeline defaults.
        BuildProfileHandle desktop = BuildProfileRegistry.LoadAll()
            .Single(h => h.Profile.PipelineId == "xengine.desktop");
        Assert.Equal(800, ((DesktopBuildProfile)desktop.Profile.PlatformSettings!).WindowWidth);
    }

    [Fact]
    public void Migration_IsIdempotent()
    {
        _settings.GetProfile<DesktopBuildProfile>(typeof(DesktopBuildPipeline));

        Assert.Equal(1, BuildProfileMigration.Migrate(_settings));
        Assert.Equal(0, BuildProfileMigration.Migrate(_settings));
        Assert.Single(BuildProfileRegistry.LoadAll());
    }

    [Fact]
    public void Migration_LeavesAFreshProjectWithNoProfiles()
    {
        Assert.Empty(_settings.PlatformProfiles);

        Assert.Equal(0, BuildProfileMigration.Migrate(_settings));
        Assert.Empty(BuildProfileRegistry.LoadAll());
        Assert.Equal(Guid.Empty, _settings.ActiveProfileGuid);

        // And a project with no profile still builds desktop, as it did before profiles existed.
        Assert.Equal("xengine.desktop", ProjectBuilder.ResolvePipeline(_settings).Id);
    }

    [Fact]
    public void ResolvedOptions_InheritSharedValuesWhenNoProfileIsActive()
    {
        _settings.OutputDirectory = "Builds";
        _settings.Config = BuildConfiguration.Release;
        _settings.PackagingMode = AssetPackagingMode.LooseFiles;
        _settings.AssetMode = AssetExportMode.AllAssets;
        _settings.MaxPakSizeMB = 512;
        var scene = new SceneBuildEntry { Path = "Main.scene", SceneGuid = Guid.NewGuid(), Enabled = true };
        _settings.Scenes.Add(scene);

        ResolvedBuildOptions options = ResolvedBuildOptions.From(_settings);

        Assert.Equal("Builds", options.OutputDirectory);
        Assert.Equal(BuildConfiguration.Release, options.Config);
        Assert.Equal(AssetPackagingMode.LooseFiles, options.PackagingMode);
        Assert.Equal(AssetExportMode.AllAssets, options.AssetMode);
        Assert.Equal(512, options.MaxPakSizeMB);
        Assert.Same(_settings.Scenes, options.Scenes);
    }

    [Fact]
    public void ResolvedOptions_PreferTheActiveProfilesOverrideFlags()
    {
        // macOS: player build/run and Windows-host toolchains are not wired for this host
        // yet; re-enable with the macOS packaging milestone.
        if (OperatingSystem.IsMacOS())
            return;
        _settings.OutputDirectory = "Builds";
        _settings.Config = BuildConfiguration.Release;
        _settings.PackagingMode = AssetPackagingMode.XEnginePak;
        _settings.AssetMode = AssetExportMode.AllAssets;
        _settings.MaxPakSizeMB = 2048;
        _settings.Scenes.Add(new SceneBuildEntry { Path = "Shared.scene", SceneGuid = Guid.NewGuid(), Enabled = true });

        BuildProfileHandle handle = BuildProfileRegistry.Create("Windows QA", "xengine.desktop")!.Value;
        BuildProfile profile = handle.Profile;
        profile.OverrideOutputDirectory = true;
        profile.OutputDirectory = "Builds/QA";
        profile.OverrideConfiguration = true;
        profile.Configuration = BuildConfiguration.Debug;
        profile.OverridePackaging = true;
        profile.PackagingMode = AssetPackagingMode.LooseFiles;
        profile.AssetMode = AssetExportMode.DependenciesOnly;
        profile.MaxPakSizeMB = 256;
        profile.OverrideScenes = true;
        profile.Scenes.Add(new SceneBuildEntry { Path = "QA.scene", SceneGuid = Guid.NewGuid(), Enabled = true });
        BuildProfileRegistry.Save(handle);
        BuildProfileRegistry.SetActive(_settings, handle.Guid, requestRecompile: false);

        ResolvedBuildOptions options = ResolvedBuildOptions.From(_settings);

        Assert.Equal("Builds/QA", options.OutputDirectory);
        Assert.Equal(BuildConfiguration.Debug, options.Config);
        Assert.Equal(AssetPackagingMode.LooseFiles, options.PackagingMode);
        Assert.Equal(AssetExportMode.DependenciesOnly, options.AssetMode);
        Assert.Equal(256, options.MaxPakSizeMB);
        Assert.Equal("QA.scene", Assert.Single(options.Scenes).Path);

        // Shared settings stay as authored - resolution is a read, not a merge into the singleton.
        Assert.Equal("Builds", _settings.OutputDirectory);
        Assert.Equal(BuildConfiguration.Release, _settings.Config);
        Assert.Equal(AssetPackagingMode.XEnginePak, _settings.PackagingMode);
        Assert.Equal("Shared.scene", Assert.Single(_settings.Scenes).Path);
    }

    [Fact]
    public void ResolvedOptions_InvocationOutputWinsOverTheProfileAndDoesNotPersist()
    {
        _settings.OutputDirectory = "Builds";

        BuildProfileHandle handle = BuildProfileRegistry.Create("Windows QA", "xengine.desktop")!.Value;
        handle.Profile.OverrideOutputDirectory = true;
        handle.Profile.OutputDirectory = "Builds/QA";
        BuildProfileRegistry.Save(handle);
        BuildProfileRegistry.SetActive(_settings, handle.Guid, requestRecompile: false);

        ResolvedBuildOptions options = ResolvedBuildOptions.From(_settings, @"D:\ci\nightly");

        Assert.Equal(@"D:\ci\nightly", options.OutputDirectory);
        Assert.Equal("Builds", _settings.OutputDirectory);
        Assert.Equal("Builds/QA", handle.Profile.ResolveOutputDirectory(_settings));
    }

    [Fact]
    public void FinalizeDefineString_UsesTheProfilesConfigurationOverride()
    {
        _settings.Config = BuildConfiguration.Release;

        BuildProfileHandle handle = BuildProfileRegistry.Create("Windows Debug", "xengine.desktop")!.Value;
        handle.Profile.OverrideConfiguration = true;
        handle.Profile.Configuration = BuildConfiguration.Debug;
        BuildProfileRegistry.Save(handle);
        BuildProfileRegistry.SetActive(_settings, handle.Guid, requestRecompile: false);

        string defines = BuildPipeline.FinalizeDefineString(_settings, new DesktopBuildPipeline());

        Assert.Contains("XENGINE_PROFILING", defines, StringComparison.Ordinal);
        Assert.Equal(BuildConfiguration.Release, _settings.Config);
    }
}
