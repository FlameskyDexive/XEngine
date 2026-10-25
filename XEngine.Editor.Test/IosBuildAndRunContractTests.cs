// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Ios;
using XEngine.Editor.Build.Profiles;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class IosBuildAndRunContractTests : EditorTestHarness
{
    [Fact]
    public void DeviceSigningAndPublishPlan_UseActiveProfileConfiguration()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
        BuildSettings settings = EditorRegistries.GetSettings<BuildSettings>();
        settings.Config = BuildConfiguration.Release;

        BuildProfileHandle handle = BuildProfileRegistry.Create("iOS Device Debug", "xengine.ios")!.Value;
        handle.Profile.OverrideConfiguration = true;
        handle.Profile.Configuration = BuildConfiguration.Debug;
        handle = BuildProfileRegistry.Save(handle)!.Value;
        BuildProfileRegistry.SetActive(settings, handle.Guid, requestRecompile: false);

        IosBuildProfile profile = (IosBuildProfile)handle.Profile.PlatformSettings!;
        ResolvedBuildOptions options = ResolvedBuildOptions.From(settings);
        IosSigningPlan signing = IosBuildPipeline.CreateSigningPlan(profile, false, options);
        string exportRoot = Path.Combine(Project.RootPath, "Builds", "iOS Device Debug");
        var layout = new IosPlayerProjectLayout(
            exportRoot,
            Path.Combine(exportRoot, "Player", "Game.iOS.csproj"),
            Path.Combine(exportRoot, "Player", "Info.plist"),
            Path.Combine(exportRoot, "xengine-ios-project.json"),
            "Game.iOS",
            "ios-arm64",
            Path.Combine(exportRoot, "Player", "Content"),
            NativeAudioPackaged: false);
        IosPublishPlan publish = IosBuildPipeline.CreatePublishPlan(layout, signing, options, "ios-arm64");

        Assert.Equal(BuildConfiguration.Debug, options.Config);
        Assert.Equal(IosSigningStyle.Automatic, signing.Style);
        Assert.Contains("Team ID", signing.Error, StringComparison.Ordinal);
        Assert.Contains("Debug", publish.PublishArgv);
        Assert.DoesNotContain("Release", publish.PublishArgv);
        Assert.Empty(publish.StripArgv);
    }

    [Fact]
    public void PublishPlan_UsesDotnetPublishAndDevicectlNeverAdb()
    {
        var signing = new IosSigningPlan
        {
            Ready = true,
            Style = IosSigningStyle.Automatic,
            TeamId = "ABCDE12345",
            BundleId = "com.xengine.game",
            CodesignIdentity = "Apple Development",
            ArchiveDsym = true,
        };

        IosPublishPlan plan = IosPublishPlanner.Create(
            @"C:\proj\Player\Game.iOS.csproj",
            @"C:\out",
            "Game.iOS",
            signing,
            BuildConfiguration.Debug);

        Assert.Equal("dotnet", plan.PublishArgv[0]);
        Assert.Contains("publish", plan.PublishArgv);
        Assert.Contains("ios-arm64", plan.PublishArgv);
        Assert.Contains("-p:EnableCodeSigning=true", plan.PublishArgv);
        Assert.Contains("-p:CodesignKey=Apple Development", plan.PublishArgv);
        Assert.Contains("-p:DevelopmentTeam=ABCDE12345", plan.PublishArgv);
        Assert.Contains("dsymutil", plan.DsymArchiveArgv);
        Assert.Empty(plan.StripArgv);
        Assert.False(IosPublishPlanner.ContainsForbiddenTool(plan.PublishArgv));
        Assert.False(IosPublishPlanner.ContainsForbiddenTool(plan.DsymArchiveArgv));
        Assert.DoesNotContain(plan.PublishArgv, token => token.Contains("adb", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.PublishArgv, token => token.Contains("ios-deploy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PublishPlan_ReleaseStripsAndPairsDsym()
    {
        var signing = new IosSigningPlan
        {
            Ready = true,
            Style = IosSigningStyle.Manual,
            TeamId = "ABCDE12345",
            BundleId = "com.xengine.game",
            CodesignIdentity = "Apple Distribution: XEngine (ABCDE12345)",
            ProvisioningProfileUuid = "11111111-2222-3333-4444-555555555555",
            StripLinkedSymbols = true,
            ArchiveDsym = true,
        };

        IosPublishPlan plan = IosPublishPlanner.Create(
            "/tmp/Player/Game.iOS.csproj",
            "/tmp/out",
            "Game.iOS",
            signing,
            BuildConfiguration.Release);

        Assert.Contains("-c", plan.PublishArgv);
        Assert.Contains("Release", plan.PublishArgv);
        Assert.Contains("-p:CodesignProvision=11111111-2222-3333-4444-555555555555", plan.PublishArgv);
        Assert.Contains("-p:MtouchNoSymbolStrip=false", plan.PublishArgv);
        Assert.Equal("strip", plan.StripArgv[0]);
        Assert.Contains("dsymutil", plan.DsymArchiveArgv);
        Assert.EndsWith(".app.dSYM", plan.DsymArchivePath, StringComparison.Ordinal);
    }

    [Fact]
    public void SimulatorBuildPlan_ResolvesAppUnderProjectDirectory()
    {
        string projectPath = Path.Combine(Path.GetTempPath(), "ios player", "Game.iOS.csproj");
        var signing = new IosSigningPlan
        {
            Ready = true,
            Style = IosSigningStyle.Automatic,
            BundleId = "com.xengine.game",
        };

        IosPublishPlan plan = IosPublishPlanner.Create(
            projectPath,
            Path.Combine(Path.GetTempPath(), "ios output"),
            "Game.iOS",
            signing,
            BuildConfiguration.Release,
            "iossimulator-arm64");

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(projectPath)!, "bin", "Release", "net10.0-ios", "iossimulator-arm64", "Game.iOS.app"),
            plan.AppBundlePath);
        Assert.Equal("build", plan.PublishArgv[1]);
    }

    [Fact]
    public async Task PublishRunner_RefusesToExecuteOffMac()
    {
        if (OperatingSystem.IsMacOS())
            return;

        var signing = new IosSigningPlan
        {
            Ready = true,
            Style = IosSigningStyle.Automatic,
            TeamId = "ABCDE12345",
            CodesignIdentity = "Apple Development",
        };
        IosPublishPlan plan = IosPublishPlanner.Create(
            "Game.iOS.csproj",
            Path.GetTempPath(),
            "Game.iOS",
            signing,
            BuildConfiguration.Debug);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => IosPublishPlanner.RunAsync(plan));
        Assert.Equal(IosPublishPlanner.OffMacExecuteError, ex.Message);
        Assert.DoesNotContain("adb", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeviceCtl_CommandsUseIphoneosSdkAndXcrunNotIosDeploy()
    {
        IReadOnlyList<string> install = IosDeviceCtlCommands.InstallApp("UDID-1", "/tmp/Game.iOS.app");
        IReadOnlyList<string> launch = IosDeviceCtlCommands.LaunchProcess("UDID-1", "com.xengine.game");
        IReadOnlyList<string> list = IosDeviceCtlCommands.ListDevices();

        Assert.Equal(["xcrun", "--sdk", "iphoneos", "devicectl", "device", "install", "app", "--device", "UDID-1", "/tmp/Game.iOS.app"], install);
        Assert.Equal(["xcrun", "--sdk", "iphoneos", "devicectl", "device", "process", "launch", "--device", "UDID-1", "com.xengine.game"], launch);
        Assert.Equal(["xcrun", "--sdk", "iphoneos", "devicectl", "list", "devices"], list);
        Assert.False(IosPublishPlanner.ContainsForbiddenTool(install));
        Assert.False(IosPublishPlanner.ContainsForbiddenTool(launch));
    }

    [Fact]
    public async Task LaunchAsync_FailsClosedOffMacWithoutAdb()
    {
        var pipeline = new IosBuildPipeline();
        BuildLaunchResult launch = await pipeline.LaunchAsync(new BuildLaunchRequest
        {
            ProjectPath = "proj",
            Settings = new BuildSettings(),
            Build = new BuildResult { Success = true, OutputPath = Path.GetTempPath() },
        });

        Assert.True(launch.Handled);
        Assert.False(launch.Success);
        Assert.DoesNotContain("adb", launch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ios-deploy", launch.Message, StringComparison.OrdinalIgnoreCase);
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Contains("macOS", launch.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Xcode", launch.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("devicectl", launch.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("build host", launch.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LaunchAsync_RequiresSuccessfulSignedApp()
    {
        var pipeline = new IosBuildPipeline();
        BuildLaunchResult launch = await pipeline.LaunchAsync(new BuildLaunchRequest
        {
            ProjectPath = "proj",
            Settings = new BuildSettings(),
            Build = new BuildResult { Success = false, OutputPath = Path.GetTempPath() },
        });

        Assert.True(launch.Handled);
        Assert.False(launch.Success);
        Assert.Contains("signed .app", launch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_WritesSigningAndDsymReportsThenFailsOffMac()
    {
        // By name and contract this asserts the OFF-Mac fail-closed path; the Mac-side
        // branches are covered by the dedicated on-Mac tests in this class.
        if (OperatingSystem.IsMacOS())
            return;
        string output = Path.Combine(Path.GetTempPath(), "xengine-ios-p5-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new BuildSettings { Config = BuildConfiguration.Debug };
            IosBuildProfile profile = settings.GetProfile<IosBuildProfile>(typeof(IosBuildPipeline));
            profile.TeamId = "ABCDE12345";
            profile.ApplicationId = "com.xengine.game";
            var pipeline = new IosBuildPipeline();
            BuildResult result = await pipeline.BuildAsync("irrelevant", settings, output);

            Assert.False(string.IsNullOrWhiteSpace(result.OutputPath));
            string signingPath = Path.Combine(result.OutputPath, "verification", IosSigningPlanner.VerificationFileName);
            string dsymPath = Path.Combine(result.OutputPath, "verification", IosPublishPlanner.DsymVerificationFileName);
            Assert.True(File.Exists(signingPath));
            Assert.True(File.Exists(dsymPath));
            using JsonDocument signing = JsonDocument.Parse(await File.ReadAllTextAsync(signingPath));
            Assert.True(signing.RootElement.GetProperty("ready").GetBoolean());
            Assert.Equal("Automatic", signing.RootElement.GetProperty("style").GetString());
            Assert.Equal("ABCDE12345", signing.RootElement.GetProperty("teamId").GetString());
            using JsonDocument dsym = JsonDocument.Parse(await File.ReadAllTextAsync(dsymPath));
            Assert.False(dsym.RootElement.GetProperty("stripInPackage").GetBoolean());
            Assert.Contains("dwarfdump", dsym.RootElement.GetProperty("pairing").GetString(), StringComparison.Ordinal);

            if (!OperatingSystem.IsMacOS())
            {
                Assert.False(result.Success);
                Assert.Contains("no desktop executable", result.Errors, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(".ipa", result.Errors, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("adb", result.Errors, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }
}
