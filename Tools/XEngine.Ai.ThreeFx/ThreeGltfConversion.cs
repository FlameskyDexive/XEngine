using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

using XEngine.Clay;
using XEngine.Clay.Importer;

namespace XEngine.Ai.ThreeFx;

/// <summary>
/// A3 — the controlled three.js → XEngine conversion layer. Parses a GLB exported by the
/// authoring host (through Clay's importer) and produces the per-material mapping onto
/// <c>Standard.shader</c> plus a fidelity report. This is deliberately scoped to the three.js
/// export surface: it never changes the global glTF import path.
///
/// Packing note: glTF stores occlusion in R, roughness in G and metallic in B of its
/// metallicRoughness/occlusion textures, and Standard.shader's <c>_SurfaceTex</c> is
/// "(AO, Roughness, Metallicness)" — the same order, so a texture-backed surface maps natively.
/// Scalar-only factors have no scalar uniform in Standard.shader; they must be baked into a
/// 1×1 surface texture (reported as <c>baked</c>).
/// </summary>
public static class ThreeGltfConversion
{
    public const string StandardShaderProperty_Surface = "_SurfaceTex";
    public const string StandardShaderProperty_MainColor = "_MainColor";
    public const string StandardShaderProperty_AlphaCutoff = "_AlphaCutoff";
    public const string StandardShaderProperty_EmissiveColor = "_EmissiveColor";

    public static ThreeGltfReport Analyze(byte[] glb, string stableId)
    {
        ArgumentNullException.ThrowIfNull(glb);

        using var stream = new MemoryStream(glb);
        Model model = ModelImporter.Load(stream, formatHint: "glb", ModelImporterSettings.GameQuality);

        string glbHash = Convert.ToHexString(SHA256.HashData(glb)).ToLowerInvariant();

        var report = new ThreeGltfReport
        {
            StableId = stableId,
            GlbSha256 = glbHash,
            NodeCount = model.Nodes.Count,
            MeshCount = model.Meshes.Count,
            RootName = model.Root.Name,
            NegativeScaleNodes = model.Nodes
                .Where(n => n.LocalScale.X < 0 || n.LocalScale.Y < 0 || n.LocalScale.Z < 0)
                .Select(n => n.Name)
                .ToArray(),
            AnimationClips = model.AnimationClips.Select(c => c.Name).ToArray(),
            SkinCount = model.Skins.Count,
            BlendShapeCount = model.Meshes.Sum(m => m.BlendShapes.Length),
            CameraCount = model.Cameras.Count,
            LightCount = model.Lights.Count,
            PrimitiveCount = model.Meshes.Sum(m => m.SubMeshes.Length),
            MultiMaterialMeshes = model.Meshes
                .Where(m => m.SubMeshes.Select(s => s.MaterialIndex).Distinct().Count() > 1)
                .Select(m => m.Name)
                .ToArray(),
        };

        foreach (var (material, index) in model.Materials.Select((m, i) => (m, i)))
            report.Materials.Add(MapMaterial(material, glbHash, stableId, index));

        return report;
    }

    /// <summary>
    /// Deterministic per-material asset key: SHA-256 over the GLB content hash + stableId +
    /// material index + name. Two exports of identical content yield identical GUIDs; any
    /// content change yields new ones — that is the "stable GUID" contract. The index guards
    /// against unnamed or duplicated material names in third-party exports.
    /// </summary>
    public static string DeriveStableGuid(string glbSha256, string stableId, int materialIndex, string materialName)
    {
        string payload = $"three-material|{glbSha256}|{stableId}|{materialIndex:D3}|{materialName}";
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
        return new Guid(hash.Take(16).ToArray()).ToString("D");
    }

    private static ThreeGltfMaterialMapping MapMaterial(
        XEngine.Clay.Material material, string glbHash, string stableId, int index)
    {
        string materialName = string.IsNullOrWhiteSpace(material.Name) ? $"material_{index:D3}" : material.Name;
        var mapping = new ThreeGltfMaterialMapping
        {
            MaterialName = materialName,
            StableGuid = DeriveStableGuid(glbHash, stableId, index, material.Name),
            AlphaMode = material.AlphaMode.ToString(),
        };

        // Native scalar/color properties of Standard.shader.
        mapping.Properties[StandardShaderProperty_MainColor] =
            $"{Fmt(material.BaseColor.R)} {Fmt(material.BaseColor.G)} {Fmt(material.BaseColor.B)} {Fmt(material.BaseColor.A)}";

        if (material.AlphaMode == MaterialAlphaMode.Mask)
            mapping.Properties[StandardShaderProperty_AlphaCutoff] = Fmt(material.AlphaCutoff);

        // Emissive factor is linear RGB; Standard.shader takes it as a color multiplied by intensity.
        if (material.EmissiveFactor.R > 0 || material.EmissiveFactor.G > 0 || material.EmissiveFactor.B > 0)
        {
            mapping.Properties[StandardShaderProperty_EmissiveColor] =
                $"{Fmt(material.EmissiveFactor.R)} {Fmt(material.EmissiveFactor.G)} {Fmt(material.EmissiveFactor.B)} 1";
            mapping.Properties["_EmissionIntensity"] = Fmt(material.EmissiveStrength);
        }

        // Texture slots. The occlusion/metallicRoughness packing maps natively; anything
        // requiring image decode or render-state changes is reported, not silently dropped.
        mapping.TextureBindings["_MainTex"] = Bind(material.BaseColorTexture);
        mapping.TextureBindings["_NormalTex"] = Bind(material.NormalTexture);
        mapping.TextureBindings["_EmissionTex"] = Bind(material.EmissiveTexture);
        mapping.TextureBindings[StandardShaderProperty_Surface] =
            material.MetallicRoughnessTexture is not null ? "native(gltf MR/occlusion packing)" : "baked:1x1-from-scalars";

        if (material.BaseColorTexture is { } baseColorSlot
            && (baseColorSlot.Offset != XEngine.Vector.Float2.Zero || baseColorSlot.Scale != XEngine.Vector.Float2.One))
        {
            mapping.Properties["_Tiling"] = $"{Fmt(baseColorSlot.Scale.X)} {Fmt(baseColorSlot.Scale.Y)}";
            mapping.Properties["_Offset"] = $"{Fmt(baseColorSlot.Offset.X)} {Fmt(baseColorSlot.Offset.Y)}";
        }

        if (material.NormalTexture is not null && MathF.Abs(material.NormalScale - 1f) > 1e-4f)
            mapping.Unsupported.Add($"normalScale {Fmt(material.NormalScale)}: Standard.shader has no _NormalScale uniform; bake into the texture.");

        if (material.AlphaMode == MaterialAlphaMode.Blend)
            mapping.Unsupported.Add("BLEND alpha mode: needs a transparent render-order material variant.");

        if (material.DoubleSided)
            mapping.Unsupported.Add("doubleSided: Standard.shader hard-codes Cull Back; needs a two-sided variant.");

        if (material.Unlit)
            mapping.Unsupported.Add("KHR_materials_unlit: no unlit variant of Standard.shader in scope; bake or mark unsupported.");

        if (material.MetallicRoughnessTexture is null)
            mapping.Unsupported.Add($"scalar metallic={Fmt(material.Metallic)} roughness={Fmt(material.Roughness)}: bake into a 1x1 _SurfaceTex.");

        return mapping;
    }

    private static string Bind(MaterialTextureSlot? slot) =>
        slot is null ? "none" : $"embedded(texture {slot.TextureIndex})";

    private static string Fmt(float v) => v.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>Whole-GLB conversion report (JSON-serializable evidence + import planning input).</summary>
public sealed record ThreeGltfReport
{
    public string StableId { get; init; } = "";
    public string GlbSha256 { get; init; } = "";
    public string RootName { get; init; } = "";
    public int NodeCount { get; init; }
    public int MeshCount { get; init; }
    public int PrimitiveCount { get; init; }

    public string[] MultiMaterialMeshes { get; init; } = Array.Empty<string>();
    public string[] NegativeScaleNodes { get; init; } = Array.Empty<string>();
    public string[] AnimationClips { get; init; } = Array.Empty<string>();
    public int SkinCount { get; init; }
    public int BlendShapeCount { get; init; }
    public int CameraCount { get; init; }
    public int LightCount { get; init; }

    public List<ThreeGltfMaterialMapping> Materials { get; } = [];

    public string ToJson() =>
        System.Text.Json.JsonSerializer.Serialize(this, JsonOptions);

    public static System.Text.Json.JsonSerializerOptions JsonOptions { get; } =
        new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };
}

/// <summary>Per-material mapping onto Standard.shader with explicit fidelity decisions.</summary>
public sealed record ThreeGltfMaterialMapping
{
    public string MaterialName { get; init; } = "";
    public string StableGuid { get; init; } = "";
    public string AlphaMode { get; init; } = "";

    /// <summary>Native scalar/color property assignments (property → value string).</summary>
    public Dictionary<string, string> Properties { get; } = new();

    /// <summary>Texture slot decisions (property → none/embedded/native/baked).</summary>
    public Dictionary<string, string> TextureBindings { get; } = new();

    /// <summary>Explicit non-native items; each must be baked or landed as unsupported (never silent).</summary>
    public List<string> Unsupported { get; } = [];
}
