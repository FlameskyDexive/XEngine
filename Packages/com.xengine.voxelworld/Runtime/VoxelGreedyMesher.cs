using System;

using XEngine.Runtime.Jobs;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Face-cull + greedy quad merge. Slice index is written to UV2.x.</summary>
public static class VoxelGreedyMesher
{
    public static VoxelMeshBuild Build(ReadOnlySpan<Voxel> voxels, VoxelAtlas? atlas = null, byte[]? light = null)
        => Build(voxels, atlas, light, null);

    /// <summary>
    /// <paramref name="borderSampler"/> maps chunk-local (x, y, z) — possibly out of
    /// [0, ChunkSize) — to the neighboring chunk's voxel. Without it, borders treat
    /// out-of-chunk as air, which emits duplicate back-to-back quads at every chunk
    /// seam (z-fighting, "single-sided"/missing faces). Out-of-world returns Empty.
    /// </summary>
    public static VoxelMeshBuild Build(ReadOnlySpan<Voxel> voxels, VoxelAtlas? atlas, byte[]? light, Func<int, int, int, Voxel>? borderSampler)
    {
        if (voxels.Length < VoxelCoords.ChunkVoxelCount)
            throw new ArgumentException($"Expected {VoxelCoords.ChunkVoxelCount} voxels.", nameof(voxels));

        Voxel[] copy = voxels.ToArray();
        byte[]? lightCopy = light == null ? null : (byte[])light.Clone();
        var faces = new VoxelMeshBuild[6];
        JobHandle handle = JobSystem.ParallelFor(6, 1, (start, end) =>
        {
            for (int face = start; face < end; face++)
                faces[face] = BuildFace(copy, face, atlas, lightCopy, borderSampler);
        });
        JobSystem.Complete(handle);

        var merged = new VoxelMeshBuild();
        for (int i = 0; i < faces.Length; i++)
        {
            if (faces[i] != null)
                merged.Append(faces[i]);
        }
        return merged;
    }

    private static VoxelMeshBuild BuildFace(Voxel[] voxels, int face, VoxelAtlas? atlas, byte[]? light, Func<int, int, int, Voxel>? borderSampler)
    {
        var build = new VoxelMeshBuild();
        var mask = new ushort[VoxelCoords.ChunkSize * VoxelCoords.ChunkSize];
        bool positive = (face & 1) == 0;
        int axis = face >> 1;

        for (int slice = 0; slice < VoxelCoords.ChunkSize; slice++)
        {
            Array.Clear(mask);
            for (int v = 0; v < VoxelCoords.ChunkSize; v++)
            {
                for (int u = 0; u < VoxelCoords.ChunkSize; u++)
                {
                    Decode(axis, slice, u, v, out int x, out int y, out int z);
                    Voxel cell = voxels[VoxelCoords.Index(x, y, z)];
                    if (cell.IsEmpty)
                        continue;

                    GetNeighbor(axis, positive, x, y, z, out int nx, out int ny, out int nz);
                    if (InChunk(nx, ny, nz))
                    {
                        if (!voxels[VoxelCoords.Index(nx, ny, nz)].IsEmpty)
                            continue;
                    }
                    else if (borderSampler != null && !borderSampler(nx, ny, nz).IsEmpty)
                        continue; // neighbor chunk is solid there — culled, no seam quad

                    mask[v * VoxelCoords.ChunkSize + u] = cell.TypeIndex;
                }
            }

            EmitGreedy(build, mask, axis, positive, slice, atlas, voxels, light);
        }

        return build;
    }

    private static void EmitGreedy(
        VoxelMeshBuild build,
        ushort[] mask,
        int axis,
        bool positive,
        int slice,
        VoxelAtlas? atlas,
        Voxel[] voxels,
        byte[]? light)
    {
        int size = VoxelCoords.ChunkSize;
        for (int v = 0; v < size; v++)
        {
            for (int u = 0; u < size; u++)
            {
                ushort type = mask[v * size + u];
                if (type == 0)
                    continue;

                int width = 1;
                while (u + width < size && mask[v * size + u + width] == type)
                    width++;

                int height = 1;
                bool grow = true;
                while (v + height < size && grow)
                {
                    for (int k = 0; k < width; k++)
                    {
                        if (mask[(v + height) * size + u + k] != type)
                        {
                            grow = false;
                            break;
                        }
                    }
                    if (grow)
                        height++;
                }

                for (int dv = 0; dv < height; dv++)
                {
                    for (int du = 0; du < width; du++)
                        mask[(v + dv) * size + u + du] = 0;
                }

                EmitFaceQuad(build, axis, positive, slice, u, v, width, height, ResolveSlice(atlas, type, axis, positive), voxels, light);
            }
        }
    }

    private static void EmitFaceQuad(
        VoxelMeshBuild build,
        int axis,
        bool positive,
        int slice,
        int u,
        int v,
        int width,
        int height,
        ushort type,
        Voxel[] voxels,
        byte[]? light)
    {
        float plane = positive ? slice + 1 : slice;
        Float3 n = axis switch
        {
            0 => positive ? new Float3(1, 0, 0) : new Float3(-1, 0, 0),
            1 => positive ? new Float3(0, 1, 0) : new Float3(0, -1, 0),
            _ => positive ? new Float3(0, 0, 1) : new Float3(0, 0, -1),
        };

        Float3 a, b, c, d;
        if (axis == 0)
        {
            a = new Float3(plane, v, u);
            b = new Float3(plane, v, u + width);
            c = new Float3(plane, v + height, u + width);
            d = new Float3(plane, v + height, u);
        }
        else if (axis == 1)
        {
            a = new Float3(u, plane, v);
            b = new Float3(u + width, plane, v);
            c = new Float3(u + width, plane, v + height);
            d = new Float3(u, plane, v + height);
        }
        else
        {
            a = new Float3(u, v, plane);
            b = new Float3(u + width, v, plane);
            c = new Float3(u + width, v + height, plane);
            d = new Float3(u, v + height, plane);
        }

        // X/Y parameterizations have a negative cross product; Z has a positive
        // one. Every triangle must face its outward normal (rendering AND physics).
        bool reverseWinding = positive == (axis != 2);

        int planeI = positive ? slice + 1 : slice;
        Color ca = ShadeVertex(voxels, light, axis, planeI, a);
        Color cb = ShadeVertex(voxels, light, axis, planeI, b);
        Color cc = ShadeVertex(voxels, light, axis, planeI, c);
        Color cd = ShadeVertex(voxels, light, axis, planeI, d);
        build.AddQuad(a, b, c, d, n, width, height, type, ca, cb, cc, cd, reverseWinding);
    }

    private static Color ShadeVertex(Voxel[] voxels, byte[]? light, int axis, int plane, Float3 vertex)
    {
        int i, j;
        if (axis == 0)
        {
            i = (int)MathF.Round(vertex.Z);
            j = (int)MathF.Round(vertex.Y);
        }
        else if (axis == 1)
        {
            i = (int)MathF.Round(vertex.X);
            j = (int)MathF.Round(vertex.Z);
        }
        else
        {
            i = (int)MathF.Round(vertex.X);
            j = (int)MathF.Round(vertex.Y);
        }

        int x, y, z;
        if (axis == 0) { x = plane; y = j; z = i; }
        else if (axis == 1) { x = i; y = plane; z = j; }
        else { x = i; y = j; z = plane; }

        byte faceLight = SampleLight(light, x, y, z);
        int occ = 0;
        if (axis == 0)
        {
            if (IsSolid(voxels, x, y - 1, z)) occ++;
            if (IsSolid(voxels, x, y, z - 1)) occ++;
            if (IsSolid(voxels, x, y - 1, z - 1)) occ++;
        }
        else if (axis == 1)
        {
            if (IsSolid(voxels, x - 1, y, z)) occ++;
            if (IsSolid(voxels, x, y, z - 1)) occ++;
            if (IsSolid(voxels, x - 1, y, z - 1)) occ++;
        }
        else
        {
            if (IsSolid(voxels, x - 1, y, z)) occ++;
            if (IsSolid(voxels, x, y - 1, z)) occ++;
            if (IsSolid(voxels, x - 1, y - 1, z)) occ++;
        }

        return VoxelLighting.FaceColor(faceLight, occ);
    }

    private static byte SampleLight(byte[]? light, int x, int y, int z)
    {
        if (!InChunk(x, y, z))
            return VoxelLighting.Max;
        if (light == null)
            return VoxelLighting.Max;
        return light[VoxelCoords.Index(x, y, z)];
    }

    private static bool IsSolid(Voxel[] voxels, int x, int y, int z) =>
        InChunk(x, y, z) && !voxels[VoxelCoords.Index(x, y, z)].IsEmpty;

    private static void Decode(int axis, int slice, int u, int v, out int x, out int y, out int z)
    {
        if (axis == 0)
        {
            x = slice;
            y = v;
            z = u;
        }
        else if (axis == 1)
        {
            x = u;
            y = slice;
            z = v;
        }
        else
        {
            x = u;
            y = v;
            z = slice;
        }
    }

    private static void GetNeighbor(int axis, bool positive, int x, int y, int z, out int nx, out int ny, out int nz)
    {
        int delta = positive ? 1 : -1;
        nx = x;
        ny = y;
        nz = z;
        if (axis == 0) nx += delta;
        else if (axis == 1) ny += delta;
        else nz += delta;
    }

    private static ushort ResolveSlice(VoxelAtlas? atlas, ushort type, int axis, bool positive) =>
        atlas == null ? type : atlas.Slice(type, axis, positive);

    private static bool InChunk(int x, int y, int z) =>
        (uint)x < VoxelCoords.ChunkSize && (uint)y < VoxelCoords.ChunkSize && (uint)z < VoxelCoords.ChunkSize;
}
