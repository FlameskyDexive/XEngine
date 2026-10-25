// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSPlatformFilteringTests
{
    [Fact]
    public void BuildPlatforms_RegistersCanonicalHarmonyOSName()
    {
        Assert.Equal("HarmonyOS", BuildPlatforms.HarmonyOS);
        Assert.Contains(BuildPlatforms.HarmonyOS, BuildPlatforms.Players);
        Assert.Contains(BuildPlatforms.HarmonyOS, BuildPlatforms.All);
        Assert.Equal(BuildPlatforms.HarmonyOS, BuildPlatforms.Normalize("harmonyos"));
        Assert.Equal(BuildPlatforms.HarmonyOS, BuildPlatforms.FromBuildTarget(BuildTarget.HarmonyOS));
    }

    [Fact]
    public void AssemblyDefinition_CanIncludeHarmonyOSWithoutIncludingLinuxOrAndroid()
    {
        var definition = new AssemblyDefinition
        {
            IncludePlatforms = { BuildPlatforms.HarmonyOS },
        };

        Assert.True(definition.IncludedFor(BuildPlatforms.HarmonyOS));
        Assert.False(definition.IncludedFor(BuildPlatforms.Linux));
        Assert.False(definition.IncludedFor(BuildPlatforms.Android));
    }

    [Theory]
    [InlineData("arm64", "ohos-arm64")]
    [InlineData("x64", "ohos-x64")]
    public void NativePlugin_UsesDistinctHarmonyOSRuntimeLayout(string cpu, string expectedRid)
    {
        var plugin = new PluginInfo
        {
            AbsolutePath = "/plugins/libexample.so",
            IsNative = true,
            EditorOnly = false,
            AnyPlatform = false,
            Platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                BuildPlatforms.HarmonyOS,
            },
            Cpu = cpu,
        };

        Assert.True(plugin.AppliesToBuild(BuildPlatforms.HarmonyOS));
        Assert.False(plugin.AppliesToBuild(BuildPlatforms.Linux));
        Assert.False(plugin.AppliesToBuild(BuildPlatforms.Android));
        Assert.Equal(expectedRid, plugin.RuntimeIdentifierFor(BuildPlatforms.HarmonyOS));
    }

    [Fact]
    public void LinuxNativePlugin_DoesNotApplyToHarmonyOS()
    {
        var plugin = new PluginInfo
        {
            AbsolutePath = "/plugins/liblinux.so",
            IsNative = true,
            EditorOnly = false,
            AnyPlatform = false,
            Platforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                BuildPlatforms.Linux,
            },
            Cpu = "arm64",
        };

        Assert.False(plugin.AppliesToBuild(BuildPlatforms.HarmonyOS));
        Assert.Equal("linux-arm64", plugin.RuntimeIdentifierFor(BuildPlatforms.Linux));
    }
}
