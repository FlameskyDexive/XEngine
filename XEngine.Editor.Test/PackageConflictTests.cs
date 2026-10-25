// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Generic package.json <c>conflicts</c> mutual exclusion. Uses throwaway package names only —
/// no product-specific types.
/// </summary>
public sealed class PackageConflictTests : EditorTestHarness
{
    public PackageConflictTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
        PackageManager.BuiltInPackagesPathOverride = Path.Combine(Project.TempPath, "NoBuiltInPackages");
        PackageManager.Invalidate();
    }

    [Fact]
    public void PackageJson_ReadsConflictsArray()
    {
        string path = Path.Combine(Project.TempPath, "conflicts.json");
        File.WriteAllText(path,
            """{"name":"com.xengine.conflict.a","version":"1.0.0","conflicts":["com.xengine.conflict.b"]}""");
        PackageJson? package = PackageJson.Read(path);
        Assert.NotNull(package);
        Assert.Equal("com.xengine.conflict.a", package!.Name);
        Assert.Equal("com.xengine.conflict.b", Assert.Single(package.Conflicts));
    }

    [Fact]
    public void Install_RejectsWhenIncomingDeclaresConflict()
    {
        string builtInRoot = WriteConflictingPair(aConflictsWithB: true, bConflictsWithA: false);
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = builtInRoot;
            PackageManager.Invalidate();

            PackageOperationResult first = PackageOperations.Install(Project, "com.xengine.conflict.b", "1.0.0");
            Assert.True(first.Success, first.Message);

            PackageOperationResult second = PackageOperations.Install(Project, "com.xengine.conflict.a", "1.0.0");
            Assert.False(second.Success);
            Assert.Contains("com.xengine.conflict.a", second.Message, StringComparison.Ordinal);
            Assert.Contains("com.xengine.conflict.b", second.Message, StringComparison.Ordinal);

            Assert.DoesNotContain(PackageManager.Resolve(Project), p => p.Name == "com.xengine.conflict.a");
            Assert.Contains(PackageManager.Resolve(Project), p => p.Name == "com.xengine.conflict.b");
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
            TryDeleteDir(builtInRoot);
        }
    }

    [Fact]
    public void Install_RejectsWhenInstalledPackageDeclaresConflict()
    {
        string builtInRoot = WriteConflictingPair(aConflictsWithB: true, bConflictsWithA: false);
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = builtInRoot;
            PackageManager.Invalidate();

            PackageOperationResult first = PackageOperations.Install(Project, "com.xengine.conflict.a", "1.0.0");
            Assert.True(first.Success, first.Message);

            PackageOperationResult second = PackageOperations.Install(Project, "com.xengine.conflict.b", "1.0.0");
            Assert.False(second.Success);
            Assert.Contains("conflicts with", second.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PackageManager.Resolve(Project), p => p.Name == "com.xengine.conflict.b");
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
            TryDeleteDir(builtInRoot);
        }
    }

    [Fact]
    public void Resolve_SurfacesConflictWhenBothArePinned()
    {
        string builtInRoot = WriteConflictingPair(aConflictsWithB: true, bConflictsWithA: false);
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = builtInRoot;
            PackageManager.Invalidate();
            Assert.True(PackageOperations.Install(Project, "com.xengine.conflict.a", "1.0.0").Success);

            string manifestPath = Path.Combine(Project.PackagesPath, "manifest.json");
            ProjectPackageManifest manifest = ProjectPackageManifest.ReadOrDefault(manifestPath);
            manifest.Dependencies["com.xengine.conflict.b"] = "1.0.0";
            manifest.Write(manifestPath);
            PackageManager.Invalidate();

            IReadOnlyList<ResolvedPackage> resolved = PackageManager.Resolve(Project);
            string? conflict = PackageOperations.FindPackageConflict(resolved);
            Assert.NotNull(conflict);
            Assert.Contains("com.xengine.conflict.a", conflict, StringComparison.Ordinal);
            Assert.Contains("com.xengine.conflict.b", conflict, StringComparison.Ordinal);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
            TryDeleteDir(builtInRoot);
        }
    }

    private string WriteConflictingPair(bool aConflictsWithB, bool bConflictsWithA)
    {
        string root = Path.Combine(Path.GetDirectoryName(Project.RootPath)!,
            "official-packages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WriteOptional(root, "com.xengine.conflict.a", aConflictsWithB ? ["com.xengine.conflict.b"] : []);
        WriteOptional(root, "com.xengine.conflict.b", bConflictsWithA ? ["com.xengine.conflict.a"] : []);
        return root;
    }

    private static void WriteOptional(string builtInRoot, string name, string[] conflicts)
    {
        string dir = Path.Combine(builtInRoot, name);
        Directory.CreateDirectory(dir);
        var package = new PackageJson
        {
            Name = name,
            Version = "1.0.0",
            DisplayName = name,
            Category = "Official",
            InstallByDefault = false,
            Conflicts = [.. conflicts],
        };
        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(dir, "package.json"),
            System.Text.Json.JsonSerializer.Serialize(package, opts));
    }
}
