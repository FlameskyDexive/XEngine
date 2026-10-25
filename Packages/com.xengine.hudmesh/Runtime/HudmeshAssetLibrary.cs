using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;

namespace XEngine.Hudmesh;

/// <summary>
/// Locates packaged Hudmesh files (shaders, HUD sprite art) across the engine-repo,
/// installed-package (Library/PackageCache) and player layouts — same search strategy
/// as the Spine 3.8 package — and caches the loaded <see cref="Texture2D"/>s.
/// </summary>
public static class HudmeshAssetLibrary
{
    public const string SpriteTopShaderAssetName = "HudSpriteTop.shader";
    public const string SpriteOccludedShaderAssetName = "HudSpriteOccluded.shader";

    public const string SpriteTopShaderPath = "$Default:Shader/HudmeshSpriteTop";
    public const string SpriteOccludedShaderPath = "$Default:Shader/HudmeshSpriteOccluded";

    public static Guid SpriteTopShaderGuid { get; } = BuiltInAssets.DeterministicGuid(SpriteTopShaderPath);
    public static Guid SpriteOccludedShaderGuid { get; } = BuiltInAssets.DeterministicGuid(SpriteOccludedShaderPath);

    private static readonly Dictionary<string, Texture2D> s_textures = new(StringComparer.OrdinalIgnoreCase);

    [ModuleInitializer]
    [OnAssemblyLoad(Order = -900)]
    internal static void Register()
    {
        BuiltInAssets.Register(SpriteTopShaderPath, "HudmeshSpriteTop", typeof(Shader),
            () => ParseShader(SpriteTopShaderAssetName, "Assets/Defaults/HudmeshSpriteTop.shader"), "com.xengine.hudmesh");
        BuiltInAssets.Register(SpriteOccludedShaderPath, "HudmeshSpriteOccluded", typeof(Shader),
            () => ParseShader(SpriteOccludedShaderAssetName, "Assets/Defaults/HudmeshSpriteOccluded.shader"), "com.xengine.hudmesh");
    }

    /// <summary>The always-on-top HUD sprite shader (ZTest Off), port of UnityHUD's Unlit/HUDSprite.</summary>
    public static Shader LoadSpriteTopShader() => (Shader)BuiltInAssets.Get(SpriteTopShaderGuid)!;

    /// <summary>The depth-occluded HUD sprite shader (ZTest LEqual).</summary>
    public static Shader LoadSpriteOccludedShader() => (Shader)BuiltInAssets.Get(SpriteOccludedShaderGuid)!;

    /// <summary>
    /// Loads (and caches by file name) a HUD sprite PNG from the package's
    /// <c>Runtime/Assets/Sprites</c> folder. Mipmaps on, straight sRGB sampling.
    /// </summary>
    public static Texture2D? LoadSpriteTexture(string fileName)
    {
        if (s_textures.TryGetValue(fileName, out Texture2D? cached) && cached is not null)
            return cached;

        string? file = FindAssetFile(Path.Combine("Assets", "Sprites", fileName));
        if (file is null)
            return null;

        Texture2D texture = Texture2D.FromFile(file, generateMipmaps: true, srgbSampling: false);
        s_textures[fileName] = texture;
        return texture;
    }

    /// <summary>Drops cached textures (e.g. before a config reload swaps art sets).</summary>
    public static void ClearTextureCache()
    {
        s_textures.Clear();
    }

    /// <summary>Returns the first existing package-relative file path, or null.</summary>
    public static string? FindAssetFile(string relativePath)
    {
        foreach (string candidate in EnumeratePackagePaths(relativePath))
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static string ReadShaderSource(string assetName)
        => ReadPackageFileOrThrow(Path.Combine("Shaders", assetName), assetName);

    private static string ReadPackageFileOrThrow(string relativePath, string assetName)
    {
        string? file = FindAssetFile(relativePath);
        if (file == null)
            throw new FileNotFoundException($"{assetName} was not found in the Hudmesh package.");
        return File.ReadAllText(file);
    }

    /// <summary>
    /// Candidate locations of a file inside the package's Runtime folder: beside the running
    /// app (engine repo or player payload), beside the compiled assembly (script assemblies
    /// build under the package), and walking up from the app directory and current directory
    /// to catch repo-rooted runs (tests, editor, headless tools).
    /// </summary>
    public static IEnumerable<string> EnumeratePackagePaths(string runtimeRelativePath)
    {
        string packageRelative = Path.Combine("Packages", "com.xengine.hudmesh", "Runtime", runtimeRelativePath);

        yield return Path.Combine(AppContext.BaseDirectory, packageRelative);

        string? assemblyDirectory = Path.GetDirectoryName(typeof(HudmeshAssetLibrary).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory))
        {
            yield return Path.Combine(assemblyDirectory, runtimeRelativePath);
            yield return Path.Combine(assemblyDirectory, "Runtime", runtimeRelativePath);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(directory.FullName, packageRelative);
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")) || File.Exists(packaged))
                yield return packaged;
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            string packaged = Path.Combine(directory.FullName, packageRelative);
            yield return packaged;
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                yield break;
            directory = directory.Parent;
        }
    }

    private static Shader ParseShader(string assetName, string parsePath)
    {
        string source = ReadShaderSource(assetName);
        if (!ShaderParser.ParseShader(parsePath, source, ResolveInclude, out Shader? shader) || shader is null)
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
