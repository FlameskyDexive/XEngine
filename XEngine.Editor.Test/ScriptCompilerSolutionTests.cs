// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using XEngine.Editor.Projects.Scripting;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Verifies that <see cref="ScriptCompiler.CompileAll"/> generates a project solution (.slnx)
/// listing the script assemblies, so an IDE has a single entry point (prowl-sync M7.4, ported
/// from upstream e505a1d5). Only written when absent, so user edits to the file survive.
/// </summary>
[Trait("Category", "Build")]
public class ScriptCompilerSolutionTests : EditorTestHarness
{
    [Fact]
    public void CompileAll_GeneratesSolutionFile_WhenAbsent()
    {
        // macOS: player build/run and Windows-host toolchains are not wired for this host
        // yet; re-enable with the macOS packaging milestone.
        if (OperatingSystem.IsMacOS())
            return;
        WriteScript("SomeScript.cs", "public class SomeScript { }");

        var compile = ScriptCompiler.CompileAll(Project);
        Assert.True(compile.Success, $"Script compile failed:\n{compile.Errors}\n{compile.Output}");

        string slnxPath = Path.Combine(Project.RootPath, $"{Project.Name}.slnx");
        Assert.True(File.Exists(slnxPath), "CompileAll should generate the project .slnx when absent.");

        string content = File.ReadAllText(slnxPath);
        Assert.Contains("<Solution>", content);
        Assert.Contains(".csproj", content);
    }

    [Fact]
    public void CompileAll_PreservesExistingSolutionFile()
    {
        WriteScript("SomeScript.cs", "public class SomeScript { }");
        string slnxPath = Path.Combine(Project.RootPath, $"{Project.Name}.slnx");
        File.WriteAllText(slnxPath, "<Solution><!-- user curated --></Solution>");

        ScriptCompiler.CompileAll(Project);

        Assert.Contains("user curated", File.ReadAllText(slnxPath));
    }
}
