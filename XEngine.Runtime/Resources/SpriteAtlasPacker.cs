// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using RectpackSharp;

namespace XEngine.Runtime.Resources;

/// <summary>One source rectangle to pack. <see cref="SourceId"/> is the stable Sprite GUID/sub-asset id.</summary>
public readonly struct SpriteAtlasPackRequest
{
    public SpriteAtlasPackRequest(string sourceId, int width, int height)
    {
        SourceId = sourceId;
        Width = width;
        Height = height;
    }

    public string SourceId { get; }
    public int Width { get; }
    public int Height { get; }
}

public readonly struct SpriteAtlasPackSettings
{
    public int Padding { get; init; }
    public int Extrude { get; init; }
    public int MaxSize { get; init; }
    public bool Trim { get; init; }

    public static SpriteAtlasPackSettings Default => new() { Padding = 2, MaxSize = 2048 };
}

public readonly struct SpriteAtlasPackedRect
{
    public string SourceId { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Rotated { get; init; }
}

public readonly struct SpriteAtlasPackResult
{
    public int Width { get; init; }
    public int Height { get; init; }
    public SpriteAtlasPackedRect[] Rects { get; init; }

    public SpriteAtlasPackedRect Find(string sourceId)
    {
        SpriteAtlasPackedRect[] rects = Rects;
        for (int i = 0; i < rects.Length; i++)
        {
            if (rects[i].SourceId == sourceId)
                return rects[i];
        }

        throw new InvalidOperationException($"Sprite '{sourceId}' was not packed into the atlas.");
    }
}

/// <summary>
/// Deterministic rectangle packer for <see cref="SpriteAtlas"/>. Uses MIT RectpackSharp and never
/// rotates sprites (nine-slice, normals, and pixel-perfect sampling stay axis-aligned).
/// </summary>
public static class SpriteAtlasPacker
{
    public static SpriteAtlasPackResult Pack(ReadOnlySpan<SpriteAtlasPackRequest> sources, SpriteAtlasPackSettings settings)
    {
        int maxSize = settings.MaxSize > 0 ? settings.MaxSize : 2048;
        int padding = Math.Max(0, settings.Padding);
        if (sources.Length == 0)
            return new SpriteAtlasPackResult { Width = 0, Height = 0, Rects = Array.Empty<SpriteAtlasPackedRect>() };

        var working = new PackingRectangle[sources.Length];
        for (int i = 0; i < sources.Length; i++)
        {
            SpriteAtlasPackRequest src = sources[i];
            if (src.Width <= 0 || src.Height <= 0)
                throw new InvalidOperationException($"Sprite '{src.SourceId}' has a non-positive size.");

            int packedW = src.Width + padding;
            int packedH = src.Height + padding;
            if (packedW > maxSize || packedH > maxSize)
                throw new InvalidOperationException($"Sprite '{src.SourceId}' ({packedW}x{packedH} with padding) exceeds atlas MaxSize {maxSize}.");

            working[i] = new PackingRectangle
            {
                Id = i,
                Width = (uint)packedW,
                Height = (uint)packedH,
            };
        }

        PackingRectangle bounds;
        try
        {
            RectanglePacker.Pack(
                working,
                out bounds,
                PackingHints.FindBest,
                acceptableDensity: 1,
                stepSize: 1,
                maxBoundsWidth: (uint)maxSize,
                maxBoundsHeight: (uint)maxSize);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not pack {sources.Length} sprites into a {maxSize}x{maxSize} atlas.", ex);
        }

        var rects = new SpriteAtlasPackedRect[sources.Length];
        for (int i = 0; i < working.Length; i++)
        {
            int id = (int)working[i].Id;
            SpriteAtlasPackRequest src = sources[id];
            rects[id] = new SpriteAtlasPackedRect
            {
                SourceId = src.SourceId,
                X = (int)working[i].X,
                Y = (int)working[i].Y,
                Width = src.Width,
                Height = src.Height,
                Rotated = false,
            };
        }

        return new SpriteAtlasPackResult
        {
            Width = (int)bounds.Width,
            Height = (int)bounds.Height,
            Rects = rects,
        };
    }
}
