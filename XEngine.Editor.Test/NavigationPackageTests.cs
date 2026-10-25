// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using XEngine.AI.Navigation;
using XEngine.AI.Navigation.Editor;
using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class NavigationPackageTests
{
    [Fact]
    public void Package_HasOptionalOfficialMetadataRuntimeExtractAndDocs()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.ai.navigation");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        Assert.Equal("com.xengine.ai.navigation", package.Name);
        Assert.Equal("0.1.0", package.Version);
        Assert.False(package.IsInstalledByDefault);
        Assert.Equal("0.1.0", package.XEngine);
        Assert.Equal("package", package.PackageType);
        Assert.Equal("Official", package.Category);
        Assert.Equal("Documentation~/index.md", package.DocumentationUrl);
        Assert.Equal("CHANGELOG.md", package.ChangelogUrl);
        Assert.Equal("LICENSE.md", package.LicensesUrl);
        Assert.True(File.Exists(Path.Combine(root, package.DocumentationUrl!)));
        Assert.True(File.Exists(Path.Combine(root, package.ChangelogUrl!)));
        Assert.True(File.Exists(Path.Combine(root, package.LicensesUrl!)));
        Assert.True(File.Exists(Path.Combine(root, "README.md")));

        Assert.Single(package.Samples);
        Assert.Equal("Samples~", package.Samples[0].Path);
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "NavMeshBuilder.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "NavMeshData.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "NavMeshAgent.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "NavTypeRedirects.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "NavMeshSurface.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "NavigationPanel.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "NavRuntimeTools.cs")));

        string docs = File.ReadAllText(Path.Combine(root, "Documentation~", "index.md"));
        Assert.Contains("Package Manager", docs, StringComparison.Ordinal);
        Assert.Contains("NavMeshSurface", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("automatic for built-in", docs, StringComparison.OrdinalIgnoreCase);
        string changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        Assert.Contains("installByDefault", changelog, StringComparison.Ordinal);

        Assert.Equal(NavigationRuntime.PackageName, package.Name);
        Assert.Equal(NavigationRuntime.Version, package.Version);
        Assert.Equal(NavigationEditor.PackageName, NavigationRuntime.PackageName);

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.AI.Navigation.Runtime.asmdef")));
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.AI.Navigation.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
    }

    [Fact]
    public void SamplesTilde_IsIgnoredByPackageAssetScan()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.ai.navigation");
        string sample = Path.Combine(root, "Samples~", "README.md");
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(sample, root));
        string runtime = Path.Combine(root, "Runtime", "NavMeshBuilder.cs");
        Assert.False(PackageManager.IsUnderIgnoredPackageDir(runtime, root));
    }

    [Fact]
    public void CoreRuntime_DoesNotReferenceDotRecast()
    {
        string repo = FindRepositoryRoot();
        string csproj = File.ReadAllText(Path.Combine(repo, "XEngine.Runtime", "XEngine.Runtime.csproj"));
        Assert.DoesNotContain("DotRecast", csproj, StringComparison.Ordinal);
        string[] remaining = Directory.GetFiles(Path.Combine(repo, "XEngine.Runtime", "Navigation"), "*.cs");
        Assert.Equal("NavMeshGeometryCollectors.cs", Path.GetFileName(Assert.Single(remaining)));
    }

    [Fact]
    public void LayoutEvidence_WritesM0Notes()
    {
        string repo = FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "navigation");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "m0-optional-package.md"),
            "# M0 AI Navigation optional package\n\n" +
            "Date: 2026-08-16. Branch: `codex/animation-module`.\n\n" +
            "`com.xengine.ai.navigation` 0.1.0 is an official optional package: " +
            "`installByDefault` is false, Package Manager lists it as Available until Install " +
            "copies it into `Library/PackageCache/com.xengine.ai.navigation@0.1.0/`.\n\n" +
            "Recast/Detour bake, query, and agents live in the package Runtime. Core Runtime keeps " +
            "only `INavMeshTriangleSink` / `INavMeshGeometryCollector`. DotRecast ProjectReferences " +
            "moved from `XEngine.Runtime.csproj` to `XEngine.Editor.csproj` so ScriptCompiler can " +
            "still copy `DotRecast.*.dll` next to the Editor.\n\n" +
            "Screenshots: [m0-optional-package-available.png](m0-optional-package-available.png), " +
            "[m0-optional-package-installed.png](m0-optional-package-installed.png).\n");
        Assert.True(File.Exists(Path.Combine(dir, "m0-optional-package.md")));
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
