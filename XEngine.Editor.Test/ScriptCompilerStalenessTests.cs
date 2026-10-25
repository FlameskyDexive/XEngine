// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Regression tests for startup staleness detection. <see cref="ScriptAssemblyManager.LoadAssemblies"/>
/// used to trigger the initial compile only when an output DLL was MISSING, so a project whose
/// sources changed while the editor was closed - most notably a built-in package version bump
/// materializing a fresh PackageCache root with preserved timestamps - silently loaded the old
/// DLLs. These invoke the .NET SDK, so they are opt-in under the "Build" category.
/// </summary>
[Trait("Category", "Build")]
public class ScriptCompilerStalenessTests : EditorTestHarness
{
    private readonly string? _prevBuiltInRoot;

    public ScriptCompilerStalenessTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();

        // Default built-in package resolution to an empty root; package tests install only their
        // controlled fixture. The test host's bin/Packages copy cannot compile
        // here anyway (XGUI's Graphic collides with the copy linked into XEngine.Editor.Test - a
        // known environment-sensitive CS0433).
        _prevBuiltInRoot = PackageManager.BuiltInPackagesPathOverride;
        string emptyRoot = Path.Combine(Project.RootPath, "EmptyBuiltInPackages");
        Directory.CreateDirectory(emptyRoot);
        PackageManager.BuiltInPackagesPathOverride = emptyRoot;
        PackageManager.Invalidate();
    }

    public override void Dispose()
    {
        PackageManager.BuiltInPackagesPathOverride = _prevBuiltInRoot;
        PackageManager.Invalidate();
        base.Dispose();
    }

    [Fact]
    public void MissingDll_IsStale_AndSuccessfulCompileClearsIt()
    {
        WriteScript("Thing.cs", "public class Thing { }");

        Assert.True(ScriptCompiler.IsCompilationStale(Project), "Missing output DLLs must read as stale.");

        var result = ScriptCompiler.CompileAll(Project);
        Assert.True(result.Success, $"Compile failed:\n{result.Errors}\n{result.Output}");

        Assert.False(ScriptCompiler.IsCompilationStale(Project), "A fresh successful compile must not be stale.");
    }

    [Fact]
    public void SourceNewerThanDll_IsStale()
    {
        WriteScript("Thing.cs", "public class Thing { }");
        Assert.True(ScriptCompiler.CompileAll(Project).Success);
        Assert.False(ScriptCompiler.IsCompilationStale(Project));

        // Edit the script and pin its timestamp after the DLL's (immune to timestamp granularity).
        WriteScript("Thing.cs", "public class Thing { public int X; }");
        string script = Path.Combine(Project.AssetsPath, "Thing.cs");
        File.SetLastWriteTimeUtc(script, File.GetLastWriteTimeUtc(Project.GameAssemblyPath).AddSeconds(5));

        Assert.True(ScriptCompiler.IsCompilationStale(Project), "A source newer than its DLL must read as stale.");
    }

    // The built-in package bump scenario: BuiltInPackageSource materializes a fresh
    // PackageCache/<name>@<version> root whose copied sources keep their original timestamps, which
    // can all predate the previously compiled DLLs. Only the plan identity (the changed script
    // paths) reveals the staleness - modeled here with a new script backdated behind the DLLs.
    [Fact]
    public void NewSourceWithOldTimestamp_IsStale()
    {
        WriteScript("Thing.cs", "public class Thing { }");
        Assert.True(ScriptCompiler.CompileAll(Project).Success);
        Assert.False(ScriptCompiler.IsCompilationStale(Project));

        WriteScript("Extra.cs", "public class Extra { }");
        string extra = Path.Combine(Project.AssetsPath, "Extra.cs");
        File.SetLastWriteTimeUtc(extra, DateTime.UtcNow.AddDays(-1));

        Assert.True(ScriptCompiler.IsCompilationStale(Project),
            "A plan change must read as stale even when every source timestamp predates the DLLs.");
    }

    // The actual startup regression: LoadAssemblies must recompile stale output instead of loading it.
    [Fact]
    public void LoadAssemblies_RecompilesStaleOutput()
    {
        WriteScript("Versioned.cs", "public static class Versioned { public static string Tag() => \"VERSION_ONE\"; }");
        Assert.True(ScriptCompiler.CompileAll(Project).Success);

        WriteScript("Versioned.cs", "public static class Versioned { public static string Tag() => \"VERSION_TWO\"; }");
        string script = Path.Combine(Project.AssetsPath, "Versioned.cs");
        File.SetLastWriteTimeUtc(script, File.GetLastWriteTimeUtc(Project.GameAssemblyPath).AddSeconds(5));

        // Simulates an editor startup that finds pre-existing but outdated DLLs.
        ScriptAssemblyManager.LoadAssemblies(Project);

        var asm = Assembly.Load(File.ReadAllBytes(Project.GameAssemblyPath));
        string tag = (string)asm.GetType("Versioned")!.GetMethod("Tag")!.Invoke(null, null)!;
        Assert.Equal("VERSION_TWO", tag);
    }

    [Fact]
    public void CompileAll_FromVarTmpProjectSymlink_UsesMatchingRelativePaths()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        WriteScript("Thing.cs", "public static class Thing { public static int Value => 1; }");
        string aliasPath = Path.Combine("/var/tmp", "xengine-script-compiler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(aliasPath, Project.RootPath);
        try
        {
            var aliasProject = XEngine.Editor.Projects.Project.Open(aliasPath);
            var result = ScriptCompiler.CompileAll(aliasProject);
            Assert.True(result.Success, $"Symlink project compile failed:\n{result.Errors}\n{result.Output}");

            string generatedProject = File.ReadAllText(aliasProject.GameCsprojPath);
            Assert.Contains("<OutputPath>Library/ScriptAssemblies</OutputPath>", generatedProject);
            Assert.Contains("<Compile Include=\"Assets/Thing.cs\" />", generatedProject);
            Assert.True(File.Exists(aliasProject.GameAssemblyPath));

            string solutionPath = Path.Combine(aliasProject.RootPath, $"{aliasProject.Name}.slnx");
            string generatedSolution = File.ReadAllText(solutionPath);
            Assert.Contains("<Project Path=\"TestProject.Game.csproj\" />", generatedSolution);
        }
        finally
        {
            // The project itself lives under the Nova-backed test temp root; remove only this alias.
            Directory.Delete(aliasPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompileAll_FromVarTmpSymlink_CompilesExternalBuiltInPackage(bool linkParent)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string packageRoot = Path.Combine(Path.GetTempPath(), "XEngine 外部 Packages " + Guid.NewGuid().ToString("N"));
        string runtimeRoot = Path.Combine(packageRoot, "com.test.external", "Runtime");
        Directory.CreateDirectory(runtimeRoot);
        File.WriteAllText(Path.Combine(packageRoot, "com.test.external", "package.json"),
            """{"name":"com.test.external","version":"1.0.0"}""");
        new AssemblyDefinition { Name = "External.Runtime", NoEngineReferences = true }
            .WriteToFile(Path.Combine(runtimeRoot, "External.Runtime.asmdef"));
        File.WriteAllText(Path.Combine(runtimeRoot, "External.cs"),
            "public static class ExternalPackage { public static int Value => 42; }");
        WriteScript("Thing.cs", "public static class Thing { public static int Value => ExternalPackage.Value; }");

        string aliasPath = Path.Combine("/var/tmp", "xengine-script-compiler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(aliasPath,
            linkParent ? Path.GetDirectoryName(Project.RootPath)! : Project.RootPath);
        try
        {
            PackageManager.BuiltInPackagesPathOverride = packageRoot;
            PackageManager.Invalidate();
            string root = linkParent ? Path.Combine(aliasPath, Path.GetFileName(Project.RootPath)) : aliasPath;
            var aliasProject = XEngine.Editor.Projects.Project.Open(root);
            var result = ScriptCompiler.CompileAll(aliasProject);
            Assert.True(result.Success, $"External package symlink compile failed:\n{result.Errors}\n{result.Output}");
            Assert.True(File.Exists(Path.Combine(aliasProject.ScriptAssemblyPath, "External.Runtime.dll")));
            Assert.True(File.Exists(aliasProject.GameAssemblyPath));
            Assert.False(ScriptCompiler.IsCompilationStale(aliasProject));
        }
        finally
        {
            Directory.Delete(aliasPath);
            Directory.Delete(packageRoot, recursive: true);
        }
    }
}
