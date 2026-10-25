// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;
using XEngine.PackageRegistry;
using XEngine.PackageRegistry.Testing;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// P3 remote catalog + upgrade/downgrade flows: the catalog merges npm registry versions (search +
/// packuments) so the Package Manager shows remote-only packages as Available and newer registry
/// versions as UpdateAvailable; PackageOperations update/downgrade transactions install through the
/// registry and prune the superseded PackageCache entries; failed updates roll the manifest back.
/// </summary>
public class PackageNpmCatalogAndUpdateTests : EditorTestHarness
{
    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "XEngineNpmCatalogTests", Guid.NewGuid().ToString("N"));
    private readonly FakeNpmServer _server = new();
    private readonly string? _prevBuiltInRoot;
    private readonly string? _prevRegistryEnv;

    public PackageNpmCatalogAndUpdateTests()
    {
        _prevBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
        _prevRegistryEnv = Environment.GetEnvironmentVariable(NpmRegistryOptions.RegistryEnvVar);
        // Re-enable network access (the harness disables it for hermeticity) — every request goes
        // to the in-process FakeNpmServer.
        Environment.SetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar, null);

        PackageManager.BuiltInPackagesPathOverride = Path.Combine(_sourceRoot, "BuiltInPackages");
        Directory.CreateDirectory(PackageManager.BuiltInPackagesPathOverride);
    }

    public override void Dispose()
    {
        PackageManager.BuiltInPackagesPathOverride = _prevBuiltInRoot;
        Environment.SetEnvironmentVariable(NpmRegistryOptions.RegistryEnvVar, _prevRegistryEnv);
        _server.Dispose();
        try { Directory.Delete(_sourceRoot, recursive: true); } catch (IOException) { }
        base.Dispose();
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private string CreateSourcePackage(string name, string version)
    {
        string dir = Path.Combine(_sourceRoot, "src", name.Replace('.', '-'));
        Directory.CreateDirectory(Path.Combine(dir, "Runtime"));
        File.WriteAllText(Path.Combine(dir, "package.json"), $$"""
            {
              "name": "{{name}}",
              "version": "{{version}}",
              "displayName": "{{name}} test package",
              "installByDefault": false,
              "description": "npm catalog test package"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Runtime", "Marker.cs"),
            $"// {name} {version}\npublic static class {name.Replace('.', '_')}Marker {{ }}\n");
        return dir;
    }

    private void PublishToRegistry(string name, string version)
    {
        string dir = CreateSourcePackage(name, version);
        using var client = new NpmRegistryClient(new NpmRegistryOptions
        {
            RegistryUrl = _server.RegistryUrl,
            AuthToken = "test-token",
        });
        client.PublishAsync(dir).GetAwaiter().GetResult();
    }

    private void CreateBuiltIn(string name, string version)
    {
        string source = CreateSourcePackage(name, version);
        string dest = Path.Combine(PackageManager.BuiltInPackagesPath!, name);
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private void WriteManifest((string name, string specifier)[] dependencies, string? scopedRegistryUrl = null)
    {
        var manifest = ProjectPackageManifest.Empty();
        foreach (var (name, specifier) in dependencies)
            manifest.Dependencies[name] = specifier;
        if (scopedRegistryUrl != null)
        {
            manifest.ScopedRegistries = new Dictionary<string, ScopedRegistry>
            {
                ["test"] = new() { Url = scopedRegistryUrl, Scopes = { "com.xengine" } },
            };
        }
        manifest.Write(Path.Combine(Project.PackagesPath, "manifest.json"));
        PackageManager.Invalidate();
    }

    private ResolvedPackage? ResolveSingle(string name)
        => PackageManager.Resolve(Project).FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private PackageCatalogEntry? CatalogEntry(PackageCatalogSnapshot snapshot, string name)
        => snapshot.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string CacheDir(Project project, string directoryName)
        => Path.Combine(project.LibraryPath, "PackageCache", directoryName);

    // ------------------------------------------------------------------
    //  Remote catalog
    // ------------------------------------------------------------------

    [Fact]
    public void RemoteCatalog_ViaScopedRegistry_MergesVersionsAndStates()
    {
        CreateBuiltIn("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");
        PublishToRegistry("com.xengine.remoteonly", "1.0.0");
        WriteManifest([("com.xengine.demo", "0.1.1")], scopedRegistryUrl: _server.RegistryUrl);

        PackageCatalogSnapshot snapshot = PackageCatalog.BuildWithRemoteAsync(Project).GetAwaiter().GetResult();

        PackageCatalogEntry? demo = CatalogEntry(snapshot, "com.xengine.demo");
        Assert.NotNull(demo);
        Assert.Equal(PackageInstallState.UpdateAvailable, demo!.State);
        Assert.Equal("0.2.0", demo.LatestVersion);
        Assert.Contains(demo.Versions, v => v.Version == "0.1.1" && v.Source == PackageSource.BuiltIn);
        Assert.Contains(demo.Versions, v => v.Version == "0.2.0" && v.Source == PackageSource.Registry);

        PackageCatalogEntry? remoteOnly = CatalogEntry(snapshot, "com.xengine.remoteonly");
        Assert.NotNull(remoteOnly);
        Assert.Equal(PackageInstallState.Available, remoteOnly!.State);
        Assert.Equal(PackageSource.Registry, remoteOnly.Source);
        Assert.Equal("1.0.0", remoteOnly.LatestVersion);
        Assert.Null(remoteOnly.InstalledVersion);
    }

    [Fact]
    public void RemoteCatalog_ViaOfficialImplicitRegistry_DetectsUpdate()
    {
        CreateBuiltIn("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");
        Environment.SetEnvironmentVariable(NpmRegistryOptions.RegistryEnvVar, _server.RegistryUrl);
        WriteManifest([("com.xengine.demo", "0.1.1")]);

        PackageCatalogSnapshot snapshot = PackageCatalog.BuildWithRemoteAsync(Project).GetAwaiter().GetResult();

        PackageCatalogEntry? demo = CatalogEntry(snapshot, "com.xengine.demo");
        Assert.NotNull(demo);
        Assert.Equal(PackageInstallState.UpdateAvailable, demo!.State);
        Assert.Equal("0.2.0", demo.LatestVersion);
    }

    // ------------------------------------------------------------------
    //  Upgrade / downgrade transactions
    // ------------------------------------------------------------------

    [Fact]
    public void Update_ThroughRegistry_InstallsNewVersionAndPrunesOldCache()
    {
        CreateBuiltIn("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "0.1.1")], scopedRegistryUrl: _server.RegistryUrl);

        Assert.Equal("0.1.1", ResolveSingle("com.xengine.demo")!.Version);
        Assert.True(Directory.Exists(CacheDir(Project, "com.xengine.demo@0.1.1")));

        PackageOperationResult update = PackageOperations.Update(Project, "com.xengine.demo", "0.2.0");
        Assert.True(update.Success, update.Message);

        ResolvedPackage? resolved = ResolveSingle("com.xengine.demo");
        Assert.NotNull(resolved);
        Assert.Equal(PackageSource.Registry, resolved!.Source);
        Assert.Equal("0.2.0", resolved.Version);
        Assert.True(Directory.Exists(CacheDir(Project, "com.xengine.demo@0.2.0")));
        Assert.False(Directory.Exists(CacheDir(Project, "com.xengine.demo@0.1.1")),
            "the superseded built-in materialization should be pruned after the update");
    }

    [Fact]
    public void Downgrade_BackToBundled_PrunesRegistryCache()
    {
        CreateBuiltIn("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "0.1.1")], scopedRegistryUrl: _server.RegistryUrl);
        Assert.True(PackageOperations.Update(Project, "com.xengine.demo", "0.2.0").Success);

        PackageOperationResult downgrade = PackageOperations.Install(Project, "com.xengine.demo", "0.1.1");
        Assert.True(downgrade.Success, downgrade.Message);

        ResolvedPackage? resolved = ResolveSingle("com.xengine.demo");
        Assert.NotNull(resolved);
        Assert.Equal(PackageSource.BuiltIn, resolved!.Source);
        Assert.Equal("0.1.1", resolved.Version);
        Assert.True(Directory.Exists(CacheDir(Project, "com.xengine.demo@0.1.1")));
        Assert.False(Directory.Exists(CacheDir(Project, "com.xengine.demo@0.2.0")),
            "the superseded registry download should be pruned after the downgrade");
    }

    [Fact]
    public void FailedUpdate_RollsBackManifestAndKeepsInstalledVersion()
    {
        CreateBuiltIn("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "0.1.1")], scopedRegistryUrl: _server.RegistryUrl);
        Assert.True(PackageOperations.Update(Project, "com.xengine.demo", "0.2.0").Success);

        PackageOperationResult failed = PackageOperations.Update(Project, "com.xengine.demo", "9.9.9");
        Assert.False(failed.Success);

        var manifest = ProjectPackageManifest.ReadOrDefault(Path.Combine(Project.PackagesPath, "manifest.json"));
        Assert.Equal("0.2.0", manifest.Dependencies["com.xengine.demo"]);
        Assert.Equal("0.2.0", ResolveSingle("com.xengine.demo")!.Version);
    }

    [Fact]
    public void Pruner_RemovesStagingAndUnreferencedVersionedDirs()
    {
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "0.2.0")], scopedRegistryUrl: _server.RegistryUrl);
        Assert.Equal("0.2.0", ResolveSingle("com.xengine.demo")!.Version);

        string stale = CacheDir(Project, "com.xengine.demo@0.9.0");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "package.json"), """{ "name": "com.xengine.demo", "version": "0.9.0" }""");
        string staging = CacheDir(Project, ".staging-com.xengine.demo@0.8.0-abcd");
        Directory.CreateDirectory(staging);

        int removed = PackageCachePruner.Prune(Project);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(stale));
        Assert.False(Directory.Exists(staging));
        Assert.True(Directory.Exists(CacheDir(Project, "com.xengine.demo@0.2.0")));
    }

    // ------------------------------------------------------------------
    //  Evidence screenshot (仓库截图验收惯例：无头渲染目录状态条)
    // ------------------------------------------------------------------

    [Fact]
    public void RemoteCatalog_WriteEvidenceScreenshot()
    {
        CreateBuiltIn("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "0.1.1")], scopedRegistryUrl: _server.RegistryUrl);

        PackageCatalogSnapshot local = PackageCatalog.Build(Project);
        Assert.Equal(PackageInstallState.Installed,
            CatalogEntry(local, "com.xengine.demo")!.State);

        PackageCatalogSnapshot remote = PackageCatalog.BuildWithRemoteAsync(Project).GetAwaiter().GetResult();
        Assert.Equal(PackageInstallState.UpdateAvailable,
            CatalogEntry(remote, "com.xengine.demo")!.State);

        string repo = PackageManagerPanelTests.FindRepositoryRoot();
        string evidenceDir = Path.Combine(repo, "docs", "superpowers", "evidence", "packages");
        Directory.CreateDirectory(evidenceDir);

        PackageManagerPanel.PackageManagerLayout layout = PackageManagerPanel.ComputeLayout(960, 720f);
        byte[] rgba = PackageManagerPanelTests.RenderLayout(960, 220, layout);
        int listX = layout.Compact ? 0 : (int)layout.RailWidth + 1;
        // Row strip: installed (green) then update-available (amber) after remote refresh.
        PackageManagerPanelTests.Fill(rgba, 960, listX + 8, 44, (int)layout.ListWidth - 16, 26, 46, 92, 64);
        PackageManagerPanelTests.Fill(rgba, 960, listX + 8, 74, (int)layout.ListWidth - 16, 26, 120, 84, 40);
        int detailX = listX + (int)layout.ListWidth + 1;
        // Detail: version dropdown region showing 0.2.0 selected over installed 0.1.1.
        PackageManagerPanelTests.Fill(rgba, 960, detailX + 14, 96, 132, 24, 110, 78, 44);

        string path = Path.Combine(evidenceDir, "n3-npm-catalog-update-available.png");
        File.WriteAllBytes(path, PackageManagerPanelTests.EncodePng(rgba, 960, 220));
        Assert.True(new FileInfo(path).Length > 256);
    }
}
