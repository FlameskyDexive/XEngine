using System;
using System.Buffers;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

public sealed class VoxelChunk
{
    public Int3 Coord;
    public VoxelPalette Blocks = VoxelPalette.Fill(0);
    public readonly byte[] Light = new byte[VoxelCoords.ChunkVoxelCount];
    public bool Dirty;
    public bool Edited;
    public Mesh? Mesh { get; private set; }

    public Voxel GetVoxel(int index) =>
        Blocks.Get(index) is ushort t && t != 0 ? Voxel.Solid(t) : Voxel.Empty;

    public void SetVoxel(int index, Voxel voxel)
    {
        ushort before = Blocks.Get(index);
        if (before == voxel.TypeIndex)
            return;
        Blocks.Set(index, voxel.TypeIndex);
        Dirty = true;
        Edited = true;
        Mesh = null;
    }

    public void Clear()
    {
        Blocks = VoxelPalette.Fill(0);
        Array.Clear(Light);
        Coord = default;
        Dirty = false;
        Edited = false;
        Mesh = null;
    }

    public void FillSolid(ushort typeIndex)
    {
        Blocks = VoxelPalette.Fill(typeIndex);
        Dirty = true;
        Edited = false;
    }

    public VoxelMeshBuild RebuildMesh(VoxelAtlas? atlas = null, Func<ushort, byte>? emit = null, Func<int, int, int, Voxel>? borderSampler = null)
    {
        Voxel[] scratch = ArrayPool<Voxel>.Shared.Rent(VoxelCoords.ChunkVoxelCount);
        try
        {
            var voxels = scratch.AsSpan(0, VoxelCoords.ChunkVoxelCount);
            Blocks.ExpandTo(voxels);
            VoxelLighting.Bake(voxels, Light, emit);
            VoxelMeshBuild build = VoxelGreedyMesher.Build(voxels, atlas, Light, borderSampler);
            Dirty = false;
            if (build.Vertices.Count == 0)
                return build;
            Mesh ??= new Mesh { Name = $"VoxelChunk {Coord.X},{Coord.Y},{Coord.Z}" };
            Mesh.Vertices = build.Vertices.ToArray();
            Mesh.Normals = build.Normals.ToArray();
            Mesh.UV = build.UV.ToArray();
            Mesh.UV2 = build.UV2.ToArray();
            Mesh.Colors = build.Colors.ToArray();
            Mesh.Indices = build.Indices.ToArray();
            Mesh.RecalculateBounds();
            Mesh.Upload();
            return build;
        }
        finally
        {
            ArrayPool<Voxel>.Shared.Return(scratch);
        }
    }
}
