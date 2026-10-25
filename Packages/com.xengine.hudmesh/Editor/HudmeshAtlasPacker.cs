using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using ImageMagick;

using XEngine.Editor;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Hudmesh.Editor;

/// <summary>
/// Packs a folder of HUD sprite PNGs into a single atlas image plus a <c>.hudsprites</c>
/// text index (UnityHUD's editor atlas packer port; the BinaryFormatter sidecar becomes an
/// Echo-diffable text file). The packer is deterministic (RectpackSharp, no rotation).
/// </summary>
internal static class HudmeshAtlasPacker
{
    [MenuItem("Tools/Hudmesh/Pack Bundled Sprites", priority: 100, Icon = EditorIcons.FileImport)]
    public static void PackBundledSprites()
    {
        string? blood = HudmeshAssetLibrary.FindAssetFile(Path.Combine("Assets", "Sprites", "Blood.png"));
        if (blood is null)
            throw new InvalidOperationException("Bundled sprite folder was not found.");

        string folder = Path.GetDirectoryName(blood)!;
        string atlasPath = Path.Combine(folder, "hud_atlas1.png");
        string indexPath = Path.Combine(folder, "hud_atlas1.hudsprites");
        int count = PackFolder(folder, atlasPath, indexPath, "hud_atlas1.png");
        Debug.Log($"[hudmesh] packed {count} sprites into {atlasPath}");
    }

    /// <summary>
    /// Packs every <c>*.png</c> in <paramref name="sourceFolder"/> (except an existing atlas
    /// output) into <paramref name="atlasPath"/> and writes the sprite index to
    /// <paramref name="indexPath"/>. Returns the packed sprite count.
    /// </summary>
    public static int PackFolder(string sourceFolder, string atlasPath, string indexPath, string atlasFileName)
    {
        string[] files = Directory.GetFiles(sourceFolder, "*.png");
        var sources = new List<(string Name, MagickImage Image)>();
        var requests = new List<SpriteAtlasPackRequest>();
        try
        {
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Equals(Path.GetFileNameWithoutExtension(atlasPath), StringComparison.OrdinalIgnoreCase))
                    continue;
                MagickImage image = new(file);
                sources.Add((name, image));
                requests.Add(new SpriteAtlasPackRequest(name, (int)image.Width, (int)image.Height));
            }

            SpriteAtlasPackResult result = SpriteAtlasPacker.Pack(
                requests.ToArray(), new SpriteAtlasPackSettings { Padding = 1, MaxSize = 2048 });

            // Compose the atlas RGBA pixels manually (y up: flip each source row).
            byte[] pixels = new byte[result.Width * result.Height * 4];
            foreach ((string name, MagickImage image) in sources)
            {
                SpriteAtlasPackedRect rect = result.Find(name);
                byte[] rgba = image.ToByteArray(MagickFormat.Rgba)
                    ?? throw new InvalidOperationException($"failed to read RGBA pixels for {name}");
                for (int row = 0; row < image.Height; row++)
                {
                    // Source row 0 is the top; atlas y is measured from the bottom.
                    int srcOffset = row * (int)image.Width * 4;
                    int dstRow = result.Height - rect.Y - rect.Height + row;
                    int dstOffset = (dstRow * result.Width + rect.X) * 4;
                    Array.Copy(rgba, srcOffset, pixels, dstOffset, (int)image.Width * 4);
                }
            }

            var settings = new MagickReadSettings
            {
                Format = MagickFormat.Rgba,
                Width = (uint)result.Width,
                Height = (uint)result.Height,
                Depth = 8,
            };
            using (MagickImage atlas = new(pixels, settings))
            {
                atlas.Write(atlasPath);
            }

            File.WriteAllText(indexPath, WriteIndex(atlasFileName, result), Encoding.UTF8);
            return sources.Count;
        }
        finally
        {
            foreach ((_, MagickImage image) in sources)
                image.Dispose();
        }
    }

    private static string WriteIndex(string atlasFileName, SpriteAtlasPackResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# Hudmesh sprite index — pixel coords, y measured from the atlas bottom\n");
        sb.Append($"atlas {atlasFileName} {result.Width} {result.Height}\n");
        foreach (SpriteAtlasPackedRect rect in result.Rects)
        {
            sb.Append($"sprite {rect.SourceId} {rect.X} {rect.Y} {rect.Width} {rect.Height} 0 0 0 0\n");
        }
        return sb.ToString();
    }
}
