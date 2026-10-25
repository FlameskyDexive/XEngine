// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Packages.EditorModules;
using XEngine.PackageRegistry;
using XEngine.PackageRegistry.Testing;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Editor-level module store: npm install/update/uninstall into the per-version user directory
/// (FakeNpmServer stands in for registry.npmjs.org). Covers cache+record layout, wire-name
/// rewriting, offline reuse, failure cleanliness (no staging debris), single-version pinning, and
/// the user-level npm token fallback.
/// </summary>
public class EditorModuleStoreTests : EditorTestHarness
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "XEngineEditorModuleTests", Guid.NewGuid().ToString("N"));
    private readonly FakeNpmServer _server = new();
    private readonly string? _prevDisableEnv;
    private readonly string? _prevTokenEnv;
    private readonly string? _prevStoreRoot;

    public EditorModuleStoreTests()
    {
        // The harness disables npm for hermeticity; these tests re-enable it against the fake.
        _prevDisableEnv = Environment.GetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar);
        _prevTokenEnv = Environment.GetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar);
        Environment.SetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar, null);
        Environment.SetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar, null);

        _prevStoreRoot = EditorModuleStore.RootOverride;
        EditorModuleStore.RootOverride = Path.Combine(_root, "store");
    }

    public override void Dispose()
    {
        EditorModuleStore.RootOverride = _prevStoreRoot;
        Environment.SetEnvironmentVariable(NpmRegistryOptions.DisableEnvVar, _prevDisableEnv);
        Environment.SetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar, _prevTokenEnv);
        _server.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        base.Dispose();
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private string CreateSourcePackage(string name, string version, bool withToolchain = false)
    {
        string dir = Path.Combine(_root, "src", name.Replace('.', '-'));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), $$"""
            {
              "name": "{{name}}",
              "version": "{{version}}",
              "displayName": "{{name}} test module",
              "installByDefault": false,
              "description": "editor module store test package"
            }
            """);
        if (withToolchain)
            File.WriteAllText(Path.Combine(dir, "toolchain.json"),
                """{ "manifestVersion": 1, "platform": "android", "components": [] }""");
        return dir;
    }

    private void PublishToRegistry(string name, string version, bool withToolchain = false)
    {
        string dir = CreateSourcePackage(name, version, withToolchain);
        using var client = new NpmRegistryClient(new NpmRegistryOptions
        {
            RegistryUrl = _server.RegistryUrl,
            AuthToken = "test-token",
        });
        client.PublishAsync(dir).GetAwaiter().GetResult();
    }

    private string CacheDir(string name, string version)
        => Path.Combine(EditorModuleStore.CacheRoot, $"{name}@{version}");

    // ------------------------------------------------------------------
    //  Install / record
    // ------------------------------------------------------------------

    [Fact]
    public void Install_ExtractsModuleIntoStoreAndRecordsIt()
    {
        PublishToRegistry("com.xengine.platform.test", "0.1.0", withToolchain: true);

        EditorModuleRecord record = EditorModuleInstaller.InstallAsync(
            "com.xengine.platform.test", registryUrl: _server.RegistryUrl).GetAwaiter().GetResult();

        Assert.Equal("0.1.0", record.Version);
        Assert.Equal("npm", record.Source);
        Assert.True(EditorModuleStore.IsInstalled("com.xengine.platform.test"));

        string root = CacheDir("com.xengine.platform.test", "0.1.0");
        Assert.True(Directory.Exists(root));
        // The tarball's package.json carries @inxep/platform.test; the stored copy must be canonical.
        var manifest = PackageJson.Read(Path.Combine(root, "package.json"));
        Assert.NotNull(manifest);
        Assert.Equal("com.xengine.platform.test", manifest!.Name);
        Assert.True(File.Exists(Path.Combine(root, "toolchain.json")));

        Assert.Contains(EditorModuleStore.ListInstalled(),
            m => m.Name == "com.xengine.platform.test" && m.Version == "0.1.0");
    }

    [Fact]
    public void Install_ReusesCacheWithoutNetwork()
    {
        PublishToRegistry("com.xengine.platform.test", "0.1.0");
        EditorModuleRecord first = EditorModuleInstaller.InstallAsync(
            "com.xengine.platform.test", registryUrl: _server.RegistryUrl).GetAwaiter().GetResult();

        // Registry gone entirely — the cached copy must satisfy a re-install.
        _server.Dispose();
        EditorModuleRecord second = EditorModuleInstaller.InstallAsync(
            "com.xengine.platform.test", registryUrl: _server.RegistryUrl).GetAwaiter().GetResult();

        Assert.Equal(first.Version, second.Version);
        Assert.True(EditorModuleStore.TryGetModuleRoot("com.xengine.platform.test",
            out _, out string root));
        Assert.Equal(Path.GetFullPath(CacheDir("com.xengine.platform.test", "0.1.0")), Path.GetFullPath(root));
    }

    [Fact]
    public void Install_UnknownModule_ThrowsAndLeavesNoDebris()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            EditorModuleInstaller.InstallAsync("com.xengine.platform.ghost",
                registryUrl: _server.RegistryUrl).GetAwaiter().GetResult());
        Assert.Contains("not found", ex.Message);

        Assert.Empty(EditorModuleStore.ListInstalled());
        Assert.False(Directory.Exists(EditorModuleStore.CacheRoot)
            && Directory.GetDirectories(EditorModuleStore.CacheRoot).Length > 0);
    }

    [Fact]
    public void Install_NetworkFailure_ThrowsWithoutStagingDebris()
    {
        PublishToRegistry("com.xengine.platform.test", "0.1.0");
        _server.Dispose();

        Assert.Throws<InvalidOperationException>(() =>
            EditorModuleInstaller.InstallAsync("com.xengine.platform.test",
                registryUrl: _server.RegistryUrl).GetAwaiter().GetResult());

        Assert.Empty(EditorModuleStore.ListInstalled());
        if (Directory.Exists(EditorModuleStore.CacheRoot))
            Assert.DoesNotContain(Directory.GetDirectories(EditorModuleStore.CacheRoot),
                d => Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal));
    }

    [Fact]
    public void Install_UpdateRange_ReplacesLowerVersion()
    {
        PublishToRegistry("com.xengine.platform.test", "0.1.0");
        EditorModuleInstaller.InstallAsync("com.xengine.platform.test",
            registryUrl: _server.RegistryUrl).GetAwaiter().GetResult();

        PublishToRegistry("com.xengine.platform.test", "0.2.0");
        // The settings UI's update button excludes the installed version, so the install goes back
        // to the registry instead of short-circuiting on the recorded copy.
        EditorModuleRecord updated = EditorModuleInstaller.InstallAsync("com.xengine.platform.test", ">0.1.0",
            registryUrl: _server.RegistryUrl).GetAwaiter().GetResult();

        Assert.Equal("0.2.0", updated.Version);
        Assert.True(Directory.Exists(CacheDir("com.xengine.platform.test", "0.2.0")));
        // The store pins one version per module; the old copy is gone.
        Assert.False(Directory.Exists(CacheDir("com.xengine.platform.test", "0.1.0")));
        Assert.Single(EditorModuleStore.ListInstalled(),
            m => m.Name == "com.xengine.platform.test");
    }

    // ------------------------------------------------------------------
    //  Uninstall / query
    // ------------------------------------------------------------------

    [Fact]
    public void Uninstall_RemovesCacheAndRecord()
    {
        PublishToRegistry("com.xengine.platform.test", "0.1.0");
        EditorModuleInstaller.InstallAsync("com.xengine.platform.test",
            registryUrl: _server.RegistryUrl).GetAwaiter().GetResult();

        Assert.True(EditorModuleInstaller.UninstallAsync("com.xengine.platform.test").GetAwaiter().GetResult());
        Assert.False(EditorModuleStore.IsInstalled("com.xengine.platform.test"));
        Assert.False(Directory.Exists(CacheDir("com.xengine.platform.test", "0.1.0")));
        Assert.False(EditorModuleInstaller.UninstallAsync("com.xengine.platform.test").GetAwaiter().GetResult());
    }

    [Fact]
    public void QueryLatestVersion_ReturnsHighestPublished()
    {
        PublishToRegistry("com.xengine.platform.test", "0.1.0");
        PublishToRegistry("com.xengine.platform.test", "0.3.0");
        PublishToRegistry("com.xengine.platform.test", "0.2.0");

        string? latest = EditorModuleInstaller.QueryLatestVersionAsync(
            "com.xengine.platform.test", _server.RegistryUrl).GetAwaiter().GetResult();
        Assert.Equal("0.3.0", latest);

        Assert.Null(EditorModuleInstaller.QueryLatestVersionAsync(
            "com.xengine.platform.ghost", _server.RegistryUrl).GetAwaiter().GetResult());
    }

    [Fact]
    public void DefaultRoot_IsVersionScopedAndSanitized()
    {
        string root = EditorModuleStore.GetDefaultRoot("1.2.3+deadbeef");
        Assert.EndsWith(Path.Combine("XEngine", "EditorModules", "1.2.3"), root);

        // Filesystem-hostile characters become dashes so CI version strings stay one folder.
        Assert.EndsWith(Path.Combine("EditorModules", "1.0-0"), EditorModuleStore.GetDefaultRoot("1.0|0"));
    }

    [Fact]
    public void KnownPlatformModules_CoverAndroidHarmonyOsAndIos()
    {
        Assert.Equal(3, EditorModuleCatalog.KnownPlatformModules.Count);
        Assert.NotNull(EditorModuleCatalog.FindKnown("com.xengine.platform.android"));
        Assert.NotNull(EditorModuleCatalog.FindKnown("com.xengine.platform.harmonyos"));
        Assert.NotNull(EditorModuleCatalog.FindKnown("com.xengine.platform.ios"));
        Assert.All(EditorModuleCatalog.KnownPlatformModules, m => Assert.False(string.IsNullOrEmpty(m.PlatformId)));

        PublishToRegistry("com.xengine.platform.test", "0.2.0");
        var latest = EditorModuleCatalog.QueryLatestVersionsAsync(
            ["com.xengine.platform.test", "com.xengine.platform.ghost"],
            _server.RegistryUrl).GetAwaiter().GetResult();
        Assert.Equal("0.2.0", latest["com.xengine.platform.test"]);
        Assert.False(latest.ContainsKey("com.xengine.platform.ghost"));
    }
}

/// <summary>Precedence of the three npm token sources: env &gt; project secrets &gt; user secrets.</summary>
public class NpmRegistryUserSecretsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "XEngineNpmUserSecretsTests", Guid.NewGuid().ToString("N"));
    private readonly string? _prevTokenEnv;
    private readonly string? _prevUserOverride;

    public NpmRegistryUserSecretsTests()
    {
        _prevTokenEnv = Environment.GetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar);
        _prevUserOverride = NpmRegistryOptions.UserSecretsPathOverride;
        Environment.SetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar, null);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar, _prevTokenEnv);
        NpmRegistryOptions.UserSecretsPathOverride = _prevUserOverride;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void WriteSecrets(string relativePath, string token)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{ "npmToken": "{{token}}" }""");
    }

    [Fact]
    public void Load_NoProjectRoot_FallsBackToUserSecrets()
    {
        WriteSecrets(Path.Combine("user", ".xengine", "secrets.json"), "user-token");
        NpmRegistryOptions.UserSecretsPathOverride =
            Path.Combine(_root, "user", ".xengine", "secrets.json");

        Assert.Equal("user-token", NpmRegistryOptions.Load(projectRoot: null).AuthToken);
    }

    [Fact]
    public void Load_ProjectSecrets_BeatUserSecrets()
    {
        WriteSecrets(Path.Combine("user", ".xengine", "secrets.json"), "user-token");
        WriteSecrets(Path.Combine("project", ".xengine", "secrets.json"), "project-token");
        NpmRegistryOptions.UserSecretsPathOverride =
            Path.Combine(_root, "user", ".xengine", "secrets.json");

        Assert.Equal("project-token",
            NpmRegistryOptions.Load(projectRoot: Path.Combine(_root, "project")).AuthToken);
    }

    [Fact]
    public void Load_EnvVar_BeatsAllFiles()
    {
        WriteSecrets(Path.Combine("user", ".xengine", "secrets.json"), "user-token");
        NpmRegistryOptions.UserSecretsPathOverride =
            Path.Combine(_root, "user", ".xengine", "secrets.json");
        Environment.SetEnvironmentVariable(NpmRegistryOptions.TokenEnvVar, "env-token");

        Assert.Equal("env-token", NpmRegistryOptions.Load(projectRoot: Path.Combine(_root, "project")).AuthToken);
    }
}
