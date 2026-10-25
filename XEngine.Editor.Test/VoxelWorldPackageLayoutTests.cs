using System.Text.Json;

using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class VoxelWorldPackageLayoutTests
{
    [Fact]
    public void Package_HasOptionalOfficialScaffold()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.voxelworld");
        PackageJson package = Assert.IsType<PackageJson>(PackageJson.Read(Path.Combine(root, "package.json")));
        Assert.Equal("com.xengine.voxelworld", package.Name);
        Assert.Equal("0.1.18", package.Version);
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
        Assert.True(File.Exists(Path.Combine(root, "THIRD_PARTY_ASSETS.md")));
        Assert.True(File.Exists(Path.Combine(root, "Documentation~", "npm.md")));
        Assert.True(File.Exists(Path.Combine(root, "README.md")));

        Assert.Single(package.Samples);
        Assert.Equal("Samples~/Empty", package.Samples[0].Path);
        Assert.True(File.Exists(Path.Combine(root, "Samples~", "Empty", "README.md")));
        Assert.True(File.Exists(Path.Combine(root, "Runtime", "VoxelWorldRuntime.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Editor", "VoxelWorldPackageEditor.cs")));

        string runtime = File.ReadAllText(Path.Combine(root, "Runtime", "VoxelWorldRuntime.cs"));
        Assert.Contains("com.xengine.voxelworld", runtime, StringComparison.Ordinal);
        Assert.Contains("0.1.18", runtime, StringComparison.Ordinal);
        Assert.Contains("namespace XEngine.VoxelWorld", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("Kronnect", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("VoxelPlayEnvironment", runtime, StringComparison.Ordinal);

        string docs = File.ReadAllText(Path.Combine(root, "Documentation~", "index.md"));
        Assert.Contains("Package Manager", docs, StringComparison.Ordinal);
        Assert.Contains("VoxelWorld", docs, StringComparison.Ordinal);

        string notice = File.ReadAllText(Path.Combine(root, "THIRD_PARTY_ASSETS.md"));
        Assert.Contains("DungeonMason", notice, StringComparison.Ordinal);
        Assert.Contains("not licensed under MIT", File.ReadAllText(Path.Combine(root, "LICENSE.md")), StringComparison.OrdinalIgnoreCase);

        using JsonDocument runtimeAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Runtime", "XEngine.VoxelWorld.Runtime.asmdef")));
        Assert.Equal(0, runtimeAsm.RootElement.GetProperty("includePlatforms").GetArrayLength());
        Assert.True(runtimeAsm.RootElement.GetProperty("allowUnsafeCode").GetBoolean());
        using JsonDocument editorAsm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Editor", "XEngine.VoxelWorld.Editor.asmdef")));
        Assert.Equal("Editor", editorAsm.RootElement.GetProperty("includePlatforms")[0].GetString());
    }

    [Fact]
    public void SamplesTilde_IsIgnoredByPackageAssetScan()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.voxelworld");
        string sample = Path.Combine(root, "Samples~", "Empty", "README.md");
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(sample, root));
        string runtime = Path.Combine(root, "Runtime", "VoxelWorldRuntime.cs");
        Assert.False(PackageManager.IsUnderIgnoredPackageDir(runtime, root));
    }

    [Fact]
    public void TestProject_PinsPackageByFileSpecifier()
    {
        string repo = FindRepositoryRoot();
        string manifestPath = Path.Combine(repo, "VoxelWorldTestProject", "Packages", "manifest.json");
        Assert.True(File.Exists(manifestPath));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        string? spec = manifest.RootElement.GetProperty("dependencies").GetProperty("com.xengine.voxelworld").GetString();
        Assert.Equal("file:../Packages/com.xengine.voxelworld", spec);
        Assert.True(File.Exists(Path.Combine(repo, "VoxelWorldTestProject", "VoxelWorldTestProject.xengine")));
        Assert.True(File.Exists(Path.Combine(repo, "VoxelWorldTestProject", "Assets", "Scenes", "Empty.scene")));
    }

    [Fact]
    public void Catalog_ListsVoxelWorldAsOfficialOptional()
    {
        string repo = FindRepositoryRoot();
        string builtIn = Path.Combine(repo, "Packages");
        Assert.True(Directory.Exists(Path.Combine(builtIn, "com.xengine.voxelworld")));
        PackageJson? metadata = PackageJson.Read(Path.Combine(builtIn, "com.xengine.voxelworld", "package.json"));
        Assert.NotNull(metadata);
        Assert.False(metadata!.IsInstalledByDefault);
        Assert.Equal("Official", metadata.Category);
        Assert.Contains("xengine", metadata.Keywords ?? []);
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
