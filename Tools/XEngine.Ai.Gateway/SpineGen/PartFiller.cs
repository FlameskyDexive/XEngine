// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Ai.SpineGen;

public enum FillStrategy
{
    /// <summary>Every seed pixel copies the nearest original pixel (breadth-first onion peel).</summary>
    Nearest,
    /// <summary>Seed runs along each row continue their nearest flanking color (ties go left).</summary>
    RowStretch,
    /// <summary>Multipass box-average growth from the original silhouette into the seeds.</summary>
    GrowBlur,
}

public static class PartFiller
{
    public static readonly FillStrategy[] MockStrategies =
        [FillStrategy.Nearest, FillStrategy.RowStretch, FillStrategy.GrowBlur];

    private const byte OpaqueAlpha = 255;

    /// <summary>Returns a new RGBA buffer with every seed pixel made opaque per strategy. The
    /// original pixels are untouched; fully deterministic.</summary>
    public static byte[] Fill(byte[] rgba, int width, int height, byte[] seedMask, FillStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(seedMask);
        if (width <= 0 || height <= 0 || rgba.Length < width * height * 4 || seedMask.Length != width * height)
            throw new ArgumentException("Buffers do not match dimensions.");

        byte[] result = (byte[])rgba.Clone();
        switch (strategy)
        {
            case FillStrategy.Nearest:
                FillNearest(result, width, height, seedMask);
                break;
            case FillStrategy.RowStretch:
                FillRowStretch(result, width, height, seedMask);
                break;
            case FillStrategy.GrowBlur:
                FillGrowBlur(result, width, height, seedMask);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(strategy));
        }
        return result;
    }

    /// <summary>Mean absolute RGB delta across the seed boundary (seed pixel ↔ original opaque
    /// neighbor). Lower = smoother continuation.</summary>
    public static double SeamScore(byte[] rgba, int width, int height, byte[] seedMask)
    {
        long total = 0;
        int samples = 0;
        int n = width * height;
        for (int i = 0; i < n; i++)
        {
            if (seedMask[i] == 0)
                continue;
            int x = i % width;
            foreach ((int dx, int dy) in NeighborOffsets)
            {
                int nx = x + dx;
                int ny = i / width + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    continue;
                int j = ny * width + nx;
                if (seedMask[j] != 0 || !IsOpaque(rgba, j))
                    continue;
                total += Math.Abs(rgba[i * 4] - rgba[j * 4])
                         + Math.Abs(rgba[i * 4 + 1] - rgba[j * 4 + 1])
                         + Math.Abs(rgba[i * 4 + 2] - rgba[j * 4 + 2]);
                samples++;
            }
        }

        return samples == 0 ? double.MaxValue : total / (double)samples / 3.0;
    }

    /// <summary>Breadth-first onion peel constrained to the seed set: each pass fills seed pixels
    /// adjacent to an already-filled pixel, so continuation grows inward from the silhouette.</summary>
    private static void FillNearest(byte[] result, int width, int height, byte[] seedMask)
    {
        var pending = new List<int>();
        for (int i = 0; i < width * height; i++)
            if (seedMask[i] != 0)
                pending.Add(i);

        while (pending.Count > 0)
        {
            var stillEmpty = new List<int>();
            foreach (int i in pending)
            {
                int donor = FindOpaqueNeighbor(result, width, height, i);
                if (donor >= 0)
                {
                    Buffer.BlockCopy(result, donor * 4, result, i * 4, 4);
                    result[i * 4 + 3] = OpaqueAlpha;
                }
                else
                {
                    stillEmpty.Add(i);
                }
            }

            if (stillEmpty.Count == pending.Count)
                break; // no progress possible
            pending = stillEmpty;
        }
    }

    private static void FillRowStretch(byte[] result, int width, int height, byte[] seedMask)
    {
        int n = width * height;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            int x = 0;
            while (x < width)
            {
                if (seedMask[rowStart + x] == 0)
                {
                    x++;
                    continue;
                }

                int runEnd = x;
                while (runEnd < width && seedMask[rowStart + runEnd] != 0)
                    runEnd++;

                int? leftDonor = x > 0 ? DonorColorIndex(result, rowStart + x - 1) : null;
                int? rightDonor = runEnd < width ? DonorColorIndex(result, rowStart + runEnd) : null;
                int? donor = leftDonor ?? rightDonor;
                if (donor is null)
                {
                    // Watershed-cut parts grow seed rings along their cut edges, where the part
                    // continues above/below rather than left/right — stretch vertically from the
                    // first run column that has an opaque pixel in the adjacent row. Runs with no
                    // donor in any direction stay transparent; the candidate simply completes
                    // less, and SeamScore ranks it.
                    for (int cx = x; cx < runEnd && donor is null; cx++)
                    {
                        donor = (y > 0 ? DonorColorIndex(result, rowStart - width + cx) : null)
                             ?? (y < height - 1 ? DonorColorIndex(result, rowStart + width + cx) : null);
                    }
                }

                if (donor.HasValue)
                {
                    for (int i = rowStart + x; i < rowStart + runEnd; i++)
                    {
                        Buffer.BlockCopy(result, donor.Value * 4, result, i * 4, 4);
                        result[i * 4 + 3] = OpaqueAlpha;
                    }
                }

                x = runEnd;
            }
        }
    }

    /// <summary>Grows one box-average layer per pass from original-opaque pixels into the seeds,
    /// newly-filled pixels joining the donor pool on the next pass.</summary>
    private static void FillGrowBlur(byte[] result, int width, int height, byte[] seedMask)
    {
        int n = width * height;
        var isOriginal = new bool[n];
        for (int i = 0; i < n; i++)
            isOriginal[i] = IsOpaque(result, i);

        var frontier = new List<int>();
        for (int i = 0; i < n; i++)
            if (isOriginal[i])
                frontier.Add(i);

        var filled = new HashSet<int>(frontier);
        while (filled.Count < n && frontier.Count > 0)
        {
            var nextFrontier = new HashSet<int>();
            // Snapshot donors so each pass averages only against the previous generation.
            var snapshotColors = new Dictionary<int, (byte r, byte g, byte b)>();
            foreach (int f in frontier)
                snapshotColors[f] = (result[f * 4], result[f * 4 + 1], result[f * 4 + 2]);

            foreach (int f in frontier)
            {
                int fx = f % width;
                int fy = f / width;
                foreach ((int dx, int dy) in NeighborOffsets)
                {
                    int nx = fx + dx;
                    int ny = fy + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                        continue;
                    int target = ny * width + nx;
                    if (seedMask[target] == 0 || filled.Contains(target) || !nextFrontier.Add(target))
                        continue;

                    long r = 0, g = 0, b = 0;
                    int contributors = 0;
                    foreach ((int ddx, int ddy) in NeighborOffsetsPlusSelf)
                    {
                        int sx = nx + ddx;
                        int sy = ny + ddy;
                        if (sx < 0 || sy < 0 || sx >= width || sy >= height)
                            continue;
                        int s = sy * width + sx;
                        if (!snapshotColors.TryGetValue(s, out (byte r, byte g, byte b) c))
                            continue;
                        r += c.r;
                        g += c.g;
                        b += c.b;
                        contributors++;
                    }

                    if (contributors == 0)
                        continue;

                    result[target * 4] = (byte)(r / contributors);
                    result[target * 4 + 1] = (byte)(g / contributors);
                    result[target * 4 + 2] = (byte)(b / contributors);
                    result[target * 4 + 3] = OpaqueAlpha;
                    nextFrontier.Add(target);
                }
            }

            foreach (int t in nextFrontier)
                filled.Add(t);
            frontier = [.. nextFrontier];
        }
    }

    private static bool IsOpaque(byte[] rgba, int i) => rgba[i * 4 + 3] >= 8;

    private static int? DonorColorIndex(byte[] result, int i) => IsOpaque(result, i) ? i : null;

    private static int FindOpaqueNeighbor(byte[] result, int width, int height, int i)
    {
        int x = i % width;
        int y = i / width;
        foreach ((int dx, int dy) in NeighborOffsets)
        {
            int nx = x + dx;
            int ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                continue;
            int j = ny * width + nx;
            if (result[j * 4 + 3] != 0)
                return j;
        }
        return -1;
    }

    private static readonly (int dx, int dy)[] NeighborOffsets =
    [
        (-1, 0), (1, 0), (0, -1), (0, 1),
        (-1, -1), (1, -1), (-1, 1), (1, 1),
    ];

    private static readonly (int dx, int dy)[] NeighborOffsetsPlusSelf =
    [
        (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1),
        (-1, -1), (1, -1), (-1, 1), (1, 1),
    ];
}
