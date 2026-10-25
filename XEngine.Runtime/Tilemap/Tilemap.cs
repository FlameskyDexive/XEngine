// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>
/// Sparse 32×32 cell chunks. Setting a cell only allocates/dirties that chunk.
/// </summary>
[AddComponentMenu("2D/Tilemap")]
[ComponentIcon("\uf00a")]
public sealed class Tilemap : MonoBehaviour
{
    public const int ChunkSize = 32;

    private readonly Dictionary<long, TilemapChunk> _chunks = [];

    public int ChunkCount => _chunks.Count;

    public TileBase? GetTile(int x, int y)
    {
        long key = ChunkKey(x, y);
        if (!_chunks.TryGetValue(key, out TilemapChunk? chunk))
            return null;
        return chunk.Cells[LocalIndex(x, y)];
    }

    public void SetTile(int x, int y, TileBase? tile)
    {
        long key = ChunkKey(x, y);
        int index = LocalIndex(x, y);

        if (tile == null)
        {
            if (!_chunks.TryGetValue(key, out TilemapChunk? chunk))
                return;

            if (chunk.Cells[index] == null)
                return;

            chunk.Cells[index] = null;
            chunk.Occupied--;
            chunk.Dirty = true;
            chunk.ColliderDirty = true;
            if (chunk.Occupied <= 0)
                _chunks.Remove(key);
            return;
        }

        if (!_chunks.TryGetValue(key, out TilemapChunk? existing))
        {
            existing = new TilemapChunk(FloorDiv(x, ChunkSize), FloorDiv(y, ChunkSize));
            _chunks[key] = existing;
        }

        if (existing.Cells[index] == null)
            existing.Occupied++;
        existing.Cells[index] = tile;
        existing.Dirty = true;
        existing.ColliderDirty = true;
    }

    internal bool TryGetChunk(int chunkX, int chunkY, out TilemapChunk chunk)
        => _chunks.TryGetValue(Pack(chunkX, chunkY), out chunk!);

    internal void CopyChunks(List<TilemapChunk> dest)
    {
        dest.Clear();
        foreach (KeyValuePair<long, TilemapChunk> pair in _chunks)
            dest.Add(pair.Value);
    }

    internal static long ChunkKey(int x, int y) => Pack(FloorDiv(x, ChunkSize), FloorDiv(y, ChunkSize));

    internal static int LocalIndex(int x, int y)
    {
        int lx = FloorMod(x, ChunkSize);
        int ly = FloorMod(y, ChunkSize);
        return ly * ChunkSize + lx;
    }

    internal static int FloorDiv(int value, int size)
    {
        int q = value / size;
        if (value < 0 && value % size != 0)
            q--;
        return q;
    }

    internal static int FloorMod(int value, int size)
    {
        int r = value % size;
        return r < 0 ? r + size : r;
    }

    internal static long Pack(int chunkX, int chunkY)
        => ((long)chunkX << 32) ^ (uint)chunkY;
}

internal sealed class TilemapChunk
{
    public readonly int ChunkX;
    public readonly int ChunkY;
    public readonly TileBase?[] Cells = new TileBase?[Tilemap.ChunkSize * Tilemap.ChunkSize];
    public int Occupied;
    public bool Dirty;
    public bool ColliderDirty;

    public TilemapChunk(int chunkX, int chunkY)
    {
        ChunkX = chunkX;
        ChunkY = chunkY;
        Dirty = true;
        ColliderDirty = true;
    }
}
