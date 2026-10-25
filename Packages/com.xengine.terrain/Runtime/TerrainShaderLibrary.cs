// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;

namespace XEngine.Terrain;

/// <summary>
/// Loads packaged Terrain/Grass shaders and materials. Registers the same
/// <c>$Default:Shader/Terrain</c> GUIDs the core used before the extract so existing
/// <c>.mat</c> / scene references keep resolving when the package is installed.
/// </summary>
public static class TerrainShaderLibrary
{
    public const string TerrainShaderAssetName = "Terrain.shader";
    public const string GrassShaderAssetName = "Grass.shader";
    public const string TerrainMaterialAssetName = "Standard Terrain.mat";
    public const string GrassMaterialAssetName = "Grass.mat";

    public const string TerrainShaderPath = "$Default:Shader/Terrain";
    public const string GrassShaderPath = "$Default:Shader/Grass";
    public const string TerrainMaterialPath = "$Default:Material/Terrain";
    public const string GrassMaterialPath = "$Default:Material/Grass";

    public static Guid TerrainShaderGuid { get; } = BuiltInAssets.DeterministicGuid(TerrainShaderPath);
    public static Guid GrassShaderGuid { get; } = BuiltInAssets.DeterministicGuid(GrassShaderPath);
    public static Guid TerrainMaterialGuid { get; } = BuiltInAssets.DeterministicGuid(TerrainMaterialPath);
    public static Guid GrassMaterialGuid { get; } = BuiltInAssets.DeterministicGuid(GrassMaterialPath);

    [ModuleInitializer]
    [OnAssemblyLoad(Order = -900)]
    internal static void Register()
    {
        BuiltInAssets.Register(TerrainShaderPath, "Terrain", typeof(Shader), ParseTerrainShader, "com.xengine.terrain");
        BuiltInAssets.Register(GrassShaderPath, "Grass", typeof(Shader), ParseGrassShader, "com.xengine.terrain");
        BuiltInAssets.Register(TerrainMaterialPath, "Terrain", typeof(Material), ParseTerrainMaterial, "com.xengine.terrain");
        BuiltInAssets.Register(GrassMaterialPath, "Grass", typeof(Material), ParseGrassMaterial, "com.xengine.terrain");
    }

    public static Shader LoadTerrainShader() => (Shader)BuiltInAssets.Get(TerrainShaderGuid)!;

    public static Shader LoadGrassShader() => (Shader)BuiltInAssets.Get(GrassShaderGuid)!;

    public static Material LoadTerrainMaterial() => new((Material)BuiltInAssets.Get(TerrainMaterialGuid)!);

    public static Material LoadGrassMaterial() => new((Material)BuiltInAssets.Get(GrassMaterialGuid)!);

    public static string ReadShaderSource(string assetName)
    {
        foreach (string candidate in EnumerateShaderPaths(assetName))
        {
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{assetName} was not found next to the Terrain package or the repository Packages folder.");
    }

    public static IEnumerable<string> EnumerateShaderPaths(string assetName)
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "Packages", "com.xengine.terrain", "Runtime", "Shaders", assetName);

        string? assemblyDirectory = Path.GetDirectoryName(typeof(TerrainShaderLibrary).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "Shaders", assetName);
            yield return Path.Combine(assemblyDirectory, "Runtime", "Shaders", assetName);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(
                directory.FullName, "Packages", "com.xengine.terrain", "Runtime", "Shaders", assetName);
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")) || File.Exists(packaged))
                yield return packaged;
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(
                directory.FullName, "Packages", "com.xengine.terrain", "Runtime", "Shaders", assetName);
            yield return packaged;
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                yield break;
            directory = directory.Parent;
        }
    }

    private static EngineObject ParseTerrainShader() => ParseShader(TerrainShaderAssetName, "Assets/Defaults/Terrain.shader");

    private static EngineObject ParseGrassShader() => ParseShader(GrassShaderAssetName, "Assets/Defaults/Grass.shader");

    private static Shader ParseShader(string assetName, string parsePath)
    {
        string source = ReadShaderSource(assetName);
        if (!ShaderParser.ParseShader(parsePath, source, ResolveInclude, out Shader? shader) || shader == null)
            throw new InvalidOperationException($"Failed to parse {assetName}.");
        return shader;
    }

    private static EngineObject ParseTerrainMaterial() => ParseMaterial(TerrainMaterialAssetName);

    private static EngineObject ParseGrassMaterial() => ParseMaterial(GrassMaterialAssetName);

    private static Material ParseMaterial(string assetName)
    {
        string? path = null;
        foreach (string candidate in EnumerateShaderPaths(assetName))
        {
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }
        }

        if (path == null)
            throw new FileNotFoundException($"{assetName} was not found in the Terrain package.");
        string text = File.ReadAllText(path);
        EchoObject echo = EchoObject.ReadFromString(text);
        return Serializer.Deserialize<Material>(echo)
            ?? throw new InvalidOperationException($"Failed to deserialize {assetName}.");
    }

    private static string? ResolveInclude(string path)
    {
        try
        {
            return EmbeddedResources.ReadAllText(path);
        }
        catch
        {
            try
            {
                return EmbeddedResources.ReadAllText($"Assets/Defaults/{path}");
            }
            catch
            {
                return null;
            }
        }
    }
}
