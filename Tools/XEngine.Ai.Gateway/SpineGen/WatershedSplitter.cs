// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Ai.SpineGen;

/// <summary>How solid-touching regions of the illustration are separated into parts.</summary>
public enum SliceSplitMode
{
    /// <summary>Each 8-connected alpha island stays exactly one part (v1 behavior).</summary>
    ConnectedComponents,

    /// <summary>Alpha islands are additionally cut at distance-transform saddles so parts that
    /// touch with fully opaque pixels (mech armor plates, overlapping limbs) split where a human
    /// artist would slice: neck, shoulders, hips, along armor seams.</summary>
    DistanceWatershed,
}

/// <summary>
/// Cuts connected components that connected-component labeling cannot separate. A chamfer
/// distance transform turns each blob into a thickness relief; local maxima of that relief seed
/// marker basins; a descending priority-flood watershed claims every pixel of the blob so basin
/// boundaries land on the relief saddles — the thinnest connecting necks between thick regions.
///
/// Deterministic by construction: candidates are collected in scan order, sorted with total
/// order (depth desc, y, x), greedy marker acceptance uses stable comparisons, and the flood
/// breaks depth ties by insertion sequence. Pure raster logic — no I/O, no codec, no RNG.
/// </summary>
internal static class WatershedSplitter
{
    /// <summary>Resolves the auto marker tuning (0 knobs) from image size. Depth scales with the
    /// smaller side (thin protrusions must not seed), spacing with the larger side (one marker
    /// per limb-scale region on default runs).</summary>
    public static (int MinDepth, int Spacing) ResolveOptions(
        int width, int height, int markerMinDepth, int markerSpacing)
    {
        int depth = markerMinDepth > 0
            ? markerMinDepth
            : Math.Max(12, Math.Min(width, height) / 96);
        int spacing = markerSpacing > 0
            ? markerSpacing
            : Math.Max(32, Math.Max(width, height) / 16);
        return (depth, spacing);
    }

    /// <summary>Replaces every component label in <paramref name="labels"/> with one or more
    /// watershed sub-labels, renumbered contiguously in first-appearance scan order.
    /// <paramref name="areas"/> and <paramref name="bounds"/> are rebuilt to match.</summary>
    public static void Refine(
        int[] labels, int width, int height,
        List<int> areas, List<(int x0, int y0, int x1, int y1)> bounds,
        int markerMinDepth, int markerSpacing)
    {
        int componentCount = areas.Count;
        if (componentCount == 0)
            return;

        int[] relief = DistanceTransform(labels, width, height);

        // Per component: seed markers, flood sub-basins. subLabels holds the LOCAL basin id
        // (1..k) inside each component; 0 means "unvisited" — such pixels default to basin 1.
        int[] subLabels = new int[labels.Length];
        for (int c = 1; c <= componentCount; c++)
        {
            List<(int depth, int y, int x)> markers = CollectMarkers(
                labels, relief, width, height, c, markerMinDepth, markerSpacing);
            if (markers.Count <= 1)
                continue; // nothing to cut — the component stays one basin

            FloodBasins(labels, relief, subLabels, width, height, c, markers);
        }

        Renumber(labels, subLabels, width, height, areas, bounds);
    }

    /// <summary>Two-pass chamfer 3-4 distance transform over the labeled mask, in px
    /// (approximation of Euclidean distance, good to ~8%).</summary>
    private static int[] DistanceTransform(int[] labels, int width, int height)
    {
        const int inf = 1 << 30;
        int[] dt = new int[labels.Length];
        for (int p = 0; p < dt.Length; p++)
            dt[p] = labels[p] != 0 ? inf : 0;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int p = row + x;
                int v = dt[p];
                if (v == 0)
                    continue;
                if (x > 0 && dt[p - 1] + 3 < v) v = dt[p - 1] + 3;
                if (y > 0)
                {
                    if (dt[p - width] + 3 < v) v = dt[p - width] + 3;
                    if (x > 0 && dt[p - width - 1] + 4 < v) v = dt[p - width - 1] + 4;
                    if (x < width - 1 && dt[p - width + 1] + 4 < v) v = dt[p - width + 1] + 4;
                }
                dt[p] = v;
            }
        }

        for (int y = height - 1; y >= 0; y--)
        {
            int row = y * width;
            for (int x = width - 1; x >= 0; x--)
            {
                int p = row + x;
                int v = dt[p];
                if (v == 0)
                    continue;
                if (x < width - 1 && dt[p + 1] + 3 < v) v = dt[p + 1] + 3;
                if (y < height - 1)
                {
                    if (dt[p + width] + 3 < v) v = dt[p + width] + 3;
                    if (x < width - 1 && dt[p + width + 1] + 4 < v) v = dt[p + width + 1] + 4;
                    if (x > 0 && dt[p + width - 1] + 4 < v) v = dt[p + width - 1] + 4;
                }
                dt[p] = v;
            }
        }

        for (int p = 0; p < dt.Length; p++)
            dt[p] = (dt[p] + 2) / 3;
        return dt;
    }

    /// <summary>Local relief maxima of the component, thin-protrusion filtered
    /// (<see cref="markerMinDepth"/>) and greedy-spaced (Chebyshev-free Euclidean
    /// <see cref="markerSpacing"/>). Ordered by depth desc, then y, then x.</summary>
    private static List<(int depth, int y, int x)> CollectMarkers(
        int[] labels, int[] relief, int width, int height,
        int component, int markerMinDepth, int markerSpacing)
    {
        var candidates = new List<(int depth, int y, int x)>();
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int p = row + x;
                if (labels[p] != component)
                    continue;
                int v = relief[p];
                if (v < markerMinDepth)
                    continue;
                if (!IsLocalMaximum(relief, width, height, x, y, v))
                    continue;
                candidates.Add((v, y, x));
            }
        }

        // Total order: deepest first, then scan position — identical input, identical markers.
        candidates.Sort(static (a, b) =>
        {
            int byDepth = b.depth.CompareTo(a.depth);
            if (byDepth != 0)
                return byDepth;
            int byY = a.y.CompareTo(b.y);
            return byY != 0 ? byY : a.x.CompareTo(b.x);
        });

        var markers = new List<(int depth, int y, int x)>();
        long spacingSq = (long)markerSpacing * markerSpacing;
        foreach ((int depth, int y, int x) in candidates)
        {
            bool tooClose = false;
            foreach ((_, int my, int mx) in markers)
            {
                long dy = my - y, dx = mx - x;
                if (dy * dy + dx * dx < spacingSq)
                {
                    tooClose = true;
                    break;
                }
            }
            if (!tooClose)
                markers.Add((depth, y, x));
        }
        return markers;
    }

    private static bool IsLocalMaximum(int[] relief, int width, int height, int x, int y, int v)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = y + dy;
            if (ny < 0 || ny >= height)
                continue;
            int row = ny * width;
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                int nx = x + dx;
                if (nx < 0 || nx >= width)
                    continue;
                if (relief[row + nx] > v)
                    return false;
            }
        }
        return true;
    }

    /// <summary>Descending priority-flood from the markers over the component's pixels. Priority
    /// packs (maxDepth - depth, insertion sequence) into one long so the heap pops deepest
    /// pixels first and breaks depth ties by insertion order — basin borders settle on saddles.</summary>
    private static void FloodBasins(
        int[] labels, int[] relief, int[] subLabels, int width, int height,
        int component, List<(int depth, int y, int x)> markers)
    {
        // depth <= 255-ish in practice; keep headroom for the shift packing.
        int depthOffset = int.MaxValue - 4096;
        var heap = new PriorityQueue<int, long>();
        long seq = 0;
        for (int i = 0; i < markers.Count; i++)
        {
            (int depth, int y, int x) = markers[i];
            int p = y * width + x;
            subLabels[p] = i + 1;
            heap.Enqueue(p, ((long)(depthOffset - depth) << 32) | (uint)seq);
            seq++;
        }

        while (heap.TryDequeue(out int p, out _))
        {
            int px = p % width;
            int py = p / width;
            int basin = subLabels[p];
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = py + dy;
                if (ny < 0 || ny >= height)
                    continue;
                int row = ny * width;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                        continue;
                    int nx = px + dx;
                    if (nx < 0 || nx >= width)
                        continue;
                    int q = row + nx;
                    if (labels[q] != component || subLabels[q] != 0)
                        continue;
                    subLabels[q] = basin;
                    heap.Enqueue(q, ((long)(depthOffset - relief[q]) << 32) | (uint)seq);
                    seq++;
                }
            }
        }
    }

    /// <summary>Assigns contiguous labels in first-appearance scan order and rebuilds the
    /// per-label area and bounds lists the caller's pipeline consumes.</summary>
    private static void Renumber(
        int[] labels, int[] subLabels, int width, int height,
        List<int> areas, List<(int x0, int y0, int x1, int y1)> bounds)
    {
        var map = new Dictionary<long, int>();
        var newAreas = new List<int>();
        var newBounds = new List<(int x0, int y0, int x1, int y1)>();

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int p = row + x;
                if (labels[p] == 0)
                    continue;

                long key = (long)labels[p] << 32 | (uint)Math.Max(1, subLabels[p]);
                if (!map.TryGetValue(key, out int newLabel))
                {
                    newLabel = newAreas.Count + 1;
                    map[key] = newLabel;
                    newAreas.Add(0);
                    newBounds.Add((width, height, -1, -1));
                }

                labels[p] = newLabel;
                newAreas[newLabel - 1]++;
                (int bx0, int by0, int bx1, int by1) = newBounds[newLabel - 1];
                newBounds[newLabel - 1] = (Math.Min(bx0, x), Math.Min(by0, y),
                                           Math.Max(bx1, x), Math.Max(by1, y));
            }
        }

        areas.Clear();
        areas.AddRange(newAreas);
        bounds.Clear();
        bounds.AddRange(newBounds);
    }
}
