// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// CPU-bakes one mesh per occupied 32×32 <see cref="Tilemap"/> chunk. Editing a cell only
/// rebuilds that chunk's mesh; GPU upload is deferred to render collect.
/// </summary>
[AddComponentMenu("2D/Tilemap Renderer")]
[ComponentIcon("\uf00a")]
public sealed class TilemapRenderer : MonoBehaviour
{
    private const float SortBias = 0.0001f;

    public AssetRef<Material> Material;
    public int SortingLayer;
    public int SortingOrder;
    public RenderingLayerMask RenderingLayers = RenderingLayerMask.Everything;

    [System.NonSerialized] private readonly Dictionary<long, Mesh> _meshes = [];
    [System.NonSerialized] private List<TilemapChunk>? _chunkScratch;
    [System.NonSerialized] private List<long>? _removeScratch;
    [System.NonSerialized] private HashSet<long>? _liveScratch;
    [System.NonSerialized] private PropertyState? _props;

    public int ChunkMeshCount => _meshes.Count;

    public Mesh? GetChunkMesh(int chunkX, int chunkY)
        => _meshes.TryGetValue(Tilemap.Pack(chunkX, chunkY), out Mesh? mesh) ? mesh : null;

    public int GetChunkQuadCount(int chunkX, int chunkY)
    {
        Mesh? mesh = GetChunkMesh(chunkX, chunkY);
        return mesh == null ? 0 : mesh.VertexCount / 4;
    }

    public void RebuildDirtyChunks()
    {
        Tilemap? map = GetComponent<Tilemap>();
        if (map == null)
        {
            DisposeMeshes();
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

            if (!_meshes.TryGetValue(key, out Mesh? mesh))
            {
                mesh = new Mesh();
                _meshes[key] = mesh;
                BakeChunk(mesh, chunk, grid);
                chunk.Dirty = false;
                continue;
            }

            if (!chunk.Dirty)
                continue;

            BakeChunk(mesh, chunk, grid);
            chunk.Dirty = false;
        }

        _removeScratch ??= [];
        _removeScratch.Clear();
        foreach (KeyValuePair<long, Mesh> pair in _meshes)
        {
            if (!_liveScratch.Contains(pair.Key))
                _removeScratch.Add(pair.Key);
        }

        for (int i = 0; i < _removeScratch.Count; i++)
        {
            long key = _removeScratch[i];
            _meshes[key].Dispose();
            _meshes.Remove(key);
        }
    }

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        RebuildDirtyChunks();
        if (_meshes.Count == 0)
            return;

        Material mat = Material.Res ?? SpriteRenderer.ResolveDefaultMaterial();
        _props ??= new PropertyState();
        _props.Clear();

        Float4x4 world = Transform.LocalToWorldMatrix;
        if (SortingLayer != 0 || SortingOrder != 0)
            world *= Float4x4.CreateTranslation(new Float3(0f, 0f, (SortingLayer * 1000 + SortingOrder) * SortBias));

        foreach (KeyValuePair<long, Mesh> pair in _meshes)
        {
            Mesh mesh = pair.Value;
            if (mesh.VertexCount < 4)
                continue;
            mesh.Upload();
            renderables.Add(new MeshRenderable(mesh, mat, world, GameObject.LayerIndex, _props,
                renderingLayerMask: RenderingLayers.Mask));
        }
    }

    public override void OnDispose()
    {
        DisposeMeshes();
    }

    private void BakeChunk(Mesh mesh, TilemapChunk chunk, Grid? grid)
    {
        int occupied = chunk.Occupied;
        if (occupied <= 0)
        {
            mesh.Vertices = [];
            mesh.Indices = [];
            return;
        }

        var verts = new Float3[occupied * 4];
        var uvs = new Float2[occupied * 4];
        var colors = new Color32[occupied * 4];
        var indices = new uint[occupied * 6];
        int quad = 0;
        int size = Tilemap.ChunkSize;

        for (int ly = 0; ly < size; ly++)
        {
            for (int lx = 0; lx < size; lx++)
            {
                TileBase? cell = chunk.Cells[ly * size + lx];
                if (cell == null)
                    continue;
                if (quad >= occupied)
                    break;

                int cellX = chunk.ChunkX * size + lx;
                int cellY = chunk.ChunkY * size + ly;
                Float3 v0, v1, v2, v3;
                if (grid != null)
                {
                    v0 = grid.CellToLocal(cellX, cellY);
                    v1 = grid.CellToLocal(cellX + 1, cellY);
                    v2 = grid.CellToLocal(cellX + 1, cellY + 1);
                    v3 = grid.CellToLocal(cellX, cellY + 1);
                }
                else
                {
                    v0 = new Float3(cellX, cellY, 0f);
                    v1 = new Float3(cellX + 1, cellY, 0f);
                    v2 = new Float3(cellX + 1, cellY + 1, 0f);
                    v3 = new Float3(cellX, cellY + 1, 0f);
                }

                Color32 tint = Color.White;
                Float2 uv0 = new(0f, 0f);
                Float2 uv1 = new(1f, 0f);
                Float2 uv2 = new(1f, 1f);
                Float2 uv3 = new(0f, 1f);
                if (cell is Tile tile)
                {
                    tint = (Color32)tile.Color;
                    TrySpriteUVs(tile, ref uv0, ref uv1, ref uv2, ref uv3);
                }

                int vi = quad * 4;
                verts[vi] = v0;
                verts[vi + 1] = v1;
                verts[vi + 2] = v2;
                verts[vi + 3] = v3;
                uvs[vi] = uv0;
                uvs[vi + 1] = uv1;
                uvs[vi + 2] = uv2;
                uvs[vi + 3] = uv3;
                colors[vi] = tint;
                colors[vi + 1] = tint;
                colors[vi + 2] = tint;
                colors[vi + 3] = tint;

                int ii = quad * 6;
                uint b = (uint)vi;
                indices[ii] = b;
                indices[ii + 1] = b + 1;
                indices[ii + 2] = b + 2;
                indices[ii + 3] = b;
                indices[ii + 4] = b + 2;
                indices[ii + 5] = b + 3;
                quad++;
            }
        }

        mesh.Vertices = verts;
        mesh.UV = uvs;
        mesh.Colors32 = colors;
        mesh.Indices = indices;
        mesh.RecalculateBounds();
    }

    private static void TrySpriteUVs(Tile tile, ref Float2 uv0, ref Float2 uv1, ref Float2 uv2, ref Float2 uv3)
    {
        Sprite? sprite = tile.Sprite.Res;
        if (sprite == null)
            return;

        Texture2D? tex = sprite.Texture.Res;
        if (tex == null || tex.Width == 0 || tex.Height == 0)
            return;

        float tw = tex.Width;
        float th = tex.Height;
        float u0 = sprite.Rect.X / tw;
        float v0 = sprite.Rect.Y / th;
        float u1 = sprite.Rect.MaxX / tw;
        float v1 = sprite.Rect.MaxY / th;
        uv0 = new Float2(u0, v0);
        uv1 = new Float2(u1, v0);
        uv2 = new Float2(u1, v1);
        uv3 = new Float2(u0, v1);
    }

    private void DisposeMeshes()
    {
        foreach (KeyValuePair<long, Mesh> pair in _meshes)
            pair.Value.Dispose();
        _meshes.Clear();
    }
}
