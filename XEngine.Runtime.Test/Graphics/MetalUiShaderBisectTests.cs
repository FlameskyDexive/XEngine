// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Reflection;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// Reproduces the UI shader's Slang Metal compile (E36107 "unavailable features in entry
/// point") directly against the SLANGPROGRAM sources, so bisects run in seconds instead of
/// editor boots. Temporary diagnostic harness — delete once the feature is identified.
/// </summary>
public class MetalUiShaderBisectTests
{
    private static (string Vertex, string Fragment, string DiagnosticDirectory) LoadSlangSources()
    {
        Assembly runtime = typeof(RuntimeSlangShaderCompiler).Assembly;
        string? resourceName = null;
        foreach (string name in runtime.GetManifestResourceNames())
        {
            if (name.EndsWith("UI.shader", StringComparison.Ordinal))
            {
                resourceName = name;
                break;
            }
        }
        Assert.NotNull(resourceName);
        using var stream = runtime.GetManifestResourceStream(resourceName!)!;
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();

        string vertex = ExtractBlock(text, "SLANGPROGRAM", "Vertex");
        string fragment = ExtractBlock(text, "SLANGPROGRAM", "Fragment");
        // The shader parser inlines #include at parse time; reproduce that here so the slang
        // session (OS file system, no registered provider) can resolve XEngineCG.
        string common = ReadEmbeddedText("XEngineCG.hlsl");
        vertex = InlineIncludes(vertex, common);
        fragment = InlineIncludes(fragment, common);
        string diagnosticDirectory = CreateDiagnosticDirectory();
        File.WriteAllText(Path.Combine(diagnosticDirectory, "ui_vertex.slang"), vertex);
        File.WriteAllText(Path.Combine(diagnosticDirectory, "ui_fragment.slang"), fragment);
        return (vertex, fragment, diagnosticDirectory);
    }

    private static string CreateDiagnosticDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "xengine-ui-shader-bisect",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ReadEmbeddedText(string fileName)
    {
        Assembly runtime = typeof(RuntimeSlangShaderCompiler).Assembly;
        foreach (string name in runtime.GetManifestResourceNames())
        {
            if (name.EndsWith(fileName, StringComparison.Ordinal))
            {
                using var stream = runtime.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }
        throw new FileNotFoundException("Embedded shader include not found: " + fileName);
    }

    private static string InlineIncludes(string source, string common)
    {
        var lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("#include", StringComparison.Ordinal))
                lines[i] = common;
        }
        return string.Join('\n', lines);
    }

    private static string ExtractBlock(string text, string programMarker, string blockName)
    {
        int programStart = text.IndexOf(programMarker, StringComparison.Ordinal);
        Assert.True(programStart >= 0, $"No {programMarker} section.");
        int blockHeader = text.IndexOf(blockName, programStart, StringComparison.Ordinal);
        Assert.True(blockHeader >= 0, $"No {blockName} block.");
        int open = text.IndexOf('{', blockHeader);
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return text[(open + 1)..i];
            }
        }
        throw new InvalidDataException($"Unbalanced braces in {blockName} block.");
    }

    [RequiresAppleMetalCompilerFact]
    public void Compile_Or_ReproduceE36107()
    {
        (string vertex, string fragment, string diagnosticDirectory) = LoadSlangSources();
        var compiler = new RuntimeSlangShaderCompiler();
        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Metal,
            Language = ShaderLanguage.Hlsl,
            VertexSource = vertex,
            FragmentSource = fragment,
            VertexEntryPoint = "main",
            FragmentEntryPoint = "main",
        });
        Assert.True(result.Success,
            "UI shader Metal compile: " + (result.ErrorMessage ?? "unknown failure")
            + $". Reproduction sources: {diagnosticDirectory}");
        Directory.Delete(diagnosticDirectory, recursive: true);
    }

    [RequiresAppleMetalCompilerFact]
    public void CompileAllPasses_Or_ReproduceE36107()
    {
        Assembly runtime = typeof(RuntimeSlangShaderCompiler).Assembly;
        string? resourceName = null;
        foreach (string name in runtime.GetManifestResourceNames())
        {
            if (name.EndsWith("UI.shader", StringComparison.Ordinal))
            {
                resourceName = name;
                break;
            }
        }
        Assert.NotNull(resourceName);
        using var stream = runtime.GetManifestResourceStream(resourceName!)!;
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        string common = ReadEmbeddedText("XEngineCG.hlsl");

        var compiler = new RuntimeSlangShaderCompiler();
        var failures = new System.Text.StringBuilder();
        string diagnosticDirectory = CreateDiagnosticDirectory();
        int passIndex = 0;
        int searchFrom = 0;
        while (true)
        {
            int passHeader = text.IndexOf("Pass \"", searchFrom, StringComparison.Ordinal);
            if (passHeader < 0)
                break;
            int nameEnd = text.IndexOf('"', passHeader + 6);
            string passName = text[(passHeader + 6)..nameEnd];
            int nextPass = text.IndexOf("Pass \"", nameEnd, StringComparison.Ordinal);
            int sectionEnd = nextPass < 0 ? text.Length : nextPass;
            string section = text[passHeader..sectionEnd];

            int slang = section.IndexOf("SLANGPROGRAM", StringComparison.Ordinal);
            if (slang >= 0)
            {
                string vertex = InlineIncludes(ExtractBlock(section, "SLANGPROGRAM", "Vertex"), common);
                string fragment = InlineIncludes(ExtractBlock(section, "SLANGPROGRAM", "Fragment"), common);
                File.WriteAllText(Path.Combine(diagnosticDirectory, $"ui_pass{passIndex}_vertex.slang"), vertex);
                File.WriteAllText(Path.Combine(diagnosticDirectory, $"ui_pass{passIndex}_fragment.slang"), fragment);
                ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
                {
                    TargetBackend = GraphicsBackend.Metal,
                    Language = ShaderLanguage.Hlsl,
                    VertexSource = vertex,
                    FragmentSource = fragment,
                    VertexEntryPoint = "main",
                    FragmentEntryPoint = "main",
                });
                if (!result.Success)
                    failures.AppendLine($"pass {passIndex} '{passName}': " + result.ErrorMessage);
            }
            passIndex++;
            searchFrom = sectionEnd;
        }

        Assert.True(failures.Length == 0,
            $"UI shader Metal compile failures (reproduction sources: {diagnosticDirectory}):\n{failures}");
        Directory.Delete(diagnosticDirectory, recursive: true);
    }
}
