// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;

using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Project window Packages root: the folder index must expose a virtual "Packages" key whose
/// children are resolved packages (DisplayName label, LogicalRoot path), and nested package
/// folders must still enumerate.
/// </summary>
public sealed class ProjectWindowPackagesTests : EditorTestHarness
{
    private readonly string? _prevBuiltInRoot;

    public ProjectWindowPackagesTests()
    {
        _prevBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
        PackageManager.BuiltInPackagesPathOverride = Path.Combine(Project.TempPath, "NoBuiltInPackages");
        PackageManager.Invalidate();
    }

    public override void Dispose()
    {
        PackageManager.BuiltInPackagesPathOverride = _prevBuiltInRoot;
        PackageManager.Invalidate();
        base.Dispose();
    }

    [Fact]
    public void GetSubFolders_Packages_ReturnsEachResolvedPackage_WithDisplayName()
    {
        WriteEmbedPackage("com.test.alpha", "1.2.3", "Alpha Pack");
        WriteEmbedPackage("com.test.beta", "0.1.0", "Beta Pack");
        ResolveAndRefreshIndex();

        var folders = Assets.GetSubFolders("Packages");
        Assert.Equal(2, folders.Count);

        var alpha = Assert.Single(folders, f => f.RelativePath == "Packages/com.test.alpha");
        Assert.Equal("Alpha Pack", alpha.Name);

        var beta = Assert.Single(folders, f => f.RelativePath == "Packages/com.test.beta");
        Assert.Equal("Beta Pack", beta.Name);
    }

    [Fact]
    public void GetSubFolders_Packages_FallsBackToTechnicalName_WhenDisplayNameMissing()
    {
        WriteEmbedPackage("com.test.nameless", "1.0.0", displayName: null);
        ResolveAndRefreshIndex();

        var folders = Assets.GetSubFolders("Packages");
        var nameless = Assert.Single(folders, f => f.RelativePath == "Packages/com.test.nameless");
        Assert.Equal("com.test.nameless", nameless.Name);
    }

    [Fact]
    public void GetSubFolders_PackageRoot_EnumeratesChildDirectories()
    {
        WriteEmbedPackage("com.test.tree", "1.0.0", "Tree Pack");
        string runtime = Path.Combine(Project.PackagesPath, "com.test.tree", "Runtime");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "readme.txt"), "ok");
        ResolveAndRefreshIndex();

        var children = Assets.GetSubFolders("Packages/com.test.tree");
        var runtimeFolder = Assert.Single(children, f => f.RelativePath == "Packages/com.test.tree/Runtime");
        Assert.Equal("Runtime", runtimeFolder.Name);
    }

    [Fact]
    public void TryResolveAbsoluteItemPath_EmbedPackage_MapsToPhysicalRoot()
    {
        WriteEmbedPackage("com.test.locate", "1.0.0", "Locate Pack");
        ResolveAndRefreshIndex();

        Assert.True(ProjectPanel.IsPackagePath("Packages/com.test.locate"));
        Assert.False(ProjectPanel.IsPackagePath("Packages"));
        Assert.False(ProjectPanel.IsPackagePath("Art/Hero"));

        Assert.True(ProjectPanel.TryResolveAbsoluteItemPath("Packages/com.test.locate", out string abs));
        Assert.True(Directory.Exists(abs));
        Assert.EndsWith("Packages/com.test.locate", abs.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    private void ResolveAndRefreshIndex()
    {
        PackageManager.Invalidate();
        _ = PackageManager.Resolve(Project);
        Assets.InvalidateFolderIndex();
    }

    private void WriteEmbedPackage(string packageName, string version, string? displayName)
    {
        string root = Path.Combine(Project.PackagesPath, packageName);
        Directory.CreateDirectory(root);
        var pj = new PackageJson { Name = packageName, Version = version, DisplayName = displayName };
        File.WriteAllText(Path.Combine(root, "package.json"),
            JsonSerializer.Serialize(pj, new JsonSerializerOptions { WriteIndented = true }));
    }
}
