// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// BP1: the <c>.buildprofile</c> asset type and <see cref="BuildProfileRegistry"/> discovery layer.
/// These cover the promise that makes build profiles worth having - several profiles can target the
/// same platform and be told apart by name or GUID.
/// </summary>
public class BuildProfileAssetTests : EditorTestHarness
{
    public BuildProfileAssetTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    [Fact]
    public void Create_SeedsThePlatformSettingsObjectBoundToThePipeline()
    {
        BuildProfileHandle? handle = BuildProfileRegistry.Create("Windows Dev", "xengine.desktop");

        Assert.NotNull(handle);
        Assert.Equal("xengine.desktop", handle!.Value.Profile.PipelineId);
        Assert.IsType<DesktopBuildProfile>(handle.Value.Profile.PlatformSettings);
        Assert.EndsWith(BuildProfile.FileExtension, handle.Value.RelativePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_KeepsTwoProfilesForTheSamePlatformDistinct()
    {
        BuildProfileHandle dev = BuildProfileRegistry.Create("Windows Dev", "xengine.desktop")!.Value;
        BuildProfileHandle release = BuildProfileRegistry.Create("Windows Release", "xengine.desktop")!.Value;

        Assert.NotEqual(dev.Guid, release.Guid);

        ((DesktopBuildProfile)dev.Profile.PlatformSettings!).PublishTrimmed = false;
        ((DesktopBuildProfile)release.Profile.PlatformSettings!).PublishTrimmed = true;
        Assert.NotNull(BuildProfileRegistry.Save(dev));
        Assert.NotNull(BuildProfileRegistry.Save(release));

        // Re-read through the database so we are asserting on what round-tripped to disk.
        Assert.False(((DesktopBuildProfile)BuildProfileRegistry.Load(dev.Guid)!.Value.Profile.PlatformSettings!).PublishTrimmed);
        Assert.True(((DesktopBuildProfile)BuildProfileRegistry.Load(release.Guid)!.Value.Profile.PlatformSettings!).PublishTrimmed);
    }

    [Fact]
    public void Create_DoesNotOverwriteAnExistingProfileWithTheSameName()
    {
        BuildProfileHandle first = BuildProfileRegistry.Create("Windows", "xengine.desktop")!.Value;
        BuildProfileHandle second = BuildProfileRegistry.Create("Windows", "xengine.desktop")!.Value;

        Assert.NotEqual(first.RelativePath, second.RelativePath);
        Assert.Equal(2, BuildProfileRegistry.LoadAll().Count);
    }

    [Fact]
    public void Save_DoesNotRegisterASecondAssetEntryForTheSameFile()
    {
        BuildProfileHandle handle = BuildProfileRegistry.Create("Windows Dev", "xengine.desktop")!.Value;

        for (int i = 0; i < 3; i++)
            handle = BuildProfileRegistry.Save(handle)!.Value;

        Assert.Single(BuildProfileRegistry.LoadAll());
    }

    [Fact]
    public void Profile_RoundTripsOverridesThroughEchoText()
    {
        BuildProfileHandle handle = BuildProfileRegistry.Create("Android QA", "xengine.android")!.Value;
        BuildProfile profile = handle.Profile;

        profile.OverrideOutputDirectory = true;
        profile.OutputDirectory = "Builds/AndroidQA";
        profile.OverrideConfiguration = true;
        profile.Configuration = BuildConfiguration.Debug;
        profile.PlatformSettings!.ScriptingDefineSymbols.Add("QA_BUILD");
        profile.AssetImportOverrides.Enabled = true;
        profile.AssetImportOverrides.MaxTextureSize = 512;
        profile.SettingOverrides.Add(new ProfileSettingOverride
        {
            SettingsName = "General",
            FieldPath = "ProductName",
            Value = "QA Build",
        });
        BuildProfileRegistry.Save(handle);

        BuildProfile reloaded = BuildProfileRegistry.Load(handle.Guid)!.Value.Profile;

        Assert.True(reloaded.OverrideOutputDirectory);
        Assert.Equal("Builds/AndroidQA", reloaded.OutputDirectory);
        Assert.Equal(BuildConfiguration.Debug, reloaded.Configuration);
        Assert.Contains("QA_BUILD", reloaded.PlatformSettings!.ScriptingDefineSymbols);
        Assert.Equal(512, reloaded.AssetImportOverrides.MaxTextureSize);
        Assert.Equal("ProductName", Assert.Single(reloaded.SettingOverrides).FieldPath);
    }

    [Fact]
    public void Resolve_MatchesByNameAndByGuidButNotByGuesswork()
    {
        BuildProfileHandle handle = BuildProfileRegistry.Create("Windows Dev", "xengine.desktop")!.Value;

        Assert.Equal(handle.Guid, BuildProfileRegistry.Resolve("Windows Dev")!.Value.Guid);
        Assert.Equal(handle.Guid, BuildProfileRegistry.Resolve("windows dev")!.Value.Guid);
        Assert.Equal(handle.Guid, BuildProfileRegistry.Resolve(handle.Guid.ToString())!.Value.Guid);
        Assert.Equal(handle.Guid, BuildProfileRegistry.Resolve(handle.RelativePath)!.Value.Guid);

        Assert.Null(BuildProfileRegistry.Resolve("Windows"));
        Assert.Null(BuildProfileRegistry.Resolve(Guid.NewGuid().ToString()));
        Assert.Null(BuildProfileRegistry.Resolve(""));
    }

    [Fact]
    public void LoadAll_ReturnsOnlyBuildProfilesAndIsOrderedByPath()
    {
        BuildProfileRegistry.Create("Zulu", "xengine.desktop");
        BuildProfileRegistry.Create("Alpha", "xengine.desktop");
        Assets.CreateAsset(ScriptableObject.CreateInstance<VolumeProfileStandIn>(), "Other.asset");
        Assets.ImportFile("Other.asset");

        List<BuildProfileHandle> all = BuildProfileRegistry.LoadAll();

        Assert.Equal(2, all.Count);
        Assert.Equal("Alpha", all[0].DisplayName);
        Assert.Equal("Zulu", all[1].DisplayName);
    }

    [Fact]
    public void SetActive_MirrorsThePipelineIdIntoTheLegacyField()
    {
        var settings = EditorRegistries.GetSettings<BuildSettings>();
        BuildProfileHandle handle = BuildProfileRegistry.Create("HarmonyOS Store", "xengine.harmonyos")!.Value;

        Assert.True(BuildProfileRegistry.SetActive(settings, handle.Guid, requestRecompile: false));
        Assert.Equal(handle.Guid, settings.ActiveProfileGuid);
        Assert.Equal("xengine.harmonyos", settings.ActivePipelineId);
        Assert.Equal(handle.Guid, BuildProfileRegistry.GetActive(settings)!.Value.Guid);

        // Selecting the same profile again is a no-op rather than a redundant save + recompile.
        Assert.False(BuildProfileRegistry.SetActive(settings, handle.Guid, requestRecompile: false));
    }

    [Fact]
    public void CollectDefines_MergesAuthoredAndPlatformDerivedDefinesWithoutDuplicates()
    {
        BuildProfile profile = BuildProfileRegistry.Create("Windows Dev", "xengine.desktop")!.Value.Profile;
        List<string> authored = profile.PlatformSettings!.ScriptingDefineSymbols;
        authored.Add("QA_BUILD");
        authored.Add("QA_BUILD");
        authored.Add("XENGINE_DESKTOP"); // the platform derives this one too
        authored.Add("  ");

        List<string> defines = profile.CollectDefines();

        Assert.Equal("QA_BUILD", defines[0]); // authored symbols come first, as in FinalizeDefineString
        Assert.Single(defines, d => d == "QA_BUILD");
        Assert.Single(defines, d => d == "XENGINE_DESKTOP");
        Assert.DoesNotContain(defines, string.IsNullOrWhiteSpace);
    }

    [Theory]
    [InlineData(0, 0)]      // no clamp requested
    [InlineData(1024, 1024)]
    [InlineData(1000, 512)] // non power of two rounds down so encoders can honour it
    [InlineData(8, 32)]     // below the floor
    public void AssetImportOverrides_ResolveMaxTextureSize_RoundsToPowerOfTwo(int requested, int expected)
    {
        var overrides = new ProfileAssetImportOverrides { Enabled = true, MaxTextureSize = requested };
        Assert.Equal(expected, overrides.ResolveMaxTextureSize());
    }

    [Fact]
    public void AssetImportOverrides_AreInertUntilEnabled()
    {
        var overrides = new ProfileAssetImportOverrides { MaxTextureSize = 256, ForceUncompressed = true };

        Assert.Equal(0, overrides.ResolveMaxTextureSize());
        Assert.False(overrides.ResolveForceUncompressed());
        Assert.False(overrides.HasAnyEffect());
    }

    /// <summary>A plain ScriptableObject asset, used to prove the registry filters by extension.</summary>
    public sealed class VolumeProfileStandIn : ScriptableObject
    {
        public int Value;
    }
}
