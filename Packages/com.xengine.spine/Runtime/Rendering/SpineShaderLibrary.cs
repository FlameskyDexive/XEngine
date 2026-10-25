using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;

namespace XEngine.Spine;

/// <summary>Loads packaged Spine PMA / lit / additive shaders.</summary>
public static class SpineShaderLibrary
{
    public const string SkeletonShaderAssetName = "Skeleton.shader";
    public const string SkeletonLitShaderAssetName = "SkeletonLit.shader";
    public const string SkeletonAdditiveShaderAssetName = "SkeletonAdditive.shader";

    public const string SkeletonShaderPath = "$Default:Shader/SpineSkeleton";
    public const string SkeletonLitShaderPath = "$Default:Shader/SpineSkeletonLit";
    public const string SkeletonAdditiveShaderPath = "$Default:Shader/SpineSkeletonAdditive";

    public static Guid SkeletonShaderGuid { get; } = BuiltInAssets.DeterministicGuid(SkeletonShaderPath);
    public static Guid SkeletonLitShaderGuid { get; } = BuiltInAssets.DeterministicGuid(SkeletonLitShaderPath);
    public static Guid SkeletonAdditiveShaderGuid { get; } = BuiltInAssets.DeterministicGuid(SkeletonAdditiveShaderPath);

    private static Material? s_skeleton;
    private static Material? s_lit;
    private static Material? s_additive;

    [ModuleInitializer]
    [OnAssemblyLoad(Order = -900)]
    internal static void Register()
    {
        BuiltInAssets.Register(SkeletonShaderPath, "SpineSkeleton", typeof(Shader),
            () => ParseShader(SkeletonShaderAssetName, "Assets/Defaults/SpineSkeleton.shader"), "com.xengine.spine");
        BuiltInAssets.Register(SkeletonLitShaderPath, "SpineSkeletonLit", typeof(Shader),
            () => ParseShader(SkeletonLitShaderAssetName, "Assets/Defaults/SpineSkeletonLit.shader"), "com.xengine.spine");
        BuiltInAssets.Register(SkeletonAdditiveShaderPath, "SpineSkeletonAdditive", typeof(Shader),
            () => ParseShader(SkeletonAdditiveShaderAssetName, "Assets/Defaults/SpineSkeletonAdditive.shader"), "com.xengine.spine");
    }

    public static Shader LoadSkeletonShader() => (Shader)BuiltInAssets.Get(SkeletonShaderGuid)!;
    public static Shader LoadSkeletonLitShader() => (Shader)BuiltInAssets.Get(SkeletonLitShaderGuid)!;
    public static Shader LoadSkeletonAdditiveShader() => (Shader)BuiltInAssets.Get(SkeletonAdditiveShaderGuid)!;

    public static Material ResolveSkeletonMaterial()
        => s_skeleton ??= new Material(LoadSkeletonShader());

    public static Material ResolveLitMaterial()
        => s_lit ??= new Material(LoadSkeletonLitShader());

    public static Material ResolveAdditiveMaterial()
        => s_additive ??= new Material(LoadSkeletonAdditiveShader());

    public static string ReadShaderSource(string assetName)
    {
        foreach (string candidate in EnumerateShaderPaths(assetName))
        {
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException($"{assetName} was not found in the Spine package.");
    }

    public static IEnumerable<string> EnumerateShaderPaths(string assetName)
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "Packages", "com.xengine.spine", "Runtime", "Shaders", assetName);

        string? assemblyDirectory = Path.GetDirectoryName(typeof(SpineShaderLibrary).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "Shaders", assetName);
            yield return Path.Combine(assemblyDirectory, "Runtime", "Shaders", assetName);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(
                directory.FullName, "Packages", "com.xengine.spine", "Runtime", "Shaders", assetName);
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")) || File.Exists(packaged))
                yield return packaged;
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(
                directory.FullName, "Packages", "com.xengine.spine", "Runtime", "Shaders", assetName);
            yield return packaged;
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                yield break;
            directory = directory.Parent;
        }
    }

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
