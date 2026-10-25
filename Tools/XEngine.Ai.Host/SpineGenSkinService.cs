// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;

namespace XEngine.Ai.Host;

/// <summary>
/// SpineGen stage 4 — auto-skinning: every sliced part's silhouette becomes a triangulated mesh
/// attachment (concave outlines preserved) hanging on its M4 joint bone. Parameters serialize into
/// <c>rig/skin_manifest.json</c> so UI sliders can re-run deterministically. Parts that are too
/// small or degenerate stay plain regions — never worse than stage 3's output.
/// </summary>
internal sealed class SpineGenSkinService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ProjectDescriptor _project;

    public SpineGenSkinService(ProjectDescriptor project)
    {
        _project = project;
    }

    public SpineGenSkinResult Skin(
        string sliceDir,
        int? simplifyEpsilonX100,
        int? maxHullVertices,
        int? minMeshPixels)
    {
        if (string.IsNullOrWhiteSpace(sliceDir))
            throw new ArgumentException("--slice is required (slice output directory).");

        sliceDir = sliceDir.Replace('/', Path.DirectorySeparatorChar);
        string absSliceDir = Path.IsPathRooted(sliceDir)
            ? sliceDir
            : Path.Combine(_project.ProjectRoot, sliceDir);
        string manifestPath = Path.Combine(absSliceDir, "slice_manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"slice_manifest.json not found under {absSliceDir}.");

        SliceManifest slice = JsonSerializer.Deserialize<SliceManifest>(File.ReadAllText(manifestPath))
                              ?? throw new InvalidOperationException("Unreadable slice manifest.");

        var parameters = new SkinParams
        {
            SimplifyEpsilon = (simplifyEpsilonX100 ?? 150) / 100f,
            MaxHullVertices = maxHullVertices ?? 64,
            MinMeshPixels = minMeshPixels ?? 24,
        };

        // Regenerate the rig deterministically so doc structure matches stage 3 byte-for-byte
        // except the attachments we upgrade to meshes here.
        (SpineSkeletonDoc doc, RigManifest rig) = Rigger.Build(slice);
        var bindingsByPart = rig.Parts.ToDictionary(b => b.Part, b => b);
        var bonesByName = rig.Bones.ToDictionary(b => b.Name);

        var report = new SkinManifest { Parameters = parameters };
        int meshedCount = 0;

        foreach (SlicePartEntry part in slice.Parts)
        {
            string partPngPath = Path.Combine(absSliceDir, part.Png.Replace('/', Path.DirectorySeparatorChar));
            byte[] rgba = PngRgba.Decode(File.ReadAllBytes(partPngPath), out int pw, out int ph);
            if (pw != part.Width || ph != part.Height)
                throw new InvalidOperationException($"Part PNG dims drifted for {part.Name}.");

            RigPartBinding binding = bindingsByPart[part.Name];
            RigBoneInfo anchorBone = bonesByName[binding.Bone];

            SpineAttachment attachment;
            SkinPartResult result;
            if (slice.Recipe != null)
            {
                AssemblyPart explicitPart = slice.Recipe.Parts.Single(p => p.Name == part.Name);
                if (explicitPart.Rigid)
                {
                    report.Parts.Add(new SkinPartResult { Name=part.Name,Meshed=false,Reason="explicit rigid region" });
                    continue;
                }
                attachment=AssemblySkinner.Build(explicitPart,doc,out result,part);
            }
            else attachment = Skinning.BuildMeshAttachment(part, part.Atlas, rgba,
                binding, anchorBone, rig, doc.Bones!, parameters, out result);
            report.Parts.Add(result);
            if (!result.Meshed)
                continue;

            meshedCount++;
            SpineSkinDef skin = doc.Skins![0];
            foreach ((string slotKey, Dictionary<string, SpineAttachment> placeholders) in skin.Attachments)
            {
                if (!string.Equals(slotKey, $"{part.Name}_slot", StringComparison.Ordinal))
                    continue;
                skin.Attachments[slotKey] = placeholders.ToDictionary(
                    kv => kv.Key,
                    _ => attachment);
            }
        }

        File.WriteAllText(Path.Combine(absSliceDir, "rig", "skeleton.json"), SpineExporter.Write(doc));
        File.WriteAllText(Path.Combine(absSliceDir, "rig", "skin_manifest.json"),
            JsonSerializer.Serialize(report, JsonOpts));

        return new SpineGenSkinResult
        {
            SliceDirectory = ToRelative(absSliceDir),
            SkeletonJson = ToRelative(Path.Combine(absSliceDir, "rig", "skeleton.json")),
            SkinManifest = ToRelative(Path.Combine(absSliceDir, "rig", "skin_manifest.json")),
            TotalParts = slice.Parts.Count,
            MeshedParts = meshedCount,
            Triangles = report.Parts.Sum(p => p.TriangleCount),
            Vertices = report.Parts.Sum(p => p.HullVertexCount),
        };
    }

    private string ToRelative(string absolutePath) =>
        Path.GetRelativePath(_project.ProjectRoot, absolutePath).Replace('\\', '/');
}

public sealed class SkinManifest
{
    [JsonPropertyName("parameters")] public SkinParams Parameters { get; set; } = new();
    [JsonPropertyName("parts")] public List<SkinPartResult> Parts { get; set; } = [];
}

public sealed class SpineGenSkinResult
{
    [JsonPropertyName("sliceDirectory")] public string SliceDirectory { get; set; } = "";
    [JsonPropertyName("skeletonJson")] public string SkeletonJson { get; set; } = "";
    [JsonPropertyName("skinManifest")] public string SkinManifest { get; set; } = "";
    [JsonPropertyName("totalParts")] public int TotalParts { get; set; }
    [JsonPropertyName("meshedParts")] public int MeshedParts { get; set; }
    [JsonPropertyName("triangles")] public int Triangles { get; set; }
    [JsonPropertyName("vertices")] public int Vertices { get; set; }
}
