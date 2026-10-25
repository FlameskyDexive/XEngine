// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Navigation;

using Xunit;

namespace XEngine.Runtime.Test.Navigation;

public sealed class NavExtractTests
{
    [Fact]
    public void CoreRuntimeSources_DoNotDeclareRecastNavMeshTypes()
    {
        string repo = FindRepositoryRoot();
        string csproj = File.ReadAllText(Path.Combine(repo, "XEngine.Runtime", "XEngine.Runtime.csproj"));
        Assert.DoesNotContain("DotRecast", csproj, StringComparison.Ordinal);

        foreach (string file in Directory.GetFiles(Path.Combine(repo, "XEngine.Runtime"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;
            string text = File.ReadAllText(file);
            Assert.DoesNotContain("using DotRecast", text, StringComparison.Ordinal);
            Assert.DoesNotContain("class NavMeshAgent", text, StringComparison.Ordinal);
            Assert.DoesNotContain("class NavMeshBuilder", text, StringComparison.Ordinal);
            Assert.DoesNotContain("class NavMeshData", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PackageRuntime_OwnsRecastSources()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.ai.navigation", "Runtime");
        Assert.True(File.Exists(Path.Combine(root, "NavMeshBuilder.cs")));
        Assert.True(File.Exists(Path.Combine(root, "NavMeshData.cs")));
        Assert.True(File.Exists(Path.Combine(root, "NavMeshAgent.cs")));
        Assert.True(File.Exists(Path.Combine(root, "NavTypeRedirects.cs")));
        Assert.True(File.Exists(Path.Combine(root, "NavMeshSurface.cs")));
        string builder = File.ReadAllText(Path.Combine(root, "NavMeshBuilder.cs"));
        Assert.Contains("NavMeshGeometryCollectors.TryCollect", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("TerrainComponent", builder, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyTypeNames_ResolveToPackageTypes()
    {
        Assert.Same(typeof(NavMeshData),
            RuntimeUtils.FindType("XEngine.Runtime.Navigation.NavMeshData"));
        Assert.Same(typeof(NavMeshData),
            RuntimeUtils.FindType(
                "XEngine.Runtime.Navigation.NavMeshData, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));
        Assert.Same(typeof(NavMeshAgent),
            RuntimeUtils.FindType("XEngine.Runtime.Navigation.NavMeshAgent, XEngine.Runtime"));
        Assert.Same(typeof(NavMeshData),
            RuntimeUtils.FindType("XEngine.Runtime.Navigation.NavMeshData, XEngine.AI.Navigation.Runtime"));
    }

    [Fact]
    public void CoreCollectorContract_HasNoRecastTypes()
    {
        string path = Path.Combine(FindRepositoryRoot(), "XEngine.Runtime", "Navigation", "NavMeshGeometryCollectors.cs");
        string text = File.ReadAllText(path);
        Assert.Contains("INavMeshTriangleSink", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DotRecast", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NavMeshInputGeometry", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NavMeshBuildSettings", text, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
