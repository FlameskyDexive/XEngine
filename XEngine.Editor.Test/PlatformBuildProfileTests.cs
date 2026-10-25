using System.Collections.Generic;

using XEngine.Editor.Build;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

public class PlatformBuildProfileTests
{
    [Theory]
    [InlineData(BuildTarget.Windows, "XENGINE_WINDOWS")]
    [InlineData(BuildTarget.Linux, "XENGINE_LINUX")]
    [InlineData(BuildTarget.MacOS, "XENGINE_MACOS")]
    public void DesktopProfile_EmitsCommonAndSpecificPlatformDefines(BuildTarget platform, string expectedDefine)
    {
        var profile = new DesktopBuildProfile { Platform = platform };
        var defines = new List<string>();

        profile.ModifyDefines(defines);

        Assert.Contains("XENGINE_DESKTOP", defines);
        Assert.Contains(expectedDefine, defines);
    }

    [Fact]
    public void IosProfile_EmitsMobilePlatformDefines()
    {
        var defines = new List<string>();

        new IosBuildProfile().ModifyDefines(defines);

        Assert.Contains("XENGINE_IOS", defines);
        Assert.Contains("XENGINE_MOBILE", defines);
    }
}
