// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// P2 verification: the registry source (file:// local registry) and package-level platform
/// filtering in the player build. Pure-logic cases run without the SDK; the platform-build case is
/// opt-in under the "Build" category.
/// </summary>
public class PackageRegistryAndPlatformTests : EditorTestHarness
{
    public PackageRegistryAndPlatformTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    // ---- Registry source parsing ----

    [Theory]
    [InlineData("registry:file:///x/y#1.2.3", "file:///x/y", "1.2.3")]
    [InlineData("file:///x/y#1.2.3", "file:///x/y", "1.2.3")]
    [InlineData("registry:/abs/path#1.0.0", "/abs/path", "1.0.0")]
    public void RegistryParse(string spec, string expectedLocation, string expectedVersion)
    {
        var parsed = RegistryPackageSource.Parse(spec);
        Assert.NotNull(parsed);
        Assert.Equal(expectedLocation, parsed!.Value.registryLocation);
        Assert.Equal(expectedVersion, parsed.Value.version);
    }

    [Theory]
    [InlineData("git@https://x.git#v1")]
    [InlineData("embed:foo")]
    [InlineData("1.2.3")]
    public void RegistryParse_NonRegistrySpecifiers_ReturnNull(string spec)
    {
        Assert.Null(RegistryPackageSource.Parse(spec));
    }

    // ---- Registry source resolution against a local file:// registry ----

    [Fact]
    public void RegistrySource_ResolvesVersionFromLocalRegistry()
    {
        // Build a tiny local registry with two versions of com.test.regpkg, then resolve the lower.
        string registryRoot = Path.Combine(Path.GetDirectoryName(Project.RootPath)!, "local-registry");
        try
        {
            Directory.CreateDirectory(Path.Combine(registryRoot, "com.test.regpkg", "1.0.0"));
            Directory.CreateDirectory(Path.Combine(registryRoot, "com.test.regpkg", "1.2.0"));
            WriteRegPackageJson(registryRoot, "com.test.regpkg", "1.0.0");
            WriteRegPackageJson(registryRoot, "com.test.regpkg", "1.2.0");

            // Resolve 1.0.x — should pick 1.0.0 (the only 1.0.x available).
            var root = RegistryPackageSource.Resolve(Project, "com.test.regpkg", "1.0.x", registryRoot);
            Assert.NotNull(root);
            Assert.True(root!.EndsWith("com.test.regpkg@1.0.0", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(root, "package.json")));

            // Resolve ^1.0.0 / >=1.0.0 — should pick the highest (1.2.0).
            var rootHigh = RegistryPackageSource.Resolve(Project, "com.test.regpkg", "^1.0.0", registryRoot);
            Assert.NotNull(rootHigh);
            Assert.True(rootHigh!.EndsWith("com.test.regpkg@1.2.0", StringComparison.OrdinalIgnoreCase));
        }
        finally { TryDeleteDir(registryRoot); }
    }

    [Fact]
    public void RegistrySource_UnsatisfiableVersion_ReturnsNullWithError()
    {
        string registryRoot = Path.Combine(Path.GetDirectoryName(Project.RootPath)!, "local-registry-empty");
        try
        {
            Directory.CreateDirectory(Path.Combine(registryRoot, "com.test.regpkg", "1.0.0"));
            WriteRegPackageJson(registryRoot, "com.test.regpkg", "1.0.0");

            // Ask for 2.x — no such version exists.
            var root = RegistryPackageSource.Resolve(Project, "com.test.regpkg", "2.x", registryRoot);
            Assert.Null(root);
        }
        finally { TryDeleteDir(registryRoot); }
    }

    [Fact]
    public void PackageCatalog_IncludesUninstalledVersionsFromScopedRegistry()
    {
        string registryRoot = Path.Combine(Path.GetDirectoryName(Project.RootPath)!, "catalog-registry");
        try
        {
            Directory.CreateDirectory(Path.Combine(registryRoot, "com.test.catalog", "1.0.0"));
            Directory.CreateDirectory(Path.Combine(registryRoot, "com.test.catalog", "1.2.0"));
            WriteRegPackageJson(registryRoot, "com.test.catalog", "1.0.0");
            WriteRegPackageJson(registryRoot, "com.test.catalog", "1.2.0");
            new ProjectPackageManifest
            {
                ScopedRegistries = new()
                {
                    ["tests"] = new ScopedRegistry
                    {
                        Url = registryRoot,
                        Scopes = new() { "com.test" },
                    },
                },
            }.Write(Path.Combine(Project.PackagesPath, "manifest.json"));

            PackageManager.Invalidate();
            PackageCatalogSnapshot snapshot = PackageCatalog.Build(Project);
            PackageCatalogEntry entry = Assert.Single(snapshot.Entries,
                candidate => candidate.Name == "com.test.catalog");

            Assert.Equal(PackageInstallState.Available, entry.State);
            Assert.Equal("1.2.0", entry.LatestVersion);
            Assert.Equal(2, entry.Versions.Count);
        }
        finally { TryDeleteDir(registryRoot); }
    }

    [Fact]
    public void PackageOperation_FailedInstall_RestoresManifest()
    {
        string manifestPath = Path.Combine(Project.PackagesPath, "manifest.json");
        new ProjectPackageManifest().Write(manifestPath);

        PackageOperationResult result = PackageOperations.Install(
            Project, "com.test.missing", "9.9.9");
        ProjectPackageManifest manifest = ProjectPackageManifest.ReadOrDefault(manifestPath);

        Assert.False(result.Success);
        Assert.DoesNotContain("com.test.missing", manifest.Dependencies);
    }

    [Fact]
    public void PackageOperation_MissingTransitiveDependency_RestoresManifest()
    {
        string packageName = "com.test.incomplete";
        Directory.CreateDirectory(PackageRoot(packageName));
        WritePackageJson(new PackageJson
        {
            Name = packageName,
            Version = "1.0.0",
            Dependencies = new() { ["com.test.absent"] = "1.0.0" },
        });
        string manifestPath = Path.Combine(Project.PackagesPath, "manifest.json");
        new ProjectPackageManifest().Write(manifestPath);

        PackageManager.Invalidate();
        PackageOperationResult result = PackageOperations.Install(Project, packageName, "embed:" + packageName);
        ProjectPackageManifest manifest = ProjectPackageManifest.ReadOrDefault(manifestPath);

        Assert.False(result.Success);
        Assert.Contains("com.test.absent", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(packageName, manifest.Dependencies);
    }

    [Fact]
    public void PackageOperation_RemoveReportsCompleteDependencyChain()
    {
        WritePackageJson(new PackageJson { Name = "com.test.base", Version = "1.0.0" });
        WritePackageJson(new PackageJson
        {
            Name = "com.test.middle",
            Version = "1.0.0",
            Dependencies = new() { ["com.test.base"] = "1.0.0" },
        });
        WritePackageJson(new PackageJson
        {
            Name = "com.test.root",
            Version = "1.0.0",
            Dependencies = new() { ["com.test.middle"] = "1.0.0" },
        });
        new ProjectPackageManifest
        {
            Dependencies = new()
            {
                ["com.test.base"] = "embed:com.test.base",
                ["com.test.root"] = "embed:com.test.root",
            },
        }.Write(Path.Combine(Project.PackagesPath, "manifest.json"));

        PackageManager.Invalidate();
        PackageOperationResult result = PackageOperations.Remove(Project, "com.test.base");

        Assert.False(result.Success);
        Assert.Contains("com.test.root -> com.test.middle -> com.test.base", result.Message, StringComparison.Ordinal);
    }

    // ---- Package-level platform filtering in the player build ----

    [Fact]
    [Trait("Category", "Build")]
    public void PackageUnsupportedOnTarget_IsExcludedFromBuildAssemblies()
    {
        // com.test.winonly declares supportedPlatforms=["Windows"] and ships a script. A build for
        // Android target must exclude its assembly; a build for Windows must include it.
        WriteEmbedPackageWithPlatforms("com.test.winonly", "1.0.0",
            supportedPlatforms: new[] { BuildPlatforms.Windows });
        WritePackageScript("com.test.winonly", "Runtime",
            new AssemblyDefinition { Name = "WinOnly.Runtime" },
            "WinOnly.Runtime.asmdef",
            "namespace WinOnly.Runtime { public class W { } }",
            "W.cs");

        PackageManager.Invalidate();

        var winAsms = ScriptCompiler.GetBuildAssemblies(Project, BuildPlatforms.Windows)
            .Select(a => a.Name).ToList();
        var androidAsms = ScriptCompiler.GetBuildAssemblies(Project, BuildPlatforms.Android)
            .Select(a => a.Name).ToList();

        Assert.Contains("WinOnly.Runtime", winAsms);
        Assert.DoesNotContain("WinOnly.Runtime", androidAsms);
    }

    [Fact]
    public void PackageWithNoPlatformRestriction_IsIncludedEverywhere()
    {
        WriteEmbedPackage("com.test.anyplatform", "1.0.0", null);
        WritePackageScript("com.test.anyplatform", "Runtime",
            new AssemblyDefinition { Name = "AnyPlat.Runtime" },
            "AnyPlat.Runtime.asmdef",
            "namespace AnyPlat.Runtime { public class A { } }",
            "A.cs");

        PackageManager.Invalidate();

        // BuildPlan-level (no scripts compiled in BuildPlan itself, so just check it doesn't throw);
        // the real gate is GetBuildAssemblies, which we sanity-check across two targets.
        var winAsms = ScriptCompiler.GetBuildAssemblies(Project, BuildPlatforms.Windows);
        var androidAsms = ScriptCompiler.GetBuildAssemblies(Project, BuildPlatforms.Android);
        // Both must include the package's assembly (no platform restriction).
        Assert.Contains(winAsms, a => a.Name == "AnyPlat.Runtime");
        Assert.Contains(androidAsms, a => a.Name == "AnyPlat.Runtime");
    }

    [Fact]
    [Trait("Category", "Build")]
    public void PackageSupportedOnlyOnHarmonyOS_IsExcludedFromAndroidAndLinux()
    {
        WriteEmbedPackageWithPlatforms("com.test.harmonyosonly", "1.0.0",
            supportedPlatforms: new[] { BuildPlatforms.HarmonyOS });
        WritePackageScript("com.test.harmonyosonly", "Runtime",
            new AssemblyDefinition { Name = "HarmonyOSOnly.Runtime" },
            "HarmonyOSOnly.Runtime.asmdef",
            "namespace HarmonyOSOnly.Runtime { public class H { } }",
            "H.cs");

        PackageManager.Invalidate();

        var harmonyOsAsms = ScriptCompiler.GetBuildAssemblies(Project, BuildPlatforms.HarmonyOS);
        var androidAsms = ScriptCompiler.GetBuildAssemblies(Project, BuildPlatforms.Android);
        var linuxAsms = ScriptCompiler.GetBuildAssemblies(Project, BuildPlatforms.Linux);

        Assert.Contains(harmonyOsAsms, a => a.Name == "HarmonyOSOnly.Runtime");
        Assert.DoesNotContain(androidAsms, a => a.Name == "HarmonyOSOnly.Runtime");
        Assert.DoesNotContain(linuxAsms, a => a.Name == "HarmonyOSOnly.Runtime");
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private string PackageRoot(string packageName) => Path.Combine(Project.PackagesPath, packageName);

    private void WriteEmbedPackage(string packageName, string version, string? displayName)
    {
        Directory.CreateDirectory(PackageRoot(packageName));
        var pj = new PackageJson { Name = packageName, Version = version, DisplayName = displayName };
        File.WriteAllText(Path.Combine(PackageRoot(packageName), "package.json"),
            System.Text.Json.JsonSerializer.Serialize(pj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteEmbedPackageWithPlatforms(string packageName, string version, string[] supportedPlatforms)
    {
        Directory.CreateDirectory(PackageRoot(packageName));
        var pj = new PackageJson
        {
            Name = packageName,
            Version = version,
            SupportedPlatforms = supportedPlatforms.ToList(),
        };
        File.WriteAllText(Path.Combine(PackageRoot(packageName), "package.json"),
            System.Text.Json.JsonSerializer.Serialize(pj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private void WritePackageScript(string packageName, string subFolder,
        AssemblyDefinition asmdef, string asmdefFileName, string scriptContent, string scriptFileName)
    {
        string dir = Path.Combine(PackageRoot(packageName), subFolder);
        Directory.CreateDirectory(dir);
        asmdef.WriteToFile(Path.Combine(dir, asmdefFileName));
        File.WriteAllText(Path.Combine(dir, scriptFileName), scriptContent);
    }

    private void WritePackageJson(PackageJson package)
    {
        string root = PackageRoot(package.Name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"),
            System.Text.Json.JsonSerializer.Serialize(package,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteRegPackageJson(string registryRoot, string packageName, string version)
    {
        string dir = Path.Combine(registryRoot, packageName, version);
        Directory.CreateDirectory(dir);
        var pj = new PackageJson { Name = packageName, Version = version };
        File.WriteAllText(Path.Combine(dir, "package.json"),
            System.Text.Json.JsonSerializer.Serialize(pj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
