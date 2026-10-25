// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build.Ios;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N5 Xcode locator: DEVELOPER_DIR precedence over xcode-select, version parsing and the minimum
/// gate, Command-Line-Tools degradation with actionable guidance, not-found guidance, and the
/// non-macOS short circuit (Windows hosts get the preflight explanation, never a probe).
/// </summary>
public sealed class XcodeLocatorTests : EditorTestHarness
{
    private static XcodeLocator.ProcessRunner Fake(
        (int exit, string stdout)? xcodeSelect = null,
        (int exit, string stdout, string stderr)? xcodebuild = null,
        string? developerDir = null)
    {
        return (executable, arguments, _) =>
        {
            if (executable.Contains("xcode-select"))
                return Task.FromResult(xcodeSelect.HasValue
                    ? (xcodeSelect.Value.exit, xcodeSelect.Value.stdout, "")
                    : (1, "", "error: unable to get active developer directory"));
            return Task.FromResult(xcodebuild.HasValue
                ? xcodebuild.Value
                : (1, "", "xcodebuild: error: tool not found"));
        };
    }

    private static void WithDeveloperDir(string? value, Action action)
    {
        string? previous = Environment.GetEnvironmentVariable("DEVELOPER_DIR");
        Environment.SetEnvironmentVariable("DEVELOPER_DIR", value);
        try { action(); }
        finally { Environment.SetEnvironmentVariable("DEVELOPER_DIR", previous); }
    }

    [Fact]
    public void OnWindows_ShortCircuitsWithPreflightExplanation()
    {
        if (OperatingSystem.IsMacOS()) return; // host-gated branch only exists off-Mac
        XcodeLocatorResult result = new XcodeLocator(
            (exe, args, ct) => throw new InvalidOperationException("no processes on the Windows path"))
            .LocateAsync(new Version(16, 0)).GetAwaiter().GetResult();

        Assert.False(result.Found);
        Assert.Contains("run on macOS", result.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void FullXcode_ViaXcodeSelect_MeetsMinimum()
    {
        WithDeveloperDir(null, () =>
        {
            XcodeLocatorResult result = new XcodeLocator(Fake(
                xcodeSelect: (0, "/Applications/Xcode.app/Contents/Developer\n"),
                xcodebuild: (0, "Xcode 16.2\nBuild version 16C5032a\n", "")))
                .LocateCoreAsync(new Version(16, 0)).GetAwaiter().GetResult();

            Assert.True(result.Found);
            Assert.Equal("/Applications/Xcode.app/Contents/Developer", result.DeveloperDir);
            Assert.Equal(new Version(16, 2), result.Version);
            Assert.True(result.MeetsMinimum);
            Assert.Null(result.Guidance);
        });
    }

    [Fact]
    public void DeveloperDir_OverridesXcodeSelect()
    {
        WithDeveloperDir("/Custom/Xcode/Contents/Developer", () =>
        {
            XcodeLocatorResult result = new XcodeLocator(Fake(
                xcodeSelect: (0, "/Applications/OTHER/Developer\n"), // must never be consulted
                xcodebuild: (0, "Xcode 17.0\nBuild version X\n", "")))
                .LocateCoreAsync().GetAwaiter().GetResult();

            Assert.True(result.Found);
            Assert.Equal("/Custom/Xcode/Contents/Developer", result.DeveloperDir);
        });
    }

    [Fact]
    public void CommandLineToolsOnly_ReportsDegradedWithGuidance()
    {
        WithDeveloperDir(null, () =>
        {
            XcodeLocatorResult result = new XcodeLocator(Fake(
                xcodeSelect: (0, "/Library/Developer/CommandLineTools\n")))
                .LocateCoreAsync(new Version(16, 0)).GetAwaiter().GetResult();

            Assert.False(result.Found);
            Assert.True(result.CommandLineToolsOnly);
            Assert.Contains("Full Xcode is required", result.Guidance, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void BelowMinimum_ReportsUpgradeGuidance()
    {
        WithDeveloperDir(null, () =>
        {
            XcodeLocatorResult result = new XcodeLocator(Fake(
                xcodeSelect: (0, "/Applications/Xcode.app/Contents/Developer\n"),
                xcodebuild: (0, "Xcode 15.3\nBuild version 15E204a\n", "")))
                .LocateCoreAsync(new Version(16, 0)).GetAwaiter().GetResult();

            Assert.True(result.Found);
            Assert.False(result.MeetsMinimum);
            Assert.Contains("older than the required 16.0", result.Guidance, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void NothingFound_GivesInstallGuidance()
    {
        WithDeveloperDir(null, () =>
        {
            XcodeLocatorResult result = new XcodeLocator(Fake())
                .LocateCoreAsync().GetAwaiter().GetResult();

            Assert.False(result.Found);
            Assert.Contains("Install Xcode from the Mac App Store", result.Guidance, StringComparison.Ordinal);
        });
    }
}
