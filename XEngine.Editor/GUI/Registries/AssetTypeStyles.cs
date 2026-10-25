// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;
using System.Drawing;

using XEngine.Editor.Theming;
using XEngine.OrigamiUI;

namespace XEngine.Editor.GUI.Registries;

/// <summary>
/// Visual style for an asset/file type in the browser: either a vector <see cref="IOrigamiIcon"/> or a
/// short text <see cref="Badge"/> (e.g. "C#"), plus an accent <see cref="Color"/> used for the tinted
/// thumbnail tile. Folders are <see cref="Bare"/> (no tile, just the icon).
/// Bitmap type icons from <c>Resources/Icons/Types</c> set <see cref="PreserveIconColor"/> so they
/// are not re-tinted by the tile accent.
/// </summary>
public readonly struct AssetTypeStyle
{
    public IOrigamiIcon? Icon { get; init; }
    public string? Badge { get; init; }
    public Color Color { get; init; }
    public bool Bare { get; init; }
    public bool PreserveIconColor { get; init; }

    /// <summary>Canonical engine type name this style was declared for ("AnimationClip" for ".anim").
    /// Lets type labels and t: search work before an asset is ever imported.</summary>
    public string? TypeName { get; init; }

    /// <summary>Color used when painting <see cref="Icon"/>: white for authored bitmaps, accent otherwise.</summary>
    public Color IconColor => PreserveIconColor
        ? Color.FromArgb(Color.A == 0 ? 255 : Color.A, 255, 255, 255)
        : Color;
}

/// <summary>
/// Maps file extensions and engine type names to a <see cref="AssetTypeStyle"/>. Prefers Unity 4.7
/// type-icon PNGs (copied into <c>Resources/Icons/Types</c>) and falls back to Font Awesome glyphs.
/// Extend via <see cref="Register"/>.
/// </summary>
public static class AssetTypeStyles
{
    private static Color C(int r, int g, int b) => Color.FromArgb(255, r, g, b);
    private static readonly Color Amber = C(251, 191, 36), Green = C(74, 222, 128), Blue = C(96, 165, 250),
        Purple = C(168, 85, 247), Pink = C(217, 107, 216), Cyan = C(52, 211, 238), Orange = C(251, 146, 60),
        Gray = C(148, 143, 171), Red = C(251, 113, 133);

    /// <summary>XEngine type names that do not match Unity's PNG stem 1:1.</summary>
    private static readonly Dictionary<string, string> TypePngAliases = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["PrefabAsset"] = "PrefabNormal",
        ["Prefab"] = "PrefabNormal",
        ["Model"] = "Mesh",
        ["NavMeshData"] = "NavMesh",
        ["NavMeshSurface"] = "NavMesh",
        ["PhysicsMaterial"] = "PhysicMaterial",
        ["TimelineAsset"] = "Animation",
        ["ShaderGraphAsset"] = "Shader",
        ["VfxGraphAsset"] = "ParticleSystem",
        ["UniversalRenderPipelineAsset"] = "Shader",
        ["UniversalRenderData"] = "Shader",
        ["VolumeProfile"] = "ScriptableObject",
        ["PostProcessProfile"] = "ScriptableObject",
        ["BuildProfile"] = "EditorSettings",
        ["TerrainLayer"] = "Terrain",
        ["InputActionMap"] = "ScriptableObject",
        ["SpriteLibrary"] = "Sprite",
        ["SpriteShapeProfile"] = "Sprite",
        ["AssemblyDefinition"] = "ScriptableObject",
        ["AudioClip"] = "AudioClip",
    };

    public static AssetTypeStyle Folder => TypeIcon("Folder", EditorIcons.Folder_I, Amber, bare: true);
    public static AssetTypeStyle Package => TypeIcon("Package", EditorIcons.Box_I, Purple, bare: true);
    public static AssetTypeStyle SubAsset => TypeIcon("GameObject", EditorIcons.Cube_I, Purple);

    private static readonly Dictionary<string, AssetTypeStyle> _map =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            // Code / binaries — text badges stay more readable than a tiny type PNG.
            [".cs"] = new() { Badge = "C#", Color = Green },
            [".dll"] = new() { Badge = "DLL", Color = Orange },
            [".so"] = new() { Badge = "DLL", Color = Orange },
            [".dylib"] = new() { Badge = "DLL", Color = Orange },
            [".asmdef"] = new() { Badge = "ASM", Color = Blue },
            // Shaders / graphs.
            [".shader"] = TypeIcon("Shader", EditorIcons.Bolt_I, Pink),
            [".glsl"] = TypeIcon("CGProgram", EditorIcons.Bolt_I, Pink),
            [".hlsl"] = TypeIcon("CGProgram", EditorIcons.Bolt_I, Pink),
            [".compute"] = TypeIcon("ComputeShader", EditorIcons.Bolt_I, Pink),
            [".shadergraph"] = TypeIcon("Shader", EditorIcons.Bolt_I, Pink),
            [".vfx"] = TypeIcon("ParticleSystem", EditorIcons.Bolt_I, Pink),
            // Textures / images.
            [".png"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".jpg"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".jpeg"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".tga"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".dds"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".bmp"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".gif"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".hdr"] = TypeIcon("Cubemap", EditorIcons.Image_I, Blue),
            [".exr"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".psd"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".aseprite"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            [".ase"] = TypeIcon("Texture2D", EditorIcons.Image_I, Blue),
            // Materials.
            [".mat"] = TypeIcon("Material", EditorIcons.Palette_I, Pink),
            [".material"] = TypeIcon("Material", EditorIcons.Palette_I, Pink),
            [".physicmaterial"] = TypeIcon("PhysicMaterial", EditorIcons.Palette_I, Orange),
            [".physicmaterial2d"] = TypeIcon("PhysicsMaterial2D", EditorIcons.Palette_I, Orange),
            // Meshes / models.
            [".fbx"] = TypeIcon("PrefabModel", EditorIcons.Cubes_I, Purple),
            [".obj"] = TypeIcon("Mesh", EditorIcons.Cubes_I, Purple),
            [".gltf"] = TypeIcon("Mesh", EditorIcons.Cubes_I, Purple),
            [".glb"] = TypeIcon("Mesh", EditorIcons.Cubes_I, Purple),
            [".dae"] = TypeIcon("Mesh", EditorIcons.Cubes_I, Purple),
            [".blend"] = TypeIcon("Mesh", EditorIcons.Cubes_I, Purple),
            [".mesh"] = TypeIcon("Mesh", EditorIcons.Cubes_I, Purple),
            // Scenes / prefabs.
            [".scene"] = TypeIcon("SceneAsset", EditorIcons.Shapes_I, Amber),
            [".prefab"] = TypeIcon("PrefabNormal", EditorIcons.Cube_I, Blue),
            // Audio.
            [".mp3"] = TypeIcon("AudioClip", EditorIcons.Music_I, Cyan),
            [".wav"] = TypeIcon("AudioClip", EditorIcons.Music_I, Cyan),
            [".ogg"] = TypeIcon("AudioClip", EditorIcons.Music_I, Cyan),
            [".flac"] = TypeIcon("AudioClip", EditorIcons.Music_I, Cyan),
            // Fonts.
            [".ttf"] = TypeIcon("Font", EditorIcons.Font_I, Amber),
            [".otf"] = TypeIcon("Font", EditorIcons.Font_I, Amber),
            [".woff"] = TypeIcon("Font", EditorIcons.Font_I, Amber),
            // Animation.
            [".anim"] = TypeIcon("AnimationClip", EditorIcons.Film_I, Cyan),
            [".controller"] = TypeIcon("AnimatorController", EditorIcons.Film_I, Cyan),
            [".overridecontroller"] = TypeIcon("AnimatorOverrideController", EditorIcons.Film_I, Cyan),
            [".motion"] = TypeIcon("BlendTree", EditorIcons.Film_I, Cyan),
            [".mask"] = TypeIcon("AvatarMask", EditorIcons.Film_I, Cyan),
            [".timeline"] = TypeIcon("Animation", EditorIcons.Film_I, Cyan),
            // Rendering / engine assets.
            [".asset"] = TypeIcon("ScriptableObject", EditorIcons.FileLines_I, Gray),
            [".pipeline"] = TypeIcon("Shader", EditorIcons.Palette_I, Pink),
            [".renderer"] = TypeIcon("Shader", EditorIcons.Palette_I, Pink),
            [".volumeprofile"] = TypeIcon("ScriptableObject", EditorIcons.Palette_I, Pink),
            [".postprofile"] = TypeIcon("ScriptableObject", EditorIcons.Palette_I, Pink),
            [".buildprofile"] = TypeIcon("EditorSettings", EditorIcons.Hammer_I, Orange),
            [".navmesh"] = TypeIcon("NavMesh", EditorIcons.Map_I, Green),
            [".inputactions"] = TypeIcon("ScriptableObject", EditorIcons.Keyboard_I, Blue),
            [".terraindata"] = TypeIcon("TerrainData", EditorIcons.Mountain_I, Green),
            [".terrainlayer"] = TypeIcon("Terrain", EditorIcons.Mountain_I, Green),
            // 2D.
            [".tile"] = TypeIcon("Sprite", EditorIcons.Image_I, Blue),
            [".ruletile"] = TypeIcon("Sprite", EditorIcons.Image_I, Blue),
            [".weightedtile"] = TypeIcon("Sprite", EditorIcons.Image_I, Blue),
            [".animatedtile"] = TypeIcon("Sprite", EditorIcons.Image_I, Blue),
            [".gameobjecttile"] = TypeIcon("Sprite", EditorIcons.Image_I, Blue),
            [".tilepalette"] = TypeIcon("Sprite", EditorIcons.Image_I, Blue),
            [".spriteshape"] = TypeIcon("Sprite", EditorIcons.Image_I, Blue),
            [".spritelibrary"] = TypeIcon("Sprite", EditorIcons.Image_I, Blue),
            // Data / text.
            [".json"] = TypeIcon("TextAsset", EditorIcons.Code_I, Gray),
            [".txt"] = TypeIcon("TextAsset", EditorIcons.FileLines_I, Gray),
            [".md"] = TypeIcon("TextAsset", EditorIcons.FileLines_I, Gray),
            [".xml"] = TypeIcon("TextAsset", EditorIcons.Code_I, Gray),
            [".yaml"] = TypeIcon("TextAsset", EditorIcons.Code_I, Gray),
            [".yml"] = TypeIcon("TextAsset", EditorIcons.Code_I, Gray),
            [".csv"] = TypeIcon("TextAsset", EditorIcons.FileLines_I, Gray),
            [".log"] = TypeIcon("TextAsset", EditorIcons.FileLines_I, Gray),
            [".meta"] = TypeIcon("MetaFile", EditorIcons.FileLines_I, Gray),
            // Video.
            [".mp4"] = TypeIcon("MovieTexture", EditorIcons.Image_I, Red),
            [".mov"] = TypeIcon("MovieTexture", EditorIcons.Image_I, Red),
            [".mkv"] = TypeIcon("MovieTexture", EditorIcons.Image_I, Red),
            [".webm"] = TypeIcon("MovieTexture", EditorIcons.Image_I, Red),
            [".avi"] = TypeIcon("MovieTexture", EditorIcons.Image_I, Red),
            // Archives / packages.
            [".zip"] = TypeIcon("DefaultAsset", EditorIcons.LayerGroup_I, Gray),
            [".rar"] = TypeIcon("DefaultAsset", EditorIcons.LayerGroup_I, Gray),
            [".7z"] = TypeIcon("DefaultAsset", EditorIcons.LayerGroup_I, Gray),
            [".tar"] = TypeIcon("DefaultAsset", EditorIcons.LayerGroup_I, Gray),
            [".gz"] = TypeIcon("DefaultAsset", EditorIcons.LayerGroup_I, Gray),
            [".xenginepackage"] = TypeIcon("DefaultAsset", EditorIcons.LayerGroup_I, Purple),
        };

    private static readonly AssetTypeStyle _default = TypeIcon("DefaultAsset", EditorIcons.FileLines_I, Gray);

    /// <summary>Build a style that prefers an embedded Unity type PNG and falls back to <paramref name="glyph"/>.</summary>
    public static AssetTypeStyle UnityType(string pngStem, IOrigamiIcon glyph, Color color, bool bare = false)
        => TypeIcon(pngStem, glyph, color, bare);

    private static AssetTypeStyle TypeIcon(string pngStem, IOrigamiIcon glyph, Color color, bool bare = false)
    {
        IOrigamiIcon? bmp = TryUnityPng(pngStem);
        return new AssetTypeStyle
        {
            Icon = bmp ?? glyph,
            Color = color,
            Bare = bare,
            PreserveIconColor = bmp != null,
            TypeName = pngStem,
        };
    }

    /// <summary>Canonical engine type name for a file extension (".anim" → "AnimationClip"), or null
    /// when the extension is unknown. Used as the type label before an asset is ever imported.</summary>
    public static string? TypeNameForExtension(string extension)
        => extension != null && _map.TryGetValue(extension, out var style) ? style.TypeName : null;

    private static IOrigamiIcon? TryUnityPng(string? stem)
    {
        if (string.IsNullOrEmpty(stem)) return null;
        string key = stem.Replace(" ", "", System.StringComparison.Ordinal);
        return EditorPngIcon.TryLoad("Types." + key + ".png");
    }

    /// <summary>Register (or override) the style for an extension (leading dot, e.g. ".prefab").</summary>
    public static void Register(string extension, AssetTypeStyle style)
    {
        if (!string.IsNullOrEmpty(extension)) _map[extension] = style;
    }

    /// <summary>Style for an extension; falls back to <paramref name="typeLabel"/> (the asset's
    /// main type name) for engine assets whose extension isn't a well-known one (e.g. a <c>.asset</c> that is
    /// really TerrainData / a Texture / a Material).</summary>
    public static AssetTypeStyle For(string extension, string? typeLabel = null)
    {
        if (!string.IsNullOrEmpty(extension) && _map.TryGetValue(extension, out var s))
        {
            // `.asset` is a grab-bag of ScriptableObjects — prefer the concrete type icon when we have one.
            if (extension.Equals(".asset", System.StringComparison.OrdinalIgnoreCase)
                && TryStyleFromTypeName(typeLabel, out var specific))
                return specific;
            return s;
        }

        if (!string.IsNullOrEmpty(typeLabel))
        {
            if (TryStyleFromTypeName(typeLabel, out var fromType))
                return fromType;

            string t = typeLabel!;
            bool Has(string k) => t.Contains(k, System.StringComparison.OrdinalIgnoreCase);
            if (Has("Terrain"))                       return TypeIcon("TerrainData", EditorIcons.Mountain_I, Green);
            if (Has("Texture") || Has("Sprite"))      return TypeIcon("Texture2D", EditorIcons.Image_I, Blue);
            if (Has("Material"))                      return TypeIcon("Material", EditorIcons.Palette_I, Pink);
            if (Has("Mesh") || Has("Model"))          return TypeIcon("Mesh", EditorIcons.Cubes_I, Purple);
            if (Has("Shader") || Has("VFX") || Has("Visual Effect")) return TypeIcon("Shader", EditorIcons.Bolt_I, Pink);
            if (Has("Scene"))                         return TypeIcon("SceneAsset", EditorIcons.Shapes_I, Amber);
            if (Has("Prefab") || Has("GameObject"))   return TypeIcon("PrefabNormal", EditorIcons.Cube_I, Blue);
            if (Has("NavMesh") || Has("Navigation"))  return TypeIcon("NavMesh", EditorIcons.Map_I, Green);
            if (Has("Animation") || Has("Anim") || Has("Animator") || Has("BlendTree"))
                return TypeIcon("AnimationClip", EditorIcons.Film_I, Cyan);
            if (Has("Audio") || Has("Sound") || Has("Clip")) return TypeIcon("AudioClip", EditorIcons.Music_I, Cyan);
            if (Has("Font"))                          return TypeIcon("Font", EditorIcons.Font_I, Amber);
            if (Has("Script") || Has("MonoBehaviour")) return new() { Badge = "C#", Color = Green };
        }
        return _default;
    }

    private static bool TryStyleFromTypeName(string? typeLabel, out AssetTypeStyle style)
    {
        style = default;
        if (string.IsNullOrEmpty(typeLabel)) return false;
        string name = typeLabel;
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];

        if (TypePngAliases.TryGetValue(name, out string? alias) && TryUnityPng(alias) is { } aliased)
        {
            style = new AssetTypeStyle { Icon = aliased, Color = Gray, PreserveIconColor = true };
            return true;
        }

        if (TryUnityPng(name) is { } exact)
        {
            style = new AssetTypeStyle { Icon = exact, Color = Gray, PreserveIconColor = true };
            return true;
        }

        return false;
    }
}
