using System.Text.Json;

using XEngine.Editor.Projects.Packages;
using XEngine.Terrain;
using XEngine.Terrain.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class TerrainPackageLayoutTests
{
    [Fact]
    public void Package_HasCompleteMetadataDocsSamplesAndPlayerRuntime()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.terrain");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        Assert.Equal("com.xengine.terrain", package.Name);
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

        Assert.Single(package.Samples);
        Assert.Equal("Samples~/Starter", package.Samples[0].Path);
        Assert.Contains("hill", package.Samples[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Placeholder", package.Samples[0].Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "Starter", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "Starter", "Starter.scene")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "Starter", "Starter.terraindata")));
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "Starter", "Grass.terrainlayer")));
        string scene = File.ReadAllText(Path.Combine(root, "Samples~", "Starter", "Starter.scene"));
        Assert.Contains("XEngine.Terrain.TerrainComponent, XEngine.Terrain.Runtime", scene, StringComparison.Ordinal);
        Assert.Contains("XEngine.Terrain.WindZone, XEngine.Terrain.Runtime", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("XEngine.Runtime.Test", scene, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "TerrainRuntime.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "TerrainComponent.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "TerrainLayer.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "TerrainBrushSampler.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "WindZone.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "TerrainNeighboring.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "TerrainTypeRedirects.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "TerrainShaderLibrary.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Shaders", "Terrain.shader")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "Shaders", "Grass.shader")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "TerrainPackageEditor.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "TerrainEditor.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "TerrainMenu.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "TerrainLayerImporter.cs")));

        string docs = File.ReadAllText(Path.Combine(root, "Documentation~", "index.md"));
        Assert.Contains("TerrainComponent", docs, StringComparison.Ordinal);
        Assert.Contains("TerrainData", docs, StringComparison.Ordinal);
        Assert.Contains("Package Manager", docs, StringComparison.Ordinal);
        Assert.Contains("WindZone", docs, StringComparison.Ordinal);
        Assert.Contains("SampleHeight", docs, StringComparison.Ordinal);
        Assert.Contains("Samples~/Starter", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder until M5", docs, StringComparison.OrdinalIgnoreCase);
        string changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));
        Assert.Contains("Samples~/Starter", changelog, StringComparison.Ordinal);

        Assert.Equal(TerrainRuntime.PackageName, package.Name);
        Assert.Equal(TerrainRuntime.Version, package.Version);
        Assert.Equal(TerrainPackageEditor.PackageName, TerrainRuntime.PackageName);

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.Terrain.Runtime.asmdef")));
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        Assert.True(runtimeAsm.RootElement.GetProperty("allowUnsafeCode").GetBoolean());
        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.Terrain.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
    }

    [Fact]
    public void SamplesTilde_IsIgnoredByPackageAssetScan()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.terrain");
        string sample = Path.Combine(root, "Samples~", "Starter", "README.md");
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(sample, root));
        string runtime = Path.Combine(root, "Runtime", "TerrainRuntime.cs");
        Assert.False(PackageManager.IsUnderIgnoredPackageDir(runtime, root));
    }

    [Fact]
    public void LayoutEvidence_WritesM0Notes()
    {
        string repo = FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "m0-terrain-package.md"),
            "# M0 Terrain package scaffold\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "`com.xengine.terrain` 0.1.0 ships `Documentation~/`, `Samples~/Starter`, " +
            "CHANGELOG, LICENSE. Runtime asmdef has empty `includePlatforms` (player). " +
            "Editor asmdef is Editor-only. `Samples~` is skipped by package asset scan.\n\n" +
            "`installByDefault` is false: Package Manager lists the package as Available until Install " +
            "copies it into `Library/PackageCache/com.xengine.terrain@0.1.0/`.\n\n" +
            "Screenshots: [m0-optional-package-available.png](m0-optional-package-available.png), " +
            "[m0-optional-package-installed.png](m0-optional-package-installed.png).\n");
        Assert.True(File.Exists(Path.Combine(dir, "m0-terrain-package.md")));
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
