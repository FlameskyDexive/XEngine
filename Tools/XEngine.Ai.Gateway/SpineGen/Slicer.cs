// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Ai.SpineGen;

/// <summary>Slicing knobs. Defaults tuned for clean stylized character illustrations.</summary>
public sealed class SliceOptions
{
    /// <summary>Alpha (0-255) at or above which a pixel counts as character.</summary>
    public int AlphaThreshold { get; init; } = 8;
    /// <summary>Components smaller than this many pixels are dropped as anti-alias noise.</summary>
    public int MinAreaPixels { get; init; } = 48;
    /// <summary>Padding added around each crop rect, in source pixels.</summary>
    public int CropPadding { get; init; } = 0;
    /// <summary>How far (Chebyshev, in source pixels) an empty pixel may sit from a part's edge —
    /// and from a higher part's pixels — to count as an inpaint seed.</summary>
    public int SeedReach { get; init; } = 2;
    /// <summary>How solid-touching regions are separated. Default keeps v1 behavior; the
    /// watershed mode additionally cuts each alpha island at distance-transform saddles.</summary>
    public SliceSplitMode Split { get; init; } = SliceSplitMode.ConnectedComponents;
    /// <summary>Watershed marker minimum thickness in distance-transform px (0 = auto:
    /// max(12, minSide/96)). Ignored outside the DistanceWatershed mode.</summary>
    public int MarkerMinDepth { get; init; } = 0;
    /// <summary>Minimum px between watershed markers (0 = auto: max(32, maxSide/16)).
    /// Ignored outside the DistanceWatershed mode.</summary>
    public int MarkerSpacing { get; init; } = 0;
}

public sealed class SlicePartImage
{
    public required string Name { get; init; }
    /// <summary>Connected-component id before watershed splits this island into sub-parts.</summary>
    public required int SourceIsland { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int PixelCount { get; set; }
    public required int LayerIndex { get; init; }

    /// <summary>Cropped RGBA (w*h*4), transparent where other pixels live — callers write PNGs.</summary>
    public required byte[] Rgba { get; init; }

    /// <summary>w*h mask: 255 = this part's pixel, 0 = empty/other. Length w*h.</summary>
    public required byte[] Mask { get; init; }

    /// <summary>w*h mask: 255 = inpaint seed — transparent pixels hugging this part's edge that sit
    /// next to a HIGHER layer, i.e. where this part most likely continues underneath.</summary>
    public byte[]? InpaintSeedMask { get; internal set; }

    public int InpaintSeedPixels { get; internal set; }
    public SliceRect InpaintSeedRect { get; internal set; } = SliceRect.Empty;
}

public sealed class SliceRunResult
{
    public required int ImageWidth { get; init; }
    public required int ImageHeight { get; init; }
    public required float OpaqueRatio { get; init; }
    public required int DiscardedNoiseParts { get; init; }

    /// <summary>Split mode actually used (echoes the option, for manifests).</summary>
    public required SliceSplitMode Split { get; init; }

    /// <summary>Resolved watershed marker tuning (0 when the mode is ConnectedComponents).</summary>
    public required int MarkerMinDepth { get; init; }
    public required int MarkerSpacing { get; init; }

    /// <summary>Back-to-front (LayerIndex 0 = furthest). Deterministic order.</summary>
    public required List<SlicePartImage> Parts { get; init; }
}

/// <summary>
/// Splits a single character illustration into layered parts: alpha threshold → 8-connected
/// components → noise drop → deterministic painter ordering (large parts behind small details) →
/// per-part occlusion masks against every higher layer.
///
/// Pure raster logic on an RGBA buffer — no I/O, no image-codec dependency, fully deterministic.
/// </summary>
public static class Slicer
{
    public static SliceRunResult Slice(byte[] rgba, int width, int height, SliceOptions options)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(options);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4)
            throw new ArgumentException("RGBA buffer does not match width/height.");
        if (options.AlphaThreshold is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(options), "AlphaThreshold must be 0..255.");

        // Bounding-box pass also decides whether the illustration even has content.
        long opaque = 0;
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] >= options.AlphaThreshold)
                opaque++;
        float opaqueRatio = opaque / (float)(width * height);
        if (opaque == 0)
            throw new InvalidOperationException(
                "No opaque pixels found — the image may use premultiplied zero-alpha color or be empty.");

        // Label connected components (8-neighborhood) over the opaque mask.
        int[] labels = new int[width * height];
        var areas = new List<int>();
        var bounds = new List<(int x0, int y0, int x1, int y1)>();
        Span<int> stack = stackalloc int[0];
        int[] dynamicStack = Array.Empty<int>();
        for (int start = 0; start < labels.Length; start++)
        {
            if (labels[start] != 0 || rgba[start * 4 + 3] < options.AlphaThreshold)
                continue;

            int label = areas.Count + 1;
            areas.Add(0);
            bounds.Add((width, height, -1, -1));

            if (dynamicStack.Length == 0)
                dynamicStack = new int[1 << 12];
            int sp = 0;
            dynamicStack[sp++] = start;
            labels[start] = label;

            while (sp > 0)
            {
                int p = dynamicStack[--sp];
                int px = p % width;
                int py = p / width;
                areas[label - 1]++;
                (int x0, int y0, int x1, int y1) b = bounds[label - 1];
                bounds[label - 1] = (Math.Min(b.x0, px), Math.Min(b.y0, py),
                                     Math.Max(b.x1, px), Math.Max(b.y1, py));

                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = py + dy;
                    if (ny < 0 || ny >= height)
                        continue;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx;
                        if (nx < 0 || nx >= width || (dx == 0 && dy == 0))
                            continue;
                        int q = ny * width + nx;
                        if (labels[q] != 0 || rgba[q * 4 + 3] < options.AlphaThreshold)
                            continue;
                        labels[q] = label;
                        if (sp == dynamicStack.Length)
                            Array.Resize(ref dynamicStack, dynamicStack.Length << 1);
                        dynamicStack[sp++] = q;
                    }
                }
            }
        }

        // Keep the pre-watershed island labels. The refined labels intentionally forget this
        // relationship, but rigging needs it to distinguish a detached weapon from body pieces.
        int[] sourceIslands = (int[])labels.Clone();

        int resolvedMarkerMinDepth = 0;
        int resolvedMarkerSpacing = 0;
        if (options.Split == SliceSplitMode.DistanceWatershed)
        {
            (int markerMinDepth, int markerSpacing) = WatershedSplitter.ResolveOptions(
                width, height, options.MarkerMinDepth, options.MarkerSpacing);
            WatershedSplitter.Refine(labels, width, height, areas, bounds, markerMinDepth, markerSpacing);
            resolvedMarkerMinDepth = markerMinDepth;
            resolvedMarkerSpacing = markerSpacing;
        }

        int discardedNoise = 0;
        var kept = new List<int>();
        for (int i = 0; i < areas.Count; i++)
        {
            if (areas[i] < options.MinAreaPixels)
                discardedNoise++;
            else
                kept.Add(i);
        }

        if (kept.Count == 0)
            throw new InvalidOperationException(
                $"All {areas.Count} components are below MinAreaPixels={options.MinAreaPixels}.");

        // Painter order: big parts back, small foreground details front. Ties broken by top-left
        // position so equal-size parts keep a stable source order.
        kept.Sort((a, b) =>
        {
            int byArea = areas[b].CompareTo(areas[a]);
            if (byArea != 0)
                return byArea;
            (int ax0, int ay0, _, _) = bounds[a];
            (int bx0, int by0, _, _) = bounds[b];
            int byPos = ay0.CompareTo(by0);
            return byPos != 0 ? byPos : ax0.CompareTo(bx0);
        });

        var parts = new List<SlicePartImage>(kept.Count);
        for (int layer = 0; layer < kept.Count; layer++)
        {
            int label = kept[layer] + 1;
            (int bx0, int by0, int bx1, int by1) = bounds[kept[layer]];
            int pad = Math.Max(0, options.CropPadding);
            int cx0 = Math.Max(0, bx0 - pad);
            int cy0 = Math.Max(0, by0 - pad);
            int cx1 = Math.Min(width - 1, bx1 + pad);
            int cy1 = Math.Min(height - 1, by1 + pad);
            int cw = cx1 - cx0 + 1;
            int ch = cy1 - cy0 + 1;

            byte[] cropRgba = new byte[cw * ch * 4];
            byte[] mask = new byte[cw * ch];
            int pixels = 0;
            int sourceIsland = 0;
            for (int y = cy0; y <= cy1; y++)
            {
                int srcRow = y * width;
                int dstRow = (y - cy0) * cw;
                for (int x = cx0; x <= cx1; x++)
                {
                    int s = srcRow + x;
                    if (labels[s] != label)
                        continue;
                    if (sourceIsland == 0)
                        sourceIsland = sourceIslands[s];
                    int d = dstRow + (x - cx0);
                    Buffer.BlockCopy(rgba, s * 4, cropRgba, d * 4, 4);
                    mask[d] = 255;
                    pixels++;
                }
            }

            parts.Add(new SlicePartImage
            {
                Name = $"part_{layer:000}",
                SourceIsland = sourceIsland,
                X = cx0,
                Y = cy0,
                Width = cw,
                Height = ch,
                PixelCount = pixels,
                LayerIndex = layer,
                Rgba = cropRgba,
                Mask = mask,
            });
        }

        ComputeInpaintSeeds(parts, options.SeedReach);

        return new SliceRunResult
        {
            ImageWidth = width,
            ImageHeight = height,
            OpaqueRatio = opaqueRatio,
            DiscardedNoiseParts = discardedNoise,
            Split = options.Split,
            MarkerMinDepth = resolvedMarkerMinDepth,
            MarkerSpacing = resolvedMarkerSpacing,
            Parts = parts,
        };
    }

    /// <summary>
    /// Sliced components own disjoint pixels, so a "lower" part is not covered — it is CUT by the
    /// illustration: the pixels that would continue underneath its neighbor were never drawn.
    /// The inpaint seed for a part is therefore the transparent zone hugging its silhouette edge
    /// that ALSO borders ANY neighbor part (symmetric: whichever side moves first reveals the gap,
    /// so both sides carry their own fill target). Seeds live in the part's local crop space.
    /// </summary>
    private static void ComputeInpaintSeeds(List<SlicePartImage> parts, int reach)
    {
        foreach (SlicePartImage? part in parts)
        {
            byte[] seeds = new byte[part.Width * part.Height];
            int count = 0;
            int minX = part.Width, minY = part.Height, maxX = -1, maxY = -1;

            foreach (SlicePartImage other in parts)
            {
                if (ReferenceEquals(other, part))
                    continue;

                // Scan the union neighborhood of both crops; cheap because crops are small.
                int ix0 = Math.Max(0, Math.Min(part.X, other.X) - reach - part.X);
                int iy0 = Math.Max(0, Math.Min(part.Y, other.Y) - reach - part.Y);
                int ix1 = Math.Min(part.Width - 1,
                    Math.Max(part.X + part.Width, other.X + other.Width) + reach - part.X);
                int iy1 = Math.Min(part.Height - 1,
                    Math.Max(part.Y + part.Height, other.Y + other.Height) + reach - part.Y);
                if (ix0 > ix1 || iy0 > iy1)
                    continue;

                for (int y = iy0; y <= iy1; y++)
                {
                    for (int x = ix0; x <= ix1; x++)
                    {
                        int pi = y * part.Width + x;
                        if (seeds[pi] != 0 || part.Mask[pi] != 0)
                            continue;
                        if (!Touches(part.Mask, part.Width, part.Height, x, y, reach))
                            continue;
                        if (!OtherPixelNearby(other, part.X + x, part.Y + y, reach))
                            continue;

                        seeds[pi] = 255;
                        if (++count == 1)
                        {
                            minX = maxX = x;
                            minY = maxY = y;
                        }
                        else
                        {
                            minX = Math.Min(minX, x);
                            minY = Math.Min(minY, y);
                            maxX = Math.Max(maxX, x);
                            maxY = Math.Max(maxY, y);
                        }
                    }
                }
            }

            part.InpaintSeedMask = seeds;
            part.InpaintSeedPixels = count;
            part.InpaintSeedRect = count == 0
                ? SliceRect.Empty
                : new SliceRect { X = minX, Y = minY, Width = maxX - minX + 1, Height = maxY - minY + 1 };
        }
    }

    /// <summary>True when any mask pixel is within Chebyshev distance <paramref name="reach"/> of
    /// the given local point.</summary>
    private static bool Touches(byte[] mask, int w, int h, int x, int y, int reach)
    {
        for (int dy = -reach; dy <= reach; dy++)
        {
            int ny = y + dy;
            if (ny < 0 || ny >= h)
                continue;
            for (int dx = -reach; dx <= reach; dx++)
            {
                int nx = x + dx;
                if (nx < 0 || nx >= w || (dx == 0 && dy == 0))
                    continue;
                if (mask[ny * w + nx] != 0)
                    return true;
            }
        }
        return false;
    }

    private static bool OtherPixelNearby(SlicePartImage other, int globalX, int globalY, int reach)
    {
        for (int dy = -reach; dy <= reach; dy++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                int lx = globalX + dx - other.X;
                int ly = globalY + dy - other.Y;
                if (lx < 0 || ly < 0 || lx >= other.Width || ly >= other.Height)
                    continue;
                if (other.Mask[ly * other.Width + lx] != 0)
                    return true;
            }
        }
        return false;
    }
}
