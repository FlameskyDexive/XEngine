using System;
using System.IO;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;


namespace XEngine.UnityImporter.Editor;

/// <summary>
/// Claims <c>.mesh</c> files whose content is Unity serialized YAML and converts them directly
/// into native XEngine <see cref="Mesh"/> assets (no glTF detour: Unity is left-handed Y-up just
/// like XEngine, so positions/normals/UVs/indices pass through unchanged). Native XEngine .mesh
/// files (Echo text) are delegated to the engine's own <see cref="MeshImporter"/>.
/// </summary>
[ImporterFor(".mesh", Priority = 100)]
public sealed class UnityMeshImporter : AssetImporter
{
    public override int Version => 1;

    public override bool CanImport(string absolutePath) => true; // owns .mesh; sniffs in Import and delegates

    public override bool Import(ImportContext ctx)
    {
        if (!UnityAssetSniffer.IsUnityYaml(ctx.AbsolutePath))
            return new MeshImporter().Import(ctx);

        try
        {
            UnityYamlDocument doc = UnityYaml.ParseFile(ctx.AbsolutePath)[0];
            UnityMeshData data = UnityMeshReader.Read(doc);

            var mesh = new Mesh { Name = data.Name };
            mesh.Vertices = ToFloat3(data.Positions);
            if (data.Normals != null) mesh.Normals = ToFloat3(data.Normals);
            if (data.UV0 != null) mesh.UV = ToFloat2(data.UV0);
            if (data.UV1 != null) mesh.UV2 = ToFloat2(data.UV1);
            if (data.Colors != null)
            {
                var colors = new Color[data.Colors.Length / 4];
                for (int i = 0; i < colors.Length; i++)
                    colors[i] = new Color(data.Colors[i * 4], data.Colors[i * 4 + 1], data.Colors[i * 4 + 2], data.Colors[i * 4 + 3]);
                mesh.Colors = colors;
            }

            // Indices: submesh ranges over one shared buffer (Unity topology 0 = triangles).
            int totalIndices = 0;
            foreach (int[] subMesh in data.SubMeshes) totalIndices += subMesh.Length;
            var indices = new uint[totalIndices];
            int cursor = 0;
            mesh.SetSubMeshCount(data.SubMeshes.Count);
            for (int s = 0; s < data.SubMeshes.Count; s++)
            {
                for (int k = 0; k < data.SubMeshes[s].Length; k++)
                    indices[cursor + k] = (uint)data.SubMeshes[s][k];
                mesh.SetSubMesh(s, new SubMeshDescriptor(cursor, data.SubMeshes[s].Length));
                cursor += data.SubMeshes[s].Length;
            }
            mesh.Indices = indices;

            // Skin: engine shaders use 1-based bone indices (0 = no bone). Bind poses are stored
            // per-mesh in Unity, aligned with the SkinnedMeshRenderer bone list that the prefab
            // importer wires by path in the same order.
            if (data.SkinIndices != null && data.SkinWeights != null)
            {
                int vertexCount = data.VertexCount;
                var boneIndices = new Float4[vertexCount];
                var boneWeights = new Float4[vertexCount];
                for (int v = 0; v < vertexCount; v++)
                {
                    float w0 = data.SkinWeights[v * 4], w1 = data.SkinWeights[v * 4 + 1];
                    float w2 = data.SkinWeights[v * 4 + 2], w3 = data.SkinWeights[v * 4 + 3];
                    boneIndices[v] = new Float4(
                        w0 > 0f ? data.SkinIndices[v * 4] + 1 : 0,
                        w1 > 0f ? data.SkinIndices[v * 4 + 1] + 1 : 0,
                        w2 > 0f ? data.SkinIndices[v * 4 + 2] + 1 : 0,
                        w3 > 0f ? data.SkinIndices[v * 4 + 3] + 1 : 0);
                    boneWeights[v] = new Float4(w0, w1, w2, w3);
                }
                mesh.BoneIndices = boneIndices;
                mesh.BoneWeights = boneWeights;

                // Unity row-major (e00 e01 e02 e03 / e10 …) → engine column-vector Float4x4.
                var bindPoses = new Float4x4[data.BoneCount];
                for (int b = 0; b < data.BoneCount; b++)
                    bindPoses[b] = new Float4x4(
                        new Float4(data.BindPoses[b * 16], data.BindPoses[b * 16 + 4], data.BindPoses[b * 16 + 8], data.BindPoses[b * 16 + 12]),
                        new Float4(data.BindPoses[b * 16 + 1], data.BindPoses[b * 16 + 5], data.BindPoses[b * 16 + 9], data.BindPoses[b * 16 + 13]),
                        new Float4(data.BindPoses[b * 16 + 2], data.BindPoses[b * 16 + 6], data.BindPoses[b * 16 + 10], data.BindPoses[b * 16 + 14]),
                        new Float4(data.BindPoses[b * 16 + 3], data.BindPoses[b * 16 + 7], data.BindPoses[b * 16 + 11], data.BindPoses[b * 16 + 15]));
                mesh.BindPoses = bindPoses;
            }

            ctx.SetMainAsset(mesh);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UnityImport] Failed to import Unity mesh '{ctx.AbsolutePath}': {ex.Message}");
            return false;
        }
    }

    private static Float3[] ToFloat3(float[] values)
    {
        var result = new Float3[values.Length / 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Float3(values[i * 3], values[i * 3 + 1], values[i * 3 + 2]);
        return result;
    }

    private static Float2[] ToFloat2(float[] values)
    {
        var result = new Float2[values.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Float2(values[i * 2], values[i * 2 + 1]);
        return result;
    }
}
