using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;

namespace XEngine.VoxelWorld;

/// <summary>Loads packaged Voxel/Opaque, Voxel/Cutout, and Voxel/CutoutCross shaders.</summary>
public static class VoxelShaderLibrary
{
    public const string OpaqueShaderAssetName = "VoxelOpaque.shader";
    public const string CutoutShaderAssetName = "VoxelCutout.shader";
    public const string CutoutCrossShaderAssetName = "VoxelCutoutCross.shader";

    public const string OpaqueShaderPath = "$Default:Shader/VoxelOpaque";
    public const string CutoutShaderPath = "$Default:Shader/VoxelCutout";
    public const string CutoutCrossShaderPath = "$Default:Shader/VoxelCutoutCross";

    public static Guid OpaqueShaderGuid { get; } = BuiltInAssets.DeterministicGuid(OpaqueShaderPath);
    public static Guid CutoutShaderGuid { get; } = BuiltInAssets.DeterministicGuid(CutoutShaderPath);
    public static Guid CutoutCrossShaderGuid { get; } = BuiltInAssets.DeterministicGuid(CutoutCrossShaderPath);

    [ModuleInitializer]
    [OnAssemblyLoad(Order = -900)]
    internal static void Register()
    {
        BuiltInAssets.Register(OpaqueShaderPath, "VoxelOpaque", typeof(Shader), ParseOpaqueShader, "com.xengine.voxelworld");
        BuiltInAssets.Register(CutoutShaderPath, "VoxelCutout", typeof(Shader), ParseCutoutShader, "com.xengine.voxelworld");
        BuiltInAssets.Register(CutoutCrossShaderPath, "VoxelCutoutCross", typeof(Shader), ParseCutoutCrossShader, "com.xengine.voxelworld");
    }

    public static Shader LoadOpaqueShader() => (Shader)BuiltInAssets.Get(OpaqueShaderGuid)!;

    public static Shader LoadCutoutShader() => (Shader)BuiltInAssets.Get(CutoutShaderGuid)!;

    public static Shader LoadCutoutCrossShader() => (Shader)BuiltInAssets.Get(CutoutCrossShaderGuid)!;

    public static Material CreateOpaqueMaterial(VoxelAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        var material = new Material(LoadOpaqueShader()) { Name = "Voxel Opaque" };
        material.SetTexture2DArray("_VoxelAtlas", atlas.Texture);
        return material;
    }

    public static Material CreateCutoutMaterial(VoxelAtlas atlas)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        var material = new Material(LoadCutoutShader()) { Name = "Voxel Cutout" };
        material.SetTexture2DArray("_VoxelAtlas", atlas.Texture);
        return material;
    }

    public static string ReadShaderSource(string assetName)
    {
        foreach (string candidate in EnumerateShaderPaths(assetName))
        {
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{assetName} was not found next to the Voxel World package or the repository Packages folder.");
    }

    public static IEnumerable<string> EnumerateShaderPaths(string assetName)
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "Packages", "com.xengine.voxelworld", "Runtime", "Shaders", assetName);

        string? assemblyDirectory = Path.GetDirectoryName(typeof(VoxelShaderLibrary).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "Shaders", assetName);
            yield return Path.Combine(assemblyDirectory, "Runtime", "Shaders", assetName);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(
                directory.FullName, "Packages", "com.xengine.voxelworld", "Runtime", "Shaders", assetName);
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")) || File.Exists(packaged))
                yield return packaged;
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(
                directory.FullName, "Packages", "com.xengine.voxelworld", "Runtime", "Shaders", assetName);
            yield return packaged;
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                yield break;
            directory = directory.Parent;
        }
    }

    private static EngineObject ParseOpaqueShader() => ParseShader(OpaqueShaderAssetName, "Assets/Defaults/VoxelOpaque.shader");

    private static EngineObject ParseCutoutShader() => ParseShader(CutoutShaderAssetName, "Assets/Defaults/VoxelCutout.shader");

    private static EngineObject ParseCutoutCrossShader() => ParseShader(CutoutCrossShaderAssetName, "Assets/Defaults/VoxelCutoutCross.shader");

    private static Shader ParseShader(string assetName, string parsePath)
    {
        string source = ReadShaderSource(assetName);
        if (!ShaderParser.ParseShader(parsePath, source, ResolveInclude, out Shader? shader) || shader == null)
            throw new InvalidOperationException($"Failed to parse {assetName}.");
        return shader;
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
