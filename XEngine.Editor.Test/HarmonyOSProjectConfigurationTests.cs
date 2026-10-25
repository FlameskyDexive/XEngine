// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HarmonyOSProjectConfigurationTests
{
    [Fact]
    public void RuntimeProject_UsesIsolatedHarmonyOSTargetConfiguration()
    {
        string repoRoot = FindRepositoryRoot();
        string directoryBuildProps = File.ReadAllText(Path.Combine(repoRoot, "Directory.Build.props"));
        string runtimeProject = File.ReadAllText(Path.Combine(repoRoot, "XEngine.Runtime", "XEngine.Runtime.csproj"));

        Assert.Contains("OpenHarmonyTarget", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("IsHarmonyOSTarget", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("HarmonyOSIntermediateRoot", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("XENGINE_HARMONYOS", runtimeProject, StringComparison.Ordinal);
        Assert.Contains("XENGINE_MOBILE", runtimeProject, StringComparison.Ordinal);

        const string mobileCondition = "'$(IsAndroidTarget)' == 'true' OR '$(IsHarmonyOSTarget)' == 'true'";
        const string desktopCondition = "'$(IsAndroidTarget)' != 'true' AND '$(IsHarmonyOSTarget)' != 'true'";
        Assert.Contains(mobileCondition, runtimeProject, StringComparison.Ordinal);
        Assert.Contains(desktopCondition, runtimeProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Condition=\"'$(IsAndroidTarget)' != 'true'\"", runtimeProject, StringComparison.Ordinal);

        AssertSourceUsesMobileClosure(repoRoot, "XEngine.Runtime", "Graphics", "Graphics.cs");
        AssertSourceUsesMobileClosure(repoRoot, "XEngine.Runtime", "Graphics", "RHI", "GraphicsDeviceFactory.cs");
        AssertSourceUsesMobileClosure(repoRoot, "XEngine.Runtime", "Graphics", "RHI", "Shaders", "ShaderCompilerFactory.cs");
        AssertSourceUsesMobileClosure(repoRoot, "XEngine.Runtime", "Resources", "Texture2D.cs");
        AssertSourceUsesMobileClosure(repoRoot, "XEngine.Runtime", "Game.cs");

        string androidWindow = File.ReadAllText(Path.Combine(repoRoot, "XEngine.Runtime", "Platforms", "Android", "AndroidWindow.cs"));
        Assert.Contains("#if XENGINE_ANDROID", androidWindow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                File.Exists(Path.Combine(directory.FullName, "XEngine.Runtime", "XEngine.Runtime.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }

    private static void AssertSourceUsesMobileClosure(string repoRoot, params string[] relativePath)
    {
        string source = File.ReadAllText(Path.Combine([repoRoot, .. relativePath]));
        Assert.Contains("XENGINE_MOBILE", source, StringComparison.Ordinal);
    }
}
