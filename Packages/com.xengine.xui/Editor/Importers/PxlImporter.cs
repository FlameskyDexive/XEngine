// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.XUI.Core;

namespace XEngine.XUI.Editor;

/// <summary>
/// Imports a <c>.pxl</c> pixel-grid file as a point-filtered texture plus one <see cref="Sprite"/> per
/// section, carrying the 9-slice border the file declared.
/// </summary>
/// <remarks>
/// <para>
/// Every section of a file lands in one texture, stacked with a one-pixel transparent gap. A button's
/// normal and pressed states are authored together and belong together — one texture keeps them in the
/// same atlas page and the same import.
/// </para>
/// <para>
/// Filtering is Nearest and there are no mipmaps. Any smoothing would defeat the point: the whole
/// value of authoring art as text is that a single wrong pixel is visible.
/// </para>
/// </remarks>
[ImporterFor(".pxl")]
public sealed class PxlImporter : AssetImporter
{
    private const string TextureStableId = "__pxl_texture";

    public override int Version => 1;

    public override bool Import(ImportContext ctx)
    {
        string text;
        try
        {
            text = File.ReadAllText(ctx.AbsolutePath);
        }
        catch (IOException ex)
        {
            Debug.LogError("Failed to read pixel document '" + ctx.AbsolutePath + "': " + ex.Message);
            return false;
        }

        PxlDocument document;
        try
        {
            document = PxlParser.Parse(text, Path.GetFileName(ctx.AbsolutePath));
        }
        catch (PxlParseException ex)
        {
            Debug.LogError(ex.Message);
            return false;
        }

        WarnAboutUnresolvedColors(document, ctx.AbsolutePath);

        byte[] rgbaTopLeft = PxlRasterizer.RasterizeSheet(
            document, scale: 1, out int width, out int height, out List<PxlSheetEntry> entries);
        if (width == 0 || height == 0)
        {
            Debug.LogError("Pixel document '" + ctx.AbsolutePath + "' produced no pixels.");
            return false;
        }

        if (TryCreateTexture(rgbaTopLeft, width, height) is not { IsDisposed: false } texture)
        {
            Debug.LogError("Could not create a texture for '" + ctx.AbsolutePath + "'.");
            return false;
        }

        ctx.AddSubAsset(TextureStableId, "Texture", texture);

        Sprite? main = null;
        for (int i = 0; i < entries.Count; i++)
        {
            PxlSheetEntry entry = entries[i];
            PxlSection? section = document.FindSection(entry.Name);
            if (section == null) continue;

            Sprite sprite = CreateSprite(texture, entry, section, document, height);
            ctx.AddSubAsset(entry.Name, entry.Name, sprite);
            main ??= sprite;
        }

        if (main is not { IsDisposed: false })
        {
            Debug.LogError("Pixel document '" + ctx.AbsolutePath + "' produced no sprites.");
            return false;
        }

        // The first section is the main asset, so a single-section file drags into a field as itself
        // rather than making the author expand a sub-asset list to find the only thing in there.
        ctx.SetMainAsset(main);
        return true;
    }

    private static Sprite CreateSprite(
        Texture2D texture,
        PxlSheetEntry entry,
        PxlSection section,
        PxlDocument document,
        int sheetHeight)
    {
        // Sheet coordinates are top-down; sprite rects are bottom-up.
        int bottom = sheetHeight - (entry.Y + entry.Height);

        var sprite = new Sprite
        {
            Name = entry.Name,
            Rect = new SpriteRect(entry.X, bottom, entry.Width, entry.Height),
            Pivot = new Float2(0.5f, 0.5f),
            PixelsPerUnit = document.PixelsPerUnit,
            Texture = texture,
        };

        if (section.HasBorder)
        {
            // Sprite.Border is (Left, Top, Right, Bottom); the file declares top,right,bottom,left.
            sprite.Border = new Float4(
                section.BorderLeft,
                section.BorderTop,
                section.BorderRight,
                section.BorderBottom);
        }

        sprite.BuildQuadGeometry((int)texture.Width, (int)texture.Height);
        return sprite;
    }

    private static Texture2D? TryCreateTexture(byte[] rgbaTopLeft, int width, int height)
    {
        try
        {
            var texture = new Texture2D(
                (uint)width, (uint)height, generateMipmaps: false, TextureImageFormat.Color4b)
            {
                Name = "Texture",
            };
            texture.SetData<byte>(FlipVertical(rgbaTopLeft, width, height));
            texture.SetTextureFilters(TextureMin.Nearest, TextureMag.Nearest);
            texture.SetWrapModes(TextureWrap.ClampToEdge, TextureWrap.ClampToEdge);
            return texture;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The rasterizer emits top row first; textures upload bottom row first.</summary>
    private static byte[] FlipVertical(byte[] rgba, int width, int height)
    {
        var flipped = new byte[rgba.Length];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(rgba, y * stride, flipped, (height - 1 - y) * stride, stride);
        return flipped;
    }

    private static void WarnAboutUnresolvedColors(PxlDocument document, string path)
    {
        foreach (KeyValuePair<char, string> unresolved in document.UnresolvedNames)
        {
            Debug.LogWarning(
                "'" + Path.GetFileName(path) + "': character '" + unresolved.Key + "' uses the colour name \""
                + unresolved.Value + "\", which did not resolve. Those pixels import transparent. "
                + "Use a hex value such as #3a4466.");
        }
    }
}
