// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;

namespace XEngine.Ai.Host;

/// <summary>
/// SpineGen stage 3 — one-click rigging: classify sliced parts into a biped template, infer joint
/// anchors, and export a Spine 4.x skeleton (regions in the default skin) that reproduces the
/// illustration exactly. Writes <c>rig/skeleton.json</c> + <c>rig/rig_manifest.json</c> inside the
/// slice output directory so animation can iterate on stable bone names.
/// </summary>
internal sealed class SpineGenRigService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly ProjectDescriptor _project;

    public SpineGenRigService(ProjectDescriptor project)
    {
        _project = project;
    }

    public SpineGenRigResult Rig(string sliceDir)
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

        (SpineSkeletonDoc doc, RigManifest rig) = Rigger.Build(slice);

        string rigDir = Path.Combine(absSliceDir, "rig");
        Directory.CreateDirectory(rigDir);

        string skeletonRel = ToRelative(Path.Combine(rigDir, "skeleton.json"));
        File.WriteAllText(Path.Combine(rigDir, "skeleton.json"), SpineExporter.Write(doc));
        string manifestRel = ToRelative(Path.Combine(rigDir, "rig_manifest.json"));
        File.WriteAllText(Path.Combine(rigDir, "rig_manifest.json"), JsonSerializer.Serialize(rig, JsonOpts));

        return new SpineGenRigResult
        {
            SliceDirectory = ToRelative(absSliceDir),
            SkeletonJson = skeletonRel,
            RigManifest = manifestRel,
            BoneCount = doc.Bones!.Count,
            SlotCount = doc.Slots!.Count,
            Roles = rig.Parts.GroupBy(p => p.Role.ToString())
                .ToDictionary(g => g.Key, g => g.Count()),
            Bones = rig.Bones.Select(b =>
                new SpineGenBoneInfo(b.Name, b.Parent ?? "", b.ImageX, b.ImageY)).ToList(),
        };
    }

    private string ToRelative(string absolutePath) =>
        Path.GetRelativePath(_project.ProjectRoot, absolutePath).Replace('\\', '/');
}

public sealed record SpineGenBoneInfo(string Name, string Parent, float ImageX, float ImageY);

public sealed class SpineGenRigResult
{
    [JsonPropertyName("sliceDirectory")] public string SliceDirectory { get; set; } = "";
    [JsonPropertyName("skeletonJson")] public string SkeletonJson { get; set; } = "";
    [JsonPropertyName("rigManifest")] public string RigManifest { get; set; } = "";
    [JsonPropertyName("boneCount")] public int BoneCount { get; set; }
    [JsonPropertyName("slotCount")] public int SlotCount { get; set; }
    [JsonPropertyName("roles")] public Dictionary<string, int> Roles { get; set; } = [];
    [JsonPropertyName("bones")] public List<SpineGenBoneInfo> Bones { get; set; } = [];
}
