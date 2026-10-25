using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.UnityImporter.Editor;

/// <summary>Decoded vertex data of a Unity serialized Mesh (class 43).</summary>
public sealed class UnityMeshData
{
    public string Name = string.Empty;
    public int VertexCount;
    /// <summary>XYZ per vertex.</summary>
    public float[] Positions = Array.Empty<float>();
    public float[]? Normals;
    public float[]? Colors;
    /// <summary>UV0 (xy per vertex, Unity bottom-left V origin).</summary>
    public float[]? UV0;
    public float[]? UV1;
    /// <summary>4 weights per vertex (aligned to the SkinnedMeshRenderer bone list).</summary>
    public float[]? SkinWeights;
    /// <summary>4 bone indices per vertex (aligned to the SkinnedMeshRenderer bone list).</summary>
    public uint[]? SkinIndices;
    /// <summary>One index array per submesh.</summary>
    public List<int[]> SubMeshes = new();
    /// <summary>16 floats per bone, row-major (e00 e01 e02 e03 / e10 ...), column-vector convention.</summary>
    public float[] BindPoses = Array.Empty<float>();
    public int BoneCount => BindPoses.Length / 16;
}

/// <summary>Reads Unity's serializedVersion-10 Mesh YAML: vertex streams + index buffer + bind poses.</summary>
public static class UnityMeshReader
{
    private static readonly int[] FormatSize = { 4, 2, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4 };

    public static UnityMeshData Read(UnityYamlDocument doc)
    {
        UnityYamlNode root = doc.Root;
        var mesh = new UnityMeshData
        {
            Name = root.ScalarOf("m_Name") ?? string.Empty,
        };

        var vertexData = root["m_VertexData"] ?? throw new InvalidDataException($"Mesh '{mesh.Name}' has no m_VertexData.");
        mesh.VertexCount = (int)vertexData.LongOf("m_VertexCount");
        byte[] blob = (vertexData["_typelessdata"] ?? vertexData["m_DataSize"] ?? throw new InvalidDataException("missing _typelessdata"))
            .HexBytes();

        // Channel semantics by index: 0 pos, 1 normal, 2 tangent, 3 color, 4..11 uv0..uv7,
        // 12/13 skin (data-type validated below).
        var channels = vertexData["m_Channels"]?.List ?? new List<UnityYamlNode>();
        long maxStream = 0;
        foreach (UnityYamlNode ch in channels)
            maxStream = Math.Max(maxStream, ch.LongOf("stream"));
        int streamCount = (int)(maxStream + 1);
        var stride = new int[streamCount];
        foreach (UnityYamlNode ch in channels)
        {
            int fmt = (int)ch.LongOf("format");
            int dim = (int)ch.LongOf("dimension");
            if (dim > 0 && fmt >= 0 && fmt < FormatSize.Length)
                stride[(int)ch.LongOf("stream")] += FormatSize[fmt] * dim;
        }

        // Vertex data is packed per stream: one contiguous block of vertexCount * stride per stream.
        var streamBase = new int[streamCount];
        for (int s = 1; s < streamCount; s++)
            streamBase[s] = streamBase[s - 1] + mesh.VertexCount * stride[s - 1];

        float[]? Channel(int index)
        {
            if (index >= channels.Count) return null;
            UnityYamlNode ch = channels[index];
            int dim = (int)ch.LongOf("dimension");
            if (dim == 0) return null;
            int fmt = (int)ch.LongOf("format");
            int stream = (int)ch.LongOf("stream");
            int offset = (int)ch.LongOf("offset");
            if (fmt < 0 || fmt >= FormatSize.Length) throw new InvalidDataException($"unsupported vertex format {fmt}");

            var result = new float[mesh.VertexCount * dim];
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                int baseOffset = streamBase[stream] + v * stride[stream] + offset;
                for (int d = 0; d < dim; d++)
                {
                    int o = baseOffset + d * FormatSize[fmt];
                    result[v * dim + d] = fmt switch
                    {
                        0 => BitConverter.ToSingle(blob, o),          // Float32
                        2 => blob[o] / 255f,                          // UNorm8
                        4 => blob[o],                                 // UInt8
                        6 => BitConverter.ToUInt16(blob, o),          // UInt16
                        10 => BitConverter.ToUInt32(blob, o),         // UInt32
                        _ => throw new InvalidDataException($"unsupported vertex format {fmt}"),
                    };
                }
            }
            return result;
        }

        mesh.Positions = Channel(0) ?? throw new InvalidDataException($"Mesh '{mesh.Name}' has no position channel.");
        mesh.Normals = Channel(1);
        mesh.Colors = Channel(3);
        mesh.UV0 = Channel(4);
        mesh.UV1 = Channel(5);

        byte[]? ChannelRaw(int index)
        {
            if (index >= channels.Count) return null;
            UnityYamlNode ch = channels[index];
            int dim = (int)ch.LongOf("dimension");
            if (dim == 0) return null;
            int fmt = (int)ch.LongOf("format");
            int stream = (int)ch.LongOf("stream");
            int offset = (int)ch.LongOf("offset");
            if (fmt < 0 || fmt >= FormatSize.Length) throw new InvalidDataException($"unsupported vertex format {fmt}");
            int size = FormatSize[fmt] * dim;

            var result = new byte[mesh.VertexCount * size];
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                int baseOffset = streamBase[stream] + v * stride[stream] + offset;
                Buffer.BlockCopy(blob, baseOffset, result, v * size, size);
            }
            return result;
        }

        // Bind poses (parsed before skin channels — the vote needs the bone count).
        var bindPoses = root["m_BindPose"]?.List;
        if (bindPoses != null)
        {
            mesh.BindPoses = new float[bindPoses.Count * 16];
            for (int b = 0; b < bindPoses.Count; b++)
            {
                UnityYamlNode bp = bindPoses[b];
                string[] keys = { "e00", "e01", "e02", "e03", "e10", "e11", "e12", "e13", "e20", "e21", "e22", "e23", "e30", "e31", "e32", "e33" };
                for (int k = 0; k < 16; k++)
                    mesh.BindPoses[b * 16 + k] = bp.FloatOf(keys[k]);
            }
        }

        // Skin channels 12/13: Unity's declared format does not always match the stored data
        // (observed: indices declared Float32 but stored as UInt32, weights declared UInt32 but
        // stored Float32). Decode BOTH interpretations per channel and vote on roles by global
        // statistics; then sanitize per vertex.
        float[]? c12f = Channel(12);
        float[]? c13f = Channel(13);
        byte[]? c12u = ChannelRaw(12);
        byte[]? c13u = ChannelRaw(13);
        if (c12f != null && c13f != null && c12u != null && c13u != null && mesh.BoneCount > 0)
        {
            float idx12 = ScoreIndices(c12u, mesh.BoneCount);
            float idx13 = ScoreIndices(c13u, mesh.BoneCount);
            byte[] indicesBytes = idx12 >= idx13 ? c12u : c13u;
            float[] weightsFloat = idx12 >= idx13 ? c13f : c12f;
            if (MathF.Max(idx12, idx13) > 0.5f)
            {
                var indices = new uint[mesh.VertexCount * 4];
                var weights = new float[mesh.VertexCount * 4];
                for (int v = 0; v < mesh.VertexCount; v++)
                {
                    var idx = new uint[4];
                    bool valid = true;
                    for (int k = 0; k < 4; k++)
                    {
                        uint bone = BitConverter.ToUInt32(indicesBytes, (v * 4 + k) * 4);
                        idx[k] = bone;
                        if (bone >= mesh.BoneCount) valid = false;
                    }
                    float w0 = weightsFloat[v * 4], w1 = weightsFloat[v * 4 + 1];
                    float w2 = weightsFloat[v * 4 + 2], w3 = weightsFloat[v * 4 + 3];
                    if (!valid || w0 < 0f || w1 < 0f || w2 < 0f || w3 < 0f)
                    {
                        // Corrupted record (first-record anomaly): bind fully to the first joint.
                        idx = new uint[] { 0, 0, 0, 0 };
                        w0 = 1f; w1 = 0f; w2 = 0f; w3 = 0f;
                    }
                    float sum = w0 + w1 + w2 + w3;
                    if (sum > 0.0001f) { w0 /= sum; w1 /= sum; w2 /= sum; w3 /= sum; }
                    indices[v * 4] = idx[0]; indices[v * 4 + 1] = idx[1];
                    indices[v * 4 + 2] = idx[2]; indices[v * 4 + 3] = idx[3];
                    weights[v * 4] = w0; weights[v * 4 + 1] = w1;
                    weights[v * 4 + 2] = w2; weights[v * 4 + 3] = w3;
                }
                mesh.SkinIndices = indices;
                mesh.SkinWeights = weights;
            }
        }

        static float ScoreIndices(byte[] raw, int boneCount)
        {
            int groups = raw.Length / 16, good = 0;
            for (int g = 0; g < groups; g++)
            {
                uint a = BitConverter.ToUInt32(raw, g * 16);
                uint b = BitConverter.ToUInt32(raw, g * 16 + 4);
                uint c = BitConverter.ToUInt32(raw, g * 16 + 8);
                uint d = BitConverter.ToUInt32(raw, g * 16 + 12);
                if (a < (uint)boneCount && b < (uint)boneCount && c < (uint)boneCount && d < (uint)boneCount)
                    good++;
            }
            return groups == 0 ? 0 : good / (float)groups;
        }

        // Index buffer → per-submesh index arrays.
        byte[] indexBlob = (root["m_IndexBuffer"] ?? throw new InvalidDataException("missing m_IndexBuffer")).HexBytes();
        bool index32 = root.LongOf("m_IndexFormat") == 1;
        var subMeshNodes = root["m_SubMeshes"]?.List ?? new List<UnityYamlNode>();
        foreach (UnityYamlNode sm in subMeshNodes)
        {
            int firstByte = (int)sm.LongOf("firstByte");
            int indexCount = (int)sm.LongOf("indexCount");
            int baseVertex = (int)sm.LongOf("baseVertex");
            var indices = new int[indexCount];
            for (int k = 0; k < indexCount; k++)
            {
                int value = index32
                    ? BitConverter.ToInt32(indexBlob, firstByte + k * 4)
                    : BitConverter.ToUInt16(indexBlob, firstByte + k * 2);
                indices[k] = value + baseVertex;
            }
            mesh.SubMeshes.Add(indices);
        }

        return mesh;
    }
}
