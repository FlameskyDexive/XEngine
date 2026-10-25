// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// M3: macOS desktop target selects its CPU architecture explicitly (Apple Silicon host) and the
/// desktop pipeline's shader-variant matrix gains Metal for macOS targets.
/// </summary>
public class DesktopBuildProfileMacTests
{
    [Fact]
    public void MacOS_DefaultsToArm64RuntimeIdentifier()
    {
        var profile = new DesktopBuildProfile { Platform = BuildTarget.MacOS };
        Assert.Equal("osx-arm64", profile.RuntimeIdentifier);
    }

    [Fact]
    public void MacOS_X64SelectionSwitchesRuntimeIdentifier()
    {
        var profile = new DesktopBuildProfile { Platform = BuildTarget.MacOS, MacOSArch = DesktopBuildProfile.MacOSArchitecture.X64 };
        Assert.Equal("osx-x64", profile.RuntimeIdentifier);
    }

    [Fact]
    public void OtherPlatforms_ArchSelectionDoesNotLeakIntoTheirRids()
    {
        Assert.Equal("win-x64", new DesktopBuildProfile { Platform = BuildTarget.Windows }.RuntimeIdentifier);
        Assert.Equal("linux-x64", new DesktopBuildProfile { Platform = BuildTarget.Linux }.RuntimeIdentifier);
    }

    [Fact]
    public void ToDefault_ResetsMacOSArch()
    {
        var profile = new DesktopBuildProfile { Platform = BuildTarget.MacOS, MacOSArch = DesktopBuildProfile.MacOSArchitecture.X64 };
        profile.ToDefault();
        Assert.Equal(DesktopBuildProfile.MacOSArchitecture.Arm64, profile.MacOSArch);
    }
}
