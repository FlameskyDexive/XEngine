// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using DotRecast.Core;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Detour.TileCache;
using DotRecast.Recast.Geom;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime.Navigation;

/// <summary>
/// Baked navigation mesh asset. Echo text manifest plus a Detour mesh-set byte payload.
/// </summary>
[Preserve]
[CreateAssetMenu("AI/NavMesh Data", Extension = ".navmesh", Order = 80)]
public sealed class NavMeshData : ScriptableObject, ISerializable
{
    public NavMeshBuildSettings Settings = new();
    public int SettingsHash;
    public AABB Bounds;
    public int TileSize;
    public int TileCountX;
    public int TileCountZ;
    public int AgentTypeId;
    public List<NavMeshAreaInfo> Areas = NavMeshAreas.DefaultAreas();
    public List<NavMeshTileSummary> Tiles = [];
    public byte[] TileData = [];
    public byte[]? TileCacheData;

    [NonSerialized] private DtNavMesh? _runtime;
    [NonSerialized] private DtTileCache? _tileCache;

    public DtNavMesh RuntimeMesh => _runtime ??= LoadRuntime();
    public DtTileCache? RuntimeTileCache
    {
        get
        {
            if (_runtime == null)
                _ = RuntimeMesh;
            return _tileCache;
        }
    }

    public static NavMeshData FromRuntime(DtNavMesh navMesh, NavMeshBuildSettings settings, IRcInputGeomProvider geom)
    {
        var data = CreateInstance<NavMeshData>();
        data.Settings = settings;
        data.SettingsHash = settings.ComputeHash();
        data.AgentTypeId = settings.AgentTypeId;
        data.TileSize = settings.TileSize;
        data.Bounds = new AABB(NavMeshConvert.ToEngine(geom.GetMeshBoundsMin()), NavMeshConvert.ToEngine(geom.GetMeshBoundsMax()));
        data.Areas = NavMeshAreas.DefaultAreas();
        data.Tiles = Summarize(navMesh);
        data.ComputeTileGrid();
        data.TileData = WriteMeshSet(navMesh);
        data._runtime = navMesh;
        return data;
    }

    internal void AttachTileCache(DtTileCache cache, byte[] bytes)
    {
        _tileCache = cache;
        _runtime = cache.GetNavMesh();
        TileCacheData = bytes;
    }

    internal void RefreshFromRuntime()
    {
        if (_runtime == null) return;
        Tiles = Summarize(_runtime);
        ComputeTileGrid();
        TileData = WriteMeshSet(_runtime);
        if (_tileCache != null)
            TileCacheData = NavMeshTileCache.Write(_tileCache);
    }

    public void InvalidateRuntime()
    {
        _runtime = null;
        _tileCache = null;
    }

    public void Serialize(ref EchoObject value, SerializationContext ctx)
    {
        value.Add("Name", new EchoObject(Name ?? ""));
        value.Add("SettingsHash", new EchoObject(SettingsHash));
        value.Add("AgentTypeId", new EchoObject(AgentTypeId));
        value.Add("TileSize", new EchoObject(TileSize));
        value.Add("TileCountX", new EchoObject(TileCountX));
        value.Add("TileCountZ", new EchoObject(TileCountZ));
        value.Add("BoundsMin", Serializer.Serialize(Bounds.Min, ctx));
        value.Add("BoundsMax", Serializer.Serialize(Bounds.Max, ctx));
        value.Add("Settings", Serializer.Serialize(Settings, ctx));
        value.Add("Areas", Serializer.Serialize(Areas, ctx));
        value.Add("Tiles", Serializer.Serialize(Tiles, ctx));
        value.Add("TileData", new EchoObject(TileData ?? []));
        if (TileCacheData is { Length: > 0 })
            value.Add("TileCacheData", new EchoObject(TileCacheData));
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        Name = value.Get("Name")?.StringValue ?? Name;
        SettingsHash = value.Get("SettingsHash")?.IntValue ?? 0;
        AgentTypeId = value.Get("AgentTypeId")?.IntValue ?? 0;
        TileSize = value.Get("TileSize")?.IntValue ?? 0;
        TileCountX = value.Get("TileCountX")?.IntValue ?? 0;
        TileCountZ = value.Get("TileCountZ")?.IntValue ?? 0;
        Float3 min = Serializer.Deserialize<Float3>(value.Get("BoundsMin"), ctx);
        Float3 max = Serializer.Deserialize<Float3>(value.Get("BoundsMax"), ctx);
        Bounds = new AABB(min, max);
        Settings = Serializer.Deserialize<NavMeshBuildSettings>(value.Get("Settings"), ctx) ?? new NavMeshBuildSettings();
        Areas = Serializer.Deserialize<List<NavMeshAreaInfo>>(value.Get("Areas"), ctx) ?? NavMeshAreas.DefaultAreas();
        Tiles = Serializer.Deserialize<List<NavMeshTileSummary>>(value.Get("Tiles"), ctx) ?? [];
        TileData = ReadBytes(value.Get("TileData")) ?? [];
        TileCacheData = ReadBytes(value.Get("TileCacheData"));
        _runtime = null;
        _tileCache = null;
    }

    internal static byte[] WriteMeshSet(DtNavMesh navMesh)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            new DtMeshSetWriter().Write(writer, navMesh, RcByteOrder.LITTLE_ENDIAN, cCompatibility: false);
        return stream.ToArray();
    }

    private DtNavMesh LoadRuntime()
    {
        if (TileCacheData is { Length: > 0 })
        {
            _tileCache = NavMeshTileCache.Read(TileCacheData, Settings);
            return _tileCache.GetNavMesh();
        }

        if (TileData == null || TileData.Length == 0)
            throw new InvalidOperationException("NavMeshData has no tile payload.");
        using var stream = new MemoryStream(TileData, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return new DtMeshSetReader().Read(reader);
    }

    private void ComputeTileGrid()
    {
        int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
        for (int i = 0; i < Tiles.Count; i++)
        {
            NavMeshTileSummary t = Tiles[i];
            if (t.TileX < minX) minX = t.TileX;
            if (t.TileZ < minZ) minZ = t.TileZ;
            if (t.TileX > maxX) maxX = t.TileX;
            if (t.TileZ > maxZ) maxZ = t.TileZ;
        }

        if (Tiles.Count == 0)
        {
            TileCountX = 0;
            TileCountZ = 0;
            return;
        }

        TileCountX = maxX - minX + 1;
        TileCountZ = maxZ - minZ + 1;
    }

    private static List<NavMeshTileSummary> Summarize(DtNavMesh navMesh)
    {
        var list = new List<NavMeshTileSummary>();
        int max = navMesh.GetMaxTiles();
        for (int i = 0; i < max; i++)
        {
            DtMeshTile? tile = navMesh.GetTile(i);
            if (tile?.data?.header == null) continue;
            list.Add(new NavMeshTileSummary
            {
                TileX = tile.data.header.x,
                TileZ = tile.data.header.y,
                PolyCount = tile.data.header.polyCount,
                VertCount = tile.data.header.vertCount,
            });
        }

        return list;
    }

    private static byte[]? ReadBytes(EchoObject? tag)
    {
        if (tag == null) return null;
        if (tag.TagType == EchoType.ByteArray) return tag.ByteArrayValue;
        string? b64 = tag.StringValue;
        return string.IsNullOrEmpty(b64) ? null : Convert.FromBase64String(b64);
    }
}
