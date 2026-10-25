using System;
using System.Buffers;
using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Logic-only chunk map. No mesh, no GameObject. Safe for a future server tick.</summary>
public sealed class VoxelWorldGrid
{
    public int ViewDistance = 2;
    public int MinChunkY;
    public int MaxChunkY = 3;
    public ushort TerrainTypeIndex = 1;
    public int BuildsPerTick = 2;
    public VoxelTerrainGenerator? Generator;
    public VoxelRegionStore? Store;

    public Int3 FocusChunk { get; private set; }

    private readonly Dictionary<Int3, VoxelChunk> _loaded = [];
    private readonly HashSet<Int3> _wanted = [];
    private readonly Queue<Int3> _queue = [];
    private readonly HashSet<Int3> _queued = [];
    private readonly List<Int3> _unloadScratch = [];
    private readonly List<Int3> _enqueueScratch = [];

    public int LoadedCount => _loaded.Count;
    public int QueueCount => _queue.Count;
    public int WantedCount => _wanted.Count;

    public bool IsLoaded(Int3 coord) => _loaded.ContainsKey(coord);

    public bool TryGetChunk(Int3 coord, out VoxelChunk chunk) => _loaded.TryGetValue(coord, out chunk!);

    public bool TryGetVoxel(Int3 world, out Voxel voxel)
    {
        voxel = default;
        if (!TryGetLive(world, out _, out int index, out VoxelChunk chunk))
            return false;
        voxel = chunk.GetVoxel(index);
        return true;
    }

    public bool TrySetVoxel(Int3 world, Voxel voxel)
    {
        if (!TryGetLive(world, out Int3 coord, out int index, out VoxelChunk chunk))
            return false;
        chunk.SetVoxel(index, voxel);
        Int3 local = VoxelCoords.LocalFromIndex(index);
        if (local.X == 0 || local.Y == 0 || local.Z == 0
            || local.X == VoxelCoords.ChunkSize - 1 || local.Y == VoxelCoords.ChunkSize - 1 || local.Z == VoxelCoords.ChunkSize - 1)
            MarkNeighborsDirty(coord);
        return true;
    }

    public bool IsSolid(Int3 world) => TryGetVoxel(world, out Voxel voxel) && !voxel.IsEmpty;

    public void CollectLoaded(List<Int3> dest)
    {
        dest.Clear();
        foreach (KeyValuePair<Int3, VoxelChunk> pair in _loaded)
            dest.Add(pair.Key);
    }

    public void SetFocus(Float3 worldPosition)
    {
        Int3 next = VoxelCoords.ChunkCoord(worldPosition);
        if (next.Equals(FocusChunk) && _wanted.Count > 0)
            return;
        FocusChunk = next;
        RebuildWanted();
    }

    public int Tick(int maxBuilds)
    {
        if (maxBuilds < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBuilds));
        if (_wanted.Count == 0)
            RebuildWanted();

        UnloadUnwanted();
        EnqueueMissing();

        int built = 0;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (built < maxBuilds && _queue.Count > 0)
        {
            Int3 coord = _queue.Dequeue();
            _queued.Remove(coord);
            if (!_wanted.Contains(coord) || _loaded.ContainsKey(coord))
                continue;
            Build(coord);
            built++;
            // frame-budget: heavy generators (carve/structures) make 16 chunks cost
            // seconds per frame — keep the tick under ~8ms regardless of the cap
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 8)
                break;
        }

        return built;
    }

    public void UnloadAll()
    {
        _queue.Clear();
        _queued.Clear();
        _wanted.Clear();
        _unloadScratch.Clear();
        foreach (KeyValuePair<Int3, VoxelChunk> pair in _loaded)
            _unloadScratch.Add(pair.Key);
        for (int i = 0; i < _unloadScratch.Count; i++)
            Unload(_unloadScratch[i]);
        _unloadScratch.Clear();
    }

    public void FlushEdited()
    {
        if (Store == null)
            return;
        Store.MinChunkY = MinChunkY;
        Store.MaxChunkY = MaxChunkY;
        foreach (KeyValuePair<Int3, VoxelChunk> pair in _loaded)
        {
            if (!pair.Value.Edited)
                continue;
            Store.SaveSection(pair.Key, pair.Value.Blocks);
        }
    }

    private bool TryGetLive(Int3 world, out Int3 chunk, out int index, out VoxelChunk live)
    {
        VoxelCoords.WorldToLocal(world, out chunk, out index);
        return _loaded.TryGetValue(chunk, out live!);
    }

    private void RebuildWanted()
    {
        _wanted.Clear();
        int vd = Math.Max(0, ViewDistance);
        for (int dz = -vd; dz <= vd; dz++)
        {
            for (int dx = -vd; dx <= vd; dx++)
            {
                for (int cy = MinChunkY; cy <= MaxChunkY; cy++)
                    _wanted.Add(new Int3(FocusChunk.X + dx, cy, FocusChunk.Z + dz));
            }
        }
    }

    private void EnqueueMissing()
    {
        _enqueueScratch.Clear();
        foreach (Int3 coord in _wanted)
        {
            if (_loaded.ContainsKey(coord) || _queued.Contains(coord))
                continue;
            _enqueueScratch.Add(coord);
        }
        if (_enqueueScratch.Count == 0) return;
        _enqueueScratch.Sort(CompareFocusDistance);
        foreach (Int3 coord in _enqueueScratch)
        {
            _queue.Enqueue(coord);
            _queued.Add(coord);
        }
    }

    private int CompareFocusDistance(Int3 a, Int3 b)
    {
        int ax = a.X - FocusChunk.X, az = a.Z - FocusChunk.Z;
        int bx = b.X - FocusChunk.X, bz = b.Z - FocusChunk.Z;
        int da = (ax * ax + az * az) * 16 + Math.Abs(a.Y - FocusChunk.Y);
        int db = (bx * bx + bz * bz) * 16 + Math.Abs(b.Y - FocusChunk.Y);
        return da.CompareTo(db);
    }

    private void UnloadUnwanted()
    {
        _unloadScratch.Clear();
        foreach (KeyValuePair<Int3, VoxelChunk> pair in _loaded)
        {
            if (!_wanted.Contains(pair.Key))
                _unloadScratch.Add(pair.Key);
        }

        for (int i = 0; i < _unloadScratch.Count; i++)
            Unload(_unloadScratch[i]);
        _unloadScratch.Clear();
    }

    /// <summary>Built chunks change cross-chunk face culling for their siblings — mark the
    /// six neighbors dirty so the mesher re-evaluates shared borders (seam fix).</summary>
    private static readonly Int3[] NeighborDirections =
        [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];

    private void MarkNeighborsDirty(Int3 coord)
    {
        foreach (var dir in NeighborDirections)
        {
            Int3 n = coord + dir;
            if (_loaded.TryGetValue(n, out VoxelChunk? neighbor))
                neighbor.Dirty = true;
        }
    }

    private void Build(Int3 coord)
    {
        VoxelChunk chunk = VoxelChunkPool.Rent();
        chunk.Coord = coord;

        if (Store != null)
        {
            Store.MinChunkY = MinChunkY;
            Store.MaxChunkY = MaxChunkY;
            if (Store.TryLoadSection(coord, out VoxelPalette pal))
            {
                chunk.Blocks = pal;
                chunk.Edited = true;
                chunk.Dirty = true;
                _loaded[coord] = chunk;
                MarkNeighborsDirty(coord);
                return;
            }
        }

        Int3 min = VoxelCoords.ChunkMin(coord);
        Voxel[] scratch = ArrayPool<Voxel>.Shared.Rent(VoxelCoords.ChunkVoxelCount);
        try
        {
            var span = scratch.AsSpan(0, VoxelCoords.ChunkVoxelCount);
            if (Generator != null)
                Generator.PaintChunk(span, min, TerrainTypeIndex);
            else
                span.Fill(Voxel.Solid(TerrainTypeIndex));
            chunk.Blocks = VoxelPalette.CompactFrom(span);
            chunk.Edited = false;
            chunk.Dirty = true;
            MarkNeighborsDirty(coord);
        }
        finally
        {
            ArrayPool<Voxel>.Shared.Return(scratch);
        }
        _loaded[coord] = chunk;
    }

    private void Unload(Int3 coord)
    {
        if (!_loaded.Remove(coord, out VoxelChunk chunk))
            return;
        MarkNeighborsDirty(coord);
        if (chunk.Edited && Store != null)
        {
            Store.MinChunkY = MinChunkY;
            Store.MaxChunkY = MaxChunkY;
            Store.SaveSection(coord, chunk.Blocks);
        }
        VoxelChunkPool.Return(chunk);
    }
}
