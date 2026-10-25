// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Globalization;
using System.Text.Json;

namespace XEngine.Ai.SpineGen;

/// <summary>Serializes a <see cref="SpineSkeletonDoc"/> to Spine 4.x JSON. Bones are reordered
/// parent-first (the runtime resolves <c>parent</c> by name at parse time, but the official editor
/// and readers expect ancestors before descendants).</summary>
public static class SpineExporter
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new SpineMeshVerticesConverter(),
            new SpineCurveConverter(),
        },
    };

    public static string Write(SpineSkeletonDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        List<SpineBoneDef> ordered = OrderBonesParentFirst(doc.Bones);
        doc.Bones = ordered;

        doc.Skeleton ??= new SpineSkeletonMeta();
        return JsonSerializer.Serialize(doc, Options);
    }

    public static void Write(SpineSkeletonDoc doc, TextWriter writer)
    {
        writer.Write(Write(doc));
    }

    /// <summary>Parses a doc written by <see cref="Write"/> — REQUIRED for skeleton round-trips
    /// because mesh vertices need the custom converter.</summary>
    public static SpineSkeletonDoc Read(string json) =>
        JsonSerializer.Deserialize<SpineSkeletonDoc>(json, Options)
        ?? throw new InvalidOperationException("skeleton JSON produced no document");

    internal static List<SpineBoneDef> OrderBonesParentFirst(List<SpineBoneDef>? bones)
    {
        bones ??= new List<SpineBoneDef>();
        var byName = new Dictionary<string, SpineBoneDef>(StringComparer.Ordinal);
        foreach (SpineBoneDef bone in bones)
        {
            if (!byName.TryAdd(bone.Name, bone))
                throw new InvalidOperationException($"Duplicate bone name '{bone.Name}'.");
        }

        var result = new List<SpineBoneDef>(bones.Count);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var inProgress = new HashSet<string>(StringComparer.Ordinal);
        foreach (SpineBoneDef bone in bones)
            Visit(bone, byName, visited, inProgress, result);
        return result;
    }

    private static void Visit(SpineBoneDef bone, Dictionary<string, SpineBoneDef> byName,
        HashSet<string> visited, HashSet<string> inProgress, List<SpineBoneDef> result)
    {
        if (!visited.Add(bone.Name))
        {
            if (inProgress.Contains(bone.Name))
                throw new InvalidOperationException($"Bone hierarchy cycle at '{bone.Name}'.");
            return;
        }
        inProgress.Add(bone.Name);
        if (!string.IsNullOrEmpty(bone.Parent))
        {
            if (!byName.TryGetValue(bone.Parent!, out SpineBoneDef? parent))
                throw new InvalidOperationException($"Bone '{bone.Name}' references missing parent '{bone.Parent}'.");
            Visit(parent, byName, visited, inProgress, result);
        }
        inProgress.Remove(bone.Name);
        result.Add(bone);
    }
}

/// <summary>A packed region row for the atlas text the generator writes.</summary>
public sealed record SpineAtlasRegion
{
    public required string Name { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>The generator's packer never rotates regions; kept for completeness.</summary>
    public bool Rotated { get; init; }
    public int OrigWidth { get; init; } = -1;
    public int OrigHeight { get; init; } = -1;
    public int OffsetX { get; init; }
    public int OffsetY { get; init; }
}

/// <summary>Writes the modern (4.1+) atlas text format: page header plus per-region bounds/offsets.
/// Region Y follows spine packer convention (origin at top-left of the page image).</summary>
public static class SpineAtlasWriter
{
    public static void Write(TextWriter writer, string pageName, int pageWidth, int pageHeight,
        IEnumerable<SpineAtlasRegion> regions, bool premultipliedAlpha = false)
    {
        // Layout mirrors what Spine.Atlas expects: page name, page fields, then every region row
        // with no separating blank lines — a blank line resets the reader to expect a NEW page.
        writer.WriteLine(pageName);
        writer.WriteLine($"size: {pageWidth},{pageHeight}");
        writer.WriteLine("format: RGBA8888");
        writer.WriteLine("filter: Linear,Linear");
        writer.WriteLine("repeat: none");
        if (premultipliedAlpha) writer.WriteLine("pma: true");

        foreach (SpineAtlasRegion region in regions)
        {
            int origWidth = region.OrigWidth >= 0 ? region.OrigWidth : region.Width;
            int origHeight = region.OrigHeight >= 0 ? region.OrigHeight : region.Height;
            writer.WriteLine(region.Name);
            writer.WriteLine($"  rotate: {BoolText(region.Rotated)}");
            writer.WriteLine(FormattableString.Invariant(
                $"  bounds: {region.X},{region.Y},{region.Width},{region.Height}"));
            writer.WriteLine(FormattableString.Invariant(
                $"  offsets: {region.OffsetX},{region.OffsetY},{origWidth},{origHeight}"));
            writer.WriteLine("  index: -1");
        }
    }

    private static string BoolText(bool value) => value ? "true" : "false";
}
