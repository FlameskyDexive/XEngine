using System;
using System.IO;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>CPU raster of streamed voxels plus cube-rig actors. Used for playable evidence shots.</summary>
public static class VoxelPlayableRender
{
    public const int Width = 1280;
    public const int Height = 720;

    public static string Write(
        string path,
        VoxelWorld world,
        Float3 camera,
        Float3 lookAt,
        VoxelBodyPart[]? playerParts,
        Float3 playerOrigin,
        VoxelMob? mob,
        bool includeDebris)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] rgba = Raster(world, camera, lookAt, playerParts, playerOrigin, mob, includeDebris);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, VoxelFaceTextures.EncodePng(rgba, Width, Height));
        return path;
    }

    public static byte[] Raster(
        VoxelWorld world,
        Float3 camera,
        Float3 lookAt,
        VoxelBodyPart[]? playerParts,
        Float3 playerOrigin,
        VoxelMob? mob,
        bool includeDebris)
    {
        ArgumentNullException.ThrowIfNull(world);
        var rgba = new byte[Width * Height * 4];
        var depth = new float[Width * Height];
        Array.Fill(depth, float.PositiveInfinity);
        FillSky(rgba);

        Float3 forward = Float3.Normalize(lookAt - camera);
        Float3 right = Float3.Normalize(Float3.Cross(new Float3(0f, 1f, 0f), forward));
        if (Float3.LengthSquared(right) < 1e-6f)
            right = new Float3(1f, 0f, 0f);
        Float3 up = Float3.Cross(forward, right);
        Float3 light = Float3.Normalize(new Float3(0.45f, 0.82f, 0.35f));
        float focal = 0.5f * Height / MathF.Tan(28f * MathF.PI / 180f);

        VoxelAtlas atlas = world.Atlas ?? VoxelWorld.CreateDemoAtlas();
        RasterWorld(world, atlas, camera, right, up, forward, light, focal, rgba, depth);
        if (playerParts is { Length: > 0 })
            RasterRig(playerParts, playerOrigin, camera, right, up, forward, light, focal, rgba, depth);
        if (mob != null)
            RasterRig(VoxelCharacterRig.Mob(), mob.Position, camera, right, up, forward, light, focal, rgba, depth);
        if (includeDebris)
            RasterDebris(world.BreakFx, camera, right, up, forward, light, focal, rgba, depth);
        return rgba;
    }

    private static void RasterWorld(
        VoxelWorld world,
        VoxelAtlas atlas,
        Float3 camera,
        Float3 right,
        Float3 up,
        Float3 forward,
        Float3 light,
        float focal,
        byte[] rgba,
        float[] depth)
    {
        Int3 focus = VoxelCoords.ChunkCoord(camera * 0.15f + world.Focus * 0.85f);
        int vd = Math.Max(1, world.Streamer.ViewDistance);
        var voxels = new Voxel[VoxelCoords.ChunkVoxelCount];
        var lightBake = new byte[VoxelCoords.ChunkVoxelCount];
        for (int dz = -vd; dz <= vd; dz++)
        {
            for (int dx = -vd; dx <= vd; dx++)
            {
                for (int cy = world.Streamer.MinChunkY; cy <= Math.Max(world.Streamer.MaxChunkY, 1); cy++)
                {
                    var coord = new Int3(focus.X + dx, cy, focus.Z + dz);
                    Int3 min = VoxelCoords.ChunkMin(coord);
                    Array.Clear(voxels);
                    bool any = false;
                    for (int i = 0; i < voxels.Length; i++)
                    {
                        Int3 local = VoxelCoords.LocalFromIndex(i);
                        var worldCell = new Int3(min.X + local.X, min.Y + local.Y, min.Z + local.Z);
                        if (!world.Streamer.TryGetVoxel(worldCell, out Voxel voxel) || voxel.IsEmpty)
                            continue;
                        voxels[i] = voxel;
                        any = true;
                    }

                    if (!any)
                        continue;
                    VoxelLighting.Bake(voxels, lightBake);
                    VoxelMeshBuild mesh = VoxelGreedyMesher.Build(voxels, atlas, lightBake);
                    RasterMesh(mesh, atlas, min, camera, right, up, forward, light, focal, rgba, depth);
                }
            }
        }
    }

    private static void RasterMesh(
        VoxelMeshBuild mesh,
        VoxelAtlas atlas,
        Int3 chunkMin,
        Float3 camera,
        Float3 right,
        Float3 up,
        Float3 forward,
        Float3 light,
        float focal,
        byte[] rgba,
        float[] depth)
    {
        Float3 origin = new(chunkMin.X, chunkMin.Y, chunkMin.Z);
        for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            int i0 = (int)mesh.Indices[i];
            int i1 = (int)mesh.Indices[i + 1];
            int i2 = (int)mesh.Indices[i + 2];
            if (!Project(mesh.Vertices[i0] + origin, camera, right, up, forward, focal, out Float3 p0))
                continue;
            if (!Project(mesh.Vertices[i1] + origin, camera, right, up, forward, focal, out Float3 p1))
                continue;
            if (!Project(mesh.Vertices[i2] + origin, camera, right, up, forward, focal, out Float3 p2))
                continue;
            int slice = mesh.UV2.Count > i0 ? (int)mesh.UV2[i0].X : 0;
            Float2 uv0 = mesh.UV.Count > i0 ? mesh.UV[i0] : default;
            Float2 uv1 = mesh.UV.Count > i1 ? mesh.UV[i1] : default;
            Float2 uv2 = mesh.UV.Count > i2 ? mesh.UV[i2] : default;
            Float3 n = mesh.Normals.Count > i0 ? mesh.Normals[i0] : new Float3(0f, 1f, 0f);
            float lambert = Math.Clamp(0.28f + 0.72f * MathF.Max(0f, Float3.Dot(n, light)), 0.12f, 1f);
            FillTextured(p0, p1, p2, uv0, uv1, uv2, atlas, slice, lambert, rgba, depth);
        }
    }

    private static void RasterRig(
        VoxelBodyPart[] parts,
        Float3 origin,
        Float3 camera,
        Float3 right,
        Float3 up,
        Float3 forward,
        Float3 light,
        float focal,
        byte[] rgba,
        float[] depth)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            VoxelBodyPart part = parts[i];
            FillBox(origin + part.LocalPosition, part.Size, part.Color, camera, right, up, forward, light, focal, rgba, depth);
        }
    }

    private static void RasterDebris(
        VoxelBreakFx fx,
        Float3 camera,
        Float3 right,
        Float3 up,
        Float3 forward,
        Float3 light,
        float focal,
        byte[] rgba,
        float[] depth)
    {
        for (int i = 0; i < fx.Alive.Count; i++)
        {
            VoxelDebris chip = fx.Alive[i];
            FillBox(
                chip.Position,
                new Float3(chip.Size, chip.Size, chip.Size),
                chip.Color,
                camera,
                right,
                up,
                forward,
                light,
                focal,
                rgba,
                depth);
        }
    }

    private static void FillBox(
        Float3 center,
        Float3 size,
        Color color,
        Float3 camera,
        Float3 right,
        Float3 up,
        Float3 forward,
        Float3 light,
        float focal,
        byte[] rgba,
        float[] depth)
    {
        float hx = size.X * 0.5f;
        float hy = size.Y * 0.5f;
        float hz = size.Z * 0.5f;
        Float3 x = new(hx, 0f, 0f);
        Float3 y = new(0f, hy, 0f);
        Float3 z = new(0f, 0f, hz);
        Face(center + z, x, y, new Float3(0f, 0f, 1f));
        Face(center - z, -x, y, new Float3(0f, 0f, -1f));
        Face(center + x, -z, y, new Float3(1f, 0f, 0f));
        Face(center - x, z, y, new Float3(-1f, 0f, 0f));
        Face(center + y, x, -z, new Float3(0f, 1f, 0f));
        Face(center - y, x, z, new Float3(0f, -1f, 0f));

        void Face(Float3 mid, Float3 a, Float3 b, Float3 n)
        {
            float lambert = Math.Clamp(0.42f + 0.58f * MathF.Max(0f, Float3.Dot(n, light)), 0.35f, 1f);
            Color shaded = new(color.R * lambert, color.G * lambert, color.B * lambert, 1f);
            Float3 v0 = mid - a - b;
            Float3 v1 = mid + a - b;
            Float3 v2 = mid + a + b;
            Float3 v3 = mid - a + b;
            if (Project(v0, camera, right, up, forward, focal, out Float3 p0) &&
                Project(v1, camera, right, up, forward, focal, out Float3 p1) &&
                Project(v2, camera, right, up, forward, focal, out Float3 p2))
                FillSolid(p0, p1, p2, shaded, rgba, depth);
            if (Project(v0, camera, right, up, forward, focal, out p0) &&
                Project(v2, camera, right, up, forward, focal, out p2) &&
                Project(v3, camera, right, up, forward, focal, out Float3 p3))
                FillSolid(p0, p2, p3, shaded, rgba, depth);
        }
    }

    private static bool Project(
        Float3 world,
        Float3 camera,
        Float3 right,
        Float3 up,
        Float3 forward,
        float focal,
        out Float3 screen)
    {
        Float3 rel = world - camera;
        float z = Float3.Dot(rel, forward);
        if (z < 0.35f)
        {
            screen = default;
            return false;
        }

        float x = Float3.Dot(rel, right);
        float y = Float3.Dot(rel, up);
        screen = new Float3(
            Width * 0.5f + x * focal / z,
            Height * 0.5f - y * focal / z,
            z);
        return true;
    }

    private static void FillTextured(
        Float3 a,
        Float3 b,
        Float3 c,
        Float2 uv0,
        Float2 uv1,
        Float2 uv2,
        VoxelAtlas atlas,
        int slice,
        float lambert,
        byte[] rgba,
        float[] depth)
    {
        Raster(a, b, c, (w0, w1, w2) =>
        {
            float u = w0 * uv0.X + w1 * uv1.X + w2 * uv2.X;
            float v = w0 * uv0.Y + w1 * uv1.Y + w2 * uv2.Y;
            Color tex = atlas.SampleLayer(slice, u, v);
            return new Color(tex.R * lambert, tex.G * lambert, tex.B * lambert, 1f);
        }, rgba, depth);
    }

    private static void FillSolid(Float3 a, Float3 b, Float3 c, Color color, byte[] rgba, float[] depth) =>
        Raster(a, b, c, (_, _, _) => color, rgba, depth);

    private static void Raster(
        Float3 a,
        Float3 b,
        Float3 c,
        Func<float, float, float, Color> shade,
        byte[] rgba,
        float[] depth)
    {
        int minX = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0, Width - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), 0, Width - 1);
        int minY = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0, Height - 1);
        int maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), 0, Height - 1);
        float area = Edge(a, b, c);
        if (MathF.Abs(area) < 1e-3f)
            return;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Float3(x + 0.5f, y + 0.5f, 0f);
                float w0 = Edge(b, c, p) / area;
                float w1 = Edge(c, a, p) / area;
                float w2 = Edge(a, b, p) / area;
                if (w0 < 0f || w1 < 0f || w2 < 0f)
                    continue;
                float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                int di = y * Width + x;
                if (z >= depth[di])
                    continue;
                depth[di] = z;
                Color color = shade(w0, w1, w2);
                int o = di * 4;
                rgba[o] = (byte)Math.Clamp((int)(color.R * 255f), 0, 255);
                rgba[o + 1] = (byte)Math.Clamp((int)(color.G * 255f), 0, 255);
                rgba[o + 2] = (byte)Math.Clamp((int)(color.B * 255f), 0, 255);
                rgba[o + 3] = 255;
            }
        }
    }

    private static float Edge(Float3 a, Float3 b, Float3 c) =>
        (c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X);

    private static void FillSky(byte[] rgba)
    {
        for (int y = 0; y < Height; y++)
        {
            float t = y / (float)(Height - 1);
            byte r = (byte)(96 + (206 - 96) * t);
            byte g = (byte)(156 + (224 - 156) * t);
            byte b = (byte)(214 + (244 - 214) * t);
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Width + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = 255;
            }
        }
    }
}
