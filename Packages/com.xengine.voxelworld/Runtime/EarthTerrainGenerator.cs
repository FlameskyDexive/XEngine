using System;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

public enum VoxelBiomeKind : byte
{
    Forest = 0,
    Desert = 1,
    Snow = 2,
}

[CreateAssetMenu("Voxel World/Earth Terrain Generator")]
public sealed class EarthTerrainGenerator : VoxelTerrainGenerator
{
    public int Seed = 1;
    public int BaseHeight = 36;
    public int Amplitude = 20;
    public ushort ForestTop = 2;
    public ushort ForestFill = 3;
    public ushort DesertTop = 6;
    public ushort DesertFill = 6;
    public ushort SnowTop = 7;
    public ushort SnowFill = 3;
    public ushort Stone = 4;
    public ushort Water = VoxelDecorations.Water;
    public float PlantChance = 0.08f;
    public float TreeChance = 0.012f;
    public bool AddClouds = true;
    public int CloudAltitude = 48;
    public bool AddCaves = true;
    /// <summary>Carve when Smooth3 density exceeds this; lower carves wider caves.</summary>
    public float CaveThreshold = 0.5f;
    public float CaveScaleXZ = 0.075f;
    public float CaveScaleY = 0.11f;
    /// <summary>Caves never carve below this world Y (soft floor).</summary>
    public int CaveFloorY = 4;
    /// <summary>Solid cap kept between the surface and any cave.</summary>
    public int CaveSurfaceGap = 3;
    /// <summary>Stamp watch towers / shrines / ruins on the structure grid.</summary>
    public bool AddStructures = true;

    public override void PaintChunk(Span<Voxel> voxels, Int3 chunkMin, ushort terrainTypeIndex)
    {
        if (voxels.Length < VoxelCoords.ChunkVoxelCount)
            throw new ArgumentException($"Chunk buffer must hold {VoxelCoords.ChunkVoxelCount} voxels.", nameof(voxels));

        _ = terrainTypeIndex;
        Span<int> grounds = stackalloc int[VoxelCoords.ChunkSize * VoxelCoords.ChunkSize];
        for (int z = 0; z < VoxelCoords.ChunkSize; z++)
        {
            for (int x = 0; x < VoxelCoords.ChunkSize; x++)
            {
                int worldX = chunkMin.X + x;
                int worldZ = chunkMin.Z + z;
                SampleColumn(worldX, worldZ, out int ground, out VoxelBiomeKind biome);
                grounds[z * VoxelCoords.ChunkSize + x] = ground;
                GetTypes(biome, out ushort top, out ushort fill);
                for (int y = 0; y < VoxelCoords.ChunkSize; y++)
                {
                    int worldY = chunkMin.Y + y;
                    Voxel cell;
                    if (worldY < ground - 3)
                        cell = Voxel.Solid(Stone);
                    else if (worldY < ground)
                        cell = Voxel.Solid(fill);
                    else if (worldY == ground)
                        cell = Voxel.Solid(top);
                    else
                        cell = Voxel.Empty;
                    voxels[VoxelCoords.Index(x, y, z)] = cell;
                }

                if (biome == VoxelBiomeKind.Forest)
                    DecorateForest(voxels, chunkMin, x, z, ground, worldX, worldZ);
            }
        }

        if (AddWater)
            VoxelDecorations.FillWater(voxels, chunkMin, WaterLevel);
        if (AddCaves)
            CarveCaves(voxels, chunkMin, grounds);
        if (AddStructures)
            VoxelStructures.StampForChunk(voxels, chunkMin, Seed, SampleColumnForStructures);
        if (AddClouds)
            VoxelDecorations.StampClouds(voxels, chunkMin, CloudAltitude, Seed);
    }

    /// <summary>Erodes solid cells below the surface cap into air; runs after water fill so
    /// flooded cells stay water and ocean floors keep an intact rim. Reuses the heights the
    /// terrain pass already sampled — a per-cell SampleColumn here would triple build cost.</summary>
    private void CarveCaves(Span<Voxel> voxels, Int3 chunkMin, ReadOnlySpan<int> grounds)
    {
        for (int y = 0; y < VoxelCoords.ChunkSize; y++)
        {
            int worldY = chunkMin.Y + y;
            if (worldY < CaveFloorY)
                continue;

            for (int z = 0; z < VoxelCoords.ChunkSize; z++)
            {
                for (int x = 0; x < VoxelCoords.ChunkSize; x++)
                {
                    int index = VoxelCoords.Index(x, y, z);
                    if (voxels[index].IsEmpty)
                        continue;

                    int ground = grounds[z * VoxelCoords.ChunkSize + x];
                    if (worldY > ground - CaveSurfaceGap)
                        continue;

                    float density = VoxelValueNoise.Smooth3(
                        (chunkMin.X + x) * CaveScaleXZ,
                        worldY * CaveScaleY,
                        (chunkMin.Z + z) * CaveScaleXZ,
                        Seed + 53);
                    if (density > CaveThreshold)
                        voxels[index] = Voxel.Empty;
                }
            }
        }
    }

    private (int ground, VoxelBiomeKind biome) SampleColumnForStructures(int worldX, int worldZ)
    {
        SampleColumn(worldX, worldZ, out int ground, out VoxelBiomeKind biome);
        return (ground, biome);
    }

    private void DecorateForest(Span<Voxel> voxels, Int3 chunkMin, int x, int z, int ground, int worldX, int worldZ)
    {
        float plant = (VoxelValueNoise.Hash(worldX, worldZ, Seed + 31) + 1f) * 0.5f;
        if (plant < PlantChance)
            VoxelDecorations.TryStampPlant(voxels, chunkMin, x, z, ground);
        float tree = (VoxelValueNoise.Hash(worldX, worldZ, Seed + 77) + 1f) * 0.5f;
        if (tree < TreeChance)
            VoxelDecorations.StampTree(voxels, chunkMin, x, z, ground);
    }

    public void SampleColumn(int worldX, int worldZ, out int ground, out VoxelBiomeKind biome)
    {
        float heightN = VoxelValueNoise.Fbm(worldX * 0.03f, worldZ * 0.03f, Seed);
        float moistN = VoxelValueNoise.Fbm(worldX * 0.05f, worldZ * 0.05f, Seed + 19);
        float altitude = Math.Clamp(0.5f + 0.5f * heightN, 0f, 1f);
        float moisture = Math.Clamp(0.5f + 0.5f * moistN, 0f, 1f);
        ground = BaseHeight + (int)MathF.Round(heightN * Amplitude);
        biome = PickBiome(altitude, moisture);
    }

    public static VoxelBiomeKind PickBiome(float altitude, float moisture)
    {
        if (altitude >= 0.72f)
            return VoxelBiomeKind.Snow;
        if (moisture < 0.45f && altitude < 0.65f)
            return VoxelBiomeKind.Desert;
        return VoxelBiomeKind.Forest;
    }

    public void GetTypes(VoxelBiomeKind biome, out ushort top, out ushort fill)
    {
        switch (biome)
        {
            case VoxelBiomeKind.Desert:
                top = DesertTop;
                fill = DesertFill;
                break;
            case VoxelBiomeKind.Snow:
                top = SnowTop;
                fill = SnowFill;
                break;
            default:
                top = ForestTop;
                fill = ForestFill;
                break;
        }
    }
}
