// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json.Serialization;

namespace XEngine.Ai.SpineGen;

/// <summary>Per-part fill outcome written next to slice_manifest.json as
/// <c>fill_manifest.json</c>. The chosen candidate's pixels are already composited into
/// <c>parts/&lt;name&gt;.png</c>; candidates stay in <c>fill/</c> as a review gallery.</summary>
public sealed class FillManifest
{
    [JsonPropertyName("sliceManifest")] public string SliceManifest { get; set; } = "";
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("parts")] public List<FillPartReport> Parts { get; set; } = [];
}

public sealed class FillPartReport
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>filled = pixels composited into parts PNG. kept = no seeds or fill failed.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "kept";
    [JsonPropertyName("seedPixels")] public int SeedPixels { get; set; }

    [JsonPropertyName("chosen")] public FillCandidate? Chosen { get; set; }
    [JsonPropertyName("candidates")] public List<FillCandidate> Candidates { get; set; } = [];
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class FillCandidate
{
    /// <summary>e.g. "mock:nearest" | "mock:row-stretch" | "openai:gpt-image-2".</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "";

    /// <summary>Relative path under the slice output dir.</summary>
    [JsonPropertyName("png")] public string Png { get; set; } = "";

    /// <summary>Mean seam RGB delta — lower is smoother continuation.</summary>
    [JsonPropertyName("seamScore")] public double SeamScore { get; set; }
}
