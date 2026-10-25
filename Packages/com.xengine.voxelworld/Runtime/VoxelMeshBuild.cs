using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

public sealed class VoxelMeshBuild
{
    public readonly List<Float3> Vertices = [];
    public readonly List<Float3> Normals = [];
    public readonly List<Float2> UV = [];
    public readonly List<Float2> UV2 = [];
    public readonly List<Color> Colors = [];
    public readonly List<uint> Indices = [];

    public int QuadCount => Indices.Count / 6;

    public void Append(VoxelMeshBuild other)
    {
        uint baseVertex = (uint)Vertices.Count;
        Vertices.AddRange(other.Vertices);
        Normals.AddRange(other.Normals);
        UV.AddRange(other.UV);
        UV2.AddRange(other.UV2);
        Colors.AddRange(other.Colors);
        for (int i = 0; i < other.Indices.Count; i++)
            Indices.Add(other.Indices[i] + baseVertex);
    }

    public void AddQuad(Float3 a, Float3 b, Float3 c, Float3 d, Float3 normal, int width, int height, ushort slice)
        => AddQuad(a, b, c, d, normal, width, height, slice, Color.White, Color.White, Color.White, Color.White);

    public void AddQuad(
        Float3 a,
        Float3 b,
        Float3 c,
        Float3 d,
        Float3 normal,
        int width,
        int height,
        ushort slice,
        Color ca,
        Color cb,
        Color cc,
        Color cd,
        bool reverseWinding = false)
    {
        uint i = (uint)Vertices.Count;
        Vertices.Add(a);
        Vertices.Add(b);
        Vertices.Add(c);
        Vertices.Add(d);
        Normals.Add(normal);
        Normals.Add(normal);
        Normals.Add(normal);
        Normals.Add(normal);
        Colors.Add(ca);
        Colors.Add(cb);
        Colors.Add(cc);
        Colors.Add(cd);
        for (int n = 0; n < 4; n++)
            UV2.Add(new Float2(slice, 0f));
        UV.Add(new Float2(0f, 0f));
        UV.Add(new Float2(width, 0f));
        UV.Add(new Float2(width, height));
        UV.Add(new Float2(0f, height));
        Indices.Add(i);
        Indices.Add(i + (reverseWinding ? 2u : 1u));
        Indices.Add(i + (reverseWinding ? 1u : 2u));
        Indices.Add(i);
        Indices.Add(i + (reverseWinding ? 3u : 2u));
        Indices.Add(i + (reverseWinding ? 2u : 3u));
    }
}
