// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Core;
using XEngine.Editor.Inspector;
using XEngine.Editor.Importers;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Platforms.Android;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// P0 verification for the package manager: an embed package under Packages/&lt;name&gt;/ with a
/// package.json + an asmdef + a script is discovered by PackageManager.Resolve, picked up by
/// AssemblyDefinitionDatabase.LoadAll, compiled by ScriptCompiler, and shipped by the build pipeline —
/// without touching the existing Assets/-only behaviour. Pure-planner cases run without the SDK;
/// the compile/run case is opt-in under the "Build" category.
/// </summary>
public class PackageManagerTests : EditorTestHarness
{
    public PackageManagerTests()
    {
        // Keep resolution-only fixtures focused on their declared packages. The dedicated built-in
        // XGUI test below injects the real Editor package root explicitly.
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
        PackageManager.BuiltInPackagesPathOverride = Path.Combine(Project.TempPath, "NoBuiltInPackages");
        PackageManager.Invalidate();
    }

    // ---- Pure resolution: PackageManager.Resolve discovers embed packages ----

    [Fact]
    public void EmbedPackageUnderPackages_IsResolvedAsEmbed()
    {
        WriteEmbedPackage("com.test.alpha", "1.2.3", "Alpha");

        PackageManager.Invalidate();
        var resolved = PackageManager.Resolve(Project);

        var pkg = Assert.Single(resolved);
        Assert.Equal("com.test.alpha", pkg.Name);
        Assert.Equal("1.2.3", pkg.Version);
        Assert.Equal("Alpha", pkg.DisplayName);
        Assert.Equal(PackageSource.Embed, pkg.Source);
        Assert.True(pkg.ResolvedRoot.Replace('\\', '/').EndsWith("Packages/com.test.alpha", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmbedPackageAutoDiscovered_EvenWithoutManifestEntry()
    {
        // No manifest.json is written — the package folder alone should still be discovered.
        WriteEmbedPackage("com.test.beta", "0.1.0", null);

        PackageManager.Invalidate();
        var resolved = PackageManager.Resolve(Project);

        Assert.Contains(resolved, p => p.Name == "com.test.beta");
    }

    [Fact]
    public void ManifestDependency_PinsEmbedPackageByName()
    {
        WriteEmbedPackage("com.test.gamma", "2.0.0", "Gamma");
        WriteManifest(new() { ["com.test.gamma"] = "embed:com.test.gamma" });

        PackageManager.Invalidate();
        var resolved = PackageManager.Resolve(Project);

        var pkg = Assert.Single(resolved);
        Assert.Equal("com.test.gamma", pkg.Name);
        Assert.Equal("2.0.0", pkg.Version);
    }

    [Fact]
    public void ManifestFileReference_ResolvesLocalPackage()
    {
        // A local package lives outside Packages/ and is referenced by file: from the manifest.
        string externalRoot = Path.Combine(Path.GetDirectoryName(Project.RootPath)!, "external-pkg");
        try
        {
            Directory.CreateDirectory(externalRoot);
            WritePackageJson(externalRoot, "com.test.delta", "3.1.4", "Delta");

            // file: path relative to the project root.
            WriteManifest(new() { ["com.test.delta"] = "file:../external-pkg" });

            PackageManager.Invalidate();
            var resolved = PackageManager.Resolve(Project);

            var pkg = Assert.Single(resolved);
            Assert.Equal("com.test.delta", pkg.Name);
            Assert.Equal("3.1.4", pkg.Version);
            Assert.Equal(PackageSource.Local, pkg.Source);
            Assert.True(Directory.Exists(pkg.ResolvedRoot));
        }
        finally { TryDeleteDir(externalRoot); }
    }

    [Fact]
    public void LocalPackage_UsesStableLogicalPathAndMountResolution()
    {
        string externalRoot = Path.Combine(Path.GetDirectoryName(Project.RootPath)!, "external-stable-pkg");
        try
        {
            Directory.CreateDirectory(externalRoot);
            WritePackageJson(externalRoot, "com.test.stable", "1.0.0", "Stable");
            string assetPath = Path.Combine(externalRoot, "Runtime", "marker.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllText(assetPath, "stable content");
            WriteManifest(new() { ["com.test.stable"] = "file:../external-stable-pkg" });

            PackageManager.Invalidate();
            var pkg = Assert.Single(PackageManager.Resolve(Project));

            Assert.Equal("Packages/com.test.stable", pkg.LogicalRoot);
            Assert.NotEmpty(pkg.ContentHash);
            Assert.True(PackageManager.TryResolveLogicalAssetPath(
                Project, "Packages/com.test.stable/Runtime/marker.txt", out var resolved));
            Assert.Equal(Path.GetFullPath(assetPath), resolved);
            Assert.False(PackageManager.TryResolveLogicalAssetPath(
                Project, "Packages/com.test.stable/../outside.txt", out _));
        }
        finally { TryDeleteDir(externalRoot); }
    }

    [Fact]
    public void TransitiveDependency_IsPulledInViaPackageJson()
    {
        // com.test.parent declares a dependency on com.test.child in its package.json; the child is
        // present as an embed package under Packages/. Resolve must pull the child in transitively.
        WriteEmbedPackage("com.test.child", "1.0.0", "Child");
        WriteEmbedPackageWithDeps("com.test.parent", "1.0.0", "Parent",
            dependencies: new() { ["com.test.child"] = "1.0.0" });

        PackageManager.Invalidate();
        var resolved = PackageManager.Resolve(Project);

        Assert.Contains(resolved, p => p.Name == "com.test.parent");
        Assert.Contains(resolved, p => p.Name == "com.test.child");
    }

    [Fact]
    public void Resolve_WritesLockWithDeterministicClosureAndContentHashes()
    {
        WriteEmbedPackage("com.test.lock-child", "1.0.0", "Child");
        WriteEmbedPackageWithDeps("com.test.lock-parent", "1.0.0", "Parent",
            dependencies: new() { ["com.test.lock-child"] = "^1.0.0" });
        WriteManifest(new()
        {
            ["com.test.lock-parent"] = "embed:com.test.lock-parent",
            ["com.test.lock-child"] = "embed:com.test.lock-child",
        });

        PackageManager.Invalidate();
        var resolved = PackageManager.Resolve(Project);

        string lockPath = PackageLockFile.GetPath(Project);
        Assert.True(File.Exists(lockPath));
        var lockFile = Assert.IsType<PackageLockFile>(PackageLockFile.Read(Project));
        Assert.Equal(PackageLockFile.CurrentFormatVersion, lockFile.LockfileVersion);
        Assert.Equal(PackageLockFile.CurrentResolverVersion, lockFile.ResolverVersion);
        Assert.Equal(2, lockFile.Packages.Count);
        Assert.Equal("com.test.lock-child", lockFile.Packages[0].Name);
        Assert.Equal("com.test.lock-parent", lockFile.Packages[1].Name);

        var parent = Assert.Single(lockFile.Packages, p => p.Name == "com.test.lock-parent");
        Assert.Equal("Packages/com.test.lock-parent", parent.LogicalRoot);
        Assert.Equal("Packages/com.test.lock-parent", parent.ResolvedPath);
        Assert.NotEmpty(parent.ContentHash);
        Assert.Equal("^1.0.0", parent.Dependencies["com.test.lock-child"]);
        Assert.Contains("com.test.lock-child", parent.DependencyClosure);
        Assert.Equal(2, resolved.Count);
    }

    [Fact]
    public void Resolve_ContentChangeWithoutInvalidate_KeepsCachedInstance()
    {
        WriteEmbedPackage("com.test.content-hash", "1.0.0", null);
        PackageManager.Resolve(Project);
        PackageManager.Invalidate();
        var first = Assert.Single(PackageManager.Resolve(Project));
        string firstHash = first.ContentHash;

        string marker = Path.Combine(PackageRoot("com.test.content-hash"), "Runtime", "marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "changed package content");

        // Cheap cache is top-level names + manifest/lock metadata. Nested file writes do not
        // remount until a watcher Invalidate (or an explicit one).
        var second = Assert.Single(PackageManager.Resolve(Project));
        Assert.Same(first, second);
        Assert.Equal(firstHash, second.ContentHash);
    }

    [Fact]
    public void Resolve_TimestampOnlyChange_PreservesHashAndLockBytes()
    {
        WriteEmbedPackage("com.test.timestamp", "1.0.0", null);
        var first = Assert.Single(PackageManager.Resolve(Project));
        string lockPath = PackageLockFile.GetPath(Project);
        byte[] originalLock = File.ReadAllBytes(lockPath);
        foreach (string file in Directory.GetFiles(first.ResolvedRoot, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddDays(1));

        PackageManager.Invalidate();
        var second = Assert.Single(PackageManager.Resolve(Project));
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(originalLock, File.ReadAllBytes(lockPath));
    }

    [Fact]
    public void Resolve_SameLengthAndTimestampContentEdit_ChangesHash()
    {
        WriteEmbedPackage("com.test.same-size", "1.0.0", null);
        string marker = Path.Combine(PackageRoot("com.test.same-size"), "marker.txt");
        File.WriteAllText(marker, "before");
        DateTime timestamp = File.GetLastWriteTimeUtc(marker);
        var first = Assert.Single(PackageManager.Resolve(Project));

        File.WriteAllText(marker, "edited");
        File.SetLastWriteTimeUtc(marker, timestamp);
        PackageManager.Invalidate();
        var second = Assert.Single(PackageManager.Resolve(Project));
        Assert.NotEqual(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void Resolve_ContentChangeAfterInvalidate_UpdatesContentStamp()
    {
        WriteEmbedPackage("com.test.content-hash-inv", "1.0.0", null);
        PackageManager.Invalidate();
        var first = Assert.Single(PackageManager.Resolve(Project));
        string firstHash = first.ContentHash;

        string marker = Path.Combine(PackageRoot("com.test.content-hash-inv"), "Runtime", "marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "changed package content");
        PackageManager.Invalidate();

        var second = Assert.Single(PackageManager.Resolve(Project));
        Assert.NotEqual(firstHash, second.ContentHash);
        var lockFile = Assert.IsType<PackageLockFile>(PackageLockFile.Read(Project));
        Assert.Equal(second.ContentHash, Assert.Single(lockFile.Packages).ContentHash);
    }

    [Fact]
    public void CheapInputStamp_IsStableUntilTopLevelPackageFolderChanges()
    {
        WriteEmbedPackage("com.test.cheap-stamp", "1.0.0", null);
        string manifestPath = Path.Combine(Project.PackagesPath, "manifest.json");
        string a = PackageManager.ComputeCheapInputStamp(Project, manifestPath);
        Assert.Equal(a, PackageManager.ComputeCheapInputStamp(Project, manifestPath));

        string marker = Path.Combine(PackageRoot("com.test.cheap-stamp"), "Runtime", "nested.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "nested edit");
        Assert.Equal(a, PackageManager.ComputeCheapInputStamp(Project, manifestPath));

        WriteEmbedPackage("com.test.cheap-stamp-b", "1.0.0", null);
        Assert.NotEqual(a, PackageManager.ComputeCheapInputStamp(Project, manifestPath));
    }

    [Fact]
    public void PeekCached_IsNullAfterInvalidate_AndSameInstanceAfterResolve()
    {
        WriteEmbedPackage("com.test.peek-cached", "1.0.0", null);
        // Drain the embed-package watcher so a delayed Created event cannot drop the cache
        // after the assertion Resolve below.
        PackageManager.Resolve(Project);
        PackageManager.Invalidate();
        Assert.Null(PackageManager.PeekCached(Project));

        var resolved = PackageManager.Resolve(Project);
        Assert.Same(resolved, PackageManager.PeekCached(Project));
        Assert.Same(resolved, PackageManager.Resolve(Project));
    }

    [Fact]
    public void Resolve_UnchangedProject_ReturnsCachedInstance()
    {
        WriteEmbedPackage("com.test.resolve-cached", "1.0.0", null);
        PackageManager.Resolve(Project);
        PackageManager.Invalidate();
        var first = PackageManager.Resolve(Project);
        var second = PackageManager.Resolve(Project);
        Assert.Same(first, second);
    }

    [Fact]
    public void LockedRegistryCache_RestoresWhenSourceIsOffline()
    {
        const string name = "com.test.locked-registry";
        const string version = "1.0.0";
        string cacheRoot = Path.Combine(Project.LibraryPath, "PackageCache", name + "@" + version);
        Directory.CreateDirectory(cacheRoot);
        WritePackageJson(cacheRoot, name, version, "Cached registry package");
        WriteManifest(new() { [name] = "registry:file:///missing-registry#1.0.0" });

        var lockFile = new PackageLockFile
        {
            ManifestHash = PackageLockFile.ComputeManifestHash(Path.Combine(Project.PackagesPath, "manifest.json")),
            GeneratedAtUtc = "2026-08-10T00:00:00.0000000Z",
            Packages = new()
            {
                new PackageLockEntry
                {
                    Name = name,
                    Version = version,
                    Source = "registry",
                    SourceSpecifier = "registry:file:///missing-registry#1.0.0",
                    LogicalRoot = "Packages/" + name,
                    ResolvedPath = "Library/PackageCache/" + name + "@" + version,
                    ContentHash = "placeholder",
                },
            },
        };
        lockFile.Write(PackageLockFile.GetPath(Project));

        PackageManager.Invalidate();
        var package = Assert.Single(PackageManager.Resolve(Project));

        Assert.Equal(PackageSource.Registry, package.Source);
        Assert.Equal(Path.GetFullPath(cacheRoot), package.ResolvedRoot);
        Assert.Equal("Packages/" + name, package.LogicalRoot);
    }

    [Fact]
    public void DependencyResolver_SelectsHighestCompatibleCachedPackageVersion()
    {
        const string childName = "com.test.versioned";
        WriteCachedPackage(childName, "1.0.0");
        WriteCachedPackage(childName, "1.2.0");
        WriteEmbedPackageWithDeps("com.test.version-parent", "1.0.0", "Parent",
            dependencies: new() { [childName] = "^1.0.0" });
        WriteManifest(new() { ["com.test.version-parent"] = "embed:com.test.version-parent" });

        PackageManager.Invalidate();
        var resolved = PackageManager.Resolve(Project);

        var child = Assert.Single(resolved, p => p.Name == childName);
        Assert.Equal("1.2.0", child.Version);
    }

    // ---- Integration: asmdefs inside packages reach AssemblyDefinitionDatabase ----

    [Fact]
    public void PackageAsmdef_IsDiscoveredByAssemblyDefinitionDatabase()
    {
        WriteEmbedPackage("com.test.asmdef", "1.0.0", null);
        WritePackageScript("com.test.asmdef", "Runtime",
            new AssemblyDefinition { Name = "PkgAsm.Runtime" },
            "PkgAsm.Runtime.asmdef",
            "namespace PkgAsm.Runtime { public class Marker { } }",
            "Marker.cs");

        PackageManager.Invalidate();
        var asmdefs = AssemblyDefinitionDatabase.LoadAll(Project);

        Assert.Contains(asmdefs, a => a.Name == "PkgAsm.Runtime");
        // The owning directory must be inside the package root.
        var asmdef = Assert.Single(asmdefs, a => a.Name == "PkgAsm.Runtime");
        Assert.Contains("Packages/com.test.asmdef", asmdef.Directory.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TildeSuffixedPackageDirs_AreSkippedByAsmdefDiscovery()
    {
        WriteEmbedPackage("com.test.tilde", "1.0.0", null);
        // Write an asmdef inside a Samples~ directory — it must NOT be picked up.
        string samplesDir = Path.Combine(PackageRoot("com.test.tilde"), "Samples~");
        Directory.CreateDirectory(samplesDir);
        new AssemblyDefinition { Name = "Ignored.Samples" }
            .WriteToFile(Path.Combine(samplesDir, "Ignored.Samples.asmdef"));

        PackageManager.Invalidate();
        var asmdefs = AssemblyDefinitionDatabase.LoadAll(Project);

        Assert.DoesNotContain(asmdefs, a => a.Name == "Ignored.Samples");
    }

    // ---- Build: package scripts compile and ship end-to-end ----

    [Fact]
    [Trait("Category", "Build")]
    public void TestablePackage_TestsFolderCompilesIntoTestsAssembly()
    {
        // com.test.testable ships a runtime script AND a Tests/ script. With the package in the
        // manifest's testables list, the Tests/ script must land in {Project}.Tests.dll (not Game).
        WriteEmbedPackage("com.test.testable", "1.0.0", null);
        WritePackageScript("com.test.testable", "Runtime",
            new AssemblyDefinition { Name = "Testable.Runtime" },
            "Testable.Runtime.asmdef",
            """
            namespace Testable.Runtime { public static class Calc { public static int Double(int x) => x * 2; } }
            """,
            "Calc.cs");
        // Tests/ folder under the same package, NOT under an asmdef (so it's loose).
        WritePackageScript("com.test.testable", "Tests",
            new AssemblyDefinition { Name = "_unused_" }, "_unused_.asmdef", // placeholder, not read
            """
            using Testable.Runtime;
            namespace Testable.Tests {
                public static class CalcTests {
                    public static void Run() => System.Console.WriteLine("TEST_OK:" + Calc.Double(21));
                }
            }
            """,
            "CalcTests.cs");
        // Remove the placeholder asmdef so the Tests/ scripts stay loose (route to default Tests unit).
        string placeholderAsmdef = Path.Combine(PackageRoot("com.test.testable"), "Tests", "_unused_.asmdef");
        if (File.Exists(placeholderAsmdef)) File.Delete(placeholderAsmdef);

        WriteManifest(new() { ["com.test.testable"] = "embed:com.test.testable" });
        // Mark it testable so its Tests/ folder participates.
        var manifestPath = Path.Combine(Project.PackagesPath, "manifest.json");
        var manifest = ProjectPackageManifest.ReadOrDefault(manifestPath);
        manifest.Testables.Add("com.test.testable");
        manifest.Write(manifestPath);

        PackageManager.Invalidate();
        var compile = ScriptCompiler.CompileAll(Project);
        Assert.True(compile.Success, $"Testables compile failed:\n{compile.Errors}\n{compile.Output}");

        // The Tests assembly must have been produced (the test script landed there, not in Game).
        Assert.True(File.Exists(Path.Combine(Project.ScriptAssemblyPath, $"{Project.Name}.Tests.dll")),
            "Test script was not routed into the Tests assembly.");
        // And it must reference Testable.Runtime (the package's runtime asmdef).
        string testsCsproj = File.ReadAllText(Path.Combine(Project.RootPath, $"{Project.Name}.Tests.csproj"));
        Assert.Contains("Testable.Runtime", testsCsproj);

        string runtimeCsproj = File.ReadAllText(Path.Combine(Project.RootPath, "Testable.Runtime.csproj"));
        Assert.DoesNotContain("XEngine.Editor.dll", runtimeCsproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XENGINE_EDITOR", runtimeCsproj, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Build")]
    public void PackageScript_CompilesAndShips()
    {
        // macOS: user-script/package csproj generation uses relative HintPaths that break
        // under /var symlinked temp dirs (CS2001) and the built player apphost is win-x64;
        // both land with the macOS packaging milestone.
        if (OperatingSystem.IsMacOS())
            return;
        WriteEmbedPackage("com.test.runpkg", "1.0.0", null);
        WritePackageScript("com.test.runpkg", "Runtime",
            new AssemblyDefinition { Name = "RunPkg.Runtime" },
            "RunPkg.Runtime.asmdef",
            """
            namespace RunPkg.Runtime
            {
                public static class PkgMath
                {
                    public static int Answer() => 42;
                }
            }
            """,
            "PkgMath.cs");

        // Loose script in Assets/ uses the package assembly via auto-reference.
        WriteScript("Main.cs", $$"""
            using XEngine.Runtime;
            using RunPkg.Runtime;
            public class Main : MonoBehaviour
            {
                public override void Start() => System.Console.WriteLine("PKG_OK:" + PkgMath.Answer());
            }
            """);

        PackageManager.Invalidate();
        var compile = ScriptCompiler.CompileAll(Project);
        Assert.True(compile.Success, $"Package compile failed:\n{compile.Errors}\n{compile.Output}");

        // The package's asmdef must have produced its own DLL alongside the default Game/Editor DLLs.
        Assert.True(File.Exists(Path.Combine(Project.ScriptAssemblyPath, "RunPkg.Runtime.dll")),
            "Package assembly was not produced.");

        // And the player build must ship it next to the executable.
        string outDir = RunBuild(AuthorSceneWithComponent("Main"));
        try
        {
            Assert.True(File.Exists(Path.Combine(outDir, "RunPkg.Runtime.dll")),
                "Package assembly was not shipped into the player build.");
            string playerOutput = RunPlayerHeadless(outDir, frames: 5);
            Assert.Contains("PKG_OK:42", playerOutput);
        }
        finally { TryDeleteDir(outDir); }
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private string PackageRoot(string packageName)
        => Path.Combine(Project.PackagesPath, packageName);

    private void WriteEmbedPackage(string packageName, string version, string? displayName)
    {
        string root = PackageRoot(packageName);
        Directory.CreateDirectory(root);
        WritePackageJson(root, packageName, version, displayName);
    }

    private void WriteEmbedPackageWithDeps(string packageName, string version, string? displayName,
        Dictionary<string, string> dependencies)
    {
        string root = PackageRoot(packageName);
        Directory.CreateDirectory(root);
        var pj = new PackageJson
        {
            Name = packageName,
            Version = version,
            DisplayName = displayName,
            Dependencies = dependencies,
        };
        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(root, "package.json"),
            System.Text.Json.JsonSerializer.Serialize(pj, opts));
    }

    private static void WritePackageJson(string root, string packageName, string version, string? displayName)
    {
        // Use System.Text.Json directly so we never produce malformed JSON from string interpolation.
        var pj = new PackageJson { Name = packageName, Version = version, DisplayName = displayName };
        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(root, "package.json"),
            System.Text.Json.JsonSerializer.Serialize(pj, opts));
    }

    [Fact]
    [Trait("Category", "Build")]
    public void BuiltInXguiPackage_RuntimeAssemblyCompilesWithoutManifestEntry()
    {
        // macOS: user-script/package csproj generation uses relative HintPaths that break
        // under /var symlinked temp dirs (CS2001) and the built player apphost is win-x64;
        // both land with the macOS packaging milestone.
        if (OperatingSystem.IsMacOS())
            return;
        string sourceRoot = FindRepositoryRoot();
        string packageSource = Path.Combine(sourceRoot, "Packages", "com.xengine.xgui");
        Assert.True(File.Exists(Path.Combine(packageSource, "package.json")),
            "The first-party XGUI package is missing from the repository.");

        string? previousBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(sourceRoot, "Packages");
            PackageManager.Invalidate();
            ResolvedPackage? package = null;
            IReadOnlyList<ResolvedPackage> resolvedPackages = PackageManager.Resolve(Project);
            for (int i = 0; i < resolvedPackages.Count; i++)
            {
                ResolvedPackage candidate = resolvedPackages[i];
                if (!string.Equals(candidate.Name, "com.xengine.xgui", StringComparison.Ordinal)) continue;
                Assert.Null(package);
                package = candidate;
            }
            Assert.NotNull(package);
            Assert.Equal("0.1.1", package.Version);
            Assert.Equal(PackageSource.BuiltIn, package.Source);
            Assert.True(package.Mount.ReadOnly);
            Assert.Equal(Path.GetFullPath(packageSource), package.ResolvedRoot);
            Assert.Equal("Packages/com.xengine.xgui", package.LogicalRoot);

            // A blank project has neither a manifest entry nor user scripts, but its built-in
            // package still contributes source to the Editor's normal compilation path.
            Assert.False(ScriptCompiler.ProjectDeclaresPackages(Project));
            Assert.True(ScriptCompiler.HasCompilationSources(Project));

            // Mirrors first launch: no project manifest or user scripts exist, yet the assembly manager
            // must compile and load Editor-shipped XGUI before editor registries scan user types.
            ScriptAssemblyManager.LoadAssemblies(Project);
            Assert.True(File.Exists(Path.Combine(Project.ScriptAssemblyPath, "XEngine.XGUI.Runtime.dll")));

        // The Runtime assembly's exported-name contract holds on its own, so check it before
        // asserting the Editor half shipped: no exported type may carry the same full name in both
        // assemblies. A package asmdef that references XEngine.Runtime and XEngine.XGUI.Runtime
        // together (the shape com.xengine.spine already declares) fails with CS0433 the moment it
        // names such a type, so the Runtime compatibility classes all carry a UI prefix the package
        // deliberately does not use.
        string xguiAssemblyPath = Path.Combine(Project.ScriptAssemblyPath, "XEngine.XGUI.Runtime.dll");
        var xguiAssembly = System.Reflection.Assembly.LoadFrom(xguiAssemblyPath);
        var runtimeTypeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Type runtimeType in typeof(GameCanvas).Assembly.GetExportedTypes())
        {
            if (runtimeType.FullName is { } name) runtimeTypeNames.Add(name);
        }
        var ambiguousNames = new List<string>();
        foreach (Type packageType in xguiAssembly.GetExportedTypes())
        {
            if (packageType.FullName is { } name && runtimeTypeNames.Contains(name)) ambiguousNames.Add(name);
        }
        ambiguousNames.Sort(StringComparer.Ordinal);
        Assert.True(ambiguousNames.Count == 0,
            "These names exist in both XEngine.Runtime and XEngine.XGUI.Runtime, so any assembly "
            + "referencing both gets CS0433 when it names one:\n  " + string.Join("\n  ", ambiguousNames));

            Assert.True(File.Exists(Path.Combine(Project.ScriptAssemblyPath, "XEngine.XGUI.Editor.dll")));

        // Loading the compiled package and running the editor assembly-load lifecycle must install
        // redirects so the old serialized name resolves to the package facade.
        Type imageType = xguiAssembly.GetType("XEngine.Runtime.UI.Image")!;
        Type inputFieldType = xguiAssembly.GetType("XEngine.Runtime.UI.InputField")!;
        Assert.NotNull(imageType);
        Assert.NotNull(inputFieldType);
        Assert.False(RuntimeTypeRegistry.IsAuthoritative);
        ScriptReloadCallbacks.InvokeAssemblyLoad();
        Type? directRedirect = RuntimeTypeRegistry.Resolve("XEngine.Runtime.UI.UIImage, XEngine.Runtime");
        Assert.Same(imageType, directRedirect);
        Type? redirected = TypeNameRegistry.ResolveFullTypeName(
            "XEngine.Runtime.UI.UIImage, XEngine.Runtime, Version=1.0.0.0, Culture=neutral");
        Assert.Same(imageType, redirected);
        Assert.Same(imageType, RuntimeTypeRegistry.Resolve("UIImage"));
        Assert.Same(inputFieldType, RuntimeTypeRegistry.Resolve("UIInputField"));

        // Layout data authored before the Runtime classes took their UI prefix still serializes the
        // unprefixed name, which no longer matches a Runtime type; both spellings must land on the
        // package component so those scenes and prefabs keep deserializing.
        Type packageLayoutGroupType = RequireType(xguiAssembly, "XEngine.Runtime.UI.LayoutGroup");
        Type packageLayoutElementType = RequireType(xguiAssembly, "XEngine.Runtime.UI.LayoutElement");
        Type packageFitterType = RequireType(xguiAssembly, "XEngine.Runtime.UI.ContentSizeFitter");
        Assert.Same(packageLayoutGroupType,
            RuntimeTypeRegistry.Resolve("XEngine.Runtime.UI.LayoutGroup, XEngine.Runtime"));
        Assert.Same(packageLayoutElementType,
            RuntimeTypeRegistry.Resolve("XEngine.Runtime.UI.LayoutElement, XEngine.Runtime"));
        Assert.Same(packageFitterType,
            RuntimeTypeRegistry.Resolve("XEngine.Runtime.UI.ContentSizeFitter, XEngine.Runtime"));
        Assert.Same(packageLayoutGroupType,
            RuntimeTypeRegistry.Resolve("XEngine.Runtime.UI.UILayoutGroup, XEngine.Runtime"));
        Assert.Same(packageLayoutElementType,
            RuntimeTypeRegistry.Resolve("XEngine.Runtime.UI.UILayoutElement, XEngine.Runtime"));
        Assert.Same(packageFitterType,
            RuntimeTypeRegistry.Resolve("XEngine.Runtime.UI.UIContentSizeFitter, XEngine.Runtime"));

        // XGUI owns the standard GameObject/UI authoring entries. This guards both the package
        // load order and the removal of the legacy UI/XGUI compatibility submenu.
        MenuRegistry.Clear();
        MenuItemAttribute.PopulateMenuRegistry();
        AppMenuItem gameObjectMenu = Assert.Single(MenuRegistry.RootMenus, item => item.Label == "GameObject");
        AppMenuItem uiMenu = Assert.Single(gameObjectMenu.SubItems, item => item.Label == "UI");
        string[] expectedXguiControls =
        [
            "Canvas", "Text", "Image", "Raw Image", "Button", "Toggle", "Toggle Group", "Panel", "Safe Area",
            "Slider", "Scrollbar", "Scroll View", "Rect Mask 2D", "Mask", "Input Field", "Dropdown", "Event System",
        ];
        for (int i = 0; i < expectedXguiControls.Length; i++)
        {
            AppMenuItem item = Assert.Single(uiMenu.SubItems, item => item.Label == expectedXguiControls[i]);
            Assert.Equal("XEngine.XGUI.Editor", item.OnClick?.Method.DeclaringType?.Assembly.GetName().Name);
        }
        Assert.DoesNotContain(uiMenu.SubItems, item => item.Label == "XGUI");
        Assert.DoesNotContain(uiMenu.SubItems, item => item.Label == "Rect Mask");

        // The Add Component popup follows the same ownership rule. Its all-type scan must not
        // expose Runtime compatibility types beside the package components with the same labels.
        Type toggleType = xguiAssembly.GetType("XEngine.Runtime.UI.Toggle")!;
        Assert.True(GameObjectInspector.IsAddComponentTypeVisible(imageType));
        Assert.True(GameObjectInspector.IsAddComponentTypeVisible(toggleType));
        Assert.False(GameObjectInspector.IsAddComponentTypeVisible(typeof(UIImage)));
        Assert.False(GameObjectInspector.IsAddComponentTypeVisible(typeof(TextComponent)));
        Assert.False(GameObjectInspector.IsAddComponentTypeVisible(typeof(UIButton)));
        Assert.False(GameObjectInspector.IsAddComponentTypeVisible(typeof(Selectable)));
        Assert.False(GameObjectInspector.IsAddComponentTypeVisible(typeof(UILayoutGroup)));
        Assert.False(GameObjectInspector.IsAddComponentTypeVisible(typeof(RectMask)));
        Assert.True(GameObjectInspector.IsAddComponentTypeVisible(typeof(CanvasGroup)));
        Assert.True(GameObjectInspector.IsAddComponentTypeVisible(typeof(GameCanvas)));
        Assert.True(GameObjectInspector.IsAddComponentTypeVisible(typeof(CanvasRenderer)));
        Assert.True(GameObjectInspector.IsAddComponentTypeVisible(typeof(EventSystem)));

        string csproj = File.ReadAllText(Path.Combine(Project.RootPath, "XEngine.XGUI.Runtime.csproj"));
        Assert.DoesNotContain("XEngine.Editor.dll", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XENGINE_EDITOR", csproj, StringComparison.Ordinal);
        Assert.Contains("XEngine.Runtime", csproj, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(packageSource, "Runtime", "Layout", "AspectRatioFitter.cs")));
        Assert.True(File.Exists(Path.Combine(packageSource, "Runtime", "Layout", "AspectRatioFitter.cs.meta")));
        Assert.True(File.Exists(Path.Combine(packageSource, "Runtime", "Layout", "SafeAreaFitter.cs")));
        Assert.True(File.Exists(Path.Combine(packageSource, "Runtime", "Layout", "SafeAreaFitter.cs.meta")));

            VerifyPackageLayoutContract(xguiAssembly);
            VerifyPackageGraphicCanvasHost(xguiAssembly);
            VerifyPackageGraphicRaycastOrder(xguiAssembly);
            VerifyPackageScenePrefabReload(xguiAssembly);
            VerifyPackageScriptPackageHotReload(xguiAssembly);
            VerifyPackageDeviceLostResize(xguiAssembly);
            VerifyPackageBackendScreenshotParity(xguiAssembly);
            VerifyPackagePlayerBuildAudit(Project);
            VerifyPackageEditorTemplates();
            VerifyPackageMigrationScanner(xguiAssembly);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previousBuiltInRoot;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void BuiltInXguiAsmdefReferencesImplicitEngineAssembliesWithoutWarnings()
    {
        // macOS: user-script/package csproj generation uses relative HintPaths that break
        // under /var symlinked temp dirs (CS2001) and the built player apphost is win-x64;
        // both land with the macOS packaging milestone.
        if (OperatingSystem.IsMacOS())
            return;
        string sourceRoot = FindRepositoryRoot();
        string? previousBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
        var warnings = new List<string>();
        OnLog handler = (message, _, severity) =>
        {
            if (severity == LogSeverity.Warning && message.Contains("references unknown assembly", StringComparison.Ordinal))
                warnings.Add(message);
        };
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(sourceRoot, "Packages");
            PackageManager.Invalidate();
            Debug.OnLog += handler;

            var result = ScriptCompiler.CompileAll(Project);

            Assert.True(result.Success, $"XGUI compile failed:\n{result.Errors}\n{result.Output}");
            Assert.DoesNotContain(warnings, warning => warning.Contains("XEngine.Runtime", StringComparison.Ordinal));
            Assert.DoesNotContain(warnings, warning => warning.Contains("XEngine.Editor", StringComparison.Ordinal));
        }
        finally
        {
            Debug.OnLog -= handler;
            PackageManager.BuiltInPackagesPathOverride = previousBuiltInRoot;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void BuiltInXguiPackage_AssetMetaFilesHaveValidGuids()
    {
        string packageRoot = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.xgui");
        Assert.True(Directory.Exists(packageRoot), "The first-party XGUI package is missing from the repository.");

        foreach (string metaPath in Directory.EnumerateFiles(packageRoot, "*.meta", SearchOption.AllDirectories))
        {
            MetaFileData meta = MetaFile.Read(metaPath);
            Assert.NotEqual(Guid.Empty, meta.Guid);
        }
    }

    [Fact]
    public void BuiltInXguiPackage_DefaultResourcesShipWithValidMetas()
    {
        string packageRoot = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.xgui");
        string resources = Path.Combine(packageRoot, "Runtime", "Resources");
        string sprite = Path.Combine(resources, "DefaultSprite.png");
        string font = Path.Combine(resources, "DefaultFont.ttf");

        Assert.True(File.Exists(sprite));
        Assert.True(File.Exists(font));
        Assert.True(new FileInfo(sprite).Length > 1000);
        Assert.True(new FileInfo(font).Length > 1000);

        MetaFileData folderMeta = MetaFile.Read(resources + ".meta");
        MetaFileData spriteMeta = MetaFile.Read(sprite + ".meta");
        MetaFileData fontMeta = MetaFile.Read(font + ".meta");

        Assert.NotEqual(Guid.Empty, folderMeta.Guid);
        Assert.Equal("DefaultImporter", folderMeta.ImporterType);
        Assert.Equal(Guid.Parse("8c4e9e61-e1be-42b6-a9bb-0017f9d86432"), spriteMeta.Guid);
        Assert.Equal("TextureImporter", spriteMeta.ImporterType);
        Assert.Equal(3, spriteMeta.ImporterVersion);
        Assert.Equal(Guid.Parse("0f1e8543-ff95-4bff-8b06-a91312e69554"), fontMeta.Guid);
        Assert.Equal("FontImporter", fontMeta.ImporterType);
        Assert.Equal(1, fontMeta.ImporterVersion);

        Assert.NotNull(spriteMeta.Settings);
        Assert.True(spriteMeta.Settings!.TryGet("sprite", out EchoObject spriteSettings));
        Assert.True(spriteSettings.TryGet("Mode", out EchoObject mode));
        Assert.Equal((long)SpriteMode.Single, mode.LongValue);
        Assert.True(spriteSettings.TryGet("PixelsPerUnit", out EchoObject ppu));
        Assert.Equal(100f, ppu.FloatValue);

        Guid derived = AssetEntry.DeriveSubAssetGuid(spriteMeta.Guid, "DefaultSprite");
        Assert.Equal(Guid.Parse("f66704c9-cd9c-759c-c49d-9a0a4d2ed4b8"), derived);
    }

    [Fact]
    public void BuiltInXguiPackage_ShipsHiddenSamplesAndDocumentation()
    {
        string packageRoot = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.xgui");
        string samples = Path.Combine(packageRoot, "Samples~");
        string docs = Path.Combine(packageRoot, "Documentation~");
        string gallery = Path.Combine(samples, "ControlsGallery", "XGuiControlsGallery.xgui-template.json");

        Assert.True(Directory.Exists(samples));
        Assert.True(Directory.Exists(docs));
        Assert.True(File.Exists(Path.Combine(samples, "README.md")));
        Assert.True(File.Exists(Path.Combine(docs, "api.md")));
        Assert.True(File.Exists(Path.Combine(docs, "migration.md")));
        Assert.True(File.Exists(gallery));
        Assert.Contains("XGUI Gallery Canvas", File.ReadAllText(gallery), StringComparison.Ordinal);
        Assert.Contains("Dropdown", File.ReadAllText(Path.Combine(docs, "migration.md")), StringComparison.Ordinal);

        string ignoredAsmdef = Path.Combine(samples, "Ignored.Samples.asmdef");
        Assert.False(File.Exists(ignoredAsmdef));
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(gallery, packageRoot));
        Assert.True(PackageManager.IsUnderIgnoredPackageDir(Path.Combine(docs, "api.md"), packageRoot));
    }

    [Fact]
    public void EditorDistribution_ContainsBuiltInXguiAndXEngineAssemblies()
    {
        string editorDistribution = Path.Combine(FindRepositoryRoot(), "Build", "Editor", "Debug", "net10.0");
        // AppHost naming: ".exe" only exists on Windows; macOS/Linux emit an extension-less binary.
        string editorExecutable = OperatingSystem.IsWindows() ? "XEngine.Editor.exe" : "XEngine.Editor";
        Assert.True(File.Exists(Path.Combine(editorDistribution, editorExecutable)),
            "The Debug Editor distribution was not produced by the test project reference.");
        Assert.True(File.Exists(Path.Combine(editorDistribution, "Packages", "com.xengine.xgui", "package.json")),
            "The Editor distribution is missing the built-in XGUI package.");
        Assert.True(File.Exists(Path.Combine(editorDistribution, "XEngine.Editor.dll")));
        Assert.True(File.Exists(Path.Combine(editorDistribution, "XEngine.Runtime.dll")));
    }

    [Fact]
    public void ProjectEmbedPackage_OverridesBuiltInPackageWithSameName()
    {
        string builtInRoot = Path.Combine(Project.TempPath, "built-in-packages");
        try
        {
            string xguiRoot = Path.Combine(builtInRoot, "com.xengine.xgui");
            Directory.CreateDirectory(xguiRoot);
            WritePackageJson(xguiRoot, "com.xengine.xgui", "0.1.0", "Built-in XGUI");
            WriteEmbedPackage("com.xengine.xgui", "9.0.0", "Project XGUI");
            WriteManifest(new() { ["com.xengine.xgui"] = "embed:com.xengine.xgui" });

            string? previousBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
            try
            {
                PackageManager.BuiltInPackagesPathOverride = builtInRoot;
                PackageManager.Invalidate();
                var package = Assert.Single(PackageManager.Resolve(Project));

                Assert.Equal(PackageSource.Embed, package.Source);
                Assert.Equal("9.0.0", package.Version);
                Assert.Equal(Path.GetFullPath(PackageRoot("com.xengine.xgui")), package.ResolvedRoot);
            }
            finally
            {
                PackageManager.BuiltInPackagesPathOverride = previousBuiltInRoot;
                PackageManager.Invalidate();
            }
        }
        finally { TryDeleteDir(builtInRoot); }
    }

    [Fact]
    public void XguiLogicalRoot_StaysStableAcrossBuiltInEmbedAndLocalSources()
    {
        const string logicalRoot = "Packages/com.xengine.xgui";
        string sourceRoot = FindRepositoryRoot();
        string externalRoot = Path.Combine(Path.GetDirectoryName(Project.RootPath)!, "external-xgui");
        string? previousBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(sourceRoot, "Packages");
            PackageManager.Invalidate();
            ResolvedPackage builtIn = Assert.Single(
                PackageManager.Resolve(Project),
                package => package.Name == "com.xengine.xgui");
            Assert.Equal(PackageSource.BuiltIn, builtIn.Source);
            Assert.Equal(logicalRoot, builtIn.LogicalRoot);

            WriteEmbedPackage("com.xengine.xgui", "9.0.0", "Project XGUI");
            WriteManifest(new() { ["com.xengine.xgui"] = "embed:com.xengine.xgui" });
            PackageManager.Invalidate();
            ResolvedPackage embed = Assert.Single(
                PackageManager.Resolve(Project),
                package => package.Name == "com.xengine.xgui");
            Assert.Equal(PackageSource.Embed, embed.Source);
            Assert.Equal(logicalRoot, embed.LogicalRoot);

            TryDeleteDir(PackageRoot("com.xengine.xgui"));
            Directory.CreateDirectory(externalRoot);
            WritePackageJson(externalRoot, "com.xengine.xgui", "0.2.0", "Local XGUI");
            WriteManifest(new() { ["com.xengine.xgui"] = "file:../external-xgui" });
            PackageManager.Invalidate();
            ResolvedPackage local = Assert.Single(
                PackageManager.Resolve(Project),
                package => package.Name == "com.xengine.xgui");
            Assert.Equal(PackageSource.Local, local.Source);
            Assert.Equal(logicalRoot, local.LogicalRoot);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previousBuiltInRoot;
            PackageManager.Invalidate();
            TryDeleteDir(PackageRoot("com.xengine.xgui"));
            TryDeleteDir(externalRoot);
        }
    }

    private static void VerifyPackageLayoutContract(System.Reflection.Assembly packageAssembly)
    {
        Type layoutElementType = RequireType(packageAssembly, "XEngine.Runtime.UI.LayoutElement");
        Type canvasScalerType = RequireType(packageAssembly, "XEngine.Runtime.UI.CanvasScaler");
        Type safeAreaFitterType = RequireType(packageAssembly, "XEngine.Runtime.UI.SafeAreaFitter");
        Type verticalType = RequireType(packageAssembly, "XEngine.Runtime.UI.VerticalLayoutGroup");
        Type gridType = RequireType(packageAssembly, "XEngine.Runtime.UI.GridLayoutGroup");
        Type fitterType = RequireType(packageAssembly, "XEngine.Runtime.UI.ContentSizeFitter");
        Type buttonType = RequireType(packageAssembly, "XEngine.Runtime.UI.Button");
        Type toggleType = RequireType(packageAssembly, "XEngine.Runtime.UI.Toggle");
        Type toggleGroupType = RequireType(packageAssembly, "XEngine.Runtime.UI.ToggleGroup");
        Type dropdownType = RequireType(packageAssembly, "XEngine.Runtime.UI.Dropdown");
        Type imageType = RequireType(packageAssembly, "XEngine.Runtime.UI.Image");
        Type textType = RequireType(packageAssembly, "XEngine.Runtime.UI.Text");
        Type sliderType = RequireType(packageAssembly, "XEngine.Runtime.UI.Slider");
        Type scrollbarType = RequireType(packageAssembly, "XEngine.Runtime.UI.Scrollbar");
        Type scrollRectType = RequireType(packageAssembly, "XEngine.Runtime.UI.ScrollRect");
        Type inputFieldType = RequireType(packageAssembly, "XEngine.Runtime.UI.InputField");
        Type eventTriggerType = RequireType(packageAssembly, "XEngine.Runtime.UI.EventTrigger");
        Type baseInputModuleType = RequireType(packageAssembly, "XEngine.Runtime.UI.BaseInputModule");
        Type pointerInputModuleType = RequireType(packageAssembly, "XEngine.Runtime.UI.PointerInputModule");
        Type standaloneInputModuleType = RequireType(packageAssembly, "XEngine.Runtime.UI.StandaloneInputModule");

        Assert.True(typeof(ILayoutElementHost).IsAssignableFrom(layoutElementType));
        Assert.True(typeof(ILayoutGroupHost).IsAssignableFrom(verticalType));
        Assert.True(typeof(ICanvasScaler).IsAssignableFrom(canvasScalerType));
        Assert.True(typeof(MonoBehaviour).IsAssignableFrom(canvasScalerType));
        Assert.True(typeof(Selectable).IsAssignableFrom(buttonType));
        Assert.True(typeof(Selectable).IsAssignableFrom(sliderType));
        Assert.True(typeof(Selectable).IsAssignableFrom(scrollbarType));
        Assert.True(typeof(UIBehaviour).IsAssignableFrom(scrollRectType));
        Assert.NotEqual(typeof(UIScrollRect), scrollRectType.BaseType);
        Assert.True(typeof(Selectable).IsAssignableFrom(inputFieldType));
        Assert.NotEqual(typeof(UIInputField), inputFieldType.BaseType);
        Assert.NotNull(inputFieldType.GetField("_text", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(inputFieldType.GetField("_textComponent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(inputFieldType.GetField("_onValueChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.True(typeof(MonoBehaviour).IsAssignableFrom(baseInputModuleType));
        Assert.True(baseInputModuleType.IsAbstract);
        Assert.True(baseInputModuleType.IsAssignableFrom(pointerInputModuleType));
        Assert.True(pointerInputModuleType.IsAbstract);
        Assert.True(pointerInputModuleType.IsAssignableFrom(standaloneInputModuleType));
        Assert.True(typeof(IEventSystemInputModule).IsAssignableFrom(standaloneInputModuleType));
        Assert.True(typeof(IDropHandler).IsAssignableFrom(eventTriggerType));
        Assert.True(typeof(ISelectHandler).IsAssignableFrom(eventTriggerType));
        Assert.True(typeof(IDeselectHandler).IsAssignableFrom(eventTriggerType));
        Assert.True(typeof(IMoveHandler).IsAssignableFrom(eventTriggerType));
        Assert.True(typeof(ISubmitHandler).IsAssignableFrom(eventTriggerType));
        Assert.True(typeof(ICancelHandler).IsAssignableFrom(eventTriggerType));
        Assert.NotEqual(typeof(UIButton), buttonType.BaseType);
        Assert.True(typeof(Selectable).IsAssignableFrom(dropdownType));
        Assert.NotEqual(typeof(UIDropdown), dropdownType.BaseType);
        Assert.NotNull(dropdownType.GetField("_options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(dropdownType.GetField("_optionsRoot", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(dropdownType.GetField("_onValueChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.NotEqual(typeof(UISlider), sliderType.BaseType);
        Assert.NotEqual(typeof(UIScrollbar), scrollbarType.BaseType);
        Assert.NotNull(eventTriggerType.GetField("Drop"));
        Assert.NotNull(eventTriggerType.GetField("Select"));
        Assert.NotNull(eventTriggerType.GetField("Deselect"));
        Assert.NotNull(eventTriggerType.GetField("Move"));
        Assert.NotNull(eventTriggerType.GetField("Submit"));
        Assert.NotNull(eventTriggerType.GetField("Cancel"));
        using var eventTriggerObject = new GameObject("PackageEventTrigger");
        MonoBehaviour eventTrigger = AddPackageComponent(eventTriggerObject, eventTriggerType);
        var eventTriggerProbe = eventTriggerObject.AddComponent<EventTriggerProbe>();
        string[] triggerActions = ["Drop", "Select", "Deselect", "Move", "Submit", "Cancel"];
        for (int i = 0; i < triggerActions.Length; i++)
        {
            System.Reflection.FieldInfo triggerField = eventTriggerType.GetField(triggerActions[i])!;
            XEngineAction action = Assert.IsType<XEngineAction>(triggerField.GetValue(eventTrigger));
            action.Calls.Add(new XEngineCall { Target = eventTriggerProbe, Member = nameof(EventTriggerProbe.Record) });
        }
        Invoke(eventTrigger, "OnDrop", new PointerEventData());
        Invoke(eventTrigger, "OnSelect");
        Invoke(eventTrigger, "OnDeselect");
        Invoke(eventTrigger, "OnMove", MoveDirection.Right);
        Invoke(eventTrigger, "OnSubmit");
        Invoke(eventTrigger, "OnCancel");
        Assert.Equal(6, eventTriggerProbe.Count);
        Assert.Equal(-1f, GetPackageProperty<float>(AddPackageComponent(new GameObject("Defaults"), layoutElementType), "PreferredWidth"));

        using var scalerObject = new GameObject("CanvasScaler");
        GameCanvas scalerCanvas = scalerObject.AddComponent<GameCanvas>();
        MonoBehaviour scaler = AddPackageComponent(scalerObject, canvasScalerType);
        Type scalerModeType = RequireNestedType(canvasScalerType, "ScaleMode");
        Type scalerMatchType = RequireNestedType(canvasScalerType, "ScreenMatchMode");
        SetPackageProperty(scaler, "ReferenceResolution", new Float2(100f, 100f));
        SetPackageProperty(scaler, "UIScaleMode", Enum.Parse(scalerModeType, "ScaleWithScreenSize"));
        SetPackageProperty(scaler, "MatchWidthOrHeight", 0f);
        Assert.Equal(2f, InvokeResult<float>(scaler, "ComputeScaleFactor", new Float2(200f, 300f), 1f), 4);
        SetPackageProperty(scaler, "MatchWidthOrHeight", 1f);
        Assert.Equal(3f, InvokeResult<float>(scaler, "ComputeScaleFactor", new Float2(200f, 300f), 1f), 4);
        SetPackageProperty(scaler, "ScreenMatch", Enum.Parse(scalerMatchType, "Expand"));
        Assert.Equal(2f, InvokeResult<float>(scaler, "ComputeScaleFactor", new Float2(200f, 300f), 1f), 4);
        SetPackageProperty(scaler, "ScreenMatch", Enum.Parse(scalerMatchType, "Shrink"));
        Assert.Equal(3f, InvokeResult<float>(scaler, "ComputeScaleFactor", new Float2(200f, 300f), 1f), 4);
        float? previousDpi = CanvasPhysicalScale.DpiOverride;
        try
        {
            CanvasPhysicalScale.DpiOverride = 192f;
            SetPackageProperty(scaler, "UIScaleMode", Enum.Parse(scalerModeType, "ConstantPhysicalSize"));
            SetPackageProperty(scaler, "DefaultSpriteDpi", 96f);
            Assert.Equal(2f, InvokeResult<float>(scaler, "ComputeScaleFactor", new Float2(800f, 450f), 1f), 4);
            CanvasPhysicalScale.DpiOverride = 144f;
            Assert.Equal(1.5f, InvokeResult<float>(scaler, "ComputeScaleFactor", new Float2(800f, 450f), 1f), 4);
        }
        finally
        {
            CanvasPhysicalScale.DpiOverride = previousDpi;
            SetPackageProperty(scaler, "UIScaleMode", Enum.Parse(scalerModeType, "ScaleWithScreenSize"));
        }

        using var scalerScene = new Scene();
        Float2? previousScalerSize = GameCanvas.ScreenSizeOverride;
        try
        {
            GameCanvas.ScreenSizeOverride = new Float2(200f, 300f);
            SetPackageProperty(scaler, "MatchWidthOrHeight", 0f);
            SetPackageProperty(scaler, "ScreenMatch", Enum.Parse(scalerMatchType, "MatchWidthOrHeight"));
            scalerScene.Add(scalerObject);
            scalerScene.Enable();
            scalerCanvas.RebuildIfDirty();
            Assert.Equal(2f, scalerCanvas.ScaleFactor, 4);
            Assert.Equal(new Float2(100f, 150f), scalerCanvas.RootRect.Size);
        }
        finally
        {
            if (scalerScene.IsActive) scalerScene.Disable();
            GameCanvas.ScreenSizeOverride = previousScalerSize;
        }

        using var safeScene = new Scene();
        using var safeCanvasObject = new GameObject("SafeCanvas");
        using var safePanelObject = new GameObject("SafePanel");
        Float2? previousSafeSize = GameCanvas.ScreenSizeOverride;
        WindowSafeAreaInsets? previousInsets = SafeAreaLayout.InsetsOverride;
        try
        {
            GameCanvas.ScreenSizeOverride = new Float2(800f, 450f);
            SafeAreaLayout.InsetsOverride = new WindowSafeAreaInsets(40, 80, 40, 20);
            GameCanvas safeCanvas = safeCanvasObject.AddComponent<GameCanvas>();
            safePanelObject.SetParent(safeCanvasObject, worldPositionStays: false);
            RectTransform safeRt = safePanelObject.EnsureRectTransform();
            safeRt.Pivot = new Float2(0.5f, 0.5f);
            AddPackageComponent(safePanelObject, safeAreaFitterType);
            safeScene.Add(safeCanvasObject);
            safeScene.Enable();
            safeCanvas.RebuildIfDirty();
            Assert.Equal(Float2.Zero, safeRt.AnchorMin);
            Assert.Equal(Float2.One, safeRt.AnchorMax);
            Assert.Equal(new Float2(-80f, -100f), safeRt.SizeDelta);
            Assert.Equal(new Float2(720f, 350f), safeRt.ComputedRect.Size);
        }
        finally
        {
            if (safeScene.IsActive) safeScene.Disable();
            GameCanvas.ScreenSizeOverride = previousSafeSize;
            SafeAreaLayout.InsetsOverride = previousInsets;
        }

        using var scrollRoot = new GameObject("ScrollRoot");
        using var scrollContent = new GameObject("ScrollContent");
        scrollContent.SetParent(scrollRoot);
        RectTransform rootRect = scrollRoot.EnsureRectTransform();
        RectTransform contentRect = scrollContent.EnsureRectTransform();
        rootRect.ComputedRect = new Rect(0f, 0f, 100f, 100f);
        contentRect.ComputedRect = new Rect(0f, 0f, 200f, 200f);
        MonoBehaviour scrollRect = AddPackageComponent(scrollRoot, scrollRectType);
        SetPackageProperty(scrollRect, "Content", contentRect);
        SetPackageProperty(scrollRect, "Movement", Enum.Parse(RequireNestedType(scrollRectType, "MovementType"), "Clamped"));
        var wheel = new PointerEventData { ScrollDelta = -1f };
        Invoke(scrollRect, "OnScroll", wheel);
        Assert.True(wheel.Used);
        Assert.Equal(-30f, contentRect.AnchoredPosition.Y);

        contentRect.AnchoredPosition = Float2.Zero;
        var edgeWheel = new PointerEventData { ScrollDelta = 1f };
        Invoke(scrollRect, "OnScroll", edgeWheel);
        Assert.False(edgeWheel.Used);
        Assert.Equal(Float2.Zero, contentRect.AnchoredPosition);

        using var buttonObject = new GameObject("PackageButton");
        MonoBehaviour button = AddPackageComponent(buttonObject, buttonType);
        int buttonClicks = 0;
        System.Reflection.EventInfo buttonClickEvent = buttonType.GetEvent("OnClick")!;
        Assert.NotNull(buttonClickEvent);
        Action buttonClickHandler = () => buttonClicks++;
        buttonClickEvent.AddEventHandler(button, buttonClickHandler);
        try
        {
            var rightClick = new PointerEventData { Button = MouseButton.Right };
            Invoke(button, "OnPointerClick", rightClick);
            Assert.False(rightClick.Used);
            Assert.Equal(0, buttonClicks);

            var leftClick = new PointerEventData { Button = MouseButton.Left };
            Invoke(button, "OnPointerClick", leftClick);
            Assert.True(leftClick.Used);
            Assert.Equal(1, buttonClicks);
            Invoke(button, "OnSubmit");
            Assert.Equal(2, buttonClicks);
        }
        finally
        {
            buttonClickEvent.RemoveEventHandler(button, buttonClickHandler);
        }

        using var spriteSwapObject = new GameObject("PackageSpriteSwapButton");
        MonoBehaviour packageImage = AddPackageComponent(spriteSwapObject, imageType);
        MonoBehaviour spriteSwapButton = AddPackageComponent(spriteSwapObject, buttonType);
        var normalSprite = new Sprite();
        var highlightedSprite = new Sprite();
        SetPackageProperty(packageImage, "Sprite", new AssetRef<Sprite>(normalSprite));
        var selectable = Assert.IsAssignableFrom<Selectable>(spriteSwapButton);
        selectable.TargetGraphicComponent = packageImage;
        selectable.SpriteState = new Selectable.SpriteSwapState
        {
            HighlightedSprite = new AssetRef<Sprite>(highlightedSprite),
        };
        selectable.TransitionMode = Selectable.Transition.SpriteSwap;
        selectable.OnPointerEnter(new PointerEventData());
        Assert.Same(highlightedSprite, GetPackageProperty<AssetRef<Sprite>>(packageImage, "Sprite").Res);
        selectable.OnPointerExit(new PointerEventData());
        Assert.Same(normalSprite, GetPackageProperty<AssetRef<Sprite>>(packageImage, "Sprite").Res);

        using var toggleGroupObject = new GameObject("PackageToggleGroup");
        using var firstToggleObject = new GameObject("FirstPackageToggle");
        using var secondToggleObject = new GameObject("SecondPackageToggle");
        MonoBehaviour toggleGroup = AddPackageComponent(toggleGroupObject, toggleGroupType);
        MonoBehaviour firstToggle = AddPackageComponent(firstToggleObject, toggleType);
        MonoBehaviour secondToggle = AddPackageComponent(secondToggleObject, toggleType);
        SetPackageProperty(firstToggle, "Group", toggleGroup);
        SetPackageProperty(secondToggle, "Group", toggleGroup);
        SetPackageProperty(firstToggle, "IsOn", true);
        Assert.True(GetPackageProperty<bool>(firstToggle, "IsOn"));
        Assert.False(GetPackageProperty<bool>(secondToggle, "IsOn"));
        SetPackageProperty(secondToggle, "IsOn", true);
        Assert.False(GetPackageProperty<bool>(firstToggle, "IsOn"));
        Assert.True(GetPackageProperty<bool>(secondToggle, "IsOn"));

        var lockedToggleClick = new PointerEventData { Button = MouseButton.Left };
        Invoke(secondToggle, "OnPointerClick", lockedToggleClick);
        Assert.True(lockedToggleClick.Used);
        Assert.True(GetPackageProperty<bool>(secondToggle, "IsOn"));
        SetPackageProperty(toggleGroup, "AllowSwitchOff", true);
        var switchOffClick = new PointerEventData { Button = MouseButton.Left };
        Invoke(secondToggle, "OnPointerClick", switchOffClick);
        Assert.True(switchOffClick.Used);
        Assert.False(GetPackageProperty<bool>(secondToggle, "IsOn"));

        using var sliderObject = new GameObject("PackageSlider");
        using var fillObject = new GameObject("PackageSliderFill");
        using var handleObject = new GameObject("PackageSliderHandle");
        fillObject.SetParent(sliderObject, worldPositionStays: false);
        handleObject.SetParent(sliderObject, worldPositionStays: false);
        RectTransform sliderTransform = sliderObject.EnsureRectTransform();
        RectTransform fillTransform = fillObject.EnsureRectTransform();
        RectTransform handleTransform = handleObject.EnsureRectTransform();
        sliderTransform.ComputedRect = new Rect(0f, 0f, 200f, 20f);
        MonoBehaviour slider = AddPackageComponent(sliderObject, sliderType);
        SetPackageProperty(slider, "FillRect", fillTransform);
        SetPackageProperty(slider, "HandleRect", handleTransform);
        SetPackageProperty(slider, "MinValue", 2f);
        SetPackageProperty(slider, "MaxValue", 10f);
        SetPackageProperty(slider, "WholeNumbers", true);
        var sliderPointerDown = new PointerEventData { Button = MouseButton.Left, DesignPosition = new Float2(150f, 10f) };
        Invoke(slider, "OnPointerDown", sliderPointerDown);
        Assert.True(sliderPointerDown.Used);
        Assert.Equal(8f, GetPackageProperty<float>(slider, "Value"));
        Assert.Equal(0.75f, GetPackageProperty<float>(slider, "NormalizedValue"));
        Assert.Equal(new Float2(0.75f, 1f), fillTransform.AnchorMax);
        Assert.Equal(new Float2(0.75f, 0f), handleTransform.AnchorMin);

        var sliderDrag = new PointerEventData { Button = MouseButton.Left, DesignPosition = new Float2(188f, 10f) };
        Invoke(slider, "OnDrag", sliderDrag);
        Assert.True(sliderDrag.Used);
        Assert.Equal(10f, GetPackageProperty<float>(slider, "Value"));
        Invoke(slider, "OnMove", MoveDirection.Left);
        Assert.Equal(9f, GetPackageProperty<float>(slider, "Value"));
        SetPackageProperty(slider, "Direction", Enum.Parse(RequireNestedType(sliderType, "SliderDirection"), "RightToLeft"));
        Invoke(slider, "OnMove", MoveDirection.Left);
        Assert.Equal(10f, GetPackageProperty<float>(slider, "Value"));
        var ignoredSliderPointerDown = new PointerEventData { Button = MouseButton.Right, DesignPosition = new Float2(0f, 10f) };
        Invoke(slider, "OnPointerDown", ignoredSliderPointerDown);
        Assert.False(ignoredSliderPointerDown.Used);
        Assert.Equal(10f, GetPackageProperty<float>(slider, "Value"));

        using var scrollbarObject = new GameObject("PackageScrollbar");
        MonoBehaviour scrollbar = AddPackageComponent(scrollbarObject, scrollbarType);
        SetPackageProperty(scrollbar, "Value", 0.5f);
        Invoke(scrollbar, "OnMove", MoveDirection.Right);
        Assert.Equal(0.6f, GetPackageProperty<float>(scrollbar, "Value"), 3);
        SetPackageProperty(scrollbar, "Direction", Enum.Parse(RequireNestedType(scrollbarType, "ScrollbarDirection"), "RightToLeft"));
        Invoke(scrollbar, "OnMove", MoveDirection.Right);
        Assert.Equal(0.5f, GetPackageProperty<float>(scrollbar, "Value"), 3);

        using var parentScrollRoot = new GameObject("ParentScrollRoot");
        using var parentScrollContent = new GameObject("ParentScrollContent");
        using var childScrollRoot = new GameObject("ChildScrollRoot");
        using var childScrollContent = new GameObject("ChildScrollContent");
        parentScrollContent.SetParent(parentScrollRoot, worldPositionStays: false);
        childScrollRoot.SetParent(parentScrollContent, worldPositionStays: false);
        childScrollContent.SetParent(childScrollRoot, worldPositionStays: false);
        RectTransform parentScrollRootTransform = parentScrollRoot.EnsureRectTransform();
        RectTransform parentScrollContentTransform = parentScrollContent.EnsureRectTransform();
        RectTransform childScrollRootTransform = childScrollRoot.EnsureRectTransform();
        RectTransform childScrollContentTransform = childScrollContent.EnsureRectTransform();
        parentScrollRootTransform.ComputedRect = new Rect(0f, 0f, 200f, 200f);
        parentScrollContentTransform.ComputedRect = new Rect(0f, 0f, 200f, 400f);
        childScrollRootTransform.ComputedRect = new Rect(0f, 0f, 100f, 100f);
        childScrollContentTransform.ComputedRect = new Rect(0f, 0f, 100f, 200f);
        MonoBehaviour parentScroll = AddPackageComponent(parentScrollRoot, scrollRectType);
        MonoBehaviour childScroll = AddPackageComponent(childScrollRoot, scrollRectType);
        SetPackageProperty(parentScroll, "Content", parentScrollContentTransform);
        SetPackageProperty(parentScroll, "Horizontal", false);
        SetPackageProperty(parentScroll, "Movement", Enum.Parse(RequireNestedType(scrollRectType, "MovementType"), "Clamped"));
        SetPackageProperty(childScroll, "Content", childScrollContentTransform);
        SetPackageProperty(childScroll, "Horizontal", false);
        SetPackageProperty(childScroll, "Movement", Enum.Parse(RequireNestedType(scrollRectType, "MovementType"), "Clamped"));
        var nestedDrag = new PointerEventData { Button = MouseButton.Left, DesignPosition = Float2.Zero };
        BubblePointerEvent<IBeginDragHandler>(childScrollRoot, nestedDrag, static (handler, e) => handler.OnBeginDrag(e));
        nestedDrag.DesignPosition = new Float2(0f, -25f);
        BubblePointerEvent<IDragHandler>(childScrollRoot, nestedDrag, static (handler, e) => handler.OnDrag(e));
        Assert.True(nestedDrag.Used);
        Assert.Equal(-25f, childScrollContentTransform.AnchoredPosition.Y);
        Assert.Equal(0f, parentScrollContentTransform.AnchoredPosition.Y);
        childScrollContentTransform.ComputedRect = new Rect(0f, -25f, 100f, 175f);
        nestedDrag.DesignPosition = new Float2(0f, -250f);
        BubblePointerEvent<IDragHandler>(childScrollRoot, nestedDrag, static (handler, e) => handler.OnDrag(e));
        Assert.True(nestedDrag.Used);
        Assert.Equal(-100f, childScrollContentTransform.AnchoredPosition.Y);
        Assert.Equal(0f, parentScrollContentTransform.AnchoredPosition.Y);
        childScrollContentTransform.ComputedRect = new Rect(0f, -100f, 100f, 100f);
        nestedDrag.Used = false;
        nestedDrag.DesignPosition = new Float2(0f, -350f);
        BubblePointerEvent<IDragHandler>(childScrollRoot, nestedDrag, static (handler, e) => handler.OnDrag(e));
        Assert.True(nestedDrag.Used);
        Assert.Equal(-100f, childScrollContentTransform.AnchoredPosition.Y);
        Assert.Equal(-200f, parentScrollContentTransform.AnchoredPosition.Y);

        using var verticalRoot = new GameObject("VerticalRoot");
        using var first = new GameObject("First");
        using var ignored = new GameObject("Ignored");
        using var second = new GameObject("Second");
        first.SetParent(verticalRoot);
        ignored.SetParent(verticalRoot);
        second.SetParent(verticalRoot);

        MonoBehaviour vertical = AddPackageComponent(verticalRoot, verticalType);
        SetPackageProperty(vertical, "Spacing", 5f);
        MonoBehaviour firstElement = AddPackageComponent(first, layoutElementType);
        MonoBehaviour ignoredElement = AddPackageComponent(ignored, layoutElementType);
        MonoBehaviour secondElement = AddPackageComponent(second, layoutElementType);
        SetPackageProperty(firstElement, "PreferredWidth", 40f);
        SetPackageProperty(firstElement, "PreferredHeight", 20f);
        SetPackageProperty(ignoredElement, "IgnoreLayout", true);
        SetPackageProperty(secondElement, "PreferredWidth", 50f);
        SetPackageProperty(secondElement, "PreferredHeight", 30f);

        Arrange(vertical, new Rect(0f, 0f, 200f, 100f));
        Assert.Equal(100f, first.RectTransform!.ComputedRect.Max.Y);
        Assert.Equal(80f, first.RectTransform.ComputedRect.Min.Y);
        Assert.Equal(75f, second.RectTransform!.ComputedRect.Max.Y);
        Assert.Equal(45f, second.RectTransform.ComputedRect.Min.Y);

        // A direct child of a layout group is still allowed to run its self-controller. Its
        // resulting authored size becomes an input to the host's bounded convergence pass.
        using var fitterCanvasScene = new Scene();
        using var fitterCanvasObject = new GameObject("FitterCanvas");
        using var fitterGroupObject = new GameObject("FitterGroup");
        using var fittedChildObject = new GameObject("FittedChild");
        GameCanvas fitterCanvas = fitterCanvasObject.AddComponent<GameCanvas>();
        fitterGroupObject.SetParent(fitterCanvasObject, worldPositionStays: false);
        fittedChildObject.SetParent(fitterGroupObject, worldPositionStays: false);
        fitterGroupObject.EnsureRectTransform();
        fittedChildObject.EnsureRectTransform();
        AddPackageComponent(fitterGroupObject, verticalType);
        MonoBehaviour fittedChildElement = AddPackageComponent(fittedChildObject, layoutElementType);
        SetPackageProperty(fittedChildElement, "PreferredWidth", 70f);
        SetPackageProperty(fittedChildElement, "PreferredHeight", 20f);
        MonoBehaviour groupedFitter = AddPackageComponent(fittedChildObject, fitterType);
        Type groupedFitModeType = RequireNestedType(fitterType, "FitMode");
        object groupedPreferredSize = Enum.Parse(groupedFitModeType, "PreferredSize");
        SetPackageProperty(groupedFitter, "HorizontalFit", groupedPreferredSize);
        SetPackageProperty(groupedFitter, "VerticalFit", groupedPreferredSize);
        fitterCanvasScene.Add(fitterCanvasObject);
        fitterCanvasScene.Enable();
        fitterCanvas.RebuildIfDirty();
        Assert.Equal(new Float2(70f, 20f), fittedChildObject.RectTransform!.SizeDelta);

        ILayoutGroupHost verticalHost = Assert.IsAssignableFrom<ILayoutGroupHost>(vertical);
        verticalHost.Arrange(new Rect(0f, 0f, 200f, 100f));
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        verticalHost.Arrange(new Rect(0f, 0f, 200f, 100f));
        long layoutAllocation = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        Assert.Equal(0, layoutAllocation);

        using var fitterRoot = new GameObject("FitterRoot");
        using var fitterFirst = new GameObject("FitterFirst");
        using var fitterSecond = new GameObject("FitterSecond");
        fitterFirst.SetParent(fitterRoot);
        fitterSecond.SetParent(fitterRoot);
        MonoBehaviour fitterLayout = AddPackageComponent(fitterRoot, verticalType);
        SetPackageProperty(fitterLayout, "Spacing", 5f);
        MonoBehaviour fitterFirstElement = AddPackageComponent(fitterFirst, layoutElementType);
        MonoBehaviour fitterSecondElement = AddPackageComponent(fitterSecond, layoutElementType);
        SetPackageProperty(fitterFirstElement, "PreferredWidth", 40f);
        SetPackageProperty(fitterFirstElement, "PreferredHeight", 20f);
        SetPackageProperty(fitterSecondElement, "PreferredWidth", 50f);
        SetPackageProperty(fitterSecondElement, "PreferredHeight", 30f);
        Assert.Equal(20f, GetPackageProperty<float>(fitterFirstElement, "PreferredHeight"));
        Assert.Contains(typeof(ILayoutElementHost), fitterFirstElement.GetType().GetInterfaces());
        Assert.Equal(50f, GetPackageProperty<float>(fitterLayout, "PreferredWidth"));
        Assert.Equal(55f, GetPackageProperty<float>(fitterLayout, "PreferredHeight"));
        Assert.True(fitterLayout is ILayoutElementHost);
        using var fitObject = new GameObject("FitObject");
        MonoBehaviour fitElement = AddPackageComponent(fitObject, layoutElementType);
        SetPackageProperty(fitElement, "PreferredWidth", 70f);
        SetPackageProperty(fitElement, "PreferredHeight", 80f);
        MonoBehaviour fitter = AddPackageComponent(fitObject, fitterType);
        Type fitModeType = RequireNestedType(fitterType, "FitMode");
        object preferredSize = Enum.Parse(fitModeType, "PreferredSize");
        SetPackageProperty(fitter, "HorizontalFit", preferredSize);
        SetPackageProperty(fitter, "VerticalFit", preferredSize);
        Invoke(fitter, "ApplyFit");
        Assert.Equal(new Float2(70f, 80f), fitObject.RectTransform!.SizeDelta);

        using var gridRoot = new GameObject("GridRoot");
        using var gridA = new GameObject("GridA");
        using var gridB = new GameObject("GridB");
        using var gridC = new GameObject("GridC");
        gridA.SetParent(gridRoot);
        gridB.SetParent(gridRoot);
        gridC.SetParent(gridRoot);
        MonoBehaviour grid = AddPackageComponent(gridRoot, gridType);
        AddPackageComponent(gridA, layoutElementType);
        AddPackageComponent(gridB, layoutElementType);
        AddPackageComponent(gridC, layoutElementType);
        SetPackageProperty(grid, "CellSize", new Float2(30f, 20f));
        SetPackageProperty(grid, "Spacing", new Float2(5f, 7f));
        Type constraintType = RequireNestedType(gridType, "Constraint");
        SetPackageProperty(grid, "GridConstraint", Enum.Parse(constraintType, "FixedColumnCount"));
        SetPackageProperty(grid, "ConstraintCount", 2);
        Arrange(grid, new Rect(0f, 0f, 100f, 100f));
        Assert.Equal(new Rect(0f, 80f, 30f, 100f), gridA.RectTransform!.ComputedRect);
        Assert.Equal(new Rect(35f, 80f, 65f, 100f), gridB.RectTransform!.ComputedRect);
        Assert.Equal(new Rect(0f, 53f, 30f, 73f), gridC.RectTransform!.ComputedRect);

        using var inputScene = new Scene();
        using var inputSystemObject = new GameObject("InputEventSystem");
        using var inputObject = new GameObject("PackageInputField");
        EventSystem inputSystem = inputSystemObject.AddComponent<EventSystem>();
        inputObject.EnsureRectTransform();
        MonoBehaviour inputField = AddPackageComponent(inputObject, inputFieldType);
        inputScene.Add(inputSystemObject);
        inputScene.Add(inputObject);
        var inputHandler = new AndroidInputHandler(softKeyboard: static _ => { });
        Input.PushHandler(inputHandler);
        bool previousPlaying = Application.IsPlaying;
        try
        {
            Application.IsPlaying = true;
            inputScene.Enable();
            inputSystem.SetSelected(inputObject);

            SetPackageProperty(inputField, "Text", "A\U0001F600e\u0301");
            SetPackageProperty(inputField, "CharacterLimit", 2);
            Assert.Equal("A\U0001F600", GetPackageProperty<string>(inputField, "Text"));

            SetPackageProperty(inputField, "CharacterLimit", 0);
            SetPackageProperty(inputField, "Text", "A\U0001F600e\u0301");
            Invoke(inputField, "SetCaret", 3, false);
            Invoke(inputField, "Backspace");
            Assert.Equal("Ae\u0301", GetPackageProperty<string>(inputField, "Text"));

            SetPackageProperty(inputField, "Text", "a\U0001F642b");
            Invoke(inputField, "SetCaret", 1, false);
            Invoke(inputField, "MoveRight", false);
            Assert.Equal(3, GetPackageProperty<int>(inputField, "CaretPosition"));
            Invoke(inputField, "MoveLeft", false);
            Assert.Equal(1, GetPackageProperty<int>(inputField, "CaretPosition"));

            SetPackageProperty(inputField, "Text", "old");
            inputHandler.SetTextInputState("old", 3, 0);
            inputHandler.SetComposingText("世", 1);
            Assert.Equal("old", GetPackageProperty<string>(inputField, "Text"));
            Assert.Equal("世", GetPackageProperty<string>(inputField, "Composition"));
            inputHandler.CommitText("界");
            Assert.Equal("old界", GetPackageProperty<string>(inputField, "Text"));
            Assert.False(GetPackageProperty<bool>(inputField, "IsComposing"));

            SetPackageProperty(inputField, "ReadOnly", true);
            inputHandler.CommitText("x");
            Assert.Equal("old界", GetPackageProperty<string>(inputField, "Text"));
            Assert.Equal("old界", inputHandler.TextInputState.Text);
        }
        finally
        {
            if (inputScene.IsActive) inputScene.Disable();
            Input.PopHandler();
            Application.IsPlaying = previousPlaying;
        }

        using var dropdownScene = new Scene();
        using var dropdownSystemObject = new GameObject("DropdownEventSystem");
        using var dropdownCanvasObject = new GameObject("DropdownCanvas");
        using var dropdownObject = new GameObject("PackageDropdown");
        using var overlayObject = new GameObject("DropdownOverlay");
        using var popupObject = new GameObject("DropdownPopup");
        using var templateObject = new GameObject("DropdownItemTemplate");
        using var templateLabelObject = new GameObject("DropdownItemLabel");
        EventSystem dropdownSystem = dropdownSystemObject.AddComponent<EventSystem>();
        dropdownCanvasObject.AddComponent<GameCanvas>();
        dropdownObject.EnsureRectTransform();
        overlayObject.EnsureRectTransform();
        popupObject.EnsureRectTransform();
        templateObject.EnsureRectTransform();
        templateLabelObject.EnsureRectTransform();
        dropdownObject.SetParent(dropdownCanvasObject, worldPositionStays: false);
        overlayObject.SetParent(dropdownCanvasObject, worldPositionStays: false);
        popupObject.SetParent(dropdownCanvasObject, worldPositionStays: false);
        templateObject.SetParent(popupObject, worldPositionStays: false);
        templateLabelObject.SetParent(templateObject, worldPositionStays: false);
        AddPackageComponent(templateObject, imageType);
        AddPackageComponent(templateObject, buttonType);
        AddPackageComponent(templateLabelObject, textType);
        MonoBehaviour dropdown = AddPackageComponent(dropdownObject, dropdownType);
        SetPackageProperty(dropdown, "OptionsRoot", popupObject.RectTransform!);
        SetPackageProperty(dropdown, "OverlayRoot", overlayObject.RectTransform!);
        SetPackageProperty(dropdown, "ItemTemplate", templateObject.RectTransform!);
        GetPackageProperty<List<string>>(dropdown, "Options").AddRange(["Alpha", "Beta"]);
        dropdownScene.Add(dropdownSystemObject);
        dropdownScene.Add(dropdownCanvasObject);
        previousPlaying = Application.IsPlaying;
        try
        {
            Application.IsPlaying = true;
            dropdownScene.Enable();
            dropdownSystem.SetSelected(dropdownObject);

            Assert.True(InvokeResult<bool>(dropdown, "Show"));
            Assert.True(GetPackageProperty<bool>(dropdown, "IsOpen"));
            Assert.True(overlayObject.Enabled);
            Assert.False(templateObject.Enabled);
            Assert.Equal(3, popupObject.Children.Count);
            Assert.NotSame(dropdownObject, dropdownSystem.Selected);

            GameObject? secondItem = null;
            foreach (GameObject child in popupObject.Children)
            {
                if (child.Name == "Dropdown Item 1")
                {
                    secondItem = child;
                    break;
                }
            }
            Assert.NotNull(secondItem);
            MonoBehaviour secondButton = Assert.IsAssignableFrom<MonoBehaviour>(secondItem!.GetComponent(buttonType));
            Invoke(secondButton, "OnPointerClick", new PointerEventData { Button = MouseButton.Left });
            Assert.Equal(1, GetPackageProperty<int>(dropdown, "Value"));
            Assert.False(GetPackageProperty<bool>(dropdown, "IsOpen"));
            Assert.Same(dropdownObject, dropdownSystem.Selected);

            Assert.True(InvokeResult<bool>(dropdown, "Show"));
            Assert.Equal(3, popupObject.Children.Count);
            Invoke(dropdown, "Hide");
            Assert.False(overlayObject.Enabled);
            Assert.False(popupObject.Enabled);

            // Popup rows are a separately-parented layer, so Cancel is dispatched from the focused
            // row rather than from the Dropdown hierarchy. The package-owned row handler must close
            // the popup and EventSystem must preserve the focus restored by Dropdown.Close().
            Assert.True(InvokeResult<bool>(dropdown, "Show"));
            GameObject? selectedItem = null;
            foreach (GameObject child in popupObject.Children)
            {
                if (child.Name == "Dropdown Item 1")
                {
                    selectedItem = child;
                    break;
                }
            }
            Assert.NotNull(selectedItem);
            Assert.Same(selectedItem, dropdownSystem.Selected);
            var dropdownInput = new AndroidInputHandler();
            Input.PushHandler(dropdownInput);
            try
            {
                Assert.True(dropdownInput.HandleKey(4, pressed: true)); // Android KEYCODE_BACK -> Escape.
                dropdownSystem.ProcessInput(EventSystemInputMode.Keyboard, 0f);
                Assert.False(GetPackageProperty<bool>(dropdown, "IsOpen"));
                Assert.False(overlayObject.Enabled);
                Assert.False(popupObject.Enabled);
                Assert.Same(dropdownObject, dropdownSystem.Selected);
            }
            finally
            {
                Input.PopHandler();
            }

            using var missingTemplateObject = new GameObject("MissingTemplateDropdown");
            MonoBehaviour missingTemplate = AddPackageComponent(missingTemplateObject, dropdownType);
            SetPackageProperty(missingTemplate, "OptionsRoot", popupObject.RectTransform!);
            Assert.False(InvokeResult<bool>(missingTemplate, "Show"));
            Assert.Contains("MissingTemplateDropdown/Dropdown.ItemTemplate", GetPackageProperty<string>(missingTemplate, "LastTemplateError"));
        }
        finally
        {
            if (dropdownScene.IsActive) dropdownScene.Disable();
            Application.IsPlaying = previousPlaying;
        }
    }

    private static void VerifyPackageGraphicCanvasHost(System.Reflection.Assembly packageAssembly)
    {
        Type imageType = RequireType(packageAssembly, "XEngine.Runtime.UI.Image");
        Type graphicRegistryType = RequireType(packageAssembly, "XEngine.Runtime.UI.GraphicRegistry");
        Type updateRegistryType = RequireType(packageAssembly, "XEngine.Runtime.UI.CanvasUpdateRegistry");
        using var scene = new Scene();
        using var canvasObject = new GameObject("PackageCanvas");
        using var secondCanvasObject = new GameObject("SecondPackageCanvas");
        using var imageObject = new GameObject("PackageImage");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameCanvas secondCanvas = secondCanvasObject.AddComponent<GameCanvas>();
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        imageObject.EnsureRectTransform();
        MonoBehaviour image = AddPackageComponent(imageObject, imageType);

        Float2? previousScreenSize = GameCanvas.ScreenSizeOverride;
        bool previousPlaying = Application.IsPlaying;
        try
        {
            Application.IsPlaying = true;
            GameCanvas.ScreenSizeOverride = new Float2(320f, 180f);
            scene.Add(canvasObject);
            scene.Add(secondCanvasObject);
            scene.Enable();
            canvas.RebuildIfDirty();

            canvas.PixelPerfect = true;
            canvas.ScaleFactor = 1.5f;
            Assert.True(canvas.PixelPerfect);
            Assert.NotNull(imageType.GetMethod("PixelAdjustPoint", [typeof(Float2)]));
            Float2 snapped = (Float2)imageType.GetMethod("PixelAdjustPoint", [typeof(Float2)])!
                .Invoke(image, [new Float2(10.4f, 0f)])!;
            Assert.Equal(CanvasPixelSnap.Snap(10.4f, 1.5f), snapped.X, 4);

            ScaleMode previousScaleMode = canvas.UIScaleMode;
            Float2 previousReference = canvas.ReferenceResolution;
            float previousMatch = canvas.MatchWidthOrHeight;
            canvas.UIScaleMode = ScaleMode.ScaleWithScreenSize;
            canvas.ReferenceResolution = new Float2(1280f, 720f);
            canvas.MatchWidthOrHeight = 0f;
            GameCanvas.ScreenSizeOverride = new Float2(800f, 450f);
            canvas.RebuildIfDirty();
            float landscapeScale = canvas.ScaleFactor;
            GameCanvas.ScreenSizeOverride = new Float2(450f, 800f);
            canvas.RebuildIfDirty();
            Assert.NotEqual(landscapeScale, canvas.ScaleFactor);
            Assert.NotNull(typeof(GameCanvas).GetProperty("RenderCamera"));
            canvas.UIScaleMode = previousScaleMode;
            canvas.ReferenceResolution = previousReference;
            canvas.MatchWidthOrHeight = previousMatch;
            canvas.PixelPerfect = true;
            canvas.ScaleFactor = 1.5f;
            GameCanvas.ScreenSizeOverride = new Float2(320f, 180f);
            canvas.RebuildIfDirty();

            Assert.True(GetStaticPackageProperty<int>(updateRegistryType, "PhaseExecutionCount") >= 6);
            Assert.Equal("MaterialUpdate", GetStaticPackagePropertyValue(updateRegistryType, "LastPhase")?.ToString());

            CanvasRenderer renderer = Assert.Single(canvas.RegisteredRenderers);
            Assert.Same(image, renderer.Owner);
            Assert.Null(imageObject.GetComponent<UIImage>());
            Assert.Equal(0, canvas.Tree.Count);

            var nodes = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);

            CanvasRenderNode node = Assert.Single(nodes);
            Assert.Same(renderer.RenderNode, node);
            Assert.NotNull(node.Mesh);
            Assert.NotNull(node.Material);
            Assert.True(node.IsAttached);

            Mesh stableMesh = node.Mesh!;
            InvokeStatic(updateRegistryType, "Clear");
            SetPackageProperty(image, "Color", new Color(0.25f, 0.5f, 1f, 1f));
            Assert.Equal(1, GetStaticPackageProperty<int>(updateRegistryType, "MaterialCount"));
            Assert.Equal(0, GetStaticPackageProperty<int>(updateRegistryType, "VerticesCount"));
            Assert.Equal(0, GetStaticPackageProperty<int>(updateRegistryType, "LayoutCount"));
            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            Assert.Same(stableMesh, Assert.Single(nodes).Mesh);
            Assert.Equal(0, GetStaticPackageProperty<int>(updateRegistryType, "MaterialCount"));

            InvokeStatic(updateRegistryType, "Clear");
            ((UIBehaviour)image).MarkDirty(UIDirtyFlags.Clip);
            Assert.Equal(1, GetStaticPackageProperty<int>(updateRegistryType, "ClipCount"));
            nodes.Clear();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
            Assert.Equal(0, GetStaticPackageProperty<int>(updateRegistryType, "ClipCount"));

            Assert.Single(GetPackageGraphics(graphicRegistryType, canvas));
            imageObject.SetParent(secondCanvasObject, worldPositionStays: false);
            Assert.Empty(GetPackageGraphics(graphicRegistryType, canvas));
            Assert.Same(image, Assert.Single(GetPackageGraphics(graphicRegistryType, secondCanvas)));
        }
        finally
        {
            if (scene.IsActive) scene.Disable();
            GameCanvas.ScreenSizeOverride = previousScreenSize;
            Application.IsPlaying = previousPlaying;
        }
    }

    private static List<object> GetPackageGraphics(Type graphicRegistryType, GameCanvas canvas)
    {
        var getGraphics = graphicRegistryType.GetMethod("GetGraphics", [typeof(GameCanvas)]);
        Assert.NotNull(getGraphics);
        var graphics = Assert.IsAssignableFrom<System.Collections.IEnumerable>(getGraphics!.Invoke(null, [canvas]));
        var result = new List<object>();
        foreach (object? graphic in graphics)
            if (graphic != null) result.Add(graphic);
        return result;
    }

    private static void VerifyPackageGraphicRaycastOrder(System.Reflection.Assembly packageAssembly)
    {
        Type imageType = RequireType(packageAssembly, "XEngine.Runtime.UI.Image");
        Type graphicRegistryType = RequireType(packageAssembly, "XEngine.Runtime.UI.GraphicRegistry");
        using var scene = new Scene();
        using var canvasObject = new GameObject("PackageRaycastCanvas");
        using var bottomObject = new GameObject("PackageBottom");
        using var topObject = new GameObject("PackageTop");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        bottomObject.SetParent(canvasObject, worldPositionStays: false);
        topObject.SetParent(canvasObject, worldPositionStays: false);
        bottomObject.EnsureRectTransform();
        topObject.EnsureRectTransform();
        MonoBehaviour bottom = AddPackageComponent(bottomObject, imageType);
        MonoBehaviour top = AddPackageComponent(topObject, imageType);

        Float2? previousScreenSize = GameCanvas.ScreenSizeOverride;
        try
        {
            GameCanvas.ScreenSizeOverride = new Float2(320f, 180f);
            scene.Add(canvasObject);
            scene.Enable();
            canvas.RebuildIfDirty();

            List<object> candidates = GetPackageRaycastCandidates(graphicRegistryType, canvas);
            Assert.Equal(2, candidates.Count);
            Assert.Same(top, candidates[0]);
            Assert.Same(bottom, candidates[1]);
            Assert.True(topObject.GetComponent<CanvasRenderer>()!.RaycastSortKey
                > bottomObject.GetComponent<CanvasRenderer>()!.RaycastSortKey);
            Assert.True(UIRaycaster.TryPick(scene, new Float2(160f, 90f), new Float2(320f, 180f),
                out UIRaycaster.Hit hit));
            Assert.Same(topObject, hit.GameObject);
            Assert.NotNull(imageType.GetProperty("RaycastPadding"));
            SetPackageProperty(top, "RaycastPadding", new Float4(40f, 0f, 0f, 0f));
            Assert.Equal(new Float4(40f, 0f, 0f, 0f), GetPackageProperty<Float4>(top, "RaycastPadding"));
        }
        finally
        {
            if (scene.IsActive) scene.Disable();
            GameCanvas.ScreenSizeOverride = previousScreenSize;
        }
    }

    private static void VerifyPackageScenePrefabReload(System.Reflection.Assembly packageAssembly)
    {
        Type imageType = RequireType(packageAssembly, "XEngine.Runtime.UI.Image");
        Type graphicRegistryType = RequireType(packageAssembly, "XEngine.Runtime.UI.GraphicRegistry");
        using var sceneA = new Scene();
        using var sceneB = new Scene();
        using var canvasAObject = new GameObject("ReloadCanvasA");
        using var canvasBObject = new GameObject("ReloadCanvasB");
        using var imageAObject = new GameObject("ReloadImageA");
        using var imageBObject = new GameObject("ReloadImageB");
        GameCanvas canvasA = canvasAObject.AddComponent<GameCanvas>();
        GameCanvas canvasB = canvasBObject.AddComponent<GameCanvas>();
        EventSystem eventA = canvasAObject.AddComponent<EventSystem>();
        EventSystem eventB = canvasBObject.AddComponent<EventSystem>();
        imageAObject.SetParent(canvasAObject, worldPositionStays: false);
        imageBObject.SetParent(canvasBObject, worldPositionStays: false);
        imageAObject.EnsureRectTransform();
        imageBObject.EnsureRectTransform();
        AddPackageComponent(imageAObject, imageType);
        object imageB = AddPackageComponent(imageBObject, imageType);

        bool previousPlaying = Application.IsPlaying;
        try
        {
            Application.IsPlaying = true;
            sceneA.Add(canvasAObject);
            sceneB.Add(canvasBObject);
            sceneA.Enable();
            sceneB.Enable();
            Assert.Same(eventA, EventSystem.Current);
            sceneA.Disable();
            Assert.Same(eventB, EventSystem.Current);
            Assert.Empty(CanvasRegistry.GetCanvases(sceneA));
            Assert.Contains(canvasB, CanvasRegistry.GetCanvases(sceneB));
            Assert.Same(imageB, Assert.Single(GetPackageGraphics(graphicRegistryType, canvasB)));
        }
        finally
        {
            if (sceneA.IsActive) sceneA.Disable();
            if (sceneB.IsActive) sceneB.Disable();
            Application.IsPlaying = previousPlaying;
        }
    }

    private static void VerifyPackageScriptPackageHotReload(System.Reflection.Assembly packageAssembly)
    {
        Type imageType = RequireType(packageAssembly, "XEngine.Runtime.UI.Image");
        Type graphicRegistryType = RequireType(packageAssembly, "XEngine.Runtime.UI.GraphicRegistry");
        Type canvasUpdateRegistryType = RequireType(packageAssembly, "XEngine.Runtime.UI.CanvasUpdateRegistry");
        using var scene = new Scene();
        using var canvasObject = new GameObject("HotReloadCanvas");
        using var imageObject = new GameObject("HotReloadImage");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        EventSystem eventSystem = canvasObject.AddComponent<EventSystem>();
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.One;
        rt.SizeDelta = Float2.Zero;
        object image = AddPackageComponent(imageObject, imageType);

        bool previousPlaying = Application.IsPlaying;
        Float2? previousScreenSize = GameCanvas.ScreenSizeOverride;
        try
        {
            Application.IsPlaying = true;
            GameCanvas.ScreenSizeOverride = new Float2(100f, 100f);
            scene.Add(canvasObject);
            scene.Enable();
            canvas.RebuildIfDirty();
            Assert.Same(eventSystem, EventSystem.Current);
            Assert.Same(image, Assert.Single(GetPackageGraphics(graphicRegistryType, canvas)));
            Assert.True(CanvasUpdateDispatcher.IsRegistered);

            InvokeStaticAny(canvasUpdateRegistryType, "ReleaseDriver");
            InvokeStaticAny(graphicRegistryType, "ReleaseProvider");
            CanvasRegistry.Reset();
            EventSystem.ResetStatics();
            CanvasUpdateDispatcher.Reset();
            Assert.Null(EventSystem.Current);
            Assert.Empty(CanvasRegistry.GetCanvases(scene));
            Assert.Empty(GetPackageGraphics(graphicRegistryType, canvas));
            Assert.False(CanvasUpdateDispatcher.IsRegistered);

            InvokeStatic(canvasUpdateRegistryType, "RebindDriver");
            CanvasHotReload.Rebind(scene);
            InvokeStatic(graphicRegistryType, "Rebind", scene);
            Assert.True(CanvasUpdateDispatcher.IsRegistered);
            Assert.Same(eventSystem, EventSystem.Current);
            Assert.Contains(canvas, CanvasRegistry.GetCanvases(scene));
            Assert.Same(image, Assert.Single(GetPackageGraphics(graphicRegistryType, canvas)));
            canvas.RebuildIfDirty();
            Assert.True(UIRaycaster.TryPick(scene, new Float2(50f, 50f), new Float2(100f, 100f),
                out UIRaycaster.Hit hit));
            Assert.Same(imageObject, hit.GameObject);
        }
        finally
        {
            if (scene.IsActive) scene.Disable();
            GameCanvas.ScreenSizeOverride = previousScreenSize;
            Application.IsPlaying = previousPlaying;
        }
    }

    private static void VerifyPackageDeviceLostResize(System.Reflection.Assembly packageAssembly)
    {
        Type imageType = RequireType(packageAssembly, "XEngine.Runtime.UI.Image");
        Type graphicRegistryType = RequireType(packageAssembly, "XEngine.Runtime.UI.GraphicRegistry");
        using var scene = new Scene();
        using var canvasObject = new GameObject("ResizeCanvas");
        using var imageObject = new GameObject("ResizeImage");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        canvas.UIScaleMode = ScaleMode.ScaleWithScreenSize;
        canvas.ReferenceResolution = new Float2(800f, 450f);
        canvas.MatchWidthOrHeight = 0f;
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.One;
        rt.SizeDelta = Float2.Zero;
        object image = AddPackageComponent(imageObject, imageType);

        bool previousPlaying = Application.IsPlaying;
        Float2? previousScreenSize = GameCanvas.ScreenSizeOverride;
        try
        {
            Application.IsPlaying = true;
            GameCanvas.ScreenSizeOverride = new Float2(800f, 450f);
            scene.Add(canvasObject);
            scene.Enable();
            canvas.RebuildIfDirty();
            Assert.Equal(1f, canvas.ScaleFactor, 4);
            Assert.Same(image, Assert.Single(GetPackageGraphics(graphicRegistryType, canvas)));

            GameCanvas.ScreenSizeOverride = new Float2(400f, 225f);
            CanvasDisplayChange.NotifyResize();
            canvas.RebuildIfDirty();
            Assert.Equal(0.5f, canvas.ScaleFactor, 4);
            Assert.Same(image, Assert.Single(GetPackageGraphics(graphicRegistryType, canvas)));
            Assert.True(UIRaycaster.TryPick(scene, new Float2(200f, 112.5f), new Float2(400f, 225f),
                out UIRaycaster.Hit hit));
            Assert.Same(imageObject, hit.GameObject);

            CanvasDisplayChange.NotifyDeviceLost();
            CanvasDisplayChange.NotifyDeviceRestored();
            canvas.RebuildIfDirty();
            Assert.True(UIRaycaster.TryPick(scene, new Float2(200f, 112.5f), new Float2(400f, 225f),
                out hit));
            Assert.Same(imageObject, hit.GameObject);
        }
        finally
        {
            if (scene.IsActive) scene.Disable();
            GameCanvas.ScreenSizeOverride = previousScreenSize;
            Application.IsPlaying = previousPlaying;
        }
    }

    private static void VerifyPackageBackendScreenshotParity(System.Reflection.Assembly packageAssembly)
    {
        Type imageType = RequireType(packageAssembly, "XEngine.Runtime.UI.Image");
        using var scene = new Scene();
        using var canvasObject = new GameObject("ParityCanvas");
        using var imageObject = new GameObject("ParityImage");
        canvasObject.AddComponent<GameCanvas>();
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        RectTransform rt = imageObject.EnsureRectTransform();
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.One;
        rt.SizeDelta = Float2.Zero;
        AddPackageComponent(imageObject, imageType);

        bool previousPlaying = Application.IsPlaying;
        Float2? previousScreenSize = GameCanvas.ScreenSizeOverride;
        try
        {
            Application.IsPlaying = true;
            GameCanvas.ScreenSizeOverride = new Float2(200f, 120f);
            scene.Add(canvasObject);
            scene.Enable();
            var first = new List<CanvasRenderNode>();
            var second = new List<CanvasRenderNode>();
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, first);
            CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, second);
            Assert.Single(first);
            Assert.True(CanvasBackendParity.AreEqual(first, second));
            Assert.True(CanvasBackendParity.Fingerprint(first[0]).VertexCount > 0);
            Assert.False(CanvasBackendParity.TryValidateBackend(
                XEngine.Runtime.RHI.GraphicsBackend.Vulkan,
                XEngine.Runtime.RHI.GraphicsBackend.Null, out _));
        }
        finally
        {
            if (scene.IsActive) scene.Disable();
            GameCanvas.ScreenSizeOverride = previousScreenSize;
            Application.IsPlaying = previousPlaying;
        }
    }

    private static void VerifyPackagePlayerBuildAudit(Project project)
    {
        string[] platforms =
        [
            BuildPlatforms.Windows,
            BuildPlatforms.Android,
            BuildPlatforms.HarmonyOS,
        ];
        for (int i = 0; i < platforms.Length; i++)
        {
            var assemblies = ScriptCompiler.GetBuildAssemblies(project, platforms[i]);
            var names = new List<string>(assemblies.Count);
            for (int a = 0; a < assemblies.Count; a++)
                names.Add(assemblies[a].Name);
            Assert.True(PlayerXguiAssemblyPolicy.TryValidatePlayerAssemblies(names, out string error), error);
        }
    }

    private static List<object> GetPackageRaycastCandidates(Type graphicRegistryType, GameCanvas canvas)
    {
        var getCandidates = graphicRegistryType.GetMethod("GetRaycastCandidates", [typeof(GameCanvas)]);
        Assert.NotNull(getCandidates);
        var candidates = Assert.IsAssignableFrom<System.Collections.IEnumerable>(getCandidates!.Invoke(null, [canvas]));
        var result = new List<object>();
        foreach (object? candidate in candidates)
            if (candidate != null) result.Add(candidate);
        return result;
    }

    private static T GetStaticPackageProperty<T>(Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
        Assert.NotNull(property);
        return Assert.IsType<T>(property!.GetValue(null));
    }

    private static object? GetStaticPackagePropertyValue(Type type, string propertyName)
    {
        var property = type.GetProperty(propertyName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
        Assert.NotNull(property);
        return property!.GetValue(null);
    }

    private static void InvokeStatic(Type type, string methodName)
    {
        var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
        Assert.NotNull(method);
        method!.Invoke(null, null);
    }

    private static void InvokeStatic(Type type, string methodName, object argument)
    {
        var method = type.GetMethod(methodName, [argument.GetType()]);
        Assert.NotNull(method);
        method!.Invoke(null, [argument]);
    }

    private static void InvokeStaticAny(Type type, string methodName)
    {
        var method = type.GetMethod(methodName,
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(null, null);
    }

    private static MonoBehaviour AddPackageComponent(GameObject gameObject, Type type)
        => Assert.IsAssignableFrom<MonoBehaviour>(gameObject.AddComponent(type));

    private static void VerifyPackageEditorTemplates()
    {
        System.Reflection.Assembly? editorAssembly = null;
        System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            if (string.Equals(assemblies[i].GetName().Name, "XEngine.XGUI.Editor", StringComparison.Ordinal))
            {
                editorAssembly = assemblies[i];
                break;
            }
        }
        Assert.NotNull(editorAssembly);

        Type factoryType = RequireType(editorAssembly!, "XEngine.XGUI.Editor.XGuiTemplateFactory");
        using var scene = new Scene();
        Scene.Load(scene);
        GameObject canvas = CreatePackageTemplate(factoryType, "Canvas", null);
        Assert.NotNull(canvas.GetComponent<GameCanvas>());

        GameObject button = CreatePackageTemplate(factoryType, "Button", canvas);
        Assert.Single(button.Children);
        object buttonComponent = GetComponentByName(button, "Button");
        object buttonImage = GetComponentByName(button, "Image");
        Assert.NotNull(buttonComponent);
        Assert.NotNull(buttonImage);
        Assert.Same(buttonImage, RequireProperty(buttonComponent, "TargetGraphicComponent").GetValue(buttonComponent));
        Assert.NotNull(GetComponentByName(button.Children[0], "Text"));

        GameObject scrollView = CreatePackageTemplate(factoryType, "ScrollView", canvas);
        object scrollRect = GetComponentByName(scrollView, "ScrollRect");
        Assert.NotNull(scrollRect);
        GameObject viewport = FindTemplateNode(scrollView, "Viewport");
        GameObject content = FindTemplateNode(viewport, "Content");
        GameObject vertical = FindTemplateNode(scrollView, "Scrollbar Vertical");
        GameObject horizontal = FindTemplateNode(scrollView, "Scrollbar Horizontal");
        Assert.Same(viewport.RectTransform, RequireProperty(scrollRect, "Viewport").GetValue(scrollRect));
        Assert.Same(content.RectTransform, RequireProperty(scrollRect, "Content").GetValue(scrollRect));
        Assert.Same(GetComponentByName(vertical, "Scrollbar"), RequireProperty(scrollRect, "VerticalScrollbar").GetValue(scrollRect));
        Assert.Same(GetComponentByName(horizontal, "Scrollbar"), RequireProperty(scrollRect, "HorizontalScrollbar").GetValue(scrollRect));

        GameObject input = CreatePackageTemplate(factoryType, "InputField", canvas);
        object inputField = GetComponentByName(input, "InputField");
        Assert.NotNull(inputField);
        GameObject textArea = FindTemplateNode(input, "Text Area");
        Assert.Same(textArea.RectTransform, RequireProperty(inputField, "TextArea").GetValue(inputField));
        Assert.Same(GetComponentByName(FindTemplateNode(textArea, "Text"), "Text"), RequireProperty(inputField, "TextComponent").GetValue(inputField));
        Assert.Same(GetComponentByName(FindTemplateNode(textArea, "Placeholder"), "Text"), RequireProperty(inputField, "Placeholder").GetValue(inputField));
        Assert.Same(FindTemplateNode(textArea, "Selection").RectTransform, RequireProperty(inputField, "Selection").GetValue(inputField));
        Assert.Same(FindTemplateNode(textArea, "Caret").RectTransform, RequireProperty(inputField, "Caret").GetValue(inputField));

        GameObject dropdown = CreatePackageTemplate(factoryType, "Dropdown", canvas);
        object dropdownComponent = GetComponentByName(dropdown, "Dropdown");
        Assert.NotNull(dropdownComponent);
        GameObject options = FindTemplateNode(canvas, "Options");
        GameObject itemTemplate = FindTemplateNode(options, "Item Template");
        Assert.Same(canvas, options.Parent);
        Assert.False(options.Enabled);
        Assert.False(itemTemplate.Enabled);
        Assert.Same(options.RectTransform, RequireProperty(dropdownComponent, "OptionsRoot").GetValue(dropdownComponent));
        Assert.Same(options.RectTransform, RequireProperty(dropdownComponent, "OverlayRoot").GetValue(dropdownComponent));
        Assert.Same(itemTemplate.RectTransform, RequireProperty(dropdownComponent, "ItemTemplate").GetValue(dropdownComponent));
        Assert.Equal(3, Assert.IsAssignableFrom<System.Collections.ICollection>(RequireProperty(dropdownComponent, "Options").GetValue(dropdownComponent)).Count);

        // One template creation = ONE undo step. Dropdown is the hardest case: its root sits under
        // the canvas while its Options popup is a separate canvas-level node — a single undo must
        // remove both.
        Undo.Clear();
        int canvasChildrenBefore = canvas.Children.Count;
        CreatePackageTemplate(factoryType, "Dropdown", canvas);
        Assert.Equal(canvasChildrenBefore + 2, canvas.Children.Count); // control root + Options popup
        Undo.IncrementGroup(); // flush, same path as the per-frame FlushFrame
        Assert.True(Undo.CanUndo);
        Undo.PerformUndo();
        Assert.Equal(canvasChildrenBefore, canvas.Children.Count);
        Assert.False(Undo.CanUndo); // the whole template was a single step

        // Mid-creation failure must roll back everything already built. A Dropdown outside any
        // Canvas fails in ResolveCanvasRoot AFTER its root hierarchy exists — no debris may remain.
        int rootCountBefore = scene.RootObjects.Count();
        var creationError = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => CreatePackageTemplate(factoryType, "Dropdown", null));
        Assert.IsType<InvalidDataException>(creationError.InnerException);
        Assert.Equal(rootCountBefore, scene.RootObjects.Count());
        Undo.Clear();

        string[] names = ["Canvas", "Button", "ScrollView", "InputField", "Dropdown", "SafeArea"];
        for (int i = 0; i < names.Length; i++)
        {
            string logicalPath = "Packages/com.xengine.xgui/Editor/Templates/" + names[i] + ".xgui-template.json";
            Assert.True(PackageManager.TryResolveLogicalAssetPath(Project.Current!, logicalPath, out string physicalPath));
            Assert.True(File.Exists(physicalPath));
            Assert.True(File.Exists(physicalPath + ".meta"));
        }
    }

    private static void VerifyPackageMigrationScanner(System.Reflection.Assembly packageAssembly)
    {
        System.Reflection.Assembly? editorAssembly = null;
        System.Reflection.Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            if (string.Equals(assemblies[i].GetName().Name, "XEngine.XGUI.Editor", StringComparison.Ordinal))
            {
                editorAssembly = assemblies[i];
                break;
            }
        }
        Assert.NotNull(editorAssembly);

        Type scannerType = RequireType(editorAssembly!, "XEngine.XGUI.Editor.XGuiMigrationScanner");
        Type imageType = RequireType(packageAssembly, "XEngine.Runtime.UI.Image");
        using var scene = new Scene();
        using var packageObject = new GameObject("Package Image");
        using var legacyObject = new GameObject("Disabled Legacy Image");
        using var missingPackageObject = new GameObject("Missing Package UI");
        using var unrelatedMissingObject = new GameObject("Missing User Script");
        AddPackageComponent(packageObject, imageType);
        legacyObject.AddComponent<UIImage>();
        legacyObject.Enabled = false;
        AddMissingComponent(missingPackageObject, "XEngine.Runtime.UI.Toggle, XEngine.XGUI.Runtime");
        AddMissingComponent(missingPackageObject, "XEngine.Runtime.UI.UIImage, XEngine.Runtime");
        AddMissingComponent(unrelatedMissingObject, "Game.UserComponent, Game");
        scene.Add(packageObject);
        scene.Add(legacyObject);
        scene.Add(missingPackageObject);
        scene.Add(unrelatedMissingObject);

        System.Reflection.MethodInfo? scan = scannerType.GetMethod("Scan",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
        Assert.NotNull(scan);
        object? reportResult = scan!.Invoke(null, [scene]);
        Assert.NotNull(reportResult);
        object report = reportResult;
        Assert.Equal(4, GetIntProperty(report, "ObjectsScanned"));
        Assert.Equal(1, GetIntProperty(report, "PackageComponentCount"));
        Assert.Equal(1, GetIntProperty(report, "LegacyCompatibilityCount"));
        Assert.Equal(2, GetIntProperty(report, "MissingXGuiComponentCount"));

        System.Reflection.PropertyInfo? findingsProperty = report.GetType().GetProperty("Findings");
        Assert.NotNull(findingsProperty);
        var findings = Assert.IsAssignableFrom<System.Collections.IEnumerable>(findingsProperty!.GetValue(report));
        int missingFindings = 0;
        bool foundDisabledLegacy = false;
        foreach (object finding in findings)
        {
            string kind = Assert.IsType<string>(finding.GetType().GetProperty("Kind")!.GetValue(finding)!.ToString());
            string path = Assert.IsType<string>(finding.GetType().GetProperty("HierarchyPath")!.GetValue(finding));
            string? serializedType = finding.GetType().GetProperty("SerializedType")!.GetValue(finding) as string;
            if (kind == "MissingXGuiComponent")
            {
                missingFindings++;
                Assert.StartsWith("XEngine.Runtime.UI.", serializedType);
            }
            if (kind == "LegacyCompatibilityComponent" && path == "Disabled Legacy Image")
                foundDisabledLegacy = true;
        }
        Assert.Equal(2, missingFindings);
        Assert.True(foundDisabledLegacy);
    }

    private static int GetIntProperty(object target, string propertyName)
    {
        System.Reflection.PropertyInfo? property = target.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<int>(property!.GetValue(target));
    }

    private static void AddMissingComponent(GameObject gameObject, string serializedType)
    {
        var componentData = EchoObject.NewCompound();
        componentData["$type"] = new EchoObject(serializedType);
        gameObject.AddComponent(new MissingMonobehaviour { ComponentData = componentData });
    }

    private static GameObject CreatePackageTemplate(Type factoryType, string name, GameObject? parent)
    {
        System.Reflection.MethodInfo? create = factoryType.GetMethod("Create",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(create);
        return Assert.IsType<GameObject>(create!.Invoke(null, [name, parent]));
    }

    private static MonoBehaviour GetComponentByName(GameObject gameObject, string typeName)
    {
        foreach (MonoBehaviour component in gameObject.GetComponents())
        {
            if (string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal))
                return component;
        }
        throw new Xunit.Sdk.XunitException("Component '" + typeName + "' was not found on template node '" + gameObject.Name + "'.");
    }

    private static GameObject FindTemplateNode(GameObject root, string name)
    {
        if (string.Equals(root.Name, name, StringComparison.Ordinal)) return root;
        for (int i = 0; i < root.Children.Count; i++)
        {
            GameObject? result = FindTemplateNodeOrNull(root.Children[i], name);
            if (result != null) return result;
        }
        throw new Xunit.Sdk.XunitException("Template node '" + name + "' was not found under '" + root.Name + "'.");
    }

    private static GameObject? FindTemplateNodeOrNull(GameObject root, string name)
    {
        if (string.Equals(root.Name, name, StringComparison.Ordinal)) return root;
        for (int i = 0; i < root.Children.Count; i++)
        {
            GameObject? result = FindTemplateNodeOrNull(root.Children[i], name);
            if (result != null) return result;
        }
        return null;
    }

    private static T GetPackageProperty<T>(object component, string propertyName)
    {
        var property = RequireProperty(component, propertyName);
        return Assert.IsType<T>(property.GetValue(component));
    }

    private static void SetPackageProperty(object component, string propertyName, object value)
    {
        var property = RequireProperty(component, propertyName);
        property.SetValue(component, value);
    }

    private static void Arrange(object component, Rect rect) => Invoke(component, "Arrange", rect);

    private static void BubblePointerEvent<TInterface>(GameObject root, PointerEventData e,
        Action<TInterface, PointerEventData> action)
        where TInterface : class
    {
        System.Reflection.MethodInfo? bubble = typeof(EventSystem).GetMethod("Bubble",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(bubble);
        bubble!.MakeGenericMethod(typeof(TInterface)).Invoke(null, [root, e, action]);
    }

    private static void Invoke(object component, string methodName, params object[] arguments)
    {
        var method = component.GetType().GetMethod(methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(component, arguments);
    }

    private static T InvokeResult<T>(object component, string methodName, params object[] arguments)
    {
        var method = component.GetType().GetMethod(methodName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<T>(method.Invoke(component, arguments));
    }

    private static Type RequireType(System.Reflection.Assembly assembly, string fullName)
    {
        Type? type = assembly.GetType(fullName);
        Assert.NotNull(type);
        return type!;
    }

    private static Type RequireNestedType(Type type, string name)
    {
        Type? nested = type.GetNestedType(name);
        Assert.NotNull(nested);
        return nested!;
    }

    private sealed class EventTriggerProbe : MonoBehaviour
    {
        public int Count;

        public void Record() => Count++;
    }

    private static System.Reflection.PropertyInfo RequireProperty(object component, string name)
    {
        System.Reflection.PropertyInfo? property = component.GetType().GetProperty(name);
        Assert.NotNull(property);
        return property!;
    }

    private void WriteCachedPackage(string packageName, string version)
    {
        string root = Path.Combine(Project.LibraryPath, "PackageCache", packageName + "@" + version);
        Directory.CreateDirectory(root);
        WritePackageJson(root, packageName, version, packageName);
    }

    private void WriteManifest(Dictionary<string, string> dependencies)
    {
        var pkgManifest = new ProjectPackageManifest { Dependencies = dependencies };
        pkgManifest.Write(Path.Combine(Project.PackagesPath, "manifest.json"));
    }

    /// <summary>Write a script + its asmdef under a package subfolder (e.g. Runtime/).</summary>
    private void WritePackageScript(string packageName, string subFolder,
        AssemblyDefinition asmdef, string asmdefFileName, string scriptContent, string scriptFileName)
    {
        string dir = Path.Combine(PackageRoot(packageName), subFolder);
        Directory.CreateDirectory(dir);
        asmdef.WriteToFile(Path.Combine(dir, asmdefFileName));
        File.WriteAllText(Path.Combine(dir, scriptFileName), scriptContent);
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

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
