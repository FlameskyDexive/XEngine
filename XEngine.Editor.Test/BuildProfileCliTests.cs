// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// BP5: the headless contract. A CI job or an agent names the profile it wants on the command line,
/// so <c>--build-profile</c> has to select the same settings the Build panel would, and has to fail
/// loudly rather than quietly building the wrong platform.
/// </summary>
public class BuildProfileCliTests : EditorTestHarness
{
    private readonly BuildSettings _settings;

    public BuildProfileCliTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
        _settings = EditorRegistries.GetSettings<BuildSettings>();
        ResetBuildArguments();
    }

    [Fact]
    public void BuildProfile_SelectsAProfileByNameAndDerivesThePipelineFromIt()
    {
        BuildProfileHandle handle = BuildProfileRegistry.Create("Windows Nightly", "xengine.desktop")!.Value;
        ((DesktopBuildProfile)handle.Profile.PlatformSettings!).WindowWidth = 2560;

        Program.ReadArguments(["--buildmode", "project", "--build-profile", "Windows Nightly"]);
        Program.ApplyBuildProfileSelection(_settings);

        Assert.Equal(handle.Guid, _settings.ActiveProfileGuid);
        Assert.Equal("xengine.desktop", _settings.ActivePipelineId);
        Assert.Equal(2560, _settings.GetProfile<DesktopBuildProfile>(typeof(DesktopBuildPipeline)).WindowWidth);
    }

    [Fact]
    public void BuildProfile_AlsoAcceptsAnAssetGuid()
    {
        BuildProfileHandle handle = BuildProfileRegistry.Create("Android QA", "xengine.android")!.Value;

        Program.ReadArguments(["--buildmode", "project", "--build-profile", handle.Guid.ToString()]);
        Program.ApplyBuildProfileSelection(_settings);

        Assert.Equal(handle.Guid, _settings.ActiveProfileGuid);
        Assert.Equal("xengine.android", _settings.ActivePipelineId);
    }

    [Fact]
    public void BuildProfile_PicksTheNamedProfileEvenWhenAnotherOneIsActive()
    {
        BuildProfileHandle active = BuildProfileRegistry.Create("Windows Debug", "xengine.desktop")!.Value;
        BuildProfileHandle wanted = BuildProfileRegistry.Create("Android Store", "xengine.android")!.Value;
        BuildProfileRegistry.SetActive(_settings, active.Guid, requestRecompile: false);

        Program.ReadArguments(["--buildmode", "project", "--build-profile", "Android Store"]);
        Program.ApplyBuildProfileSelection(_settings);

        Assert.Equal(wanted.Guid, _settings.ActiveProfileGuid);
        Assert.Equal("xengine.android", _settings.ActivePipelineId);
    }

    [Fact]
    public void BuildProfile_ReportsTheProfilesItDoesKnowWhenTheNameIsWrong()
    {
        BuildProfileRegistry.Create("Windows Nightly", "xengine.desktop");

        Program.ReadArguments(["--buildmode", "project", "--build-profile", "Winodws Nightly"]);

        var error = Assert.Throws<InvalidOperationException>(() => Program.ApplyBuildProfileSelection(_settings));
        Assert.Contains("Winodws Nightly", error.Message, StringComparison.Ordinal);
        Assert.Contains("Windows Nightly", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProfile_RejectsAPlatformThatContradictsTheProfile()
    {
        BuildProfileRegistry.Create("Android Store", "xengine.android");

        Program.ReadArguments(["--buildmode", "project", "--build-profile", "Android Store", "--platform", "desktop"]);

        var error = Assert.Throws<InvalidOperationException>(() => Program.ApplyBuildProfileSelection(_settings));
        Assert.Contains("xengine.android", error.Message, StringComparison.Ordinal);
        Assert.Contains("xengine.desktop", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildProfile_AllowsAPlatformThatAgreesWithTheProfile()
    {
        BuildProfileHandle handle = BuildProfileRegistry.Create("Android Store", "xengine.android")!.Value;

        Program.ReadArguments(["--buildmode", "project", "--build-profile", "Android Store", "--platform", "android"]);
        Program.ApplyBuildProfileSelection(_settings);

        Assert.Equal(handle.Guid, _settings.ActiveProfileGuid);
    }

    [Fact]
    public void Platform_DropsAnActiveProfileThatTargetsAnotherPlatform()
    {
        // Otherwise `--platform android` would still build with the Windows profile's player setting
        // and asset import overrides applied.
        BuildProfileHandle windows = BuildProfileRegistry.Create("Windows Debug", "xengine.desktop")!.Value;
        BuildProfileRegistry.SetActive(_settings, windows.Guid, requestRecompile: false);

        Program.ReadArguments(["--buildmode", "project", "--platform", "android"]);
        Program.ApplyBuildProfileSelection(_settings);

        Assert.Equal(Guid.Empty, _settings.ActiveProfileGuid);
        Assert.Equal("xengine.android", _settings.ActivePipelineId);
    }

    [Fact]
    public void Platform_KeepsAnActiveProfileThatTargetsTheSamePlatform()
    {
        BuildProfileHandle desktop = BuildProfileRegistry.Create("Windows Debug", "xengine.desktop")!.Value;
        BuildProfileRegistry.SetActive(_settings, desktop.Guid, requestRecompile: false);

        Program.ReadArguments(["--buildmode", "project", "--platform", "desktop"]);
        Program.ApplyBuildProfileSelection(_settings);

        Assert.Equal(desktop.Guid, _settings.ActiveProfileGuid);
    }

    [Fact]
    public void Platform_Ios_SelectsTheIosPipeline()
    {
        Program.ReadArguments(["--buildmode", "project", "--platform", "ios"]);
        Program.ApplyBuildProfileSelection(_settings);

        Assert.Equal("xengine.ios", _settings.ActivePipelineId);
        Assert.Equal(Guid.Empty, _settings.ActiveProfileGuid);
    }

    [Fact]
    public void BuildProfile_AllowsIosPlatformThatAgreesWithTheProfile()
    {
        BuildProfileHandle handle = BuildProfileRegistry.Create("iOS Device", "xengine.ios")!.Value;

        Program.ReadArguments(["--buildmode", "project", "--build-profile", "iOS Device", "--platform", "ios"]);
        Program.ApplyBuildProfileSelection(_settings);

        Assert.Equal(handle.Guid, _settings.ActiveProfileGuid);
        Assert.Equal("xengine.ios", _settings.ActivePipelineId);
    }

    [Fact]
    public void NoArguments_LeaveTheProjectsOwnSelectionAlone()
    {
        BuildProfileHandle handle = BuildProfileRegistry.Create("Windows Debug", "xengine.desktop")!.Value;
        BuildProfileRegistry.SetActive(_settings, handle.Guid, requestRecompile: false);

        Program.ReadArguments(["--buildmode", "project"]);
        Program.ApplyBuildProfileSelection(_settings);

        Assert.Equal(handle.Guid, _settings.ActiveProfileGuid);
        Assert.Equal("xengine.desktop", _settings.ActivePipelineId);
    }

    [Fact]
    public void BuildProfile_RequiresBuildMode()
    {
        var error = Assert.Throws<Exception>(() => Program.ReadArguments(["--build-profile", "Windows Debug"]));
        Assert.Contains("--buildmode", error.Message, StringComparison.Ordinal);
    }

    private static void ResetBuildArguments()
    {
        SetStaticAutoProperty(nameof(Program.BuildMode), false);
        SetStaticAutoProperty(nameof(Program.BuildPipelineId), null);
        SetStaticAutoProperty(nameof(Program.BuildProfileToken), null);
    }

    private static void SetStaticAutoProperty(string name, object? value)
    {
        PropertyInfo property = typeof(Program).GetProperty(name, BindingFlags.Public | BindingFlags.Static)!;
        property.SetValue(null, value);
    }
}
