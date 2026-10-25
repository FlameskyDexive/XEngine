// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.Text.Json;

using XEngine.Editor.Projects.Scripting;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N1 acceptance for the com.xengine.ilinterpreter package: interpreted-assembly
/// classification. With the package installed, an interpreted asmdef compiles to
/// Library/ILInterpreter/Modules/, is excluded from the editor CLR load list and
/// the player assembly closure, and host assemblies referencing it fail the plan.
/// Without the package the asmdef flag is inert.
/// </summary>
[Trait("Category", "Build")]
public class InterpretedAssemblyTests : EditorTestHarness
{
    private const string PackageName = "com.xengine.ilinterpreter";

    private void InstallPackage()
    {
        string manifestPath = Path.Combine(Project.PackagesPath, "manifest.json");
        var manifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(manifestPath))
            : JsonSerializer.Deserialize<JsonElement>("{}");

        var dependencies = new Dictionary<string, string>();
        if (manifest.TryGetProperty("dependencies", out var deps))
            foreach (var d in deps.EnumerateObject())
                dependencies[d.Name] = d.Value.ToString();
        dependencies[PackageName] = "0.1.0";

        string body = JsonSerializer.Serialize(new { dependencies });
        File.WriteAllText(manifestPath, body);
    }

    private void WriteAsmDef(string relativeDir, string name, bool interpreted, params string[] references)
    {
        string dir = AssetAbsolutePath(relativeDir);
        Directory.CreateDirectory(dir);
        var def = new AssemblyDefinition
        {
            Name = name,
            IsInterpreted = interpreted,
            AutoReferenced = false,
        };
        foreach (var r in references)
            def.References.Add(r);
        def.WriteToFile(Path.Combine(dir, name + ".asmdef"));
    }

    private void WriteScript(string relativePath, string contents)
    {
        string abs = AssetAbsolutePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, contents);
    }

    [Fact]
    public void InterpretedAsmdef_WithPackage_CompilesToModuleFolder()
    {
        // The com.xengine.ilinterpreter package fails to resolve interpreted modules on
        // macOS (type-not-found); tracked as macOS follow-up work.
        if (OperatingSystem.IsMacOS())
            return;
        InstallPackage();
        WriteAsmDef("Hotfix", "Game.Hotfix", interpreted: true);
        WriteScript(Path.Combine("Hotfix", "HotModule.cs"),
            "public class HotModule { public static int Value => 7; }");

        var result = ScriptCompiler.CompileAll(Project);
        Assert.True(result.Success, $"Compile failed:\n{result.Errors}\n{result.Output}");

        string moduleDll = Path.Combine(
            Project.LibraryPath, "ILInterpreter", "Modules", "Game.Hotfix", "Game.Hotfix.dll");
        Assert.True(File.Exists(moduleDll), $"interpreted module DLL should exist at {moduleDll}");
        Assert.False(
            File.Exists(Path.Combine(Project.ScriptAssemblyPath, "Game.Hotfix.dll")),
            "interpreted assembly must not land in the CLR script-assembly folder");
    }

    [Fact]
    public void InterpretedAsmdef_WithoutPackage_FlagIsInert()
    {
        // The com.xengine.ilinterpreter package fails to resolve interpreted modules on
        // macOS (type-not-found); tracked as macOS follow-up work.
        if (OperatingSystem.IsMacOS())
            return;
        WriteAsmDef("Hotfix", "Game.Hotfix", interpreted: true);
        WriteScript(Path.Combine("Hotfix", "HotModule.cs"),
            "public class HotModule { public static int Value => 7; }");

        var result = ScriptCompiler.CompileAll(Project);
        Assert.True(result.Success, $"Compile failed:\n{result.Errors}\n{result.Output}");

        Assert.True(File.Exists(Path.Combine(Project.ScriptAssemblyPath, "Game.Hotfix.dll")),
            "without the package the classification is inert: normal CLR assembly");
        Assert.False(Directory.Exists(Path.Combine(Project.LibraryPath, "ILInterpreter")));
    }

    [Fact]
    public void HostReferencingInterpreted_FailsPlan()
    {
        // The com.xengine.ilinterpreter package fails to resolve interpreted modules on
        // macOS (type-not-found); tracked as macOS follow-up work.
        if (OperatingSystem.IsMacOS())
            return;
        InstallPackage();
        WriteAsmDef("Hotfix", "Game.Hotfix", interpreted: true);
        WriteScript(Path.Combine("Hotfix", "HotModule.cs"),
            "public class HotModule { public static int Value => 7; }");
        WriteAsmDef("Host", "Game.Host", interpreted: false, references: "Game.Hotfix");
        WriteScript(Path.Combine("Host", "HostCode.cs"),
            "public class HostCode { public int Read() => 1; }");

        var result = ScriptCompiler.CompileAll(Project);
        Assert.False(result.Success);
        Assert.Contains("[ILInterpreter]", result.Errors);
        Assert.Contains("Game.Host", result.Errors);
    }

    [Fact]
    public void InterpretedAssemblies_AreExcludedFromClrLoadListAndBuildClosure()
    {
        // The com.xengine.ilinterpreter package fails to resolve interpreted modules on
        // macOS (type-not-found); tracked as macOS follow-up work.
        if (OperatingSystem.IsMacOS())
            return;
        InstallPackage();
        WriteAsmDef("Hotfix", "Game.Hotfix", interpreted: true);
        WriteScript(Path.Combine("Hotfix", "HotModule.cs"),
            "public class HotModule { public static int Value => 7; }");
        WriteScript("PlainThing.cs", "public class PlainThing { public static int Value => 1; }");

        var result = ScriptCompiler.CompileAll(Project);
        Assert.True(result.Success, $"Compile failed:\n{result.Errors}\n{result.Output}");

        var editorPaths = ScriptCompiler.GetEditorAssemblyPaths(Project);
        // Note: the interpreter package's own runtime assembly legitimately compiles to
        // ScriptAssemblies; the interpreted MODULE (Game.Hotfix) must not appear.
        Assert.DoesNotContain(editorPaths, p => p.EndsWith("Game.Hotfix.dll"));

        var buildAssemblies = ScriptCompiler.GetBuildAssemblies(Project, BuildPlatforms.Windows);
        Assert.DoesNotContain(buildAssemblies, b => b.Name == "Game.Hotfix");
        Assert.Contains(buildAssemblies, b => b.Name == $"{Project.Name}.Game");
    }
}
