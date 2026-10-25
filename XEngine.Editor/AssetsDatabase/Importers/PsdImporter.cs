// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using ImageMagick;
using ImageMagick.Formats;

using XEngine.Runtime;

namespace XEngine.Editor.Importers;

/// <summary>
/// Claims <c>.psd</c> ahead of <see cref="TextureImporter"/> so layered Photoshop files are not
/// flattened as a single texture. Layer names come from Magick.NET; sprites are a later slice.
/// </summary>
[ImporterFor(".psd", Priority = 100)]
public sealed class PsdImporter : AssetImporter
{
    public override int Version => 3;

    public override bool Import(ImportContext ctx)
    {
        PsdDocument doc = PsdDocument.Read(File.ReadAllBytes(ctx.AbsolutePath));
        ctx.SetMainAsset(new PsdAsset
        {
            Width = doc.Width,
            Height = doc.Height,
        });

        AddLayerSubAssets(ctx);
        return true;
    }

    private static void AddLayerSubAssets(ImportContext ctx)
    {
        try
        {
            using var images = new MagickImageCollection();
            images.Read(ctx.AbsolutePath, MagickFormat.Psd);
            for (int i = 0; i < images.Count; i++)
            {
                IMagickImage<byte> image = images[i];
                string name = LayerName(image);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string stableId = $"layer:{i}";
                if (ExtractGeneratedAssets.TrySkipExtracted(ctx, stableId))
                    continue;

                ctx.AddSubAsset(stableId, name, new PsdLayerAsset
                {
                    Index = i,
                    LayerName = name,
                });
            }
        }
        catch (MagickException)
        {
            // Header-only fixtures and truncated files still import dimensions.
        }
    }

    private static string LayerName(IMagickImage<byte> image)
    {
        PsdAdditionalInfo? info = PsdAdditionalInfo.FromImage(image);
        if (!string.IsNullOrWhiteSpace(info?.LayerName))
            return info.LayerName;
        return image.Label ?? "";
    }
}
