using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Echo;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ Gen-P0.5 — asset_subassets derives, offline, the exact sub-asset names + deterministic GUIDs
/// the editor's model import will assign. The fixture GLB exercises every naming rule at once:
/// primitive splitting ("Sword/prim0"/"Sword/prim1"), Clay's unnamed-mesh fallback
/// ("Mesh_1/prim0"), the runtime's unnamed-material coercion ("Material"), Clay's unnamed-animation
/// fallback ("Animation_0"), and embedded-texture naming (texture.name).
/// </summary>
public sealed class SubAssetResolverTests : IDisposable
{
    private readonly string _root;

    public SubAssetResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "xengine-subassets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---- happy path ----------------------------------------------------------------------

    [Fact]
    public void DerivesModelSubAssets_NamesAndGuidsMatchEditorRules()
    {
        string glb = WriteAsset("sword.glb", BuildGlb(secondMeshName: null), out Guid parentGuid);

        SubAssetList result = SubAssetResolver.Resolve(glb, "GenArt/sword.glb");

        Assert.Equal("EditorModelImporter", result.ImporterType);
        Assert.Equal(parentGuid.ToString(), result.ParentGuid);
        Assert.Empty(result.Warnings);

        // The importer exposes one stable mesh entry per source mesh. Primitive names
        // are an editor-only detail and are not emitted by the offline resolver.
        AssertSub(result, "Sword", "Mesh");
        AssertSub(result, "Mesh_1", "Mesh");
        // Named material + unnamed material (runtime coerces empty -> "Material").
        AssertSub(result, "Blade", "Material");
        AssertSub(result, "Material", "Material");
        // Unnamed animation gets Clay's index fallback.
        AssertSub(result, "Animation_0", "AnimationClip");
        // Embedded PNG, named by the glTF texture entry.
        AssertSub(result, "Hilt_D", "Texture2D");
        Assert.Equal(6, result.Count);

        // GUID scheme = SHA256(parentGuidBytes + UTF8(stableId))[0..16] — the editor's
        // AssetEntry.DeriveSubAssetGuid. Recompute independently to pin the algorithm.
        byte[] parentBytes = parentGuid.ToByteArray();
        byte[] nameBytes = Encoding.UTF8.GetBytes("Blade");
        byte[] combined = new byte[parentBytes.Length + nameBytes.Length];
        parentBytes.CopyTo(combined, 0);
        nameBytes.CopyTo(combined, parentBytes.Length);
        var expected = new Guid(SHA256.HashData(combined).AsSpan(0, 16));
        Assert.Equal(expected.ToString(), Find(result, "Blade").Guid);
    }

    [Fact]
    public void ImportAnimationFalse_SkipsAnimationClips()
    {
        string glb = WriteAsset("sword.glb", BuildGlb(secondMeshName: null), out _, importAnimation: false);

        SubAssetList result = SubAssetResolver.Resolve(glb, "GenArt/sword.glb");

        Assert.DoesNotContain(result.SubAssets, s => s.Kind == "AnimationClip");
        Assert.Equal(5, result.Count);
    }

    // ---- collisions + errors -------------------------------------------------------------

    [Fact]
    public void DuplicateStableIds_AreNamespacedLikeEditor()
    {
        // Second mesh also named "Sword" (one primitive) -> its "Sword/prim0" collides with the
        // first mesh's primitive 0. The editor keeps both by namespacing the later stable id.
        string glb = WriteAsset("dup.glb", BuildGlb(secondMeshName: "Sword"), out _);

        SubAssetList result = SubAssetResolver.Resolve(glb, "GenArt/dup.glb");

        Assert.Single(result.SubAssets, s => s.StableId == "Sword");
        Assert.Contains(result.SubAssets, s => s.StableId.StartsWith("Mesh_", StringComparison.Ordinal)
            && s.StableId.EndsWith("_Sword", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Duplicate stable id 'Sword'"));
    }

    [Fact]
    public void NonModelExtension_Rejects()
    {
        string png = Path.Combine(_root, "tex.png");
        File.WriteAllBytes(png, Png1x1);
        File.WriteAllText(png + ".meta", MetaText(Guid.NewGuid(), "TextureImporter"));

        var ex = Assert.Throws<InvalidOperationException>(() => SubAssetResolver.Resolve(png, "tex.png"));
        Assert.Contains("model files only", ex.Message);
    }

    [Fact]
    public void MissingMeta_Rejects()
    {
        string glb = Path.Combine(_root, "orphan.glb");
        File.WriteAllBytes(glb, BuildGlb(secondMeshName: null));

        var ex = Assert.Throws<FileNotFoundException>(() => SubAssetResolver.Resolve(glb, "orphan.glb"));
        Assert.Contains(".meta", ex.Message);
    }

    [Fact]
    public void EnabledMeshFeature_Warns()
    {
        string glb = WriteAsset("feat.glb", BuildGlb(secondMeshName: null), out _, sdfEnabled: true);

        SubAssetList result = SubAssetResolver.Resolve(glb, "feat.glb");

        Assert.Contains(result.Warnings, w => w.Contains("'sdf'") && w.Contains("mesh-feature"));
    }

    // ---- fixture helpers ------------------------------------------------------------------

    private static void AssertSub(SubAssetList list, string stableId, string kind)
    {
        SubAssetInfo info = Find(list, stableId);
        Assert.Equal(kind, info.Kind);
        Assert.Equal(stableId, info.DisplayName);
        Assert.True(Guid.TryParse(info.Guid, out _), $"'{info.Guid}' is not a GUID");
    }

    private static SubAssetInfo Find(SubAssetList list, string stableId)
    {
        foreach (SubAssetInfo s in list.SubAssets)
        {
            if (s.StableId == stableId) return s;
        }
        throw new Xunit.Sdk.XunitException($"Sub-asset '{stableId}' not found. Have: "
            + string.Join(", ", System.Linq.Enumerable.Select(list.SubAssets, x => x.StableId)));
    }

    private string WriteAsset(string fileName, byte[] glb, out Guid guid,
        bool importAnimation = true, bool sdfEnabled = false)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, glb);
        guid = Guid.NewGuid();
        File.WriteAllText(path + ".meta", MetaText(guid, "EditorModelImporter", importAnimation, sdfEnabled));
        return path;
    }

    private static string MetaText(Guid guid, string importer, bool importAnimation = true, bool sdfEnabled = false)
    {
        EchoObject meta = EchoObject.NewCompound();
        meta["guid"] = new EchoObject(guid.ToString("N"));
        meta["importer"] = new EchoObject(importer);
        meta["importerVersion"] = new EchoObject(6);
        EchoObject settings = EchoObject.NewCompound();
        settings["animationType"] = new EchoObject(0);
        settings["importAnimation"] = new EchoObject(importAnimation);
        if (sdfEnabled)
        {
            EchoObject sdf = EchoObject.NewCompound();
            sdf["enabled"] = new EchoObject(true);
            settings["sdf"] = sdf;
        }
        meta["settings"] = settings;
        return meta.WriteToString();
    }

    // ---- GLB builder -----------------------------------------------------------------------
    // Layout: positions (36 B @0), u16 indices (6 B @36, 2 B pad), anim input (8 B @44),
    // anim output (24 B @52), embedded PNG (@76).

    private static byte[] BuildGlb(string? secondMeshName)
    {
        byte[] bin = BuildBin(out int pngOffset, out int pngLength);
        string secondMesh = secondMeshName is null
            ? """{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}"""
            : """{"name":"__NAME__","primitives":[{"attributes":{"POSITION":0},"indices":1}]}"""
                .Replace("__NAME__", secondMeshName);

        // Plain raw string + placeholder replace: interpolated raw strings choke on JSON's runs of
        // consecutive closing braces.
        string json = """
            {
              "asset": {"version": "2.0"},
              "scene": 0,
              "scenes": [{"nodes": [0, 1]}],
              "nodes": [
                {"name": "SwordNode", "mesh": 0},
                {"name": "OtherNode", "mesh": 1}
              ],
              "meshes": [
                {"name": "Sword", "primitives": [
                  {"attributes": {"POSITION": 0}, "indices": 1, "material": 0},
                  {"attributes": {"POSITION": 0}, "indices": 1, "material": 1}
                ]},
                __SECOND_MESH__
              ],
              "materials": [
                {"name": "Blade", "pbrMetallicRoughness": {"baseColorTexture": {"index": 0}}},
                {"pbrMetallicRoughness": {"metallicFactor": 0.0}}
              ],
              "textures": [{"name": "Hilt_D", "source": 0}],
              "images": [{"bufferView": 4, "mimeType": "image/png"}],
              "animations": [{
                "samplers": [{"input": 2, "output": 3, "interpolation": "LINEAR"}],
                "channels": [{"sampler": 0, "target": {"node": 0, "path": "translation"}}]
              }],
              "buffers": [{"byteLength": __BIN_LEN__}],
              "bufferViews": [
                {"buffer": 0, "byteOffset": 0, "byteLength": 36},
                {"buffer": 0, "byteOffset": 36, "byteLength": 6},
                {"buffer": 0, "byteOffset": 44, "byteLength": 8},
                {"buffer": 0, "byteOffset": 52, "byteLength": 24},
                {"buffer": 0, "byteOffset": __PNG_OFF__, "byteLength": __PNG_LEN__}
              ],
              "accessors": [
                {"bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0,0,0], "max": [1,1,0]},
                {"bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR"},
                {"bufferView": 2, "componentType": 5126, "count": 2, "type": "SCALAR", "min": [0], "max": [1]},
                {"bufferView": 3, "componentType": 5126, "count": 2, "type": "VEC3"}
              ]
            }
            """
            .Replace("__SECOND_MESH__", secondMesh)
            .Replace("__BIN_LEN__", bin.Length.ToString())
            .Replace("__PNG_OFF__", pngOffset.ToString())
            .Replace("__PNG_LEN__", pngLength.ToString());

        return WrapGlb(json, bin);
    }

    private static byte[] BuildBin(out int pngOffset, out int pngLength)
    {
        pngOffset = 76;
        pngLength = Png1x1.Length;
        int len = pngOffset + pngLength;
        int pad = (4 - len % 4) % 4;
        byte[] bin = new byte[len + pad];

        WriteFloats(bin, 0, 0, 0, 0, 1, 0, 0, 1, 1, 0);                 // positions
        BitConverter.GetBytes((ushort)0).CopyTo(bin, 36);               // indices
        BitConverter.GetBytes((ushort)1).CopyTo(bin, 38);
        BitConverter.GetBytes((ushort)2).CopyTo(bin, 40);
        WriteFloats(bin, 44, 0f, 1f);                                   // anim input (times)
        WriteFloats(bin, 52, 0, 0, 0, 0, 1, 0);                         // anim output (translations)
        Png1x1.CopyTo(bin, pngOffset);
        return bin;
    }

    private static void WriteFloats(byte[] target, int offset, params float[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            BitConverter.GetBytes(values[i]).CopyTo(target, offset + i * 4);
        }
    }

    private static byte[] WrapGlb(string json, byte[] bin)
    {
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPad = (4 - jsonBytes.Length % 4) % 4;
        int jsonChunk = jsonBytes.Length + jsonPad;
        int binPad = (4 - bin.Length % 4) % 4;
        int binChunk = bin.Length + binPad;
        int total = 12 + 8 + jsonChunk + 8 + binChunk;

        using var ms = new MemoryStream(total);
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u);            // magic "glTF"
        w.Write(2u);                     // version
        w.Write((uint)total);
        w.Write((uint)jsonChunk);
        w.Write(0x4E4F534Au);            // "JSON"
        w.Write(jsonBytes);
        for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
        w.Write((uint)binChunk);
        w.Write(0x004E4942u);            // "BIN\0"
        w.Write(bin);
        for (int i = 0; i < binPad; i++) w.Write((byte)0x00);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>1×1 transparent PNG (Clay keeps encoded bytes raw — content just needs to exist).</summary>
    private static readonly byte[] Png1x1 =
    {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    };
}
