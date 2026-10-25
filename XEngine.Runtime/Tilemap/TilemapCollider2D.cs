// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Builds one <see cref="BoxCollider2D"/> per occupied tile, grouped by 32×32 chunk, and feeds
/// them to a sibling <see cref="CompositeCollider2D"/>. Editing a cell only rebuilds that chunk's boxes.
/// </summary>
[AddComponentMenu("2D/Tilemap Collider 2D")]
[ComponentIcon("\uf1b2")]
public sealed class TilemapCollider2D : MonoBehaviour
{
    [System.NonSerialized] private readonly Dictionary<long, GameObject> _hosts = [];
    [System.NonSerialized] private List<TilemapChunk>? _chunkScratch;
    [System.NonSerialized] private List<long>? _removeScratch;
    [System.NonSerialized] private HashSet<long>? _liveScratch;
    [System.NonSerialized] private List<BoxCollider2D>? _boxScratch;

    public int ChunkColliderCount => _hosts.Count;

    public GameObject? GetChunkHost(int chunkX, int chunkY)
        => _hosts.TryGetValue(Tilemap.Pack(chunkX, chunkY), out GameObject? host) ? host : null;

    public int GetChunkBoxCount(int chunkX, int chunkY)
    {
        GameObject? host = GetChunkHost(chunkX, chunkY);
        if (host == null)
            return 0;
        _boxScratch ??= [];
        host.GetComponents(_boxScratch);
        return _boxScratch.Count;
    }

    public void RebuildDirtyChunks()
    {
        Tilemap? map = GetComponent<Tilemap>();
        if (map == null)
        {
            DisposeHosts();
            return;
        }

        Grid? grid = GetComponentInParent<Grid>(includeSelf: true);
        _chunkScratch ??= [];
        map.CopyChunks(_chunkScratch);

        _liveScratch ??= [];
        _liveScratch.Clear();

        for (int i = 0; i < _chunkScratch.Count; i++)
        {
            TilemapChunk chunk = _chunkScratch[i];
            long key = Tilemap.Pack(chunk.ChunkX, chunk.ChunkY);
            _liveScratch.Add(key);

            if (!_hosts.TryGetValue(key, out GameObject? host) || host.IsDisposed)
            {
                host = new GameObject("TileChunk");
                host.SetParent(GameObject, worldPositionStays: false);
                host.Transform.LocalPosition = Float3.Zero;
                _hosts[key] = host;
                BakeChunk(host, chunk, grid);
                chunk.ColliderDirty = false;
                continue;
            }

            if (!chunk.ColliderDirty)
                continue;

            BakeChunk(host, chunk, grid);
            chunk.ColliderDirty = false;
        }

        _removeScratch ??= [];
        _removeScratch.Clear();
        foreach (KeyValuePair<long, GameObject> pair in _hosts)
        {
            if (!_liveScratch.Contains(pair.Key))
                _removeScratch.Add(pair.Key);
        }

        for (int i = 0; i < _removeScratch.Count; i++)
        {
            long key = _removeScratch[i];
            _hosts[key].Dispose();
            _hosts.Remove(key);
        }

        GetComponent<CompositeCollider2D>()?.GenerateGeometry();
    }

    public override void OnDispose()
    {
        DisposeHosts();
    }

    private void BakeChunk(GameObject host, TilemapChunk chunk, Grid? grid)
    {
        _boxScratch ??= [];
        host.GetComponents(_boxScratch);
        int boxIndex = 0;
        int size = Tilemap.ChunkSize;

        for (int ly = 0; ly < size; ly++)
        {
            for (int lx = 0; lx < size; lx++)
            {
                if (chunk.Cells[ly * size + lx] == null)
                    continue;

                int cellX = chunk.ChunkX * size + lx;
                int cellY = chunk.ChunkY * size + ly;
                BoxCollider2D box = boxIndex < _boxScratch.Count
                    ? _boxScratch[boxIndex]
                    : host.AddComponent<BoxCollider2D>();
                ConfigureBox(box, cellX, cellY, grid);
                boxIndex++;
            }
        }

        for (int i = _boxScratch.Count - 1; i >= boxIndex; i--)
            host.RemoveComponent(_boxScratch[i]);
    }

    private static void ConfigureBox(BoxCollider2D box, int cellX, int cellY, Grid? grid)
    {
        Float3 origin;
        Float3 far;
        if (grid != null)
        {
            origin = grid.CellToLocal(cellX, cellY);
            far = grid.CellToLocal(cellX + 1, cellY + 1);
        }
        else
        {
            origin = new Float3(cellX, cellY, 0f);
            far = new Float3(cellX + 1, cellY + 1, 0f);
        }

        float w = Maths.Abs(far.X - origin.X);
        float h = Maths.Abs(far.Y - origin.Y);
        box.Size = new Float2(Maths.Max(w, 0.01f), Maths.Max(h, 0.01f));
        box.Offset = new Float2((origin.X + far.X) * 0.5f, (origin.Y + far.Y) * 0.5f);
        box.UsedByComposite = true;
    }

    private void DisposeHosts()
    {
        foreach (KeyValuePair<long, GameObject> pair in _hosts)
        {
            if (!pair.Value.IsDisposed)
                pair.Value.Dispose();
        }

        _hosts.Clear();
    }
}
