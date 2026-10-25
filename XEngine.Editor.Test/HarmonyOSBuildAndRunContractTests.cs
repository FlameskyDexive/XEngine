// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Build;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>Build &amp; Run contract coverage used by the HarmonyOS HDC launcher.</summary>
public sealed class HarmonyOSBuildAndRunContractTests
{
    [Fact]
    public void ProcessBuild_UsesPlatformLauncher_AndPublishesItsSuccess()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        var pipeline = new LaunchContractPipeline(BuildLaunchResult.Succeeded("Installed and launched through HDC."));
        var progress = new BuildProgress();

        ProjectBuilder.ProcessBuild("C:\\Project", pipeline, new BuildSettings(), "C:\\Output", progress, andRun: true);

        Assert.True(pipeline.LaunchCalled);
        Assert.True(progress.IsComplete);
        Assert.NotNull(progress.Result);
        Assert.True(progress.Result!.Success);
        Assert.Contains("Installed and launched through HDC.", progress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessBuild_ConvertsHandledLaunchFailure_ToBuildAndRunFailure()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        var pipeline = new LaunchContractPipeline(BuildLaunchResult.Failed("Device did not report readiness.", "redacted hdc output"));
        var progress = new BuildProgress();

        ProjectBuilder.ProcessBuild("C:\\Project", pipeline, new BuildSettings(), "C:\\Output", progress, andRun: true);

        Assert.True(pipeline.LaunchCalled);
        Assert.True(progress.IsComplete);
        Assert.NotNull(progress.Result);
        Assert.False(progress.Result!.Success);
        Assert.Contains("Device did not report readiness.", progress.Result.Errors, StringComparison.Ordinal);
        Assert.Contains("redacted hdc output", progress.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessBuild_LeavesLegacyPipelinesOnExecutablePath()
    {
        // Requires a Windows build host (cmd.exe / Windows-x64-only toolchain contract).
        if (!OperatingSystem.IsWindows())
            return;
        var pipeline = new LaunchContractPipeline(BuildLaunchResult.NotHandled());
        var progress = new BuildProgress();

        ProjectBuilder.ProcessBuild("C:\\Project", pipeline, new BuildSettings(), "C:\\Output", progress, andRun: true);

        Assert.True(pipeline.LaunchCalled);
        Assert.True(pipeline.ExecutablePathRequested);
        Assert.True(progress.Result!.Success);
    }

    private sealed class LaunchContractPipeline(BuildLaunchResult launchResult) : BuildPipeline
    {
        internal bool LaunchCalled { get; private set; }
        internal bool ExecutablePathRequested { get; private set; }

        public override string DisplayName => "Launch contract fixture";
        public override string Id => "xengine.test.launch-contract";
        public override string[] SupportedRuntimeIdentifiers => [];

        public override BuildResult Build(BuildSettings settings, BuildProgress? progress) => CreateResult();

        public override Task<BuildResult> BuildAsync(
            string projectPath,
            BuildSettings settings,
            string? outputDirectory = null,
            BuildProgress? progress = null,
            CancellationToken cancellation = default) => Task.FromResult(CreateResult());

        public override string GetExecutablePath(string outputPath, BuildSettings settings)
        {
            ExecutablePathRequested = true;
            return Path.Combine(Path.GetTempPath(), "xengine-no-legacy-launch.exe");
        }

        public override Task<BuildLaunchResult> LaunchAsync(BuildLaunchRequest request, CancellationToken cancellation = default)
        {
            LaunchCalled = true;
            Assert.True(request.Build.Success);
            Assert.NotNull(request.Progress);
            return Task.FromResult(launchResult);
        }

        private static BuildResult CreateResult() => new()
        {
            Success = true,
            OutputPath = "C:\\Output",
            Duration = TimeSpan.FromMilliseconds(1),
        };
    }
}
