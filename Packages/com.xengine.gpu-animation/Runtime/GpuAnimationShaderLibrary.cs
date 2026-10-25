using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;

namespace XEngine.GpuAnimation;

/// <summary>
/// Loads the packaged GPU-animation crowd shaders (unlit + lit VAT sampling) through the
/// engine's BuiltInAssets registry, following the TerrainShaderLibrary pattern.
/// </summary>
public static class GpuAnimationShaderLibrary
{
    public const string UnlitShaderAssetName = "GpuAnimUnlit.shader";
    public const string LitShaderAssetName = "GpuAnimLit.shader";

    public const string UnlitShaderPath = "$Default:Shader/GpuAnimUnlit";
    public const string LitShaderPath = "$Default:Shader/GpuAnimLit";

    public static Guid UnlitShaderGuid { get; } = BuiltInAssets.DeterministicGuid(UnlitShaderPath);
    public static Guid LitShaderGuid { get; } = BuiltInAssets.DeterministicGuid(LitShaderPath);

    [ModuleInitializer]
    [OnAssemblyLoad(Order = -900)]
    internal static void Register()
    {
        BuiltInAssets.Register(UnlitShaderPath, "GpuAnimUnlit", typeof(Shader), ParseUnlitShader, "com.xengine.gpu-animation");
        BuiltInAssets.Register(LitShaderPath, "GpuAnimLit", typeof(Shader), ParseLitShader, "com.xengine.gpu-animation");
    }

    public static Shader LoadUnlitShader() => (Shader)BuiltInAssets.Get(UnlitShaderGuid)!;

    public static Shader LoadLitShader() => (Shader)BuiltInAssets.Get(LitShaderGuid)!;

    public static string ReadShaderSource(string assetName)
    {
        foreach (string candidate in EnumerateShaderPaths(assetName))
        {
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }

        throw new FileNotFoundException(
            $"{assetName} was not found next to the gpu-animation package or the repository Packages folder.");
    }

    private static IEnumerable<string> EnumerateShaderPaths(string assetName)
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "Packages", "com.xengine.gpu-animation", "Runtime", "Shaders", assetName);

        string? assemblyDirectory = Path.GetDirectoryName(typeof(GpuAnimationShaderLibrary).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, "Shaders", assetName);
            yield return Path.Combine(assemblyDirectory, "Runtime", "Shaders", assetName);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(
                directory.FullName, "Packages", "com.xengine.gpu-animation", "Runtime", "Shaders", assetName);
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")) || File.Exists(packaged))
                yield return packaged;
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(
                directory.FullName, "Packages", "com.xengine.gpu-animation", "Runtime", "Shaders", assetName);
            yield return packaged;
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                yield break;
            directory = directory.Parent;
        }
    }

    private static EngineObject ParseUnlitShader() => ParseShader(UnlitShaderAssetName);

    private static EngineObject ParseLitShader() => ParseShader(LitShaderAssetName);

    private static Shader ParseShader(string assetName)
    {
        string source = ReadShaderSource(assetName);
        if (!ShaderParser.ParseShader(
                $"Packages/com.xengine.gpu-animation/Runtime/Shaders/{assetName}",
                source, ResolveInclude, out Shader? shader) || shader == null)
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
