// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Official optional packages ship with the Editor, stay unmounted until Install, then copy into
/// Library/PackageCache and are linked from Packages/manifest.json.
/// </summary>
public class OptionalBuiltInPackageTests : EditorTestHarness
{
    public OptionalBuiltInPackageTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
        PackageManager.BuiltInPackagesPathOverride = Path.Combine(Project.TempPath, "NoBuiltInPackages");
        PackageManager.Invalidate();
    }

    [Fact]
    public void PackageJson_MissingInstallByDefault_IsInstalledByDefault()
    {
        string path = Path.Combine(Project.TempPath, "default.json");
        File.WriteAllText(path, """{"name":"com.test.default","version":"1.0.0"}""");
        PackageJson? pj = PackageJson.Read(path);
        Assert.NotNull(pj);
        Assert.True(pj!.IsInstalledByDefault);
        Assert.Null(pj.InstallByDefault);
    }

    [Fact]
    public void PackageJson_InstallByDefaultFalse_IsOptional()
    {
        string path = Path.Combine(Project.TempPath, "optional.json");
        File.WriteAllText(path, """{"name":"com.test.opt","version":"1.0.0","installByDefault":false}""");
        PackageJson? pj = PackageJson.Read(path);
        Assert.NotNull(pj);
        Assert.False(pj!.IsInstalledByDefault);
        Assert.False(pj.InstallByDefault);
    }

    [Fact]
    public void OptionalBuiltIn_IsNotAutoMounted()
    {
        string builtInRoot = WriteOfficialPackages(("com.test.optional", "1.0.0", false),
            ("com.test.always", "2.0.0", true));
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = builtInRoot;
            PackageManager.Invalidate();
            IReadOnlyList<ResolvedPackage> resolved = PackageManager.Resolve(Project);

            Assert.DoesNotContain(resolved, p => p.Name == "com.test.optional");
            ResolvedPackage always = Assert.Single(resolved, p => p.Name == "com.test.always");
            Assert.Equal(PackageSource.BuiltIn, always.Source);
            Assert.Equal(Path.GetFullPath(Path.Combine(builtInRoot, "com.test.always")), always.ResolvedRoot);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
            TryDeleteDir(builtInRoot);
        }
    }

    [Fact]
    public void Catalog_ListsUninstalledOptionalAsAvailable()
    {
        string builtInRoot = WriteOfficialPackages(("com.test.optional", "1.2.0", false));
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = builtInRoot;
            PackageManager.Invalidate();
            PackageCatalogSnapshot snapshot = PackageCatalog.Build(Project);

            PackageCatalogEntry entry = Assert.Single(snapshot.Entries, e => e.Name == "com.test.optional");
            Assert.Equal(PackageInstallState.Available, entry.State);
            Assert.Null(entry.InstalledVersion);
            Assert.Equal("1.2.0", entry.LatestVersion);
            Assert.Equal(PackageSource.BuiltIn, entry.Source);
            Assert.Equal("Official", entry.Category);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
            TryDeleteDir(builtInRoot);
        }
    }

    [Fact]
    public void Install_CopiesOptionalPackageIntoPackageCacheAndManifest()
    {
        string builtInRoot = WriteOfficialPackages(("com.test.optional", "1.0.0", false));
        File.WriteAllText(Path.Combine(builtInRoot, "com.test.optional", "marker.txt"), "optional-source");
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = builtInRoot;
            PackageManager.Invalidate();

            PackageOperationResult result = PackageOperations.Install(Project, "com.test.optional", "1.0.0");
            Assert.True(result.Success, result.Message);

            string cacheRoot = Path.Combine(Project.LibraryPath, "PackageCache", "com.test.optional@1.0.0");
            Assert.True(Directory.Exists(cacheRoot));
            Assert.True(File.Exists(Path.Combine(cacheRoot, "package.json")));
            Assert.Equal("optional-source", File.ReadAllText(Path.Combine(cacheRoot, "marker.txt")));

            ResolvedPackage pkg = Assert.Single(PackageManager.Resolve(Project), p => p.Name == "com.test.optional");
            Assert.Equal(PackageSource.BuiltIn, pkg.Source);
            Assert.True(pkg.Mount.ReadOnly);
            Assert.Equal(Path.GetFullPath(cacheRoot), pkg.ResolvedRoot);
            Assert.Equal("Packages/com.test.optional", pkg.LogicalRoot);

            ProjectPackageManifest manifest = ProjectPackageManifest.ReadOrDefault(
                Path.Combine(Project.PackagesPath, "manifest.json"));
            Assert.Equal("1.0.0", manifest.Dependencies["com.test.optional"]);

            PackageLockFile? lockFile = PackageLockFile.Read(Project);
            Assert.NotNull(lockFile);
            PackageLockEntry lockEntry = Assert.Single(lockFile!.Packages, e => e.Name == "com.test.optional");
            Assert.Equal("builtin", lockEntry.Source);
            Assert.Equal("Library/PackageCache/com.test.optional@1.0.0", lockEntry.ResolvedPath);

            PackageCatalogEntry catalog = Assert.Single(PackageCatalog.Build(Project).Entries,
                e => e.Name == "com.test.optional");
            Assert.Equal(PackageInstallState.Installed, catalog.State);
            Assert.True(catalog.IsDirectDependency);
            Assert.Equal("1.0.0", catalog.InstalledVersion);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
            TryDeleteDir(builtInRoot);
        }
    }

    [Fact]
    public void Remove_UnmountsOptionalPackageWithoutDeletingEditorCopy()
    {
        string builtInRoot = WriteOfficialPackages(("com.test.optional", "1.0.0", false));
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = builtInRoot;
            PackageManager.Invalidate();
            Assert.True(PackageOperations.Install(Project, "com.test.optional", "1.0.0").Success);
            Assert.True(PackageOperations.Remove(Project, "com.test.optional").Success);

            Assert.DoesNotContain(PackageManager.Resolve(Project), p => p.Name == "com.test.optional");
            Assert.True(File.Exists(Path.Combine(builtInRoot, "com.test.optional", "package.json")));

            PackageCatalogEntry entry = Assert.Single(PackageCatalog.Build(Project).Entries,
                e => e.Name == "com.test.optional");
            Assert.Equal(PackageInstallState.Available, entry.State);
            Assert.Null(entry.InstalledVersion);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
            TryDeleteDir(builtInRoot);
        }
    }

    [Fact]
    public void Update_RecopiesNewerEditorShippedVersion()
    {
        string builtInRoot = WriteOfficialPackages(("com.test.optional", "1.0.0", false));
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = builtInRoot;
            PackageManager.Invalidate();
            Assert.True(PackageOperations.Install(Project, "com.test.optional", "1.0.0").Success);

            WritePackageJson(Path.Combine(builtInRoot, "com.test.optional"), "com.test.optional", "2.0.0",
                installByDefault: false);
            File.WriteAllText(Path.Combine(builtInRoot, "com.test.optional", "newer.txt"), "v2");
            PackageManager.Invalidate();

            PackageCatalogEntry before = Assert.Single(PackageCatalog.Build(Project).Entries,
                e => e.Name == "com.test.optional");
            var versions = new System.Text.StringBuilder();
            for (int i = 0; i < before.Versions.Count; i++)
            {
                if (i > 0) versions.Append(',');
                versions.Append(before.Versions[i].Version).Append('/').Append(before.Versions[i].Source);
            }
            Assert.Equal("1.0.0", before.InstalledVersion);
            Assert.Equal("2.0.0", before.LatestVersion);
            Assert.True(before.State == PackageInstallState.UpdateAvailable,
                $"state={before.State} installed={before.InstalledVersion} latest={before.LatestVersion} versions={versions}");

            Assert.True(PackageOperations.Update(Project, "com.test.optional", "2.0.0").Success);

            string cacheV2 = Path.Combine(Project.LibraryPath, "PackageCache", "com.test.optional@2.0.0");
            Assert.True(File.Exists(Path.Combine(cacheV2, "newer.txt")));
            ResolvedPackage pkg = Assert.Single(PackageManager.Resolve(Project), p => p.Name == "com.test.optional");
            Assert.Equal("2.0.0", pkg.Version);
            Assert.Equal(Path.GetFullPath(cacheV2), pkg.ResolvedRoot);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
            TryDeleteDir(builtInRoot);
        }
    }

    [Fact]
    public void RepoShippedVfxPackage_IsOptionalAndInstallsIntoCache()
    {
        string repo = FindRepositoryRoot();
        string source = Path.Combine(repo, "Packages", "com.xengine.vfx");
        Assert.True(File.Exists(Path.Combine(source, "package.json")));
        PackageJson? metadata = PackageJson.Read(Path.Combine(source, "package.json"));
        Assert.NotNull(metadata);
        Assert.False(metadata!.IsInstalledByDefault);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();
            Assert.DoesNotContain(PackageManager.Resolve(Project), p => p.Name == "com.xengine.vfx");

            PackageCatalogEntry available = Assert.Single(PackageCatalog.Build(Project).Entries,
                e => e.Name == "com.xengine.vfx");
            Assert.Equal(PackageInstallState.Available, available.State);

            Assert.True(PackageOperations.Install(Project, "com.xengine.vfx", metadata.Version).Success);
            ResolvedPackage pkg = Assert.Single(PackageManager.Resolve(Project), p => p.Name == "com.xengine.vfx");
            Assert.Equal(PackageSource.BuiltIn, pkg.Source);
            Assert.Contains("PackageCache", pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.EndsWith("com.xengine.vfx@" + metadata.Version,
                pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void RepoShippedOptionalTwoDPackages_AreNotAutoMountedAndInstallIntoCache()
    {
        string repo = FindRepositoryRoot();
        string[] optional =
        {
            "com.xengine.feature.2d",
            "com.xengine.2d.animation",
            "com.xengine.2d.psdimporter",
            "com.xengine.2d.aseprite",
            "com.xengine.2d.tilemap",
            "com.xengine.2d.tilemap.extras",
            "com.xengine.2d.spriteshape",
            "com.xengine.2d.pixel-perfect",
            "com.xengine.2d.tooling",
        };

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();
            IReadOnlyList<ResolvedPackage> resolved = PackageManager.Resolve(Project);
            PackageCatalogSnapshot catalog = PackageCatalog.Build(Project);

            Assert.Contains(resolved, p => p.Name == "com.xengine.2d.common");
            Assert.Contains(resolved, p => p.Name == "com.xengine.2d.sprite");

            foreach (string name in optional)
            {
                PackageJson? metadata = PackageJson.Read(Path.Combine(repo, "Packages", name, "package.json"));
                Assert.NotNull(metadata);
                Assert.False(metadata!.IsInstalledByDefault);
                Assert.DoesNotContain(resolved, p => p.Name == name);

                PackageCatalogEntry available = Assert.Single(catalog.Entries, e => e.Name == name);
                Assert.Equal(PackageInstallState.Available, available.State);
            }

            PackageJson animation = Assert.IsType<PackageJson>(PackageJson.Read(
                Path.Combine(repo, "Packages", "com.xengine.2d.animation", "package.json")));
            Assert.True(PackageOperations.Install(Project, "com.xengine.2d.animation", animation.Version).Success);
            ResolvedPackage pkg = Assert.Single(PackageManager.Resolve(Project),
                p => p.Name == "com.xengine.2d.animation");
            Assert.Equal(PackageSource.BuiltIn, pkg.Source);
            Assert.Contains("PackageCache", pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.EndsWith("com.xengine.2d.animation@" + animation.Version,
                pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);

            PackageJson feature = Assert.IsType<PackageJson>(PackageJson.Read(
                Path.Combine(repo, "Packages", "com.xengine.feature.2d", "package.json")));
            Assert.True(PackageOperations.Install(Project, "com.xengine.feature.2d", feature.Version).Success);
            IReadOnlyList<ResolvedPackage> afterFeature = PackageManager.Resolve(Project);
            Assert.Contains(afterFeature, p => p.Name == "com.xengine.feature.2d");
            Assert.Contains(afterFeature, p => p.Name == "com.xengine.2d.tilemap");
            Assert.Contains(afterFeature, p => p.Name == "com.xengine.2d.psdimporter");
            Assert.Contains(afterFeature, p => p.Name == "com.xengine.2d.aseprite");
            ResolvedPackage tilemap = Assert.Single(afterFeature, p => p.Name == "com.xengine.2d.tilemap");
            Assert.Contains("PackageCache", tilemap.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void RepoShippedTerrainPackage_IsOptionalAndInstallsIntoCache()
    {
        string repo = FindRepositoryRoot();
        string source = Path.Combine(repo, "Packages", "com.xengine.terrain");
        Assert.True(File.Exists(Path.Combine(source, "package.json")));
        PackageJson? metadata = PackageJson.Read(Path.Combine(source, "package.json"));
        Assert.NotNull(metadata);
        Assert.False(metadata!.IsInstalledByDefault);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();
            Assert.DoesNotContain(PackageManager.Resolve(Project), p => p.Name == "com.xengine.terrain");

            PackageCatalogEntry available = Assert.Single(PackageCatalog.Build(Project).Entries,
                e => e.Name == "com.xengine.terrain");
            Assert.Equal(PackageInstallState.Available, available.State);

            Assert.True(PackageOperations.Install(Project, "com.xengine.terrain", metadata.Version).Success);
            ResolvedPackage pkg = Assert.Single(PackageManager.Resolve(Project), p => p.Name == "com.xengine.terrain");
            Assert.Equal(PackageSource.BuiltIn, pkg.Source);
            Assert.Contains("PackageCache", pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.EndsWith("com.xengine.terrain@" + metadata.Version,
                pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(pkg.ResolvedRoot, "Runtime", "XEngine.Terrain.Runtime.asmdef")));
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void RepoShippedSpinePackage_IsOptionalAndInstallsIntoCache()
    {
        string repo = FindRepositoryRoot();
        string source = Path.Combine(repo, "Packages", "com.xengine.spine");
        Assert.True(File.Exists(Path.Combine(source, "package.json")));
        PackageJson? metadata = PackageJson.Read(Path.Combine(source, "package.json"));
        Assert.NotNull(metadata);
        Assert.False(metadata!.IsInstalledByDefault);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();
            Assert.DoesNotContain(PackageManager.Resolve(Project), p => p.Name == "com.xengine.spine");

            PackageCatalogEntry available = Assert.Single(PackageCatalog.Build(Project).Entries,
                e => e.Name == "com.xengine.spine");
            Assert.Equal(PackageInstallState.Available, available.State);

            Assert.True(PackageOperations.Install(Project, "com.xengine.spine", metadata.Version).Success);
            ResolvedPackage pkg = Assert.Single(PackageManager.Resolve(Project), p => p.Name == "com.xengine.spine");
            Assert.Equal(PackageSource.BuiltIn, pkg.Source);
            Assert.Contains("PackageCache", pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.EndsWith("com.xengine.spine@" + metadata.Version,
                pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(pkg.ResolvedRoot, "Runtime", "XEngine.Spine.Runtime.asmdef")));
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void RepoShippedNavigationPackage_IsOptionalAndInstallsIntoCache()
    {
        string repo = FindRepositoryRoot();
        string source = Path.Combine(repo, "Packages", "com.xengine.ai.navigation");
        Assert.True(File.Exists(Path.Combine(source, "package.json")));
        PackageJson? metadata = PackageJson.Read(Path.Combine(source, "package.json"));
        Assert.NotNull(metadata);
        Assert.False(metadata!.IsInstalledByDefault);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();
            Assert.DoesNotContain(PackageManager.Resolve(Project), p => p.Name == "com.xengine.ai.navigation");

            PackageCatalogEntry available = Assert.Single(PackageCatalog.Build(Project).Entries,
                e => e.Name == "com.xengine.ai.navigation");
            Assert.Equal(PackageInstallState.Available, available.State);

            Assert.True(PackageOperations.Install(Project, "com.xengine.ai.navigation", metadata.Version).Success);
            ResolvedPackage pkg = Assert.Single(PackageManager.Resolve(Project), p => p.Name == "com.xengine.ai.navigation");
            Assert.Equal(PackageSource.BuiltIn, pkg.Source);
            Assert.Contains("PackageCache", pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.EndsWith("com.xengine.ai.navigation@" + metadata.Version,
                pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(pkg.ResolvedRoot, "Runtime", "XEngine.AI.Navigation.Runtime.asmdef")));
            Assert.True(File.Exists(Path.Combine(pkg.ResolvedRoot, "Runtime", "NavMeshBuilder.cs")));
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void RepoShippedPostProcessingPackage_IsOptionalAndInstallsIntoCache()
    {
        string repo = FindRepositoryRoot();
        string source = Path.Combine(repo, "Packages", "com.xengine.postprocessing");
        Assert.True(File.Exists(Path.Combine(source, "package.json")));
        PackageJson? metadata = PackageJson.Read(Path.Combine(source, "package.json"));
        Assert.NotNull(metadata);
        Assert.False(metadata!.IsInstalledByDefault);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();
            Assert.DoesNotContain(PackageManager.Resolve(Project), p => p.Name == "com.xengine.postprocessing");

            PackageCatalogEntry available = Assert.Single(PackageCatalog.Build(Project).Entries,
                e => e.Name == "com.xengine.postprocessing");
            Assert.Equal(PackageInstallState.Available, available.State);

            Assert.True(PackageOperations.Install(Project, "com.xengine.postprocessing", metadata.Version).Success);
            ResolvedPackage pkg = Assert.Single(PackageManager.Resolve(Project), p => p.Name == "com.xengine.postprocessing");
            Assert.Equal(PackageSource.BuiltIn, pkg.Source);
            Assert.Contains("PackageCache", pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.EndsWith("com.xengine.postprocessing@" + metadata.Version,
                pkg.ResolvedRoot.Replace('\\', '/'), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(pkg.ResolvedRoot, "Runtime", "XEngine.PostProcessing.Runtime.asmdef")));
            Assert.True(File.Exists(Path.Combine(pkg.ResolvedRoot, "Runtime", "PostProcessLayer.cs")));
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    private string WriteOfficialPackages(params (string name, string version, bool installByDefault)[] packages)
    {
        string root = Path.Combine(Path.GetDirectoryName(Project.RootPath)!, "official-packages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (int i = 0; i < packages.Length; i++)
        {
            (string name, string version, bool installByDefault) = packages[i];
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            WritePackageJson(dir, name, version, installByDefault);
        }
        return root;
    }

    private static void WritePackageJson(string root, string packageName, string version, bool installByDefault)
    {
        var pj = new PackageJson
        {
            Name = packageName,
            Version = version,
            DisplayName = packageName,
            Category = installByDefault ? null : "Official",
            InstallByDefault = installByDefault ? null : false,
        };
        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(root, "package.json"),
            System.Text.Json.JsonSerializer.Serialize(pj, opts));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
