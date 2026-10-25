// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Terrain;

/// <summary>
/// GPU-instanced terrain component with quadtree LOD.
/// Renders terrain using a single mesh instanced many times.
/// Heightmap is sampled in the vertex shader for displacement.
/// References a TerrainData asset for height/splat/layer configuration.
/// </summary>
[FormerlySerializedAs("XEngine.Runtime.Terrain.TerrainComponent")]
[FormerlySerializedAs("TerrainComponent")]
[ExecuteAlways]
[AddComponentMenu("Terrain/Terrain")]
[ComponentIcon("\uf6fc")] // Mountain
public class TerrainComponent : MonoBehaviour
{
    #region Configuration

    /// <summary>The terrain data asset containing heightmap, splatmap, and layer configuration.</summary>
    public AssetRef<TerrainData> Data;

    /// <summary>Base material. Terrain clones this internally to set its own properties.</summary>
    public AssetRef<Material> Material;

    /// <summary>Maximum LOD subdivision levels for the quadtree.</summary>
    public int MaxLODLevel = 4;

    /// <summary>Resolution of the base mesh grid (vertices per side).</summary>
    public int MeshResolution = 32;

    /// <summary>Which rendering layers this terrain belongs to. Independent of <see cref="GameObject.LayerIndex"/>.</summary>
    public RenderingLayerMask RenderingLayers = RenderingLayerMask.Everything;

    /// <summary>
    /// LOD quality multiplier. Controls the distance at which the quadtree subdivides.
    /// 1.0 = default, higher = more detail at distance, lower = less detail.
    /// </summary>
    public float LODQuality = 1f;

    /// <summary>Grass material override. If null, uses built-in Grass material.</summary>
    public AssetRef<Material> GrassMaterial;

    /// <summary>Maximum render distance for grass.</summary>
    public float GrassDistance = 150f;

    /// <summary>
    /// Normalized start of the grass blade fade-out (0..1, fraction of <see cref="GrassDistance"/>).
    /// Blades inside this radius are full size; between it and GrassDistance they shrink to zero,
    /// hiding the hard pop that the per-patch cutoff used to show.
    /// </summary>
    public float GrassFadeStart = 0.6f;

    /// <summary>Global grass density multiplier.</summary>
    public float GrassDensityMultiplier = 1f;

    /// <summary>Maximum render distance for trees.</summary>
    public float TreeDistance = 500f;

    /// <summary>
    /// Trees closer than this keep their mesh. Between this and <see cref="TreeDistance"/> they
    /// draw as a camera-facing billboard.
    /// </summary>
    public float TreeBillboardDistance = 80f;

    /// <summary>Tiles with the same id auto-connect when they share an XZ edge.</summary>
    public int GroupingId;

    /// <summary>When true, <see cref="ReconnectNeighbors"/> fills the four neighbor slots from grouping.</summary>
    public bool AllowAutoConnect = true;

    [NonSerialized] public TerrainComponent? LeftNeighbor;
    [NonSerialized] public TerrainComponent? RightNeighbor;
    [NonSerialized] public TerrainComponent? TopNeighbor;
    [NonSerialized] public TerrainComponent? BottomNeighbor;

    private static readonly List<TerrainComponent> s_enabled = [];

    #endregion

    #region State

    private TerrainQuadtree _quadtree;
    private Mesh _baseMesh;
    private Float4x4[] _transforms = Array.Empty<Float4x4>();
    private PropertyState _properties = new();

    // Material instances (cloned from assets each frame so edits propagate immediately)
    [NonSerialized] private Material? _materialInstance;
    [NonSerialized] private Material? _grassMaterialInstance;

    // Vegetation renderers
    [NonSerialized] private TerrainGrassRenderer? _grassRenderer;
    [NonSerialized] private TerrainTreeRenderer? _treeRenderer;

    // Cached default materials and textures (avoid LoadDefault every frame)
    [NonSerialized] private static Material? s_defaultTerrainMat;
    [NonSerialized] private static Material? s_defaultGrassMat;
    [NonSerialized] private static Texture2D? s_defaultWhite;
    [NonSerialized] private static Texture2D? s_defaultNormal;

    #endregion

    #region Brush Preview (set by editor each frame)

    [NonSerialized] public Float2 BrushPosition;
    [NonSerialized] public float BrushRadius;
    [NonSerialized] public float BrushFalloff;
    [NonSerialized] public bool BrushVisible;

    #endregion

    #region Public Accessors

    /// <summary>Invalidate cached grass patches (call after painting grass density).</summary>
    public void InvalidateGrassCache() => _grassRenderer?.InvalidateCache();

    /// <summary>Shortcut to terrain size from the data asset.</summary>
    public float TerrainSize => Data.Res?.Size ?? 1024f;

    /// <summary>Shortcut to terrain height from the data asset.</summary>
    public float TerrainHeight => Data.Res?.Height ?? 100f;

    #endregion

    #region Lifecycle

    public override void OnEnable()
    {
        base.OnEnable();
        if (_baseMesh == null)
            CreateBaseMesh();
        if (_quadtree == null)
            _quadtree = new TerrainQuadtree(Float3.Zero, TerrainSize, MaxLODLevel);

        _grassRenderer ??= new TerrainGrassRenderer();
        _grassRenderer.Initialize();
        _treeRenderer ??= new TerrainTreeRenderer();
        if (!s_enabled.Contains(this))
            s_enabled.Add(this);
        ReconnectNeighbors();
    }

    public override void OnDisable()
    {
        s_enabled.Remove(this);
        ClearNeighbors();
        ReconnectNeighbors();
        base.OnDisable();
        _baseMesh?.Dispose();
        _baseMesh = null;
        _materialInstance = null;
        _grassMaterialInstance = null;
        _grassRenderer?.Dispose();
        _grassRenderer = null;
        _treeRenderer?.Dispose();
        _treeRenderer = null;
    }

    /// <summary>Transform a point from terrain-local space to world space.</summary>
    public Float3 TerrainToWorld(Float3 localPoint) =>
        Float4x4.TransformPoint(localPoint, Transform.LocalToWorldMatrix);

    /// <summary>Transform a point from world space to terrain-local space.</summary>
    public Float3 WorldToTerrain(Float3 worldPoint) =>
        Float4x4.TransformPoint(worldPoint, Transform.WorldToLocalMatrix);

    public Float2 WorldToUv(Float3 worldPoint)
    {
        float size = MathF.Max(TerrainSize, 1e-3f);
        Float3 local = WorldToTerrain(worldPoint);
        return new Float2(local.X / size, local.Z / size);
    }

    public Float3 UvToWorld(Float2 uv) =>
        TerrainToWorld(new Float3(uv.X * TerrainSize, 0f, uv.Y * TerrainSize));

    public void GetWorldAabb(out Float3 min, out Float3 max)
        => TerrainNeighboring.WorldAabb(Transform.LocalToWorldMatrix, TerrainSize, out min, out max);

    /// <summary>Assign four neighbors and stop auto-connect from overwriting them.</summary>
    public void SetNeighbors(TerrainComponent? left, TerrainComponent? top, TerrainComponent? right, TerrainComponent? bottom)
    {
        AllowAutoConnect = false;
        ClearNeighbors();
        AssignNeighbor(TerrainEdge.Left, left);
        AssignNeighbor(TerrainEdge.Top, top);
        AssignNeighbor(TerrainEdge.Right, right);
        AssignNeighbor(TerrainEdge.Bottom, bottom);
        if (left != null) left.AssignNeighbor(TerrainEdge.Right, this);
        if (top != null) top.AssignNeighbor(TerrainEdge.Bottom, this);
        if (right != null) right.AssignNeighbor(TerrainEdge.Left, this);
        if (bottom != null) bottom.AssignNeighbor(TerrainEdge.Top, this);
    }

    public static IReadOnlyList<TerrainComponent> EnabledTerrains => s_enabled;

    /// <summary>Enabled terrains in loaded scenes (Unity <c>Terrain.activeTerrains</c>).</summary>
    public static IReadOnlyList<TerrainComponent> ActiveTerrains => s_enabled;

    /// <summary>First enabled terrain, or null.</summary>
    public static TerrainComponent? ActiveTerrain => s_enabled.Count > 0 ? s_enabled[0] : null;

    /// <summary>World-space surface Y at <paramref name="worldPosition"/> (XZ mapped onto this tile).</summary>
    public float SampleHeight(Float3 worldPosition)
    {
        var data = Data.Res;
        if (data == null)
            return worldPosition.Y;
        Float2 uv = WorldToUv(worldPosition);
        float localY = data.GetInterpolatedHeight(uv.X, uv.Y);
        return TerrainToWorld(new Float3(uv.X * TerrainSize, localY, uv.Y * TerrainSize)).Y;
    }

    /// <summary>World-space heightmap normal at a terrain UV (0-1).</summary>
    public Float3 GetInterpolatedNormal(float u, float v)
    {
        Float3 local = Data.Res?.GetInterpolatedNormal(u, v) ?? Float3.UnitY;
        Float4x4 l2w = Transform.LocalToWorldMatrix;
        Float3 world = Float4x4.TransformPoint(local, l2w) - Float4x4.TransformPoint(Float3.Zero, l2w);
        float len = Float3.Length(world);
        return len > 1e-8f ? world / len : Float3.UnitY;
    }

    /// <summary>World-space heightmap normal at a world XZ position.</summary>
    public Float3 GetInterpolatedNormal(Float3 worldPosition)
    {
        Float2 uv = WorldToUv(worldPosition);
        return GetInterpolatedNormal(uv.X, uv.Y);
    }

    public void AddTreeInstance(TreeInstance instance)
    {
        Data.Res?.AddTreeInstance(instance);
    }

    public void ReconnectNeighbors() => TerrainNeighboring.Reconnect(s_enabled);

    public void PushHeightSeams() => TerrainNeighboring.PushSeams(this);

    internal void ClearNeighbors()
    {
        Detach(ref LeftNeighbor, TerrainEdge.Right);
        Detach(ref RightNeighbor, TerrainEdge.Left);
        Detach(ref TopNeighbor, TerrainEdge.Bottom);
        Detach(ref BottomNeighbor, TerrainEdge.Top);
    }

    internal void AssignNeighbor(TerrainEdge edge, TerrainComponent? neighbor)
    {
        switch (edge)
        {
            case TerrainEdge.Left: LeftNeighbor = neighbor; break;
            case TerrainEdge.Right: RightNeighbor = neighbor; break;
            case TerrainEdge.Bottom: BottomNeighbor = neighbor; break;
            default: TopNeighbor = neighbor; break;
        }
    }

    private void Detach(ref TerrainComponent? slot, TerrainEdge oppositeOnNeighbor)
    {
        TerrainComponent? other = slot;
        slot = null;
        if (other == null || other.IsDisposed)
            return;
        if (other.Neighbor(oppositeOnNeighbor) == this)
            other.AssignNeighbor(oppositeOnNeighbor, null);
    }

    private TerrainComponent? Neighbor(TerrainEdge edge) => edge switch
    {
        TerrainEdge.Left => LeftNeighbor,
        TerrainEdge.Right => RightNeighbor,
        TerrainEdge.Bottom => BottomNeighbor,
        _ => TopNeighbor,
    };

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        var terrainData = Data.Res;
        if (terrainData == null) return;

        float terrainSize = terrainData.Size;
        Float4x4 terrainToWorld = Transform.LocalToWorldMatrix;
        Float4x4 worldToTerrain = Transform.WorldToLocalMatrix;

        // Camera position in terrain-local space for LOD
        Float3 camLocal = WorldToTerrain(camera.Transform.Position);
        camLocal.Y = 0;

        if (_quadtree == null
            || MathF.Abs(_quadtree.ChunkSize - terrainSize) > 0.01f
            || _quadtree.MaxLODLevel != MaxLODLevel)
        {
            _quadtree = new TerrainQuadtree(Float3.Zero, terrainSize, MaxLODLevel);
        }

        _quadtree.Update(camLocal, LODQuality);
        UpdateInstanceData(terrainToWorld);

        var mat = GetMaterialInstance();
        if (mat == null || _transforms.Length == 0 || _baseMesh == null) return;

        // Enable 8-layer keyword when more than 4 layers are active
        mat.SetKeyword("TERRAIN_8_LAYERS", terrainData.LayerCount > 4);
        mat.SetKeyword("TERRAIN_BICUBIC", terrainData.Interpolation == TerrainInterpolation.Bicubic);

        _properties.Clear();
        _properties.SetInt("_ObjectID", InstanceID);

        // GPU textures from TerrainData
        var heightmapTex = terrainData.GetHeightmapTexture();
        var splatmapTextures = terrainData.GetSplatmapTextures();

        var holesTex = terrainData.GetHolesTexture();

        if (heightmapTex != null) _properties.SetTexture("_Heightmap", heightmapTex);
        for (int si = 0; si < splatmapTextures.Count; si++)
            _properties.SetTexture($"_Splatmap{si}", splatmapTextures[si]);
        if (holesTex != null) _properties.SetTexture("_HolesMap", holesTex);
        _properties.SetInt("_HasHoles", holesTex != null ? 1 : 0);
        _properties.SetInt("_LayerCount", terrainData.LayerCount);

        // Per-layer textures and settings
        for (int i = 0; i < terrainData.LayerCount; i++)
        {
            TerrainLayer? layer = terrainData.GetLayer(i);
            string prefix = $"_Layer{i}";

            s_defaultWhite ??= Texture2D.LoadDefault(DefaultTexture.White);
            s_defaultNormal ??= Texture2D.LoadDefault(DefaultTexture.Normal);
            if (layer == null)
            {
                _properties.SetTexture(prefix, s_defaultWhite);
                _properties.SetTexture(prefix + "Normal", s_defaultNormal);
                _properties.SetFloat(prefix + "Tiling", 10f);
                _properties.SetFloat(prefix + "Roughness", 1f);
                _properties.SetFloat(prefix + "Metallic", 0f);
                continue;
            }

            layer.Albedo.EnsureLoaded();
            layer.NormalMap.EnsureLoaded();
            _properties.SetTexture(prefix, layer.Albedo.Res ?? s_defaultWhite);
            _properties.SetTexture(prefix + "Normal", layer.NormalMap.Res ?? s_defaultNormal);

            _properties.SetFloat(prefix + "Tiling", layer.Tiling);
            _properties.SetFloat(prefix + "Roughness", layer.Roughness);
            _properties.SetFloat(prefix + "Metallic", layer.Metallic);
        }

        _properties.SetFloat("_TerrainSize", terrainSize);
        _properties.SetFloat("_TerrainHeight", terrainData.Height);
        _properties.SetVector("_TerrainOffset", this.Transform.Position);
        _properties.SetMatrix("_TerrainWorldToLocal", worldToTerrain);
        _properties.SetMatrix("_TerrainLocalToWorld", terrainToWorld);

        // Brush preview
        mat.SetVector("_BrushPosition", BrushPosition);
        mat.SetFloat("_BrushRadius", BrushRadius);
        mat.SetFloat("_BrushFalloff", BrushFalloff);
        mat.SetFloat("_BrushVisible", BrushVisible ? 1f : 0f);

        // World-space bounds (transform terrain AABB corners to world)
        float height = terrainData.Height;
        Float3 localMin = new(0, -height * 2f, 0);
        Float3 localMax = new(terrainSize, height * 2f, terrainSize);
        var bounds = TransformAABB(localMin, localMax, terrainToWorld);

        InstancedMeshRenderable.CreateBatched(
            renderables, _baseMesh, mat, _transforms,
            (bounds.Min + bounds.Max) * 0.5f,
            layer: GameObject.LayerIndex,
            properties: _properties, bounds: bounds,
            renderingLayerMask: RenderingLayers.Mask);

        // Grass
        var grassMat = GetGrassMaterialInstance();
        if (grassMat != null && terrainData.DetailPrototypes.Count > 0)
        {
            // Pass terrain transform info to grass shader
            Float3 terrainUp = Float3.Normalize(Float4x4.TransformPoint(Float3.UnitY, terrainToWorld) - Float4x4.TransformPoint(Float3.Zero, terrainToWorld));
            grassMat.SetVector("_TerrainUp", terrainUp);
            grassMat.SetMatrix("_TerrainWorldToLocal", worldToTerrain);
            grassMat.SetMatrix("_TerrainLocalToWorld", terrainToWorld);
            grassMat.SetFloat("_TerrainSize", terrainSize);
            grassMat.SetFloat("_TerrainHeight", terrainData.Height);
            // Distance fade: blades scale from 1 at FadeStart*Distance down to 0 at Distance,
            // smoothing the per-patch cutoff in TerrainGrassRenderer.
            float fadeStartWorld = GrassDistance * Math.Clamp(GrassFadeStart, 0f, 0.99f);
            grassMat.SetFloat("_GrassDistance", GrassDistance);
            grassMat.SetFloat("_GrassFadeStart", fadeStartWorld);
            var htex = terrainData.GetHeightmapTexture();
            if (htex != null) grassMat.SetTexture("_Heightmap", htex);
            Float3 grassSample = TerrainToWorld(new Float3(terrainSize * 0.5f, 0f, terrainSize * 0.5f));
            WindZone.ApplyToGrassMaterial(grassMat, grassSample);

            _grassRenderer?.CollectRenderables(terrainData, this, camera, grassMat, GrassDistance, GrassDensityMultiplier, renderables);
        }

        // Trees
        _treeRenderer?.CollectRenderables(terrainData, this, camera, TreeDistance, renderables);
    }

    public override void DrawGizmos()
    {
        // _quadtree?.DrawGizmos(this.Transform.Position);
    }

    #endregion

    #region Material Instance

    private Material? GetMaterialInstance()
    {
        var sourceMat = Material.Res;
        if (sourceMat == null)
        {
            s_defaultTerrainMat ??= TerrainShaderLibrary.LoadTerrainMaterial();
            sourceMat = s_defaultTerrainMat;
        }
        if (sourceMat == null) return null;

        // Re-clone from source each frame so edits to the material asset
        // are reflected immediately without requiring reimport or restart.
        _materialInstance = sourceMat.Clone();
        return _materialInstance;
    }

    private Material? GetGrassMaterialInstance()
    {
        var sourceMat = GrassMaterial.Res;
        if (sourceMat == null)
        {
            s_defaultGrassMat ??= TerrainShaderLibrary.LoadGrassMaterial();
            sourceMat = s_defaultGrassMat;
        }
        if (sourceMat == null) return null;

        // Re-clone from source each frame so edits to the material asset
        // are reflected immediately.
        _grassMaterialInstance = sourceMat.Clone();
        return _grassMaterialInstance;
    }

    #endregion

    #region Raycast

    /// <summary>
    /// Raycast against the terrain heightmap surface.
    /// Returns true if the ray hits the terrain, with the world-space hit point and terrain UV.
    /// </summary>
    public bool Raycast(Ray ray, out Float3 hitPoint, out Float2 terrainUV)
    {
        hitPoint = Float3.Zero;
        terrainUV = Float2.Zero;

        var terrainData = Data.Res;
        if (terrainData == null || terrainData.Heights == null) return false;

        float size = terrainData.Size;
        Float4x4 worldToLocal = Transform.WorldToLocalMatrix;

        // Transform ray into terrain-local space
        Float3 localOrigin = Float4x4.TransformPoint(ray.Origin, worldToLocal);
        Float3 localDir = Float3.Normalize(Float4x4.TransformPoint(ray.Origin + ray.Direction, worldToLocal) - localOrigin);

        float stepSize = size / terrainData.HeightmapResolution * 0.5f;
        float maxDist = size * 2f;
        float t = 0f;

        float prevDiff = GetHeightDiffLocal(localOrigin, terrainData);
        bool prevAbove = prevDiff > 0;

        while (t < maxDist)
        {
            t += stepSize;
            Float3 localPoint = localOrigin + localDir * t;
            float diff = GetHeightDiffLocal(localPoint, terrainData);
            bool above = diff > 0;

            if (!above && prevAbove)
            {
                float tLo = t - stepSize;
                float tHi = t;
                for (int i = 0; i < 8; i++)
                {
                    float tMid = (tLo + tHi) * 0.5f;
                    Float3 mid = localOrigin + localDir * tMid;
                    if (GetHeightDiffLocal(mid, terrainData) > 0)
                        tLo = tMid;
                    else
                        tHi = tMid;
                }

                Float3 localHit = localOrigin + localDir * ((tLo + tHi) * 0.5f);
                terrainUV = new Float2((float)(localHit.X / size), (float)(localHit.Z / size));
                hitPoint = TerrainToWorld(localHit);
                return true;
            }

            prevDiff = diff;
            prevAbove = above;
        }

        return false;
    }

    private static float GetHeightDiffLocal(Float3 localPoint, TerrainData data)
    {
        float u = (float)(localPoint.X / data.Size);
        float v = (float)(localPoint.Z / data.Size);
        if (u < 0 || u > 1 || v < 0 || v > 1) return 1f;
        float terrainY = data.GetInterpolatedHeight(u, v);
        return (float)localPoint.Y - terrainY;
    }

    #endregion

    #region Mesh Creation

    private void CreateBaseMesh()
    {
        int resolution = MeshResolution;
        int vertexCount = (resolution + 1) * (resolution + 1);
        int indexCount = resolution * resolution * 6;

        Float3[] vertices = new Float3[vertexCount];
        Float2[] uvs = new Float2[vertexCount];
        uint[] indices = new uint[indexCount];

        for (int z = 0; z <= resolution; z++)
        {
            for (int x = 0; x <= resolution; x++)
            {
                int index = z * (resolution + 1) + x;
                float u = x / (float)resolution;
                float v = z / (float)resolution;
                vertices[index] = new Float3(u, 0, v);
                uvs[index] = new Float2(u, v);
            }
        }

        int triIndex = 0;
        for (int z = 0; z < resolution; z++)
        {
            for (int x = 0; x < resolution; x++)
            {
                int vertIndex = z * (resolution + 1) + x;
                indices[triIndex++] = (uint)(vertIndex);
                indices[triIndex++] = (uint)(vertIndex + resolution + 1);
                indices[triIndex++] = (uint)(vertIndex + 1);
                indices[triIndex++] = (uint)(vertIndex + 1);
                indices[triIndex++] = (uint)(vertIndex + resolution + 1);
                indices[triIndex++] = (uint)(vertIndex + resolution + 2);
            }
        }

        _baseMesh = new Mesh();
        _baseMesh.Vertices = vertices;
        _baseMesh.UV = uvs;
        _baseMesh.Indices = indices;
        _baseMesh.RecalculateBounds();
        _baseMesh.Upload();
    }

    #endregion

    #region Instance Data

    private void UpdateInstanceData(Float4x4 terrainToWorld)
    {
        var visibleChunks = _quadtree.GetVisibleChunks();

        if (_transforms.Length != visibleChunks.Count)
            _transforms = new Float4x4[visibleChunks.Count];

        for (int i = 0; i < visibleChunks.Count; i++)
        {
            var chunk = visibleChunks[i];
            // Chunk position is in terrain-local space; transform to world via terrain matrix
            Float4x4 localChunk = Float4x4.CreateTranslation(chunk.Position) * Float4x4.CreateScale((float)chunk.Size, 1.0f, (float)chunk.Size);
            _transforms[i] = terrainToWorld * localChunk;
        }
    }

    private static AABB TransformAABB(Float3 localMin, Float3 localMax, Float4x4 matrix)
    {
        // Transform all 8 corners and find new AABB
        Float3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            Float3 corner = new(
                (i & 1) == 0 ? localMin.X : localMax.X,
                (i & 2) == 0 ? localMin.Y : localMax.Y,
                (i & 4) == 0 ? localMin.Z : localMax.Z);
            Float3 world = Float4x4.TransformPoint(corner, matrix);
            min = new Float3(MathF.Min(min.X, world.X), MathF.Min(min.Y, world.Y), MathF.Min(min.Z, world.Z));
            max = new Float3(MathF.Max(max.X, world.X), MathF.Max(max.Y, world.Y), MathF.Max(max.Z, world.Z));
        }
        return new AABB(min, max);
    }

    #endregion
}
