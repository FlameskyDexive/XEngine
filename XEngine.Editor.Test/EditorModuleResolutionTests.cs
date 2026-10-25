// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Build.PlatformToolchains;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Packages.EditorModules;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Editor-level platform-module resolution: the per-user module store wins over the bundled copy,
/// the bundle is the fallback, and project manifest pins resolve through the store (materialized
/// like an optional built-in) without module installs ever living inside a project on their own.
/// </summary>
public class EditorModuleResolutionTests : EditorTestHarness
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "XEngineEditorModuleResolutionTests", Guid.NewGuid().ToString("N"));
    private readonly string? _prevBuiltInRoot;
    private readonly string? _prevStoreRoot;

    /// <summary>The real android module name — what ReadFromEditorModule(Android) looks up.</summary>
    private const string AndroidModuleName = "com.xengine.platform.android";

    /// <summary>A synthetic module name for PackageManager manifest-pin tests (isolated from the
    /// android lookup so the two concerns never alias).</summary>
    private const string TestModuleName = "com.xengine.platform.test";

    public EditorModuleResolutionTests()
    {
        _prevBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
        _prevStoreRoot = EditorModuleStore.RootOverride;

        PackageManager.BuiltInPackagesPathOverride = Path.Combine(_root, "builtin", "Packages");
        Directory.CreateDirectory(PackageManager.BuiltInPackagesPathOverride);
        EditorModuleStore.RootOverride = Path.Combine(_root, "store");
    }

    public override void Dispose()
    {
        PackageManager.BuiltInPackagesPathOverride = _prevBuiltInRoot;
        EditorModuleStore.RootOverride = _prevStoreRoot;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        base.Dispose();
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>Place a module copy (package.json + toolchain.json with <paramref name="notes"/>)
    /// under <paramref name="parent"/>/<c>&lt;name&gt;@&lt;version&gt;</c> and return its root.</summary>
    private static string WriteModuleCopy(string parent, string name, string version, string notes)
    {
        string dir = Path.Combine(parent, $"{name}@{version}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), $$"""
            {
              "name": "{{name}}",
              "version": "{{version}}",
              "displayName": "Test platform module",
              "installByDefault": false
            }
            """);
        File.WriteAllText(Path.Combine(dir, ToolchainManifest.FileName),
            $$"""{ "manifestVersion": 1, "platform": "android", "notes": "{{notes}}", "components": [] }""");
        return dir;
    }

    /// <summary>Register a store-installed module (cache copy + modules.json record).</summary>
    private void InstallIntoStore(string name, string version, string notes)
    {
        Directory.CreateDirectory(EditorModuleStore.CacheRoot);
        WriteModuleCopy(EditorModuleStore.CacheRoot, name, version, notes);
        EditorModuleStore.UpsertRecord(new EditorModuleRecord
        {
            Name = name, Version = version, InstalledAtUtc = DateTime.UtcNow,
            Source = "npm", Registry = null,
        });
    }

    /// <summary>Ship a module copy with the Editor bundle (plain folder, no version suffix).</summary>
    private void InstallIntoBuiltIn(string name, string version, string notes)
    {
        string source = WriteModuleCopy(Path.Combine(_root, "builtin"), name, version, notes);
        string dest = Path.Combine(PackageManager.BuiltInPackagesPathOverride!, name);
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    // ------------------------------------------------------------------
    //  Manifest resolution: store > built-in
    // ------------------------------------------------------------------

    [Fact]
    public void ReadFromEditorModule_StoreWinsOverBuiltIn()
    {
        InstallIntoStore(AndroidModuleName, "0.2.0", "store-copy");
        InstallIntoBuiltIn(AndroidModuleName, "0.1.0", "builtin-copy");

        ToolchainManifest? manifest = ToolchainManifest.ReadFromEditorModule(PlatformToolchainKind.Android);
        Assert.NotNull(manifest);
        Assert.Equal("store-copy", manifest!.Notes);
        Assert.True(ToolchainManifest.IsEditorModulePresent(PlatformToolchainKind.Android));
    }

    [Fact]
    public void ReadFromEditorModule_FallsBackToBuiltInWhenStoreEmpty()
    {
        InstallIntoBuiltIn(AndroidModuleName, "0.1.0", "builtin-copy");

        ToolchainManifest? manifest = ToolchainManifest.ReadFromEditorModule(PlatformToolchainKind.Android);
        Assert.NotNull(manifest);
        Assert.Equal("builtin-copy", manifest!.Notes);
    }

    [Fact]
    public void ReadForPlatform_NoModuleAnywhere_ReturnsNull()
    {
        Assert.Null(ToolchainManifest.ReadForPlatform(null, PlatformToolchainKind.Android));
        Assert.Null(ToolchainManifest.ReadForPlatform(Project, PlatformToolchainKind.Android));
        Assert.False(ToolchainManifest.IsEditorModulePresent(PlatformToolchainKind.Android));
    }

    [Fact]
    public void ReadForPlatform_NullProject_UsesEditorLevelSources()
    {
        InstallIntoStore(AndroidModuleName, "0.2.0", "store-copy");

        ToolchainManifest? manifest = ToolchainManifest.ReadForPlatform(null, PlatformToolchainKind.Android);
        Assert.NotNull(manifest);
        Assert.Equal("store-copy", manifest!.Notes);
    }

    [Fact]
    public void ModulePackageName_MapsAllThreePlatforms()
    {
        Assert.Equal("com.xengine.platform.android", ToolchainManifest.ModulePackageName(PlatformToolchainKind.Android));
        Assert.Equal("com.xengine.platform.harmonyos", ToolchainManifest.ModulePackageName(PlatformToolchainKind.HarmonyOS));
        Assert.Equal("com.xengine.platform.ios", ToolchainManifest.ModulePackageName(PlatformToolchainKind.Ios));
    }

    // ------------------------------------------------------------------
    //  PackageManager: manifest pins resolve through the editor store
    // ------------------------------------------------------------------

    [Fact]
    public void PackageManager_BarePin_ResolvesThroughEditorStore()
    {
        InstallIntoStore(TestModuleName, "0.1.0", "store-copy");
        var manifest = ProjectPackageManifest.Empty();
        manifest.Dependencies[TestModuleName] = "0.1.0";
        manifest.Write(Path.Combine(Project.PackagesPath, "manifest.json"));
        PackageManager.Invalidate();

        ResolvedPackage? pkg = PackageManager.Resolve(Project)
            .FirstOrDefault(p => p.Name.Equals(TestModuleName, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(pkg);
        Assert.Equal(PackageSource.BuiltIn, pkg!.Source);
        Assert.Equal("0.1.0", pkg.Version);
        // installByDefault:false modules materialize a project-local read-only copy, exactly like
        // Editor-bundled optional packages.
        Assert.Contains("PackageCache", pkg.ResolvedRoot);
        Assert.True(File.Exists(Path.Combine(pkg.ResolvedRoot, "toolchain.json")));
    }

    [Fact]
    public void PackageManager_PinAboveStoreVersion_DoesNotDowngrade()
    {
        InstallIntoStore(TestModuleName, "0.1.0", "store-copy");
        var manifest = ProjectPackageManifest.Empty();
        manifest.Dependencies[TestModuleName] = "9.9.9";
        manifest.Write(Path.Combine(Project.PackagesPath, "manifest.json"));
        PackageManager.Invalidate();

        // npm is disabled by the harness, so nothing can satisfy the pin — the store copy must NOT
        // silently downgrade to 0.1.0.
        ResolvedPackage? pkg = PackageManager.Resolve(Project)
            .FirstOrDefault(p => p.Name.Equals(TestModuleName, StringComparison.OrdinalIgnoreCase));
        Assert.Null(pkg);
    }
}
