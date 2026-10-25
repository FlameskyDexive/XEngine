// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;
using XEngine.PackageRegistry;
using XEngine.PackageRegistry.Testing;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// P3 registry integration: manifests resolve http(s) registries through the npm protocol
/// (FakeNpmServer stands in for registry.npmjs.org). Covers install-into-PackageCache, the
/// built-in-vs-registry precedence rule for bare version pins, range selection, offline cache
/// reuse, transitive registry dependencies, and lock recording.
/// </summary>
public class PackageManagerNpmRegistryTests : EditorTestHarness
{
    private readonly string _sourceRoot =
        Path.Combine(Path.GetTempPath(), "XEngineNpmRegistryTests", Guid.NewGuid().ToString("N"));
    private readonly FakeNpmServer _server = new();
    private readonly string? _prevBuiltInRoot;
    private readonly string? _prevRegistryEnv;
    private readonly string? _prevDisableEnv;

    public PackageManagerNpmRegistryTests()
    {
        _prevBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
        _prevRegistryEnv = Environment.GetEnvironmentVariable(NpmRegistryOptions.RegistryEnvVar);
        _prevDisableEnv = Environment.GetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar);
        Environment.SetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar, null);

        PackageManager.BuiltInPackagesPathOverride = Path.Combine(_sourceRoot, "BuiltInPackages");
        Directory.CreateDirectory(PackageManager.BuiltInPackagesPathOverride);
    }

    public override void Dispose()
    {
        PackageManager.BuiltInPackagesPathOverride = _prevBuiltInRoot;
        Environment.SetEnvironmentVariable(NpmRegistryOptions.RegistryEnvVar, _prevRegistryEnv);
        Environment.SetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar, _prevDisableEnv);
        _server.Dispose();
        try { Directory.Delete(_sourceRoot, recursive: true); } catch (IOException) { }
        base.Dispose();
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private string CreateSourcePackage(string name, string version, string? dependencyName = null)
    {
        string dir = Path.Combine(_sourceRoot, "src", name.Replace('.', '-'));
        Directory.CreateDirectory(Path.Combine(dir, "Runtime"));
        File.WriteAllText(Path.Combine(dir, "package.json"), $$"""
            {
              "name": "{{name}}",
              "version": "{{version}}",
              "displayName": "{{name}} test package",
              "installByDefault": false,
              {{(dependencyName is null ? "" : $"\"dependencies\": {{ \"{dependencyName}\": \"1.0.0\" }},")}}
              "description": "npm registry test package"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Runtime", "Marker.cs"),
            $"// {name} {version}\npublic static class {name.Replace('.', '_')}Marker {{ }}\n");
        return dir;
    }

    private void PublishToRegistry(string name, string version, string? dependencyName = null)
    {
        string dir = CreateSourcePackage(name, version, dependencyName);
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

    // ------------------------------------------------------------------
    //  Tests
    // ------------------------------------------------------------------

    [Fact]
    public void ScopedRegistryHttp_InstallsFromNpmIntoPackageCache()
    {
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "0.2.0")], scopedRegistryUrl: _server.RegistryUrl);

        ResolvedPackage? pkg = ResolveSingle("com.xengine.demo");
        Assert.NotNull(pkg);
        Assert.Equal(PackageSource.Registry, pkg!.Source);
        Assert.Equal("0.2.0", pkg.Version);
        Assert.Equal(_server.RegistryUrl, pkg.RegistryUrl);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(Project.LibraryPath, "PackageCache", "com.xengine.demo@0.2.0")),
            Path.GetFullPath(pkg.ResolvedRoot));
        Assert.True(File.Exists(Path.Combine(pkg.ResolvedRoot, "Runtime", "Marker.cs")));

        // The tarball's package.json carries @inxep/demo; the installed copy must be canonical
        // engine-named so mounts/dependencies behave exactly like a built-in materialization.
        var installed = PackageJson.Read(Path.Combine(pkg.ResolvedRoot, "package.json"));
        Assert.NotNull(installed);
        Assert.Equal("com.xengine.demo", installed!.Name);

        var lockFile = PackageLockFile.Read(Project);
        Assert.NotNull(lockFile);
        PackageLockEntry entry = Assert.Single(lockFile!.Packages, p => p.Name == "com.xengine.demo");
        Assert.Equal("registry", entry.Source, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_server.RegistryUrl, entry.RegistryUrl);
        Assert.Equal("0.2.0", entry.Version);
    }

    [Fact]
    public void ExplicitRegistrySpecifier_AcceptsHttpUrlForm()
    {
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "registry:" + _server.RegistryUrl.TrimEnd('/') + "#0.2.0")]);

        ResolvedPackage? pkg = ResolveSingle("com.xengine.demo");
        Assert.NotNull(pkg);
        Assert.Equal(PackageSource.Registry, pkg!.Source);
        Assert.Equal("0.2.0", pkg.Version);
        Assert.Equal(_server.RegistryUrl.TrimEnd('/'), pkg.RegistryUrl);
    }

    [Fact]
    public void BarePin_BundledVersionSatisfies_WinsOverRegistry()
    {
        CreateBuiltIn("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "0.1.1")], scopedRegistryUrl: _server.RegistryUrl);

        ResolvedPackage? pkg = ResolveSingle("com.xengine.demo");
        Assert.NotNull(pkg);
        Assert.Equal(PackageSource.BuiltIn, pkg!.Source);
        Assert.Equal("0.1.1", pkg.Version);
    }

    [Fact]
    public void BarePin_AboveBundled_FallsThroughToImplicitOfficialRegistry()
    {
        CreateBuiltIn("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");
        Environment.SetEnvironmentVariable(NpmRegistryOptions.RegistryEnvVar, _server.RegistryUrl);
        WriteManifest([("com.xengine.demo", "0.2.0")]);

        ResolvedPackage? pkg = ResolveSingle("com.xengine.demo");
        Assert.NotNull(pkg);
        Assert.Equal(PackageSource.Registry, pkg!.Source);
        Assert.Equal("0.2.0", pkg.Version);
        Assert.Equal(_server.RegistryUrl, pkg.RegistryUrl);
    }

    [Fact]
    public void RangePin_PicksHighestSatisfyingRegistryVersion()
    {
        PublishToRegistry("com.xengine.demo", "0.1.1");
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "0.1.x")], scopedRegistryUrl: _server.RegistryUrl);

        ResolvedPackage? pkg = ResolveSingle("com.xengine.demo");
        Assert.NotNull(pkg);
        Assert.Equal("0.1.1", pkg!.Version);
    }

    [Fact]
    public void Offline_ReusesPackageCacheWithoutNetwork()
    {
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "0.2.0")], scopedRegistryUrl: _server.RegistryUrl);

        ResolvedPackage? first = ResolveSingle("com.xengine.demo");
        Assert.NotNull(first);

        // Simulate the registry going away entirely — the cached download must satisfy the pin.
        _server.Dispose();
        PackageManager.Invalidate();
        ResolvedPackage? second = ResolveSingle("com.xengine.demo");
        Assert.NotNull(second);
        Assert.Equal(PackageSource.Registry, second!.Source);
        Assert.Equal("0.2.0", second.Version);
        Assert.Equal(first!.ResolvedRoot, second.ResolvedRoot);
    }

    [Fact]
    public void TransitiveDependency_PulledFromRegistryWhenMissingLocally()
    {
        PublishToRegistry("com.xengine.child", "1.0.0");
        PublishToRegistry("com.xengine.parent", "1.0.0", dependencyName: "com.xengine.child");
        WriteManifest([("com.xengine.parent", "1.0.0")], scopedRegistryUrl: _server.RegistryUrl);

        var packages = PackageManager.Resolve(Project);
        ResolvedPackage? parent = packages.FirstOrDefault(p => p.Name == "com.xengine.parent");
        ResolvedPackage? child = packages.FirstOrDefault(p => p.Name == "com.xengine.child");
        Assert.NotNull(parent);
        Assert.NotNull(child);
        Assert.Equal(PackageSource.Registry, child!.Source);
        Assert.Equal("1.0.0", child.Version);
        Assert.True(child.ResolvedRoot.Contains("com.xengine.child@1.0.0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("com.xengine.child", parent!.DependencySpecifiers.Keys);
    }

    [Fact]
    public void MissingVersionOnRegistry_PackageSkipped()
    {
        PublishToRegistry("com.xengine.demo", "0.2.0");
        WriteManifest([("com.xengine.demo", "9.9.9")], scopedRegistryUrl: _server.RegistryUrl);

        Assert.Null(ResolveSingle("com.xengine.demo"));
    }
}
