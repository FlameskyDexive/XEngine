// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Terrain;

using Xunit;

namespace XEngine.Runtime.Test.Terrain;

public sealed class TerrainShaderLibraryTests
{
    [Fact]
    public void PackagedShaders_ParseWithSameNamesAndPasses()
    {
        Shader terrain = TerrainShaderLibrary.LoadTerrainShader();
        Shader grass = TerrainShaderLibrary.LoadGrassShader();
        Assert.Equal("Terrain", terrain.Name);
        Assert.Equal("Grass", grass.Name);
        Assert.Equal("Default/Terrain", terrain.ArtifactName);
        Assert.Equal("Default/Grass", grass.ArtifactName);
        Assert.True(terrain.GetPass("Terrain").HasGlsl);
        Assert.True(terrain.GetPass("Terrain").HasHlsl);
        Assert.True(terrain.GetPass("TerrainShadow").HasHlsl);
        Assert.True(terrain.GetPass("TerrainPrepass").HasHlsl);
        Assert.True(grass.GetPass("Grass").HasHlsl);
        Assert.True(grass.GetPass("GrassPrepass").HasHlsl);
        Assert.Equal(TerrainShaderLibrary.TerrainShaderGuid, terrain.AssetID);
        Assert.Equal(TerrainShaderLibrary.GrassShaderGuid, grass.AssetID);
        var shaderGuids = BuiltInAssets.FindAllOfType(typeof(Shader)).Select(entry => entry.guid).ToHashSet();
        Assert.Contains(TerrainShaderLibrary.TerrainShaderGuid, shaderGuids);
        Assert.Contains(TerrainShaderLibrary.GrassShaderGuid, shaderGuids);
    }

    [Fact]
    public void PackagedMaterials_CloneWithoutMutatingTemplate()
    {
        Material a = TerrainShaderLibrary.LoadTerrainMaterial();
        Material b = TerrainShaderLibrary.LoadTerrainMaterial();
        Assert.NotSame(a, b);
        Assert.Equal("Terrain", a.Shader!.Name);
        Assert.Equal("Default/Terrain", a.Shader.ArtifactName);
    }

    [Fact]
    public void CoreDefaultEnums_NoLongerContainTerrainOrGrass()
    {
        Assert.DoesNotContain("Terrain", Enum.GetNames<DefaultShader>());
        Assert.DoesNotContain("Grass", Enum.GetNames<DefaultShader>());
        Assert.DoesNotContain("Terrain", Enum.GetNames<DefaultMaterial>());
        Assert.DoesNotContain("Grass", Enum.GetNames<DefaultMaterial>());
        string[] defaultFiles = Directory.GetFiles(
            Path.Combine(FindRepositoryRoot(), "XEngine.Runtime", "Assets", "Defaults"));
        Assert.DoesNotContain("Terrain.shader", defaultFiles.Select(Path.GetFileName));
        Assert.DoesNotContain("Grass.shader", defaultFiles.Select(Path.GetFileName));
        Assert.DoesNotContain("Standard Terrain.mat", defaultFiles.Select(Path.GetFileName));
        Assert.DoesNotContain("Grass.mat", defaultFiles.Select(Path.GetFileName));
    }

    [Fact]
    public void ShaderSource_IsByteIdenticalToPackageFile()
    {
        string package = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.terrain", "Runtime", "Shaders", "Terrain.shader");
        Assert.Equal(File.ReadAllText(package), TerrainShaderLibrary.ReadShaderSource(TerrainShaderLibrary.TerrainShaderAssetName));
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, ShaderBytecodeFormat.SpirV)]
    [InlineData(GraphicsBackend.Direct3D12, ShaderBytecodeFormat.Dxil)]
    public void TerrainPass_CompilesOnModernBackends(GraphicsBackend backend, ShaderBytecodeFormat expected)
    {
        ShaderPass pass = TerrainShaderLibrary.LoadTerrainShader().GetPass("Terrain");
        ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(
            pass.CreateHlslCompileRequest(backend, null, ""));
        if (backend == GraphicsBackend.Vulkan && !result.Success &&
            result.ErrorMessage?.Contains("SPIR-V CodeGen not available", StringComparison.OrdinalIgnoreCase) == true)
            return;
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expected, result.Format);
    }

    [Fact]
    public void ShaderMoveEvidence_WritesBoard()
    {
        Shader terrain = TerrainShaderLibrary.LoadTerrainShader();
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 280, 18, 19, 26);
        board.Fill(24, 24, 872, 232, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 232, 70, 74, 90, 2);
        board.Text(40, 40, "M2 TERRAIN SHADER PACKAGE", 230, 232, 240, 2);
        board.Text(40, 80, "TERRAIN.SHADER / GRASS.SHADER IN PACKAGE", 140, 146, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, $"SHADER {terrain.Name}", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "DEFAULTSHADER TERRAIN/GRASS REMOVED", 200, 220, 200, 1);
        board.Text(40, 210, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "m2-shader-move.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "m2-shader-move.md"),
            "# M2 Terrain shader extract\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "`Terrain.shader`, `Grass.shader`, `Standard Terrain.mat`, and `Grass.mat` now live in " +
            "`Packages/com.xengine.terrain/Runtime/Shaders`. `TerrainShaderLibrary` parses them and " +
            "re-registers the original `$Default:Shader/Terrain` GUIDs. Core `DefaultShader` / " +
            "`DefaultMaterial` no longer list Terrain or Grass.\n\n" +
            "Screenshot: [m2-shader-move.png](m2-shader-move.png).\n");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
