// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.Importers;

/// <summary>
/// Claims <c>.aseprite</c>/<c>.ase</c> so they are not treated as unknown binaries.
/// Priority is above the default so a later texture importer cannot steal the extension.
/// Opaque frames emit a packed <see cref="Texture2D"/> plus one <see cref="Sprite"/> per frame.
/// </summary>
[ImporterFor(".aseprite", ".ase", Priority = 100)]
public sealed class AsepriteImporter : AssetImporter
{
    public override int Version => 4;

    public const string TextureStableId = "texture:sheet";
    public const float DefaultPixelsPerUnit = 100f;

    public override bool Import(ImportContext ctx)
    {
        AsepriteDocument doc = AsepriteDocument.Read(File.ReadAllBytes(ctx.AbsolutePath));
        var main = new AsepriteAsset
        {
            Width = doc.Width,
            Height = doc.Height,
            FrameCount = doc.FrameCount,
        };
        ctx.SetMainAsset(main);

        Sprite[] spritesByIndex = EmitSheet(ctx, doc, main);

        for (int i = 0; i < doc.FrameCount; i++)
        {
            string frameId = $"frame:{i}";
            if (ExtractGeneratedAssets.TrySkipExtracted(ctx, frameId))
                continue;
            int duration = 0;
            for (int p = 0; p < doc.FramePixels.Count; p++)
            {
                if (doc.FramePixels[p].Index == i)
                {
                    duration = doc.FramePixels[p].DurationMs;
                    break;
                }
            }

            ctx.AddSubAsset(frameId, $"Frame {i}", new AsepriteFrameAsset
            {
                Index = i,
                DurationMs = duration,
                Sprite = i < spritesByIndex.Length ? spritesByIndex[i] : default,
            });
        }

        for (int i = 0; i < doc.Tags.Count; i++)
        {
            AsepriteTag tag = doc.Tags[i];
            string tagId = $"tag:{tag.Name}";
            if (ExtractGeneratedAssets.TrySkipExtracted(ctx, tagId))
                continue;
            var tagSprites = new List<AssetRef<Sprite>>();
            int from = Math.Max(0, tag.FromFrame);
            int to = Math.Min(doc.FrameCount - 1, tag.ToFrame);
            for (int f = from; f <= to && f < spritesByIndex.Length; f++)
            {
                if (spritesByIndex[f] != null)
                    tagSprites.Add(spritesByIndex[f]);
            }

            ctx.AddSubAsset(tagId, tag.Name, new AsepriteTagAsset
            {
                TagName = tag.Name,
                FromFrame = tag.FromFrame,
                ToFrame = tag.ToFrame,
                Sprites = tagSprites,
            });
        }

        for (int i = 0; i < doc.Slices.Count; i++)
        {
            AsepriteSlice slice = doc.Slices[i];
            string sliceId = $"slice:{slice.Name}";
            if (ExtractGeneratedAssets.TrySkipExtracted(ctx, sliceId))
                continue;
            ctx.AddSubAsset(sliceId, slice.Name, new AsepriteSliceAsset
            {
                SliceName = slice.Name,
                X = slice.X,
                Y = slice.Y,
                Width = slice.Width,
                Height = slice.Height,
            });
        }

        return true;
    }

    static Sprite[] EmitSheet(ImportContext ctx, AsepriteDocument doc, AsepriteAsset main)
    {
        var sprites = new Sprite[doc.FrameCount];
        AsepritePackedSheet? sheet = AsepriteSpriteSheet.PackHorizontal(doc.FramePixels);
        if (sheet == null || !AsepriteSpriteSheet.HasOpaque(sheet))
            return sprites;

        Texture2D? texture = TryCreateSheetTexture(sheet);
        Guid textureGuid = default;
        if (texture != null)
        {
            if (ExtractGeneratedAssets.TrySkipExtracted(ctx, TextureStableId))
            {
                if (ctx.Settings != null && ExtractGeneratedAssets.TryGetExternal(ctx.Settings, TextureStableId, out Guid mapped))
                    textureGuid = mapped;
                texture.Dispose();
                texture = null;
            }
            else
            {
                ctx.AddSubAsset(TextureStableId, "Sheet", texture);
                textureGuid = texture.AssetID;
                main.Texture = texture;
            }
        }

        foreach (AsepritePackedFrame frame in sheet.Frames)
        {
            if ((uint)frame.Index >= (uint)sprites.Length)
                continue;
            if (ExtractGeneratedAssets.TrySkipExtracted(ctx, frame.SpriteId))
                continue;

            var sprite = new Sprite
            {
                Name = frame.DisplayName,
                Rect = frame.Rect,
                Pivot = new Float2(0.5f, 0.5f),
                PixelsPerUnit = DefaultPixelsPerUnit,
            };
            if (texture != null)
                sprite.Texture = texture;
            else if (textureGuid != Guid.Empty)
                sprite.Texture = new AssetRef<Texture2D>(textureGuid);

            sprite.BuildQuadGeometry(sheet.Width, sheet.Height);
            ctx.AddSubAsset(frame.SpriteId, frame.DisplayName, sprite);
            sprites[frame.Index] = sprite;
        }

        return sprites;
    }

    static Texture2D? TryCreateSheetTexture(AsepritePackedSheet sheet)
    {
        try
        {
            var texture = new Texture2D((uint)sheet.Width, (uint)sheet.Height, generateMipmaps: false,
                TextureImageFormat.Color4b);
            texture.Name = "Sheet";
            byte[] flipped = AsepriteSpriteSheet.FlipVertical(sheet.RgbaTopLeft, sheet.Width, sheet.Height);
            texture.SetData<byte>(flipped);
            texture.SetTextureFilters(TextureMin.Nearest, TextureMag.Nearest);
            texture.SetWrapModes(TextureWrap.ClampToEdge, TextureWrap.ClampToEdge);
            return texture;
        }
        catch
        {
            return null;
        }
    }
}
