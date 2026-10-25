// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.TileCache;
using DotRecast.Detour.TileCache.Io;
using DotRecast.Detour.TileCache.Io.Compress;
using DotRecast.Recast;
using DotRecast.Recast.Geom;

namespace XEngine.Runtime.Navigation;

/// <summary>Builds and serializes a <see cref="DtTileCache"/> used for runtime obstacle carving.</summary>
internal static class NavMeshTileCache
{
    public const bool Compatibility = true;

    public static DtTileCache Build(IRcInputGeomProvider geom, NavMeshBuildSettings settings, RcConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(geom);
        int tileSize = Math.Max(1, settings.TileSize);
        RcVec3f bmin = geom.GetMeshBoundsMin();
        RcVec3f bmax = geom.GetMeshBoundsMax();
        RcRecast.CalcTileCount(bmin, bmax, settings.CellSize, tileSize, tileSize, out int tw, out int th);

        var cacheParams = new DtTileCacheParams
        {
            orig = bmin,
            cs = settings.CellSize,
            ch = settings.CellHeight,
            width = tileSize,
            height = tileSize,
            walkableHeight = settings.AgentHeight,
            walkableRadius = settings.AgentRadius,
            walkableClimb = settings.AgentMaxClimb,
            maxSimplificationError = settings.EdgeMaxError,
            maxTiles = Math.Max(1, tw * th * DtTileCacheLayer.EXPECTED_LAYERS_PER_TILE),
            maxObstacles = 128,
        };

        int tileBits = Math.Min(DtUtils.Ilog2(DtUtils.NextPow2(Math.Max(1, tw * th))), 14);
        int polyBits = 22 - tileBits;
        var navParams = new DtNavMeshParams
        {
            orig = bmin,
            tileWidth = tileSize * settings.CellSize,
            tileHeight = tileSize * settings.CellSize,
            maxTiles = 1 << tileBits,
            maxPolys = 1 << polyBits,
        };

        var navMesh = new DtNavMesh();
        navMesh.Init(navParams, settings.VertsPerPoly);
        var storage = new DtTileCacheStorageParams(RcByteOrder.LITTLE_ENDIAN, Compatibility);
        IRcCompressor compressor = DtTileCacheCompressorFactory.Shared.Create(Compatibility ? 0 : 1);
        var cache = new DtTileCache(cacheParams, storage, navMesh, compressor, new NavMeshTileCacheProcess());

        var builder = new DtTileCacheLayerBuilder(DtTileCacheCompressorFactory.Shared);
        List<DtTileCacheLayerBuildResult> layers = builder.Build(geom, cfg, storage, threads: 1, tw, th);
        for (int i = 0; i < layers.Count; i++)
        {
            List<byte[]> tileLayers = layers[i].layers;
            if (tileLayers == null) continue;
            for (int j = 0; j < tileLayers.Count; j++)
            {
                long tileRef = cache.AddTile(tileLayers[j], 0);
                if (tileRef != 0)
                    cache.BuildNavMeshTile(tileRef);
            }
        }

        return cache;
    }

    public static byte[] Write(DtTileCache cache)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            new DtTileCacheWriter(DtTileCacheCompressorFactory.Shared)
                .Write(writer, cache, RcByteOrder.LITTLE_ENDIAN, Compatibility);
        return stream.ToArray();
    }

    public static DtTileCache Read(byte[] bytes, NavMeshBuildSettings settings)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return new DtTileCacheReader(DtTileCacheCompressorFactory.Shared)
            .Read(reader, settings.VertsPerPoly, new NavMeshTileCacheProcess());
    }

    public static void Pump(DtTileCache cache)
    {
        for (int i = 0; i < 64; i++)
        {
            if (cache.Update())
                return;
        }
    }

    private sealed class NavMeshTileCacheProcess : IDtTileCacheMeshProcess
    {
        public void Process(DtNavMeshCreateParams option)
        {
            if (option.polyFlags == null) return;
            for (int i = 0; i < option.polyCount; i++)
                option.polyFlags[i] = NavMeshAreas.FlagWalk;
        }
    }
}
