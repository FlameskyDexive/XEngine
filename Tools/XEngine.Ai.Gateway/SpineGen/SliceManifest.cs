// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json.Serialization;

namespace XEngine.Ai.SpineGen;

/// <summary>Deterministic result of slicing one character illustration into spine-ready parts.
/// Serialized as <c>slice_manifest.json</c>; downstream stages (rig / skin / animate) consume it.</summary>
public sealed class SliceManifest
{
    [JsonPropertyName("algorithmVersion")] public string? AlgorithmVersion { get; set; }
    [JsonPropertyName("recipe")] public AssemblyRecipe? Recipe { get; set; }
    [JsonPropertyName("recipeHash")] public string? RecipeHash { get; set; }
    [JsonPropertyName("sourceHashes")] public SortedDictionary<string,string> SourceHashes { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("imageWidth")] public int ImageWidth { get; set; }
    [JsonPropertyName("imageHeight")] public int ImageHeight { get; set; }
    [JsonPropertyName("alphaThreshold")] public int AlphaThreshold { get; set; }
    [JsonPropertyName("split")] public string Split { get; set; } = SliceSplitMode.ConnectedComponents.ToString();

    /// <summary>Resolved watershed marker tuning (0 when split is ConnectedComponents).</summary>
    [JsonPropertyName("markerMinDepth")] public int MarkerMinDepth { get; set; }
    [JsonPropertyName("markerSpacing")] public int MarkerSpacing { get; set; }
    [JsonPropertyName("opaqueRatio")] public float OpaqueRatio { get; set; }
    [JsonPropertyName("discardedNoiseParts")] public int DiscardedNoiseParts { get; set; }

    [JsonPropertyName("page")] public SlicePageInfo Page { get; set; } = new();
    [JsonPropertyName("parts")] public List<SlicePartEntry> Parts { get; set; } = [];

    /// <summary>Painter order: Parts are stored back-to-front.</summary>
    [JsonIgnore]
    public IEnumerable<SlicePartEntry> BackToFront => Parts.OrderBy(p => p.Layer);
}

public sealed class SlicePageInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("padding")] public int Padding { get; set; }
}

public sealed class SlicePartEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Original alpha-island id before optional watershed refinement. Parts cut from
    /// the same solid character keep the same id; detached props retain a different id. This is
    /// the provenance the rig stage needs to avoid treating a separately drawn weapon as a limb.</summary>
    [JsonPropertyName("sourceIsland")] public int SourceIsland { get; set; }

    /// <summary>Crop rect in source-image space, y-from-top.</summary>
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    [JsonPropertyName("pixels")] public int PixelCount { get; set; }

    /// <summary>Transparent pixels hugging this part that border a higher layer — the region a
    /// fill pass must complete so the part survives independent motion.</summary>
    [JsonPropertyName("inpaintSeedPixels")] public int InpaintSeedPixels { get; set; }

    /// <summary>Bbox of inpaint seeds inside this crop (empty rect when none).</summary>
    [JsonPropertyName("inpaintSeedRect")] public SliceRect InpaintSeedRect { get; set; } = new();

    [JsonPropertyName("layer")] public int Layer { get; set; }

    /// <summary>Relative path of the cropped part PNG (may be unfilled — see fill stage).</summary>
    [JsonPropertyName("png")] public string Png { get; set; } = "";

    /// <summary>Relative path of the seed-mask PNG (white+alpha where fill continuation is needed).
    /// Written by the slice stage, consumed by the fill stage.</summary>
    [JsonPropertyName("seedPng")] public string SeedPng { get; set; } = "";

    [JsonPropertyName("atlas")] public SliceAtlasPlacement Atlas { get; set; } = new();
}

public sealed class SliceRect
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    public static SliceRect Empty => new();

    [JsonIgnore]
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed class SliceAtlasPlacement
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("rotated")] public bool Rotated { get; set; }
}
