using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using XEngine.Ai.ThreeFx;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// A3 — the controlled GLB conversion layer. Builds a minimal but complete GLB fixture
/// (multi-material mesh, negative-scale node, MASK/double-sided/normal-scale/UV-transform
/// material, BLEND material, animation clip) and pins the mapping + stable-GUID semantics.
/// </summary>
public sealed class ThreeGltfConversionTests
{
    [Fact]
    public void Analyze_ReportsHierarchyAndAnimation()
    {
        ThreeGltfReport report = ThreeGltfConversion.Analyze(GltfFixtureBuilder.Build(), "fixture_a");

        Assert.Equal(4, report.NodeCount); // 3 authored + the synthetic root Clay adds.
        Assert.Equal(1, report.MeshCount);
        // Both authored materials survived the merge and kept their names; the multi-primitive
        // merge itself (per-face material split) is Clay's documented behaviour, not this layer's.
        Assert.Equal(2, report.Materials.Count);
        Assert.Contains(report.Materials, m => m.MaterialName == "cutter");
        Assert.Contains(report.Materials, m => m.MaterialName == "glow");
        Assert.Contains("mirror", report.NegativeScaleNodes);
        Assert.Contains("spin", report.AnimationClips);
        Assert.Equal(0, report.SkinCount);
    }

    [Fact]
    public void Analyze_MapsCutoffDoubleSidedAndUvTransform()
    {
        ThreeGltfReport report = ThreeGltfConversion.Analyze(GltfFixtureBuilder.Build(), "fixture_a");

        var cutter = Assert.Single(report.Materials, m => m.MaterialName == "cutter");
        Assert.Equal("Mask", cutter.AlphaMode);
        Assert.Equal("0.4", cutter.Properties["_AlphaCutoff"]);
        Assert.Contains(cutter.Unsupported, u => u.StartsWith("doubleSided", StringComparison.Ordinal));
        Assert.Contains(cutter.Unsupported, u => u.StartsWith("normalScale 2", StringComparison.Ordinal));
        Assert.Equal("2 3", cutter.Properties["_Tiling"]);
        Assert.Equal("0.5 0.25", cutter.Properties["_Offset"]);
    }

    [Fact]
    public void Analyze_MapsEmissiveAndBlend()
    {
        ThreeGltfReport report = ThreeGltfConversion.Analyze(GltfFixtureBuilder.Build(), "fixture_a");

        var glow = Assert.Single(report.Materials, m => m.MaterialName == "glow");
        Assert.Equal("0.8 0.2 0.05 1", glow.Properties["_EmissiveColor"]);
        Assert.Equal("2", glow.Properties["_EmissionIntensity"]);
        Assert.Contains(glow.Unsupported, u => u.StartsWith("BLEND", StringComparison.Ordinal));
    }

    [Fact]
    public void StableGuids_AreDistinctPerMaterial_AndDeterministic()
    {
        byte[] glb = GltfFixtureBuilder.Build();
        ThreeGltfReport first = ThreeGltfConversion.Analyze(glb, "fixture_a");
        ThreeGltfReport second = ThreeGltfConversion.Analyze(glb, "fixture_a");

        Assert.All(first.Materials, m => Assert.Matches("^[0-9a-f-]{36}$", m.StableGuid));
        foreach (ThreeGltfMaterialMapping material in first.Materials)
        {
            ThreeGltfMaterialMapping again = Assert.Single(second.Materials, m => m.MaterialName == material.MaterialName);
            Assert.Equal(material.StableGuid, again.StableGuid);
        }
        Assert.Distinct(first.Materials.Select(m => m.StableGuid));
    }

    [Fact]
    public void StableGuids_ChangeWhenContentChanges()
    {
        byte[] original = GltfFixtureBuilder.Build();
        byte[] edited = GltfFixtureBuilder.Build(hue: 0.75f);

        ThreeGltfReport a = ThreeGltfConversion.Analyze(original, "fixture_a");
        ThreeGltfReport b = ThreeGltfConversion.Analyze(edited, "fixture_a");
        Assert.NotEqual(a.Materials[0].StableGuid, b.Materials[0].StableGuid);
        Assert.NotEqual(a.GlbSha256, b.GlbSha256);
    }
}

/// <summary>Builds a tiny binary glTF (GLB) covering the A3 acceptance surface.</summary>
internal static class GltfFixtureBuilder
{
    public static byte[] Build(float hue = 0.25f)
    {
        // BIN chunk layout (offsets in the bufferViews below):
        // posA(24B) | posB(36B) | indicesA(12B) | indicesB(6B)+pad2 | animIn(12B) | animOut(48B)
        var bin = new List<byte>();
        WriteFloats(bin, [0f, 0f, 0f, 1f, 0f, 0f]);             // 2 verts * vec3 (positions A)
        WriteFloats(bin, [0f, 1f, 0f, 0f, 0f, 1f, 1f, 1f, 1f]); // 3 verts * vec3 (positions B)
        WriteUShorts(bin, [0, 1, 0, 0, 1, 0]);                  // 6 indices (primitive A)
        WriteUShorts(bin, [0, 1, 2, 0]);                        // 3 indices + 2 pad (primitive B)
        WriteFloats(bin, [0f, 1f, 2f]);                         // 3 keyframe times (4-byte aligned)
        WriteFloats(bin, [0f, 0f, 0f, 1f, 0f, 0.7f, 0f, 0.7f, 0f, 0f, 0f, 1f]); // 3 quats

        float tint = 0.5f + hue;
        string json = """
            {
              "asset": {"version": "2.0"},
              "scene": 0,
              "scenes": [{"nodes": [0, 1, 2]}],
              "nodes": [
                {"name": "root", "mesh": 0},
                {"name": "mirror", "mesh": 0, "scale": [-1, 1, 1]},
                {"name": "spin_pivot"}
              ],
              "meshes": [{"name": "multi_mesh", "primitives": [
                {"attributes": {"POSITION": 0}, "indices": 1, "material": 0},
                {"attributes": {"POSITION": 2}, "indices": 4, "material": 1}
              ]}],
              "materials": [
                {"name": "cutter",
                 "alphaMode": "MASK", "alphaCutoff": 0.4, "doubleSided": true,
                 "pbrMetallicRoughness": {"baseColorFactor": [1, 1, 1, 1], "metallicFactor": 0.2, "roughnessFactor": 0.8,
                   "baseColorTexture": {"index": 0, "extensions": {
                     "KHR_texture_transform": {"offset": [0.5, 0.25], "scale": [2, 3]}}}},
                 "normalTexture": {"index": 1, "scale": 2.0}},
                {"name": "glow",
                 "alphaMode": "BLEND",
                 "pbrMetallicRoughness": {"baseColorFactor": [TINT_VALUE, 0.1, 0.0, 0.5], "metallicFactor": 0.0, "roughnessFactor": 0.3},
                 "emissiveFactor": [0.8, 0.2, 0.05],
                 "extensions": {"KHR_materials_emissive_strength": {"emissiveStrength": 2}}}
              ],
              "textures": [{"sampler": 0, "source": 0}, {"sampler": 0, "source": 0}],
              "samplers": [{}],
              "images": [{"uri": "dummy_base.png"}, {"uri": "dummy_normal.png"}],
              "accessors": [
                {"bufferView": 0, "componentType": 5126, "count": 2, "type": "VEC3", "min": [0,0,0], "max": [1,0,0]},
                {"bufferView": 1, "componentType": 5123, "count": 6, "type": "SCALAR"},
                {"bufferView": 2, "componentType": 5126, "count": 3, "type": "VEC3", "min": [0,0,0], "max": [1,1,1]},
                {"bufferView": 3, "componentType": 5126, "count": 3, "type": "SCALAR"},
                {"bufferView": 4, "componentType": 5123, "count": 3, "type": "SCALAR"},
                {"bufferView": 5, "componentType": 5126, "count": 3, "type": "VEC4"}
              ],
              "bufferViews": [
                {"buffer": 0, "byteOffset": 0, "byteLength": 24},
                {"buffer": 0, "byteOffset": 60, "byteLength": 12},
                {"buffer": 0, "byteOffset": 24, "byteLength": 36},
                {"buffer": 0, "byteOffset": 80, "byteLength": 12},
                {"buffer": 0, "byteOffset": 72, "byteLength": 6},
                {"buffer": 0, "byteOffset": 92, "byteLength": 48}
              ],
              "buffers": [{"byteLength": 140}],
              "animations": [{
                "name": "spin",
                "samplers": [{"input": 3, "output": 5}],
                "channels": [{"sampler": 0, "target": {"node": 2, "path": "rotation"}}]
              }]
            }
            """.Replace("TINT_VALUE", tint.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

        byte[] jsonBytes = Pad4(Encoding.UTF8.GetBytes(json), 0x20);
        byte[] binBytes = Pad4(bin.ToArray(), 0x00);
        int total = 12 + 8 + jsonBytes.Length + 8 + binBytes.Length;

        var glb = new List<byte>(total);
        WriteUint(glb, 0x46546C67u);   // 'glTF'.
        WriteUint(glb, 2);            // version
        WriteUint(glb, (uint)total);
        WriteUint(glb, (uint)jsonBytes.Length);
        WriteUint(glb, 0x4E4F534Au);   // 'JSON'.
        glb.AddRange(jsonBytes);
        WriteUint(glb, (uint)binBytes.Length);
        WriteUint(glb, 0x004E4942u);   // 'BIN\0'.
        glb.AddRange(binBytes);
        return glb.ToArray();
    }

    private static void WriteFloats(List<byte> target, float[] values)
    {
        foreach (float v in values)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(b, v);
            target.AddRange(b);
        }
    }

    private static void WriteUShorts(List<byte> target, ushort[] values)
    {
        foreach (ushort v in values)
        {
            target.Add((byte)(v & 0xFF));
            target.Add((byte)(v >> 8));
        }
    }

    private static void WriteUint(List<byte> target, uint v)
    {
        target.Add((byte)(v & 0xFF));
        target.Add((byte)((v >> 8) & 0xFF));
        target.Add((byte)((v >> 16) & 0xFF));
        target.Add((byte)((v >> 24) & 0xFF));
    }

    private static byte[] Pad4(byte[] data, byte pad)
    {
        int remainder = data.Length % 4;
        if (remainder == 0)
            return data;
        byte[] padded = new byte[data.Length + (4 - remainder)];
        Array.Copy(data, padded, data.Length);
        for (int i = data.Length; i < padded.Length; i++)
            padded[i] = pad;
        return padded;
    }

    private static string Invariant(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
