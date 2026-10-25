using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Presentation layer over <see cref="VoxelWorldGrid"/>: greedy mesh + scene objects.</summary>
public sealed class VoxelChunkStreamer
{
    public VoxelWorldGrid Grid { get; } = new();
    public VoxelAtlas? Atlas;
    public Material? Material;
    public GameObject? Parent;
    public bool EnableColliders = true;
    /// <summary>Physics mesh cooking dominates chunk build time (~10-30ms each) and cooked
    /// meshes tax runtime physics — only chunks within this radius of the focus chunk cook
    /// colliders; the rest render visual-only until the player approaches.</summary>
    public int ColliderChunkRadius = 2;

    public int ViewDistance
    {
        get => Grid.ViewDistance;
        set => Grid.ViewDistance = value;
    }

    public int MinChunkY
    {
        get => Grid.MinChunkY;
        set => Grid.MinChunkY = value;
    }

    public int MaxChunkY
    {
        get => Grid.MaxChunkY;
        set => Grid.MaxChunkY = value;
    }

    public ushort TerrainTypeIndex
    {
        get => Grid.TerrainTypeIndex;
        set => Grid.TerrainTypeIndex = value;
    }

    public int BuildsPerTick
    {
        get => Grid.BuildsPerTick;
        set => Grid.BuildsPerTick = value;
    }

    public VoxelTerrainGenerator? Generator
    {
        get => Grid.Generator;
        set => Grid.Generator = value;
    }

    public Int3 FocusChunk => Grid.FocusChunk;
    public int LoadedCount => Grid.LoadedCount;
    public int QueueCount => Grid.QueueCount;
    public int WantedCount => Grid.WantedCount;

    private readonly Dictionary<Int3, GameObject> _views = [];
    private readonly List<Int3> _loadedScratch = [];
    private readonly List<Int3> _viewScratch = [];
    // A successfully meshed empty/fully enclosed chunk needs no GameObject.
    private readonly HashSet<Int3> _meshed = [];
    public int MeshBuildCount { get; private set; }

    public static int CountWanted(int viewDistance, int minChunkY, int maxChunkY)
    {
        int span = (2 * viewDistance) + 1;
        return span * span * (maxChunkY - minChunkY + 1);
    }

    public bool IsLoaded(Int3 coord) => Grid.IsLoaded(coord);

    /// <summary>Switch the shared terrain material without rebuilding voxel meshes.</summary>
    public void SetMaterial(Material? material)
    {
        Material = material;
        foreach (KeyValuePair<Int3, GameObject> pair in _views)
        {
            if (pair.Value.GetComponent<MeshRenderer>() is MeshRenderer renderer && material != null)
                renderer.Material = new AssetRef<Material>(material);
        }
    }

    public bool TryGetVoxel(Int3 world, out Voxel voxel) => Grid.TryGetVoxel(world, out voxel);

    public bool TrySetVoxel(Int3 world, Voxel voxel)
    {
        if (!Grid.TrySetVoxel(world, voxel))
            return false;
        VoxelCoords.WorldToLocal(world, out Int3 coord, out _);
        RefreshView(coord);
        return true;
    }

    public bool IsSolid(Int3 world) => Grid.IsSolid(world);

    public void SetFocus(Float3 worldPosition) => Grid.SetFocus(worldPosition);

    public int Tick(int maxBuilds)
    {
        int built = Grid.Tick(maxBuilds);
        SyncViews(Math.Max(1, maxBuilds));
        UpdateColliderSet();
        CookPendingColliders(maxPerFrame: 1);
        return built;
    }

    /// <summary>Cook colliders for pending near chunks, at most one per tick (each cook is
    /// ~10-30ms — batching keeps frames smooth while the ring fills).</summary>
    private void CookPendingColliders(int maxPerFrame)
    {
        if (!EnableColliders || _colliderPending.Count == 0 || _colliderBuiltThisFrame >= maxPerFrame)
            return;

        // nearest-to-focus first: the player's own chunk must get its collider before
        // anything else, or they drop through the un-cooked ground on spawn
        Int3 focus = Grid.FocusChunk;
        var ordered = _colliderScratch;
        ordered.Clear();
        ordered.AddRange(_colliderPending);
        ordered.Sort((a, b) =>
        {
            int da = (a.X - focus.X) * (a.X - focus.X) + (a.Z - focus.Z) * (a.Z - focus.Z);
            int db = (b.X - focus.X) * (b.X - focus.X) + (b.Z - focus.Z) * (b.Z - focus.Z);
            return da.CompareTo(db);
        });

        int cooked = 0;
        foreach (Int3 coord in ordered)
        {
            if (cooked >= maxPerFrame)
                break;
            if (!_views.TryGetValue(coord, out var go) || go == null
                || go.GetComponent<MeshCollider>() != null
                || !Grid.IsLoaded(coord))
                continue;
            var collider = go.AddComponent<MeshCollider>();
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
                collider.Mesh = renderer.Mesh;
            _colliderPending.Remove(coord);
            cooked++;
        }
    }

    private readonly List<Int3> _colliderScratch = [];

    private Int3 _colliderFocus;
    private bool _colliderFocusValid;
    private readonly HashSet<Int3> _colliderPending = [];
    private int _colliderBuiltThisFrame;

    /// <summary>Add/remove chunk colliders as the focus chunk moves (near = collide).</summary>
    private void UpdateColliderSet()
    {
        if (!EnableColliders)
            return;
        Int3 focus = Grid.FocusChunk;
        _colliderBuiltThisFrame = 0;
        if (_colliderFocusValid && focus.Equals(_colliderFocus))
            return;
        _colliderFocus = focus;
        _colliderFocusValid = true;
        foreach (KeyValuePair<Int3, GameObject> pair in _views)
        {
            bool want = InColliderRange(pair.Key);
            var existing = pair.Value.GetComponent<MeshCollider>();
            if (want && existing == null)
            {
                _colliderPending.Add(pair.Key);
            }
            else if (!want && existing != null)
            {
                EngineObject.Destroy(existing);
                _colliderPending.Remove(pair.Key);
            }
        }
    }

    private bool InColliderRange(Int3 coord)
    {
        Int3 f = Grid.FocusChunk;
        int dx = coord.X - f.X;
        int dz = coord.Z - f.Z;
        int r = ColliderChunkRadius;
        return dx * dx + dz * dz <= r * r;
    }

    public void UnloadAll()
    {
        DestroyAllViews();
        _meshed.Clear();
        Grid.UnloadAll();
    }

    public void SyncViews() => SyncViews(int.MaxValue);

    private void SyncViews(int meshBudget)
    {
        _viewScratch.Clear();
        foreach (KeyValuePair<Int3, GameObject> pair in _views)
        {
            if (!Grid.IsLoaded(pair.Key))
                _viewScratch.Add(pair.Key);
        }

        for (int i = 0; i < _viewScratch.Count; i++)
            DestroyView(_viewScratch[i]);

        _viewScratch.Clear();
        foreach (Int3 coord in _meshed)
            if (!Grid.IsLoaded(coord))
                _viewScratch.Add(coord);
        for (int i = 0; i < _viewScratch.Count; i++)
            _meshed.Remove(_viewScratch[i]);

        if (Parent == null)
            return;

        Grid.CollectLoaded(_loadedScratch);
        int initialBuilds = MeshBuildCount;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < _loadedScratch.Count; i++)
        {
            RefreshView(_loadedScratch[i]);
            if (MeshBuildCount - initialBuilds >= meshBudget
                || meshBudget != int.MaxValue && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 6)
                break;
        }
    }

    private void RefreshView(Int3 coord)
    {
        if (Parent == null)
            return;
        if (!Grid.TryGetChunk(coord, out VoxelChunk chunk))
            return;
        if (!chunk.Dirty && _meshed.Contains(coord))
            return;

        // border sampler: neighbor-chunk voxels in chunk-local coords (world = coord*size + local);
        // unloaded neighbors read as air so the world edge keeps its wall of faces
        Int3 cmin = VoxelCoords.ChunkMin(coord);
        var grid = Grid;
        var border = new Func<int, int, int, Voxel>((lx, ly, lz) =>
        {
            var world = new Int3(cmin.X + lx, cmin.Y + ly, cmin.Z + lz);
            return Grid.TryGetVoxel(world, out var v) ? v : Voxel.Empty;
        });
        VoxelMeshBuild build = chunk.RebuildMesh(Atlas, borderSampler: border);
        _meshed.Add(coord);
        MeshBuildCount++;
        if (build.QuadCount == 0)
        {
            DestroyView(coord);
            return;
        }

        if (!_views.TryGetValue(coord, out GameObject? go) || go == null)
        {
            Int3 min = VoxelCoords.ChunkMin(coord);
            go = new GameObject($"VoxelChunk {coord.X},{coord.Y},{coord.Z}");
            go.SetParent(Parent, worldPositionStays: false);
            go.Transform.LocalPosition = new Float3(min.X, min.Y, min.Z);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.Mesh = new AssetRef<Mesh>(chunk.Mesh);
            if (Material != null)
                renderer.Material = new AssetRef<Material>(Material);
            if (EnableColliders && InColliderRange(coord))
                _colliderPending.Add(coord); // cooked one per tick by CookPendingColliders

            _views[coord] = go;
            return;
        }

        if (go.GetComponent<MeshRenderer>() is MeshRenderer existing)
            existing.Mesh = new AssetRef<Mesh>(chunk.Mesh);
        if (go.GetComponent<MeshCollider>() is MeshCollider existingCollider)
            existingCollider.Mesh = new AssetRef<Mesh>(chunk.Mesh);
        else if (EnableColliders && InColliderRange(coord) && go.GetComponent<MeshCollider>() == null)
        {
            _colliderPending.Add(coord);
        }
    }

    private void DestroyAllViews()
    {
        _viewScratch.Clear();
        foreach (KeyValuePair<Int3, GameObject> pair in _views)
            _viewScratch.Add(pair.Key);
        for (int i = 0; i < _viewScratch.Count; i++)
            DestroyView(_viewScratch[i]);
    }

    private void DestroyView(Int3 coord)
    {
        _colliderPending.Remove(coord);
        if (!_views.Remove(coord, out GameObject? go) || go == null)
            return;
        EngineObject.Destroy(go);
    }
}
