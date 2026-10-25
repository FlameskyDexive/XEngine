// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
namespace XEngine.Ai.SpineGen;

public sealed class PackRequest
{
    public required int Index { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}
public sealed record PackPlacement(int Index, int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}
public sealed record PackResult(int PageWidth, int PageHeight, IReadOnlyList<PackPlacement> Placements);

/// <summary>Deterministic unrotated MaxRects. POT pages are tried in increasing area,
/// then longest side. This finds the smallest page supported by fixed heuristics;
/// it does not prove that every smaller rectangle packing problem is infeasible.</summary>
public static class AtlasPacker
{
    public const int MaxPage = 4096;
    private readonly record struct Rect(int X, int Y, int W, int H)
    {
        public int Right => X + W;
        public int Bottom => Y + H;
        public bool Contains(Rect r) => X <= r.X && Y <= r.Y && Right >= r.Right && Bottom >= r.Bottom;
    }
    public static PackResult Pack(IReadOnlyList<PackRequest> items, int padding)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("No rects to pack.");
        if (padding < 0 || padding > MaxPage / 2) throw new ArgumentOutOfRangeException(nameof(padding));
        long area = 0;
        int minWidth = 1, minHeight = 1;
        var ids = new HashSet<int>();
        foreach (var item in items)
        {
            if (!ids.Add(item.Index)) throw new ArgumentException("Duplicate packing index.");
            long w = (long)item.Width + 2 * padding, h = (long)item.Height + 2 * padding;
            if (item.Width <= 0 || item.Height <= 0 || w > MaxPage || h > MaxPage)
                throw new InvalidOperationException($"Part #{item.Index} ({item.Width}x{item.Height}) does not fit the {MaxPage}px page limit.");
            minWidth = Math.Max(minWidth, (int)w); minHeight = Math.Max(minHeight, (int)h);
            area += w * h;
        }
        var pages = new List<(int W, int H)>();
        for (int w = NextPowerOfTwo(minWidth); w <= MaxPage; w *= 2)
            for (int h = NextPowerOfTwo(minHeight); h <= MaxPage; h *= 2)
                if ((long)w * h >= area) pages.Add((w, h));
        pages.Sort(static (a, b) =>
        {
            int c = (a.W * a.H).CompareTo(b.W * b.H);
            if (c == 0) c = Math.Max(a.W, a.H).CompareTo(Math.Max(b.W, b.H));
            return c != 0 ? c : a.W.CompareTo(b.W);
        });
        foreach (var page in pages)
            for (int order = 0; order < 3; order++)
                for (int score = 0; score < 2; score++)
                {
                    var placed = TryPack(items, padding, page.W, page.H, order, score);
                    if (placed != null) return new PackResult(page.W, page.H, placed);
                }
        throw new InvalidOperationException("Content cannot be packed within the maximum page size by the supported heuristics.");
    }
    private static PackPlacement[]? TryPack(IReadOnlyList<PackRequest> items, int pad, int width, int height, int order, int score)
    {
        var pending = new List<int>(items.Count);
        for (int i = 0; i < items.Count; i++) pending.Add(i);
        long Key(PackRequest p) => order switch
        {
            0 => (long)(p.Width + 2 * pad) * (p.Height + 2 * pad),
            1 => Math.Max(p.Width, p.Height),
            _ => p.Height
        };
        pending.Sort((a, b) =>
        {
            int c = Key(items[b]).CompareTo(Key(items[a]));
            return c != 0 ? c : items[a].Index.CompareTo(items[b].Index);
        });
        var free = new List<Rect> { new(0, 0, width, height) };
        var result = new PackPlacement[items.Count];
        foreach (int i in pending)
        {
            var item = items[i]; int w = item.Width + pad * 2, h = item.Height + pad * 2;
            Rect chosen = default;
            (int A, int B, int Y, int X) best = (int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);
            bool found = false;
            foreach (var r in free)
            {
                if (w > r.W || h > r.H) continue;
                int shortSide = Math.Min(r.W - w, r.H - h);
                var rank = (score == 0 ? shortSide : r.W * r.H - w * h,
                    score == 0 ? Math.Max(r.W - w, r.H - h) : shortSide, r.Y, r.X);
                if (rank.CompareTo(best) >= 0) continue;
                best = rank; chosen = new Rect(r.X, r.Y, w, h); found = true;
            }
            if (!found) return null;
            result[i] = new PackPlacement(item.Index, chosen.X + pad, chosen.Y + pad, item.Width, item.Height);
            var split = new List<Rect>();
            foreach (var r in free)
            {
                if (chosen.X >= r.Right || chosen.Right <= r.X || chosen.Y >= r.Bottom || chosen.Bottom <= r.Y)
                { split.Add(r); continue; }
                if (chosen.X > r.X) split.Add(new Rect(r.X, r.Y, chosen.X - r.X, r.H));
                if (chosen.Right < r.Right) split.Add(new Rect(chosen.Right, r.Y, r.Right - chosen.Right, r.H));
                if (chosen.Y > r.Y) split.Add(new Rect(r.X, r.Y, r.W, chosen.Y - r.Y));
                if (chosen.Bottom < r.Bottom) split.Add(new Rect(r.X, chosen.Bottom, r.W, r.Bottom - chosen.Bottom));
            }
            for (int a = split.Count - 1; a >= 0; a--)
                for (int b = 0; b < split.Count; b++)
                    if (a != b && split[b].Contains(split[a])) { split.RemoveAt(a); break; }
            free = split;
        }
        return result;
    }
    internal static int NextPowerOfTwo(int value)
    {
        if (value < 1 || value > MaxPage) throw new ArgumentOutOfRangeException(nameof(value));
        int p = 1;
        while (p < value) p <<= 1;
        return p;
    }
}
