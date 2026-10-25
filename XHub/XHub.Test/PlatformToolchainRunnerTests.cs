using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XHub.Models;
using XHub.Services;
using Xunit;

namespace XHub.Test;

public class PlatformToolchainRunnerTests
{
    [Fact]
    public void BuildArgs_Android_RequiresExplicitConsentFlag()
    {
        // §3.4: sdkmanager runs only with --accept-sdk-licenses — the flag must never be
        // implied by the UI flow.
        string[] without = PlatformToolchainRunner.BuildArgs(PlatformToolchainTarget.Android, acceptLicenses: false, null);
        Assert.Equal(["--install-platform-tools", "android"], without);

        string[] with = PlatformToolchainRunner.BuildArgs(PlatformToolchainTarget.Android, acceptLicenses: true, null);
        Assert.Equal(["--install-platform-tools", "android", "--accept-sdk-licenses"], with);
    }

    [Fact]
    public void BuildArgs_HarmonyOS_CarriesLocalBundle()
    {
        string[] args = PlatformToolchainRunner.BuildArgs(
            PlatformToolchainTarget.HarmonyOS, acceptLicenses: false, harmonyBundlePath: @"C:\sdk\bundle.zip");
        Assert.Equal(["--install-platform-tools", "harmonyos", "--harmonyos-bundle", @"C:\sdk\bundle.zip"], args);
    }

    [Fact]
    public void BuildArgs_Ios_Plain()
    {
        string[] args = PlatformToolchainRunner.BuildArgs(PlatformToolchainTarget.Ios, acceptLicenses: true, "ignored");
        Assert.Equal(["--install-platform-tools", "ios"], args);
    }

    [Fact]
    public void IosSupportedOnHost_FalseOnNonMacOS()
    {
        // Host limitation path (§3.4): iOS detection requires macOS.
        Assert.Equal(OperatingSystem.IsMacOS(), PlatformToolchainRunner.IosSupportedOnHost);
    }

    [Fact]
    public async Task RunAsync_MissingEditor_FailsWithDiagnostic()
    {
        var install = new InstallationRecord
        {
            Path = Path.Combine(Path.GetTempPath(), "nowhere"),
            EditorExePath = Path.Combine(Path.GetTempPath(), "nowhere", "XEngine.Editor.exe"),
        };
        var lines = new System.Collections.Concurrent.ConcurrentQueue<ToolchainLogLine>();
        ToolchainRunResult result = await PlatformToolchainRunner.RunAsync(
            install, PlatformToolchainTarget.Android, acceptLicenses: false, null,
            line => lines.Enqueue(line), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.LicenseConsentRequired);
        Assert.NotEmpty(result.Log);
    }
}
