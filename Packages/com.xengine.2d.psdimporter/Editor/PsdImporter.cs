// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using PaintDotNet.Data.PhotoshopFileType;
using PDNWrapper;

using Color32 = PDNWrapper.Color32;

using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.TwoD.PsdImporter;

/// <summary>
/// Imports a layered Photoshop document: the flattened composite as the main texture, plus one
/// sprite per pixel layer, packed into a single atlas.
/// </summary>
/// <remarks>
/// <para>
/// Priority 200 beats the built-in Magick.NET-based placeholder (priority 100), so installing this
/// package upgrades every <c>.psd</c> in the project from "dimensions and layer names" to real
/// pixels — and the package is the sole owner of <c>.psb</c>.
/// </para>
/// <para>
/// Hidden layers still import. Whether a hidden layer means "cut content" or "alternate state" is
/// an authoring decision the consumer makes with the layer's own visibility flag in hand; an
/// importer that silently drops them makes that decision for everyone, invisibly.
/// </para>
/// <para>
/// Group structure survives in the sprite <b>names</b> (<c>header/title</c>): a slash-joined path
/// is stable across sibling reorders, where a flat layer name would collide the moment a designer
/// reuses "bg" in two groups.
/// </para>
/// </remarks>
[ImporterFor(".psd", ".psb", Priority = 200)]
public sealed class PsdLayerImporter : AssetImporter
{
    private const string AtlasStableId = "__psd_atlas";
    private const int AtlasPadding = 1;

    public override int Version => 1;

    public override bool Import(ImportContext ctx)
    {
        Document document;
        PhotoshopFile.PsdFile file;
        try
        {
            using FileStream stream = File.OpenRead(ctx.AbsolutePath);
            document = PsdLoad.Load(stream, out file);
        }
        catch (Exception ex) when (ex is PhotoshopFile.PsdInvalidException or IOException or EndOfStreamException)
        {
            Debug.LogError("Failed to import PSD '" + ctx.AbsolutePath + "': " + ex.Message);
            return false;
        }

        // ---- flattened composite: what Photoshop shows, as the main asset -----------------
        Texture2D? composite = CreateCompositeTexture(file, document);
        if (composite is not { IsDisposed: false })
        {
            Debug.LogError("PSD '" + ctx.AbsolutePath + "' produced no composite image.");
            return false;
        }
        composite.Name = Path.GetFileNameWithoutExtension(ctx.AbsolutePath);
        ctx.SetMainAsset(composite);

        // ---- per-layer sprites, packed into one atlas -------------------------------------
        List<(string Path, BitmapLayer Layer)> pixelLayers = CollectPixelLayers(document);
        if (pixelLayers.Count > 0)
        {
            Texture2D? atlas = PackAtlas(pixelLayers, out List<Rectangle> placements);
            if (atlas is { IsDisposed: false })
            {
                atlas.Name = "Atlas";
                ctx.AddSubAsset(AtlasStableId, "Atlas", atlas);

                for (int i = 0; i < pixelLayers.Count; i++)
                {
                    (string path, BitmapLayer layer) = pixelLayers[i];
                    Rectangle place = placements[i];

                    // Sprite rects are bottom-up; the atlas rows were written top-down.
                    int bottom = (int)atlas.Height - (place.Y + place.Height);
                    var sprite = new Sprite
                    {
                        Name = path,
                        Rect = new SpriteRect(place.X, bottom, place.Width, place.Height),
                        Pivot = new Float2(0.5f, 0.5f),
                        PixelsPerUnit = 100f,
                        Texture = atlas,
                    };
                    sprite.BuildQuadGeometry((int)atlas.Width, (int)atlas.Height);
                    ctx.AddSubAsset("layer:" + path, path, sprite);
                }
            }
        }

        file.Cleanup();
        document.Dispose();
        return true;
    }

    // ------------------------------------------------------------------
    //  Composite
    // ------------------------------------------------------------------

    private static Texture2D? CreateCompositeTexture(PhotoshopFile.PsdFile file, Document document)
    {
        if (document.width <= 0 || document.height <= 0) return null;

        file.BaseLayer.CreateMissingChannels();
        BitmapLayer flattened = PDNWrapper.Layer.CreateBackgroundLayer(document.width, document.height);
        ImageDecoderPdn.DecodeImage(flattened, file.BaseLayer);
        Texture2D? texture = CreateTexture(flattened.Surface);
        flattened.Dispose();
        return texture;
    }

    // ------------------------------------------------------------------
    //  Layers → atlas
    // ------------------------------------------------------------------

    private static List<(string, BitmapLayer)> CollectPixelLayers(Document document)
    {
        var result = new List<(string, BitmapLayer)>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (BitmapLayer root in document.Layers)
            Collect(root, string.Empty, result, taken);
        return result;
    }

    private static void Collect(
        BitmapLayer layer,
        string prefix,
        List<(string, BitmapLayer)> into,
        HashSet<string> taken)
    {
        string name = string.IsNullOrWhiteSpace(layer.Name) ? "Layer" : layer.Name.Trim();
        // A path separator inside one layer's own name would fake a group boundary.
        name = name.Replace('/', '_');
        string path = prefix.Length == 0 ? name : prefix + "/" + name;

        if (layer.IsGroup)
        {
            foreach (BitmapLayer child in layer.ChildLayer)
                Collect(child, path, into, taken);
            return;
        }

        if (layer.IsEmpty) return;

        string unique = path;
        for (int suffix = 2; !taken.Add(unique); suffix++)
            unique = path + " (" + suffix + ")";
        into.Add((unique, layer));
    }

    /// <summary>
    /// Shelf-packs the layers into one texture, tallest first.
    /// </summary>
    /// <remarks>
    /// One atlas rather than one texture per layer for the same reason the <c>.pxl</c> importer packs
    /// a sheet: a document's layers are used together, and belong on one page. Shelf packing wastes
    /// some space against a real rect packer, but its placement is trivially deterministic — the same
    /// document always produces the same atlas, byte for byte, which is what reimport stability needs.
    /// </remarks>
    private static Texture2D? PackAtlas(
        List<(string Path, BitmapLayer Layer)> layers,
        out List<Rectangle> placements)
    {
        placements = new List<Rectangle>(layers.Count);

        var order = new List<int>();
        for (int i = 0; i < layers.Count; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            int byHeight = layers[b].Layer.localRect.Height.CompareTo(layers[a].Layer.localRect.Height);
            return byHeight != 0 ? byHeight : a.CompareTo(b);
        });

        int maxLayerWidth = 0;
        long area = 0;
        foreach ((_, BitmapLayer layer) in layers)
        {
            maxLayerWidth = Math.Max(maxLayerWidth, layer.localRect.Width + AtlasPadding);
            area += (long)(layer.localRect.Width + AtlasPadding) * (layer.localRect.Height + AtlasPadding);
        }

        int atlasWidth = Math.Max(64, Math.Max(maxLayerWidth, (int)Math.Sqrt(area) + 1));

        // First pass places rects; the atlas height is whatever the shelves add up to.
        var slots = new Rectangle[layers.Count];
        int cursorX = 0;
        int cursorY = 0;
        int shelfHeight = 0;
        foreach (int index in order)
        {
            Rectangle rect = layers[index].Layer.localRect;
            if (cursorX + rect.Width > atlasWidth)
            {
                cursorX = 0;
                cursorY += shelfHeight;
                shelfHeight = 0;
            }
            slots[index] = new Rectangle(cursorX, cursorY, rect.Width, rect.Height);
            cursorX += rect.Width + AtlasPadding;
            shelfHeight = Math.Max(shelfHeight, rect.Height + AtlasPadding);
        }
        int atlasHeight = Math.Max(64, cursorY + shelfHeight);

        var rgba = new byte[atlasWidth * atlasHeight * 4];
        for (int i = 0; i < layers.Count; i++)
        {
            BitmapLayer layer = layers[i].Layer;
            Rectangle slot = slots[i];
            placements.Add(slot);

            Color32[] pixels = layer.Surface.color;
            int width = layer.localRect.Width;
            for (int y = 0; y < layer.localRect.Height; y++)
            {
                int dstRow = ((slot.Y + y) * atlasWidth + slot.X) * 4;
                int srcRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 c = pixels[srcRow + x];
                    int dst = dstRow + x * 4;
                    rgba[dst] = c.r;
                    rgba[dst + 1] = c.g;
                    rgba[dst + 2] = c.b;
                    rgba[dst + 3] = c.a;
                }
            }
        }

        return CreateTexture(rgba, atlasWidth, atlasHeight);
    }

    // ------------------------------------------------------------------
    //  Texture plumbing
    // ------------------------------------------------------------------

    private static Texture2D? CreateTexture(Surface surface)
    {
        var rgba = new byte[surface.width * surface.height * 4];
        Color32[] pixels = surface.color;
        for (int i = 0; i < pixels.Length; i++)
        {
            rgba[i * 4] = pixels[i].r;
            rgba[i * 4 + 1] = pixels[i].g;
            rgba[i * 4 + 2] = pixels[i].b;
            rgba[i * 4 + 3] = pixels[i].a;
        }
        return CreateTexture(rgba, surface.width, surface.height);
    }

    private static Texture2D? CreateTexture(byte[] rgbaTopDown, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        try
        {
            var texture = new Texture2D(
                (uint)width, (uint)height, generateMipmaps: false, TextureImageFormat.Color4b);
            texture.SetData<byte>(FlipVertical(rgbaTopDown, width, height));
            // Linear, not Nearest: PSD UI art is continuous-tone, unlike the .pxl pixel grids.
            texture.SetTextureFilters(TextureMin.Linear, TextureMag.Linear);
            texture.SetWrapModes(TextureWrap.ClampToEdge, TextureWrap.ClampToEdge);
            return texture;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The decoder emits top row first; textures upload bottom row first.</summary>
    private static byte[] FlipVertical(byte[] rgba, int width, int height)
    {
        var flipped = new byte[rgba.Length];
        int stride = width * 4;
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(rgba, y * stride, flipped, (height - 1 - y) * stride, stride);
        return flipped;
    }
}
