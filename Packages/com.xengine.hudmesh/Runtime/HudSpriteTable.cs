using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// One registered HUD sprite: source texture plus its sub-rect. Borders use engine pixel
/// order (left, top, right, bottom) matching <c>SpriteGeometry</c>. A zero border means the
/// sprite is not nine-sliced. <see cref="MaterialSlot"/> is assigned lazily by the
/// <see cref="HudManager"/> when the sprite is first used.
/// </summary>
public sealed class HudSpriteInfo
{
    public string Name = "";
    public Texture2D? Texture;

    /// <summary>Normalized UV of the sub-rect's bottom-left corner.</summary>
    public Float2 UvMin;

    /// <summary>Normalized UV of the sub-rect's top-right corner.</summary>
    public Float2 UvMax = Float2.One;

    public float Width;
    public float Height;

    public Float4 Border;

    public int MaterialSlot = -1;
}

/// <summary>
/// Name → sprite registry. The bundled set maps every UnityHUD art PNG under
/// <c>Runtime/Assets/Sprites</c> to a full-rect sprite; a packed atlas (editor packer,
/// later milestone) can register sub-rect entries over the same names.
/// </summary>
public sealed class HudSpriteTable
{
    private readonly Dictionary<string, HudSpriteInfo> _sprites = new(StringComparer.OrdinalIgnoreCase);

    public HudSpriteInfo? Get(string name)
        => _sprites.TryGetValue(name, out HudSpriteInfo? info) ? info : null;

    public HudSpriteInfo Register(string name, Texture2D texture)
    {
        HudSpriteInfo info = new()
        {
            Name = name,
            Texture = texture,
            UvMin = Float2.Zero,
            UvMax = Float2.One,
            Width = texture.Width,
            Height = texture.Height,
        };
        _sprites[name] = info;
        return info;
    }

    /// <summary>Registers a sprite backed by a bundled PNG file (loaded through <see cref="HudmeshAssetLibrary"/>).</summary>
    public HudSpriteInfo? RegisterFile(string name, string fileName)
    {
        Texture2D? texture = HudmeshAssetLibrary.LoadSpriteTexture(fileName);
        return texture is null ? null : Register(name, texture);
    }

    public bool Remove(string name) => _sprites.Remove(name);

    /// <summary>
    /// Registers a packed atlas and all sprites listed in its <c>.hudsprites</c> index
    /// (lines: <c>sprite &lt;name&gt; &lt;x&gt; &lt;y&gt; &lt;w&gt; &lt;h&gt; &lt;bl&gt; &lt;bt&gt; &lt;br&gt; &lt;bb&gt;</c>,
    /// pixel coordinates with y measured from the atlas bottom — the engine's texture
    /// orientation). Lines starting with <c>atlas</c> declare the atlas image and size.
    /// Returns the number of sprites registered.
    /// </summary>
    public int RegisterAtlas(string atlasImageFile, string indexText)
    {
        Texture2D? atlas = HudmeshAssetLibrary.LoadSpriteTexture(atlasImageFile);
        return atlas is null ? 0 : RegisterAtlas(atlas, indexText);
    }

    public int RegisterAtlas(Texture2D atlas, string indexText)
    {
        int count = 0;
        foreach (string rawLine in indexText.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 5 || parts[0] != "sprite")
                continue;

            HudSpriteInfo info = new()
            {
                Name = parts[1],
                Texture = atlas,
                Width = float.Parse(parts[4]),
                Height = float.Parse(parts[5]),
                Border = parts.Length >= 9
                    ? new Float4(float.Parse(parts[6]), float.Parse(parts[7]), float.Parse(parts[8]), float.Parse(parts[9]))
                    : Float4.Zero,
            };
            float x = float.Parse(parts[2]);
            float y = float.Parse(parts[3]);
            info.UvMin = new Float2(x / atlas.Width, y / atlas.Height);
            info.UvMax = new Float2((x + info.Width) / atlas.Width, (y + info.Height) / atlas.Height);
            _sprites[info.Name] = info;
            count++;
        }
        return count;
    }

    /// <summary>The bundled sprite names (file stems under Runtime/Assets/Sprites).</summary>
    public static readonly string[] BundledNames =
    {
        "Blood", "Blood_bg", "_Bg",
        "_Fill_1", "_Fill_2", "_Fill_3", "_Fill_4",
        "UI_headtop_bubble_001_17", "UI_headtop_sign_001_6", "UI_headtop_sign_002_7",
        "slider_icontype_01_fill", "slider_icontype_01_frame",
    };

    /// <summary>Bundled digit-set prefixes (files are e.g. "b0".."b9", "b+", "b-").</summary>
    public static readonly char[] DigitPrefixes = { 'b', 'c', 'h', 'l' };

    public static HudSpriteTable CreateBundled()
    {
        HudSpriteTable table = new();
        foreach (string name in BundledNames)
            table.RegisterFile(name, name + ".png");

        string[] digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "+", "-" };
        foreach (char prefix in DigitPrefixes)
        foreach (string digit in digits)
            table.RegisterFile(prefix + digit, prefix + digit + ".png");

        return table;
    }
}
