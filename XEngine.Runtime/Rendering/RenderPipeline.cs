// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Collections.Generic;

using XEngine.Runtime.Jobs;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Rendering.VirtualGeometry;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>Editor scene-view draw mode: scene geometry rasterizes solid (default) or as wireframe.
/// Grid, gizmos, skybox and UI always draw solid.</summary>
public enum DebugViewMode
{
    Shaded = 0,
    Wireframe = 1,
    /// <summary>Every fragment layer adds one step of a heat ramp, so overdraw shows up as brightness.
    /// Drawn through the material override channel with additive blending and depth off.</summary>
    Overdraw = 2,

    /// <summary>
    /// Scene lighting off (Unity's scene-view light-bulb toggle): the frame's light list is dropped
    /// before culling, so geometry shades from ambient / GI only and albedo stays readable. No shader
    /// variant is involved — the lights simply are not there.
    /// </summary>
    Unlit = 3,
}

public struct RenderingData
{
    /// <summary>Whether to draw gizmos (editor scene view).</summary>
    public bool DisplayGizmos;

    /// <summary>Whether to draw the editor grid.</summary>
    public bool DisplayGrid;

    /// <summary>Whether the render is happening from the Scene View.</summary>
    public bool IsSceneView;

    /// <summary>Scene-view draw mode (see <see cref="DebugViewMode"/>).</summary>
    public DebugViewMode DebugView;

    /// <summary>
    /// ★ M7 — GameObjects to ring with the selection outline (editor scene view). Null or empty
    /// skips the outline passes entirely, so the player path costs nothing.
    /// </summary>
    public IReadOnlyList<GameObject>? SelectionOutlineTargets;

    /// <summary>Colour of that outline. Defaults to Unity's selection orange.</summary>
    public Color SelectionOutlineColor;

    public bool SkipUI;
    /// <summary>Overlay camera in a stack: reuse the Base color target (do not wipe it).</summary>
    public bool StackShareColor;
    /// <summary>More cameras in this stack will present; skip the backbuffer blit.</summary>
    public bool StackSkipFinalBlit;
}

/// <summary>
/// Interface for all renderable objects in the scene.
/// Supports both single-instance and GPU-instanced rendering through a unified API.
/// </summary>
public interface IRenderable
{
    public Material GetMaterial();
    public int GetLayer();

    /// <summary>Which rendering layers this renderer belongs to. Independent of <see cref="GetLayer"/>.</summary>
    public uint GetRenderingLayerMask() => RenderingLayerMask.Everything.Mask;

    /// <summary>
    /// ★ M7 — The scene object this renderable was submitted for, when there is one. Renderers hand the
    /// pipeline lightweight proxies (one per submesh) rather than themselves, so editor-side features
    /// that work per object — the selection outline — need the way back. Null for renderables with no
    /// scene owner (procedural draws, UI items).
    /// </summary>
    public GameObject? GetOwner() => null;

    /// <summary>
    /// Gets the world-space position of this renderable (typically the transform position).
    /// Used for depth sorting (e.g., back-to-front sorting for transparent objects).
    /// </summary>
    public Float3 GetPosition();

    /// <summary>
    /// Gets the submesh index to draw. -1 means draw the entire index buffer (no submeshes).
    /// </summary>
    public int GetSubMeshIndex() => -1;

    /// <summary>
    /// Gets the rendering data for this renderable.
    /// </summary>
    /// <param name="viewer">Camera viewing data for culling/LOD</param>
    /// <param name="properties">Shader properties (per-object or shared for instances)</param>
    /// <param name="mesh">Mesh to render</param>
    /// <param name="model">Model matrix (only used for single-instance rendering)</param>
    /// <param name="instanceData">Instance data array for GPU instancing, or null for single-instance rendering</param>
    public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh mesh, out Float4x4 model, out InstanceData[]? instanceData);

    /// <summary>
    /// World-to-object matrix (the inverse of the model matrix), bound as <c>xengine_WorldToObject</c>
    /// for normal transforms. The default inverts on demand; renderables whose transform is fixed
    /// for the frame should cache the result so it isn't re-inverted once per render pass.
    /// </summary>
    public Float4x4 GetWorldToObjectMatrix(in Float4x4 model) => model.Invert();

    public void GetCullingData(out bool isRenderable, out AABB bounds);

    /// <summary>True only when this renderable's shadow-casting geometry is immutable.</summary>
    public bool IsStaticForShadows => false;

    /// <summary>Monotonic pose/material version used to invalidate cached shadow tiles.</summary>
    public ulong ShadowCasterVersion => 0;

    /// <summary>
    /// When non-null, <see cref="RenderPipeline.DrawRenderables"/> issues
    /// <see cref="CommandBuffer.DrawIndexedIndirect"/> instead of a CPU-counted draw.
    /// Used by GPU particle systems that write instance counts from compute.
    /// </summary>
    public GraphicsBuffer? GetIndirectDrawArgs() => null;
}

public enum LightType
{
    Directional,
    Spot,
    Point,
    //Area
}

/// <summary>
/// Per-frame light parameters surfaced by every <see cref="IRenderableLight"/>. Consumed by
/// <see cref="SceneLightSystem"/>, packed into the BVH leaf textures, and (for the directional
/// light + closest-N shadow casters) uploaded as conventional uniforms.
/// </summary>
public struct ForwardLightData
{
    public LightType Type;
    public Float3 Position;
    public Float3 Direction;
    public Float3 Color;
    public float Intensity;
    public float Range;
    public float SpotAngle;       // degrees
    public float InnerSpotAngle;  // degrees

    // Shadow
    public bool ShadowEnabled;
    public float ShadowBias;
    public float ShadowNormalBias;
    public float ShadowStrength;
    public float ShadowQuality;   // 0 = Hard, 1 = Soft

    // Directional cascade data (only for LightType.Directional)
    public int CascadeCount;
    public Float4x4[] CascadeShadowMatrices; // [4]
    public Float4[] CascadeAtlasParams;      // [4]

    // Point shadow data (6 faces)
    public Float4x4[] PointShadowMatrices; // [6]
    public Float4[] PointShadowFaceParams; // [6]

    // Spot shadow data (1 matrix)
    public Float4x4 SpotShadowMatrix;
    public Float4 SpotShadowAtlasParams;

    /// <summary>Eight lighting bits. Default <see cref="RenderingLayerMask.Everything"/>.</summary>
    public uint RenderingLayerMask;
}

public interface IRenderableLight
{
    public int GetLightID();
    public int GetLayer();
    public LightType GetLightType();
    public Float3 GetLightPosition();
    public Float3 GetLightDirection();
    public bool DoCastShadows();

    /// <summary>Which rendering layers this light illuminates. Independent of <see cref="GetLayer"/>.</summary>
    public uint GetRenderingLayerMask() => RenderingLayerMask.Everything.Mask;

    /// <summary>
    /// Returns the light's data for forward rendering (position, color, shadow data, etc.)
    /// </summary>
    public ForwardLightData GetForwardLightData();
}

public abstract class RenderPipeline : EngineObject
{
    /// <summary>Diagnostic A/B switch for compatible Standard skinned instance draws.</summary>
    public static bool EnableSkinnedInstancing { get; set; } = true;
    private readonly SkinnedInstanceBatch _skinnedInstanceBatch = new();

    public override void OnDispose()
    {
        _skinnedInstanceBatch.Dispose();
        base.OnDispose();
    }
    private static Shader? s_blitShader;
    private static Material? s_blitMaterial;

    /// <summary>Default material used by <c>cmd.Blit</c> when no material is supplied.
    /// Lazy-loaded on first call.</summary>
    internal static Material GetBlitMaterial()
    {
        if (s_blitShader.IsNotValid())
            s_blitShader = Shader.LoadDefault(DefaultShader.Blit);
        if (s_blitMaterial.IsNotValid())
            s_blitMaterial = new Material(s_blitShader);
        return s_blitMaterial!;
    }

    internal static void DisposeSharedResources()
    {
        s_blitMaterial?.Dispose();
        s_blitMaterial = null;
        s_blitShader = null;
    }

    public struct CameraSnapshot(Camera camera, Plane[]? frustumPlanes = null)
    {
        public Scene Scene = camera.RenderScene;

        public Float3 CameraPosition = camera.Transform.Position;
        public Float3 CameraRight = camera.Transform.Right;
        public Float3 CameraUp = camera.Transform.Up;
        public Float3 CameraForward = camera.Transform.Forward;
        public LayerMask CullingMask = camera.CullingMask;
        public CameraClearFlags ClearFlags = camera.ClearFlags;
        public float NearClipPlane = camera.NearClipPlane;
        public float FarClipPlane = camera.FarClipPlane;
        public uint PixelWidth = camera.PixelWidth;
        public uint PixelHeight = camera.PixelHeight;
        public int ViewportX = camera.PixelRectX;
        public int ViewportY = camera.PixelRectY;
        public uint ViewportWidth = camera.PixelRectWidth;
        public uint ViewportHeight = camera.PixelRectHeight;
        public float Aspect = camera.Aspect;
        public Float4x4 View = camera.ViewMatrix;
        public Float4x4 ViewInverse = camera.ViewMatrix.Invert();
        public Float4x4 Projection = camera.ProjectionMatrix;
        public Float4x4 NonJitteredProjection = camera.NonJitteredProjectionMatrix;
        public bool ProjectionFlipsWinding;
        public Float4x4 PreviousViewProj = camera.PreviousViewProjectionMatrix;
        public bool HasPreviousViewProj = camera.HasPreviousViewProjectionMatrix;
        public Frustum WorldFrustum = frustumPlanes == null
            ? Frustum.FromMatrix(camera.ProjectionMatrix * camera.ViewMatrix)
            : CreateFrustum(camera.ProjectionMatrix * camera.ViewMatrix, frustumPlanes);

        internal static Frustum CreateFrustum(Float4x4 viewProjectionMatrix, Plane[] planes)
        {
            if (planes == null || planes.Length != 6)
                throw new ArgumentException("A reusable frustum requires exactly six planes.", nameof(planes));

            Float3 left = new(
                viewProjectionMatrix.c0.W + viewProjectionMatrix.c0.X,
                viewProjectionMatrix.c1.W + viewProjectionMatrix.c1.X,
                viewProjectionMatrix.c2.W + viewProjectionMatrix.c2.X);
            Float3 right = new(
                viewProjectionMatrix.c0.W - viewProjectionMatrix.c0.X,
                viewProjectionMatrix.c1.W - viewProjectionMatrix.c1.X,
                viewProjectionMatrix.c2.W - viewProjectionMatrix.c2.X);
            Float3 bottom = new(
                viewProjectionMatrix.c0.W + viewProjectionMatrix.c0.Y,
                viewProjectionMatrix.c1.W + viewProjectionMatrix.c1.Y,
                viewProjectionMatrix.c2.W + viewProjectionMatrix.c2.Y);
            Float3 top = new(
                viewProjectionMatrix.c0.W - viewProjectionMatrix.c0.Y,
                viewProjectionMatrix.c1.W - viewProjectionMatrix.c1.Y,
                viewProjectionMatrix.c2.W - viewProjectionMatrix.c2.Y);
            Float3 near = new(viewProjectionMatrix.c0.Z, viewProjectionMatrix.c1.Z, viewProjectionMatrix.c2.Z);
            Float3 far = new(
                viewProjectionMatrix.c0.W - viewProjectionMatrix.c0.Z,
                viewProjectionMatrix.c1.W - viewProjectionMatrix.c1.Z,
                viewProjectionMatrix.c2.W - viewProjectionMatrix.c2.Z);

            planes[0] = new Plane(near, -viewProjectionMatrix.c3.Z);
            planes[1] = new Plane(far, -(viewProjectionMatrix.c3.W - viewProjectionMatrix.c3.Z));
            planes[2] = new Plane(left, -(viewProjectionMatrix.c3.W + viewProjectionMatrix.c3.X));
            planes[3] = new Plane(right, -(viewProjectionMatrix.c3.W - viewProjectionMatrix.c3.X));
            planes[4] = new Plane(top, -(viewProjectionMatrix.c3.W - viewProjectionMatrix.c3.Y));
            planes[5] = new Plane(bottom, -(viewProjectionMatrix.c3.W + viewProjectionMatrix.c3.Y));

            return new Frustum { Planes = planes };
        }
    }

    internal HashSet<int> ActiveObjectIds { get => s_activeObjectIds; set => s_activeObjectIds = value; }

    private Dictionary<int, Float4x4> s_prevModelMatrices = [];
    private HashSet<int> s_activeObjectIds = [];
    private readonly List<int> _unusedModelMatrixKeys = new();
    private const int CLEANUP_INTERVAL_FRAMES = 120; // Clean up every 120 frames
    private int s_framesSinceLastCleanup = 0;

    private void CleanupUnusedModelMatrices()
    {
        // Increment frame counter
        s_framesSinceLastCleanup++;

        // Only perform cleanup at specified interval
        if (s_framesSinceLastCleanup < CLEANUP_INTERVAL_FRAMES)
            return;

        s_framesSinceLastCleanup = 0;

        // Remove all matrices that weren't used in this frame (no LINQ — PR0001).
        // Collect first because we can't mutate the dict while iterating its keys.
        List<int> unusedKeys = _unusedModelMatrixKeys;
        unusedKeys.Clear();
        foreach (int key in s_prevModelMatrices.Keys)
            if (!ActiveObjectIds.Contains(key))
                unusedKeys.Add(key);

        foreach (int key in unusedKeys)
            s_prevModelMatrices.Remove(key);

        // Clear the active IDs set for next frame
        ActiveObjectIds.Clear();
    }

    /// <summary>
    /// Tracks an object's model matrix for motion vector computation.
    /// Returns the previous frame's model matrix (or current if first frame).
    /// </summary>
    private Float4x4 TrackModelMatrix(int objectId, Float4x4 currentModel)
    {
        // Mark this object ID as active this frame
        ActiveObjectIds.Add(objectId);

        Float4x4 prevModel;
        if (!s_prevModelMatrices.TryGetValue(objectId, out prevModel))
            prevModel = currentModel; // First frame, use current matrix

        s_prevModelMatrices[objectId] = currentModel;
        return prevModel;
    }

    /// <summary>
    /// Collects renderables and lights from the scene for the given camera.
    /// Components receive camera info for LOD/culling decisions.
    /// </summary>
    internal static (List<IRenderable> renderables, List<IRenderableLight> lights) CollectRenderables(Scene scene, Camera camera)
    {
        var renderables = new List<IRenderable>();
        var lights = new List<IRenderableLight>();
        scene.CollectRenderables(camera, renderables, lights);
        return (renderables, lights);
    }

    protected static void CollectRenderables(
        Scene scene,
        Camera camera,
        List<IRenderable> renderables,
        List<IRenderableLight> lights)
    {
        renderables.Clear();
        lights.Clear();
        scene.CollectRenderables(camera, renderables, lights);
    }

    /// <summary>Render a scene through a camera without changing scene membership.
    /// Synchronous scope supports nesting and restores context even if rendering fails.</summary>
    internal void RenderScene(Camera camera, Scene scene, in RenderingData data)
    {
        ArgumentNullException.ThrowIfNull(scene);
        Scene? previous = camera.RenderSceneOverride;
        camera.RenderSceneOverride = scene;
        try { Render(camera, data); }
        finally { camera.RenderSceneOverride = previous; }
    }

    internal virtual void Render(Camera camera, in RenderingData data)
    {
        _skinnedInstanceBatch.ReleaseRetiredPalettes();
        // Clean up unused matrices after rendering
        CleanupUnusedModelMatrices();
    }

    private bool[] _scratchCulledRenderableIndices = System.Array.Empty<bool>();

    /// <summary>
    /// Returns a per-index "culled" mask (true == culled, skip this renderable) aligned to
    /// <paramref name="renderables"/>. A bool[] keyed by index is cheaper to allocate than a
    /// HashSet and turns the per-object membership test in every pass into an O(1) array read
    /// instead of a hash lookup.
    /// </summary>
    internal bool[] CullRenderables(IReadOnlyList<IRenderable> renderables, Frustum? worldFrustum, LayerMask cullingMask)
        => CullRenderables(renderables, worldFrustum, cullingMask, ref _scratchCulledRenderableIndices);

    protected bool[] CullRenderables(
        IReadOnlyList<IRenderable> renderables,
        Frustum? worldFrustum,
        LayerMask cullingMask,
        ref bool[] culledRenderableIndices)
    {
        EnsureWorldBounds(renderables);

        int renderableCount = renderables.Count;
        if (culledRenderableIndices.Length < renderableCount)
            culledRenderableIndices = new bool[renderableCount];
        else
            System.Array.Clear(culledRenderableIndices, 0, renderableCount);

        int culled = CullRange(culledRenderableIndices, renderableCount, worldFrustum, cullingMask);

        int collected = renderableCount;
        RenderStats.AddRenderables(collected, culled, collected - culled);

        return culledRenderableIndices;
    }

    /// <summary>
    /// Renderables per job chunk. Sized so a chunk is worth a few microseconds of plane tests:
    /// smaller chunks lose more to the interlocked claim than they win, and scenes below the
    /// threshold never leave the serial path at all.
    /// </summary>
    internal const int CullChunkSize = 256;

    /// <summary>Below this renderable count the parallel split costs more than it saves.</summary>
    internal const int ParallelCullThreshold = 512;

    // Parallel-cull scratch: the body reads these instead of capturing, so the delegate is
    // allocated once per pipeline instead of once per cull (up to 17 culls per camera per frame).
    private bool[]? _cullMask;
    private Frustum _cullFrustum;
    private bool _cullHasFrustum;
    private LayerMask _cullMaskLayers;
    private int[]? _cullChunkCounts;
    private Action<int, int>? _cullBody;

    /// <summary>
    /// Writes the culled mask for <c>[0, count)</c> and returns how many were culled. Splits
    /// across <see cref="JobSystem"/> workers once the scene is big enough to pay for it; each
    /// chunk writes only its own slice of the mask and its own counter slot, so the parallel and
    /// serial results are bit-identical by construction (the test suite pins that).
    /// </summary>
    private int CullRange(bool[] mask, int count, Frustum? worldFrustum, LayerMask cullingMask)
    {
        if (count < ParallelCullThreshold || !JobSystem.Enabled)
            return CullSerial(mask, 0, count, worldFrustum, cullingMask, _boundsRenderable, _worldBounds, _boundsLayer);

        int chunkCount = (count + CullChunkSize - 1) / CullChunkSize;
        if (_cullChunkCounts == null || _cullChunkCounts.Length < chunkCount)
            _cullChunkCounts = new int[chunkCount];
        else
            System.Array.Clear(_cullChunkCounts, 0, chunkCount);

        _cullMask = mask;
        _cullHasFrustum = worldFrustum != null;
        _cullFrustum = worldFrustum ?? default;
        _cullMaskLayers = cullingMask;
        _cullBody ??= CullChunk;

        JobSystem.ParallelFor(count, CullChunkSize, _cullBody).Complete();

        int culled = 0;
        for (int i = 0; i < chunkCount; i++)
            culled += _cullChunkCounts[i];
        return culled;
    }

    private void CullChunk(int start, int end)
    {
        Frustum? frustum = _cullHasFrustum ? _cullFrustum : null;
        _cullChunkCounts![start / CullChunkSize] = CullSerial(
            _cullMask!, start, end, frustum, _cullMaskLayers, _boundsRenderable, _worldBounds, _boundsLayer);
    }

    private static int CullSerial(
        bool[] mask,
        int start,
        int end,
        Frustum? worldFrustum,
        LayerMask cullingMask,
        bool[] boundsRenderable,
        AABB[] worldBounds,
        int[] layers)
    {
        int culled = 0;
        for (int i = start; i < end; i++)
        {
            bool frustumCull = worldFrustum != null
                && (!boundsRenderable[i] || !FrustumIntersectsAabb(worldFrustum.Value, worldBounds[i]));

            if (frustumCull || cullingMask.HasLayer(layers[i]) == false)
            {
                mask[i] = true;
                culled++;
            }
        }
        return culled;
    }

    internal static bool FrustumIntersectsAabb(in Frustum frustum, in AABB bounds)
    {
        Plane[] planes = frustum.Planes;
        for (int i = 0; i < 6; i++)
        {
            Plane plane = planes[i];
            Float3 point = bounds.Min;
            if (plane.Normal.X >= 0f) point.X = bounds.Max.X;
            if (plane.Normal.Y >= 0f) point.Y = bounds.Max.Y;
            if (plane.Normal.Z >= 0f) point.Z = bounds.Max.Z;
            if (Float3.Dot(plane.Normal, point) - plane.D < -0.00001f)
                return false;
        }
        return true;
    }

    internal bool CullRenderable(IRenderable renderable, Frustum cameraFrustum)
    {
        renderable.GetCullingData(out bool isRenderable, out AABB bounds);

        return !isRenderable || !cameraFrustum.Intersects(bounds);
    }

    internal enum SortMode
    {
        FrontToBack,
        BackToFront
    }

    /// <summary>
    /// Sorts renderables by distance from camera and returns a new sorted list.
    /// FrontToBack: Nearest objects first (optimal for opaque objects - early Z rejection)
    /// BackToFront: Farthest objects first (required for transparent objects - correct alpha blending)
    /// </summary>
    // Cached comparators so the per-comparison branch and the per-sort delegate allocation are
    // paid once at startup instead of every SortRenderables call.
    private static readonly Comparison<(IRenderable renderable, float distSq)> s_frontToBack =
        (a, b) => a.distSq.CompareTo(b.distSq);
    private static readonly Comparison<(IRenderable renderable, float distSq)> s_backToFront =
        (a, b) => b.distSq.CompareTo(a.distSq);

    // Reused across frames; only one sort is in flight at a time (sequential encode).
    private readonly List<(IRenderable renderable, float distSq)> _sortPairs = new();
    private readonly List<IRenderable> _sortResult = new();

    // DrawRenderables scratch, reused across passes (encode is sequential, never re-entrant).
    private readonly List<RenderBatch> _batches = new();
    private readonly Dictionary<(ulong, int, Mesh), int> _batchLookup = new();

    /// <summary>Virtual-geometry batches collected alongside <see cref="_batches"/>; drawn
    /// after the regular batches within the same pass (depth-tested, order-insensitive).</summary>
    private struct VgBatch
    {
        public Material Material;
        public int PassIndex;
        public int SortKey;
        public int RenderIndex;
    }

    private readonly List<VgBatch> _vgBatches = new();
    private readonly List<List<int>> _indexListPool = new();
    private int _indexListRented;

    // Per-frame world-space AABB cache shared by the main cull and every shadow cascade cull, so each
    // renderable's bounds are transformed once per frame instead of once per frustum (main + 4 cascades).
    private AABB[] _worldBounds = System.Array.Empty<AABB>();
    private bool[] _boundsRenderable = System.Array.Empty<bool>();
    // Layers are snapshotted alongside the bounds so the parallel cull body never calls back into
    // IRenderable (an interface dispatch into component state) from a worker thread.
    private int[] _boundsLayer = System.Array.Empty<int>();
    private IReadOnlyList<IRenderable> _boundsFrameList;
    private int _boundsCount;

    protected void InvalidateWorldBounds()
    {
        _boundsFrameList = null!;
        _boundsCount = 0;
    }

    /// <summary>
    /// Computes (or returns the cached) per-renderable world-space bounds for this frame's list. Keyed
    /// on list identity + count: the first cull of the frame builds it, later culls reuse it. Renderables
    /// don't move between collection and drawing, so caching for the frame is safe.
    /// </summary>
    internal void EnsureWorldBounds(IReadOnlyList<IRenderable> renderables)
    {
        int count = renderables.Count;
        if (ReferenceEquals(_boundsFrameList, renderables) && _boundsCount == count)
            return;

        if (_worldBounds.Length < count)
        {
            _worldBounds = new AABB[count];
            _boundsRenderable = new bool[count];
            _boundsLayer = new int[count];
        }
        for (int i = 0; i < count; i++)
        {
            IRenderable renderable = renderables[i];
            renderable.GetCullingData(out bool isRenderable, out AABB bounds);
            _boundsRenderable[i] = isRenderable;
            _worldBounds[i] = bounds;
            _boundsLayer[i] = renderable.GetLayer();
        }
        _boundsFrameList = renderables;
        _boundsCount = count;
    }

    // Rent a cleared per-batch index list from the pool, growing it only when a frame needs
    // more distinct batches than any previous frame.
    private List<int> RentIndexList()
    {
        List<int> list;
        if (_indexListRented < _indexListPool.Count)
        {
            list = _indexListPool[_indexListRented];
            list.Clear();
        }
        else
        {
            list = new List<int>();
            _indexListPool.Add(list);
        }
        _indexListRented++;
        return list;
    }

    /// <summary>Masks out culled-in renderables that are not part of the requested caster set
    /// (in place; the mask is per-cull scratch). Used by the layered shadow cache.</summary>
    internal static void FilterCulledByCasterSet(bool[] culledRenderableIndices,
        IReadOnlyList<IRenderable> renderables, ShadowCasterSet casterSet)
    {
        if (casterSet == ShadowCasterSet.All)
            return;
        bool wantStatic = casterSet == ShadowCasterSet.StaticOnly;
        int count = renderables.Count;
        for (int i = 0; i < count; i++)
        {
            if (culledRenderableIndices[i] && renderables[i].IsStaticForShadows != wantStatic)
                culledRenderableIndices[i] = false;
        }
    }

    internal List<IRenderable> SortRenderables(IReadOnlyList<IRenderable> renderables, bool[] culledRenderableIndices, Float3 cameraPosition, SortMode mode)
    {
        _sortResult.Clear();
        int count = renderables?.Count ?? 0;
        if (count == 0)
            return _sortResult;

        _sortPairs.Clear();

        // Collect only non-culled renderables
        for (int i = 0; i < count; i++)
        {
            if (culledRenderableIndices != null && culledRenderableIndices[i])
                continue;

            var renderable = renderables[i];
            // Depth-layer-aware sort key: in side-scrolling scenes every sprite spans the XY
            // plane and the camera looks down -Z, so the object's Z is its intended layer.
            // A plain 3D distance lets a wide background (origin far away in XY) interleave
            // with near foreground sprites as the camera pans — the classic cross-blending
            // artifact. Z dominates the key; in-plane distance only breaks ties.
            Float3 p = renderable.GetPosition();
            float distSq = Float3.DistanceSquared(p, cameraPosition);
            float key = p.Z * 1_000_000f + (mode == SortMode.BackToFront ? distSq : -distSq);
            _sortPairs.Add((renderable, key));
        }

        // Sort by the layered key (avoid sqrt)
        _sortPairs.Sort(mode == SortMode.BackToFront ? s_backToFront : s_frontToBack);

        for (int i = 0; i < _sortPairs.Count; i++)
            _sortResult.Add(_sortPairs[i].renderable);

        return _sortResult;
    }

    internal void SetupGlobalUniforms(CameraSnapshot css)
    {
        // Previous-frame VP for motion vectors (jitter-free). Fall back to the current
        // non-jittered VP on the first frame so motion reads zero instead of garbage.
        GlobalUniforms.SetPrevViewProj(css.HasPreviousViewProj
            ? css.PreviousViewProj
            : css.NonJitteredProjection * css.View);

        // Current-frame VP without TAA jitter, so the prepass can compute motion vectors
        // jitter-free while still rasterizing with the jittered projection.
        GlobalUniforms.SetMatrixVPNonJittered(css.NonJitteredProjection * css.View);

        // Setup Default Uniforms for this frame
        // Camera
        GlobalUniforms.SetWorldSpaceCameraPos(css.CameraPosition);
        GlobalUniforms.SetProjectionParams(new Float4(1.0f, css.NearClipPlane, css.FarClipPlane, 1.0f / css.FarClipPlane));
        GlobalUniforms.SetScreenParams(new Float4(css.PixelWidth, css.PixelHeight, 1.0f + 1.0f / css.PixelWidth, 1.0f + 1.0f / css.PixelHeight));

        // Time
        GlobalUniforms.SetTime(new Float4(Time.TimeSinceStartup * 0.5f, Time.TimeSinceStartup, Time.TimeSinceStartup * 2, Time.FrameCount));
        GlobalUniforms.SetSinTime(new Float4(Maths.Sin(Time.TimeSinceStartup / 8), Maths.Sin(Time.TimeSinceStartup / 4), Maths.Sin(Time.TimeSinceStartup / 2), Maths.Sin(Time.TimeSinceStartup)));
        GlobalUniforms.SetCosTime(new Float4(Maths.Cos(Time.TimeSinceStartup / 8), Maths.Cos(Time.TimeSinceStartup / 4), Maths.Cos(Time.TimeSinceStartup / 2), Maths.Cos(Time.TimeSinceStartup)));
        GlobalUniforms.SetDeltaTime(new Float4(Time.DeltaTime, 1.0f / Time.DeltaTime, Time.SmoothDeltaTime, 1.0f / Time.SmoothDeltaTime));

        // Upload the global uniform buffer
        GlobalUniforms.Upload();
    }

    internal void AssignCameraMatrices(Float4x4 view, Float4x4 projection)
    {
        Float4x4 viewProj = projection * view;

        GlobalUniforms.SetMatrixV(view);
        GlobalUniforms.SetMatrixIV(view.Invert());
        GlobalUniforms.SetMatrixP(projection);
        GlobalUniforms.SetMatrixVP(viewProj);

        // Precompute the inverse projection / view-projection so fragment shaders that
        // reconstruct view/world position from depth (GTAO, SSR, fog, shadow reprojection)
        // read them from the UBO instead of running a 4x4 inverse() per fragment.
        GlobalUniforms.SetMatrixIP(projection.Invert());
        GlobalUniforms.SetMatrixIVP(viewProj.Invert());

        // Upload the global uniform buffer
        GlobalUniforms.Upload();
    }

    // DrawMeshNow and the static Blit overloads were removed in the CommandBuffer
    // migration. Use cmd.DrawMesh / cmd.Blit on a rented CommandBuffer instead.

    /// <summary>
    /// Represents a render batch: a group of objects sharing the same material, mesh, and shader pass.
    /// Batching reduces GPU state changes by binding material uniforms once for all objects in the batch.
    /// </summary>
    private struct RenderBatch
    {
        public Material Material;      // Shared material for all objects in this batch
        public Mesh Mesh;              // Shared mesh for all objects in this batch
        public int PassIndex;          // Shader pass index
        public ulong MaterialHash;     // Hash of material uniforms (for sorting/grouping)
        public int SortKey;            // Sort order based on tag value + offset
        public List<int> RenderableIndices;  // Indices of objects in this batch
        public bool IsInstanced;       // True if this batch uses GPU instancing
        public bool IsIndirect;        // True if this batch uses DrawIndexedIndirect
        public int InstancedRenderableIndex;  // Index of the instanced/indirect renderable
    }

    internal static bool IsModelWindingFlipped(in Float4x4 model)
    {
        Float3 c0 = new(model.c0.X, model.c0.Y, model.c0.Z);
        Float3 c1 = new(model.c1.X, model.c1.Y, model.c1.Z);
        Float3 c2 = new(model.c2.X, model.c2.Y, model.c2.Z);
        return Float3.Dot(Float3.Cross(c0, c1), c2) < 0f;
    }

    internal static RasterizerState ResolveRasterStateForWinding(
        in RasterizerState state, bool projectionFlipsWinding, bool modelFlipsWinding)
    {
        if (state.CullFace is RasterizerState.PolyFace.None or RasterizerState.PolyFace.FrontAndBack ||
            projectionFlipsWinding == modelFlipsWinding)
            return state;

        RasterizerState resolved = state;
        resolved.Winding = state.Winding == RasterizerState.WindingOrder.CW
            ? RasterizerState.WindingOrder.CCW
            : RasterizerState.WindingOrder.CW;
        return resolved;
    }

    internal static RasterizerState ResolveRasterStateForModel(
        in RasterizerState state, bool projectionFlipsWinding, in Float4x4 model) =>
        ResolveRasterStateForWinding(state, projectionFlipsWinding, IsModelWindingFlipped(model));

    /// <summary>
    /// Renders all given objects with optimized batching. Objects are grouped by (material, mesh, pass)
    /// to minimize GPU state changes. This achieves:
    /// - Material uniforms bound once per batch (instead of per object)
    /// - Mesh data uploaded once per batch
    /// - Shader variant selected once per batch
    /// - Per-object uniforms still bound individually
    ///
    /// Performance: 100 objects with same material = 1 material bind (vs 100 without batching)
    /// </summary>
    /// <param name="cmd">CommandBuffer the batches encode into. The caller is responsible
    /// for binding the target framebuffer + viewport before calling this method, and
    /// for submitting the buffer after.</param>
    /// <param name="currentRT">Currently bound color render target, used for the
    /// GrabTexture handshake (read FB for the blit-into-grab-RT). Pass null if no
    /// pass in this batch will request a grab texture.</param>
    /// <param name="skipGBufferMaterials">When true, skip shaders that already wrote a GBuffer pass
    /// (Deferred hybrid: Lit deferred, Unlit/Terrain stay Forward).</param>
    internal void DrawRenderables(CommandBuffer cmd, IReadOnlyList<IRenderable> renderables, string shaderTag, string tagValue, ViewerData viewer, bool[] culledRenderableIndices, bool updatePreviousMatrices, RenderTexture? currentRT = null, bool skipGBufferMaterials = false, Material? overrideMaterial = null)
    {
        bool hasRenderOrder = !string.IsNullOrWhiteSpace(shaderTag);
        bool hasSortOffsets = false;

        // The depth-only shadow-caster pass never samples the inverse model matrix, so skip inverting
        // and encoding xengine_WorldToObject per object per cascade.
        bool needsWorldToObject = tagValue != "ShadowCaster";

        // ★ M7 — Material override channel. When set, every renderable draws with this one material
        // (Overdraw accumulation, flat/unlit debug views, a selection mask) instead of its own: the
        // per-object material lookup and the GBuffer skip are bypassed, and the batch key degenerates
        // to (override hash, pass, mesh) so batching still merges everything sharing a mesh.
        int overridePassIndex = -1;
        if (overrideMaterial != null)
        {
            if (overrideMaterial.Shader.IsNotValid()) return;   // shader still streaming in
            overridePassIndex = ResolveOverridePass(overrideMaterial, shaderTag, tagValue, hasRenderOrder);
            if (overridePassIndex < 0) return;                  // nothing drawable on the override
        }

        // ========== PHASE 1: Build Batches ==========
        // Group renderables by (material hash, shader pass, mesh). These two containers plus the
        // per-batch index lists are reused across calls (encode is sequential, never re-entrant)
        // so a multi-pass frame doesn't allocate a fresh dictionary + lists for every pass.
        List<RenderBatch> batches = _batches;
        Dictionary<(ulong, int, Mesh), int> batchLookup = _batchLookup;
        batches.Clear();
        batchLookup.Clear();
        _vgBatches.Clear();
        _indexListRented = 0;

        for (int renderIndex = 0; renderIndex < renderables.Count; renderIndex++)
        {
            // Skip culled objects
            if (culledRenderableIndices != null && culledRenderableIndices[renderIndex])
                continue;

            IRenderable renderable = renderables[renderIndex];

            Material material = overrideMaterial ?? renderable.GetMaterial();
            // Skip until the material AND its shader have streamed in (async loading). Accessing
            // material.Shader (=_shader.Res) queues the shader load; the object pops in once ready.
            if (material == null || material.Shader.IsNotValid()) continue;
            // The GBuffer split is a property of the object's own shader; an override draws everything.
            if (overrideMaterial == null && skipGBufferMaterials && material.Shader.HasGBufferPass)
                continue;

            // Virtual geometry renderables carry no Mesh — they draw through the dedicated
            // branch with per-view cluster selection. One batch per matching shader pass.
            if (renderable is VirtualGeometryRenderable vgRenderable)
            {
                int vgPassIndex = -1;
                foreach (ShaderPass pass in material.Shader.PassArray)
                {
                    vgPassIndex++;
                    if (pass.HasCompute && !pass.HasHlslGraphics)
                        continue;
                    if (overrideMaterial != null)
                    {
                        if (vgPassIndex != overridePassIndex) continue;
                    }
                    else if (hasRenderOrder && !pass.HasTag(shaderTag, tagValue))
                        continue;

                    int sortKey = overrideMaterial == null && hasRenderOrder ? vgPassIndex + pass.GetTagSortOffset(shaderTag) : vgPassIndex;
                    hasSortOffsets |= sortKey != vgPassIndex;
                    _vgBatches.Add(new VgBatch { Material = material, PassIndex = vgPassIndex, SortKey = sortKey, RenderIndex = renderIndex });
                }
                continue;
            }

            // Get rendering data to determine if this is instanced or single-instance rendering
            renderable.GetRenderingData(viewer, out PropertyState _, out Mesh mesh, out Float4x4 _, out InstanceData[]? instanceData);
            if (mesh == null || mesh.VertexCount <= 0) continue;

            GraphicsBuffer? indirectArgs = renderable.GetIndirectDrawArgs();
            if (indirectArgs != null)
            {
                ulong indirectMaterialHash = material.GetStateHash();
                int indirectPassIndex = -1;
                foreach (ShaderPass pass in material.Shader.PassArray)
                {
                    indirectPassIndex++;

                    if (pass.HasCompute && !pass.HasHlslGraphics)
                        continue;

                    if (overrideMaterial != null)
                    {
                        if (indirectPassIndex != overridePassIndex) continue;
                    }
                    else if (hasRenderOrder && !pass.HasTag(shaderTag, tagValue))
                        continue;

                    int sortKey = overrideMaterial == null && hasRenderOrder ? indirectPassIndex + pass.GetTagSortOffset(shaderTag) : indirectPassIndex;
                    hasSortOffsets |= sortKey != indirectPassIndex;

                    batches.Add(new RenderBatch
                    {
                        Material = material,
                        Mesh = mesh,
                        PassIndex = indirectPassIndex,
                        MaterialHash = indirectMaterialHash,
                        SortKey = sortKey,
                        IsIndirect = true,
                        InstancedRenderableIndex = renderIndex,
                        RenderableIndices = null
                    });
                }
                continue;
            }

            // Handle instanced renderables - add to batches with proper sorting (instanceData != null)
            if (instanceData != null && instanceData.Length > 0)
            {
                // Get material hash for batching
                ulong instancedMaterialHash = material.GetStateHash();

                // Find ALL shader passes matching the requested tag and add to batches
                int instancedPassIndex = -1;
                foreach (ShaderPass pass in material.Shader.PassArray)
                {
                    instancedPassIndex++;

                    if (overrideMaterial != null)
                    {
                        if (instancedPassIndex != overridePassIndex) continue;
                    }
                    else if (hasRenderOrder && !pass.HasTag(shaderTag, tagValue))
                        continue;

                    // Compute sort key for this pass (same as non-instanced)
                    int sortKey = overrideMaterial == null && hasRenderOrder ? instancedPassIndex + pass.GetTagSortOffset(shaderTag) : instancedPassIndex;
                    hasSortOffsets |= sortKey != instancedPassIndex;

                    // Create batch for instanced renderable
                    // Each instanced renderable gets its own batch since it draws all instances in one call
                    RenderBatch newBatch = new()
                    {
                        Material = material,
                        Mesh = mesh,
                        PassIndex = instancedPassIndex,
                        MaterialHash = instancedMaterialHash,
                        SortKey = sortKey,
                        IsInstanced = true,
                        InstancedRenderableIndex = renderIndex,
                        RenderableIndices = null  // Not used for instanced batches
                    };
                    batches.Add(newBatch);
                }
                continue;
            }

            // Get material hash for batching - materials with identical uniforms will batch together
            ulong materialHash = material.GetStateHash();

            // Find ALL shader passes matching the requested tag (e.g., "Opaque", "Transparent", "ShadowCaster")
            // Multi-pass rendering: materials can have multiple passes with the same tag (e.g., terrain with many texture layers)
            int passIndex = -1;
            foreach (ShaderPass pass in material.Shader.PassArray)
            {
                passIndex++;

                if (overrideMaterial != null)
                {
                    if (passIndex != overridePassIndex) continue;
                }
                else if (hasRenderOrder && !pass.HasTag(shaderTag, tagValue))
                    continue;


                // Found matching pass - add to appropriate batch
                // Batch key: (material hash, pass index, mesh) ensures each pass gets its own batch
                var batchKey = (materialHash, passIndex, mesh);
                if (batchLookup.TryGetValue(batchKey, out int batchIndex))
                {
                    // Batch already exists - add this object to it
                    batches[batchIndex].RenderableIndices.Add(renderIndex);
                }
                else
                {
                    // Compute sort key for this pass
                    int sortKey = overrideMaterial == null && hasRenderOrder ? passIndex + pass.GetTagSortOffset(shaderTag) : passIndex;
                    hasSortOffsets |= sortKey != passIndex;

                    // Create new batch for this unique material+pass+mesh combination
                    List<int> indices = RentIndexList();
                    indices.Add(renderIndex);
                    RenderBatch newBatch = new()
                    {
                        Material = material,
                        Mesh = mesh,
                        PassIndex = passIndex,
                        MaterialHash = materialHash,
                        SortKey = sortKey,
                        RenderableIndices = indices
                    };
                    batchLookup[batchKey] = batches.Count;
                    batches.Add(newBatch);
                }

                // Continue to next pass - materials can have multiple passes with the same tag
                // They will execute in order they appear in the shader file (Pass 0 → Pass 1 → Pass 2, etc.)
            }
        }

        // Sort batches by their sort key (respects tag offsets like "Transparent+1000").
        // Static lambda: an instance lambda here allocated a fresh Comparison<T> per call.
        if (hasSortOffsets)
        {
            batches.Sort(static (a, b) => a.SortKey.CompareTo(b.SortKey));
        }

        for (int i = 0; i < batches.Count; i++)
            RenderStats.AddBatch();

        // ========== PHASE 2: Draw Batches ==========
        // For each batch, bind state once then draw all objects in that batch
        foreach (RenderBatch batch in batches)
        {
            if (batch.IsIndirect)
            {
                IRenderable indirectRenderable = renderables[batch.InstancedRenderableIndex];
                DrawIndirectRenderablePass(cmd, indirectRenderable, batch.Material, batch.Mesh, batch.PassIndex, viewer, currentRT);
                continue;
            }

            // Handle instanced batches separately
            if (batch.IsInstanced)
            {
                IRenderable instancedRenderable = renderables[batch.InstancedRenderableIndex];
                DrawInstancedRenderablePass(cmd, instancedRenderable, batch.Material, batch.Mesh, batch.PassIndex,
                    viewer, currentRT, tagValue == "Transparent");
                continue;
            }

            Material material = batch.Material;
            Mesh mesh = batch.Mesh;
            int passIndex = batch.PassIndex;
            // Visibility has already been resolved for this camera or shadow cascade.
            // Never merge transparent, motion-vector or override/picking passes.
            if (EnableSkinnedInstancing && overrideMaterial == null &&
                (tagValue == "Opaque" || tagValue == "ShadowCaster") &&
                _skinnedInstanceBatch.TryPrepare(cmd, renderables, batch.RenderableIndices, material, mesh, viewer))
            {
                DrawInstancedRenderablePass(cmd, _skinnedInstanceBatch.Renderable, material, mesh, passIndex,
                    viewer, currentRT, tagValue == "Transparent");
                _skinnedInstanceBatch.ReleaseDrawReferences();
                if (updatePreviousMatrices)
                    foreach (int index in batch.RenderableIndices)
                    {
                        renderables[index].GetRenderingData(viewer, out var props, out _, out var model, out _);
                        int id = props.GetInt("_ObjectID");
                        if (id != 0) TrackModelMatrix(id, model);
                    }
                continue;
            }
            RenderTexture grabRT = null;

            // Configure shader keywords based on mesh attributes. Once per batch since
            // all objects in this batch share the same mesh.
            material.SetKeyword("HAS_NORMALS", mesh.HasNormals);
            material.SetKeyword("HAS_TANGENTS", mesh.HasTangents);
            material.SetKeyword("HAS_UV", mesh.HasUV);
            material.SetKeyword("HAS_UV2", mesh.HasUV2);
            material.SetKeyword("HAS_COLORS", mesh.HasColors || mesh.HasColors32);
            material.SetKeyword("HAS_BONEINDICES", mesh.HasBoneIndices);
            material.SetKeyword("HAS_BONEWEIGHTS", mesh.HasBoneWeights);
            material.SetKeyword("SKINNED", mesh.HasBoneIndices && mesh.HasBoneWeights);
            material.SetKeyword("BLENDSHAPES", mesh.HasBlendShapes);

            ShaderPass pass = material.Shader.GetPass(passIndex);
            if (!pass.TryGetMaterialVariant(material, out ShaderVariant? variantNullable) || variantNullable == null)
                continue;

            ShaderVariant variant = variantNullable;

            // Ensure GPU buffers exist before touching grab textures or encoding draws. Upload no longer
            // throws on invalid geometry (it leaves the VAO null), so skip the batch when that happens
            // rather than encoding a draw against a null VAO (which would fault on the render thread).
            mesh.Upload();
            if (mesh.VertexArrayObject == null)
                continue;

            grabRT = BeginGrabTexture(cmd, pass, currentRT);

            // Bind state for the batch. Globals UBO + material uniforms (with shader
            // defaults filled in) apply once; per-object only sets instance uniforms
            // and transforms.
            cmd.SetShader(variant);

            // GlobalUniforms UBO is bound automatically by the executor's PrepareDraw
            // for every draw no explicit cmd.SetBuffer needed here.

            cmd.SetMaterialProperties(material);

            // ========== PHASE 3: Draw Objects in Batch ==========
            bool hasRasterState = false;
            RasterizerState previousRasterState = default;
            foreach (int renderIndex in batch.RenderableIndices)
            {
                IRenderable renderable = renderables[renderIndex];

                renderable.GetRenderingData(viewer, out PropertyState properties, out Mesh _, out Float4x4 model, out InstanceData[]? _);

                RasterizerState rasterState = ResolveRasterStateForModel(
                    pass.State, viewer.ProjectionFlipsWinding, in model);
                if (!hasRasterState || previousRasterState.Winding != rasterState.Winding)
                {
                    cmd.SetRasterState(in rasterState);
                    previousRasterState = rasterState;
                    hasRasterState = true;
                }

                properties.SetInt("_RenderingLayerMask", unchecked((int)renderable.GetRenderingLayerMask()));

                int instanceId = properties.GetInt("_ObjectID");
                Float4x4 prevModel = model;
                if (updatePreviousMatrices && instanceId != 0)
                    prevModel = TrackModelMatrix(instanceId, model);

                cmd.SetInstanceProperties(properties);

                // Per-object transform uniforms. Encoded after SetInstanceProperties so
                // they apply last and can't be clobbered by an instance property of the
                // same name (matches today's order).
                cmd.SetMatrix("xengine_ObjectToWorld", in model);
                if (needsWorldToObject)
                {
                    Float4x4 inv = renderable.GetWorldToObjectMatrix(in model);
                    cmd.SetMatrix("xengine_WorldToObject", in inv);
                }
                if (updatePreviousMatrices)
                    cmd.SetMatrix("xengine_PrevObjectToWorld", in prevModel);

                if (FrameDebugger.IsCapturing)
                    FrameDebugger.RecordDrawAnnotation(cmd, mesh, material, passIndex, DescribeRenderableForDebug(renderable));

                int subIdx = renderable.GetSubMeshIndex();
                bool i32 = mesh.IndexFormat == IndexFormat.UInt32;
                if (subIdx >= 0 && subIdx < mesh.SubMeshCount)
                {
                    var sub = mesh.GetSubMesh(subIdx);
                    cmd.DrawIndexed(mesh.VertexArrayObject, sub.Topology, (uint)sub.IndexCount, (uint)sub.IndexStart, 0, i32);
                }
                else
                {
                    cmd.DrawIndexed(mesh.VertexArrayObject, mesh.MeshTopology, (uint)mesh.IndexCount, 0, 0, i32);
                }
            }

            EndGrabTexture(cmd, pass, grabRT);
        }

        // ========== PHASE 2b: Virtual Geometry Batches ==========
        // Cluster selection runs here (per pass, per view) so shadow cascades and color
        // passes each see their own LOD. Scratch updates and draws are encoded as
        // [update slice, draw] pairs, and the executor preserves that order.
        if (_vgBatches.Count > 0)
        {
            if (hasSortOffsets)
                _vgBatches.Sort(static (a, b) => a.SortKey.CompareTo(b.SortKey));

            for (int i = 0; i < _vgBatches.Count; i++)
            {
                VgBatch batch = _vgBatches[i];
                DrawVirtualGeometryBatch(cmd, renderables[batch.RenderIndex], batch.Material, batch.PassIndex,
                    viewer, needsWorldToObject, updatePreviousMatrices, tagValue);
            }
        }
    }

    // Every graphics draw path must bind captures before encoding its draw. Keeping
    // this in one helper prevents instanced/indirect batches bypassing the grab handshake.
    private static RenderTexture? BeginGrabTexture(CommandBuffer cmd, ShaderPass pass, RenderTexture? currentRT)
    {
        RenderTexture? grabRT = null;
        // GrabTexture: blit current framebuffer into a temporary RT and expose it
        // as a global texture for the shader to sample. Uses the CB's read/draw
        // FB split so the blit happens in the correct order relative to the draws.
        if (pass.HasGrabTexture && currentRT != null && currentRT.IsValid())
        {
            int fbWidth = currentRT.Width;
            int fbHeight = currentRT.Height;
            // A multisample source cannot be blitted from, and its depth cannot be resolved at
            // all, so an MSAA grab takes the color-only resolve path.
            bool multisampledSource = currentRT.IsMultisampled;
            bool wantDepth = pass.HasGrabDepth && !multisampledSource;
            // Preserve the camera format for both copies and MSAA resolves. Converting an
            // HDR grab to RGBA8 quantizes dark linear colors and clips values above one
            // before distortion/refraction can sample the scene.
            TextureImageFormat[] grabFormats = currentRT.ColorFormats.Length > 0
                ? [currentRT.ColorFormats[0]]
                : [TextureImageFormat.Color4b];
            grabRT = RenderTexture.GetTemporaryRT(fbWidth, fbHeight, wantDepth, grabFormats);

            if (multisampledSource)
            {
                cmd.ResolveTexture(currentRT, grabRT);
            }
            else
            {
                cmd.SetRenderTargets(grabRT.frameBuffer, currentRT.frameBuffer);
                cmd.BlitFramebuffer(0, 0, fbWidth, fbHeight, 0, 0, fbWidth, fbHeight, ClearFlags.Color, BlitFilter.Nearest);
                if (wantDepth)
                    cmd.BlitFramebuffer(0, 0, fbWidth, fbHeight, 0, 0, fbWidth, fbHeight, ClearFlags.Depth, BlitFilter.Nearest);
            }

            // Restore both targets back to the original RT.
            cmd.SetRenderTarget(currentRT.frameBuffer);

            cmd.GenerateMipmap(grabRT.MainTexture);
            // Filter is a sticky texture property; setting it directly is fine and
            // doesn't need to go through the CB (no ordering constraint vs draws).
            grabRT.MainTexture.SetTextureFilters(TextureMin.LinearMipmapLinear, TextureMag.Linear);

            // Encode the global set as a CB opcode so it's ordered against the
            // draws below at EXECUTE time. Writing PropertyState.SetGlobalTexture
            // directly here would set the static at encode time, but by the time
            // mainCmd actually runs we've already encoded the matching clear
            // below and the executor would see an empty global slot.
            cmd.SetGlobalTexture(pass.GrabTextureName, grabRT.MainTexture);
            if (wantDepth && grabRT.InternalDepth != null)
                cmd.SetGlobalTexture(pass.GrabDepthTextureName, grabRT.InternalDepth);
        }
        return grabRT;
    }

    private static void EndGrabTexture(CommandBuffer cmd, ShaderPass pass, RenderTexture? grabRT)
    {
        if (grabRT == null)
            return;
        cmd.ClearGlobalTexture(pass.GrabTextureName);
        if (pass.HasGrabDepth)
            cmd.ClearGlobalTexture(pass.GrabDepthTextureName);
        RenderTexture.ReleaseTemporaryRT(grabRT);
    }

    /// <summary>
    /// ★ M7 — Picks the single pass an override material draws with. Prefers the pass carrying the
    /// requested tag; when none does (an Unlit override asked for the Transparent queue, say) it
    /// falls back to the material's first graphics pass so the override still renders. Returns -1
    /// when the shader has no graphics pass at all.
    /// </summary>
    internal static int ResolveOverridePass(Material material, string shaderTag, string tagValue, bool hasRenderOrder)
    {
        int index = -1;
        int firstGraphics = -1;
        foreach (ShaderPass pass in material.Shader.PassArray)
        {
            index++;
            if (pass.HasCompute && !pass.HasHlslGraphics)
                continue;
            if (firstGraphics < 0)
                firstGraphics = index;
            if (!hasRenderOrder || pass.HasTag(shaderTag, tagValue))
                return index;
        }
        return firstGraphics;
    }

    /// <summary>
    /// Encodes one virtual-geometry submesh draw: per-view cluster selection (cached by the
    /// renderer until the view/pose/threshold changes), a partial scratch index-buffer
    /// update for the submesh's slice, then a single indexed draw over the packed vertex
    /// buffer. Existing materials and shaders render unchanged.
    /// </summary>
    private void DrawVirtualGeometryBatch(CommandBuffer cmd, IRenderable renderable, Material material, int passIndex,
        ViewerData viewer, bool needsWorldToObject, bool updatePreviousMatrices, string? tagValue)
    {
        var vg = (VirtualGeometryRenderable)renderable;
        VirtualGeometryRenderer owner = vg.Owner;
        var resources = owner.Resources;
        var vao = owner.ScratchVao;
        var scratch = owner.ScratchIndex;
        if (resources == null || vao == null || scratch == null) return;

        material.SetKeyword("HAS_NORMALS", resources.HasNormals);
        material.SetKeyword("HAS_TANGENTS", resources.HasTangents);
        material.SetKeyword("HAS_UV", resources.HasUV);
        material.SetKeyword("HAS_UV2", resources.HasUV2);
        material.SetKeyword("HAS_COLORS", resources.HasColors);
        material.SetKeyword("HAS_BONEINDICES", false);
        material.SetKeyword("HAS_BONEWEIGHTS", false);
        material.SetKeyword("SKINNED", false);
        material.SetKeyword("BLENDSHAPES", false);
        material.SetKeyword("GPU_INSTANCING", false);

        ShaderPass pass = material.Shader.GetPass(passIndex);
        if (!pass.TryGetMaterialVariant(material, out ShaderVariant? variantNullable) || variantNullable == null)
            return;

        float threshold = owner.ThresholdFor(tagValue);

        renderable.GetRenderingData(viewer, out PropertyState properties, out Mesh _, out Float4x4 model, out InstanceData[]? _);
        properties.SetInt("_RenderingLayerMask", unchecked((int)renderable.GetRenderingLayerMask()));

        // GPU path: three-pass compute cull writes the visible indices and the indirect
        // args on the GPU; the draw reads them without a CPU round-trip.
        bool gpuCull = owner.GpuCullingActive;
        if (gpuCull)
        {
            float scaleX = Float3.Length(new Float3(model[0, 0], model[1, 0], model[2, 0]));
            float scaleY = Float3.Length(new Float3(model[0, 1], model[1, 1], model[2, 1]));
            float scaleZ = Float3.Length(new Float3(model[0, 2], model[1, 2], model[2, 2]));
            VirtualGeometryGpuCulling.EncodeCull(cmd, resources, owner.GpuOutIndices!, owner.GpuCounters!,
                owner.GpuArgs!, owner.GpuParams!, model, Math.Max(scaleX, Math.Max(scaleY, scaleZ)), viewer, threshold);
        }
        else
        {
            // Selection before binding: staging must hold this view's clusters when the
            // UpdateBuffer below encodes.
            owner.EnsureSelection(viewer, threshold);
        }

        cmd.SetShader(variantNullable);
        RasterizerState rasterState = ResolveRasterStateForModel(
            pass.State, viewer.ProjectionFlipsWinding, in model);
        cmd.SetRasterState(in rasterState);
        cmd.SetMaterialProperties(material);

        int instanceId = properties.GetInt("_ObjectID");
        Float4x4 prevModel = model;
        if (updatePreviousMatrices && instanceId != 0)
            prevModel = TrackModelMatrix(instanceId, model);

        cmd.SetInstanceProperties(properties);
        cmd.SetMatrix("xengine_ObjectToWorld", in model);
        if (needsWorldToObject)
        {
            Float4x4 inv = renderable.GetWorldToObjectMatrix(in model);
            cmd.SetMatrix("xengine_WorldToObject", in inv);
        }
        if (updatePreviousMatrices)
            cmd.SetMatrix("xengine_PrevObjectToWorld", in prevModel);

        if (gpuCull)
        {
            if (FrameDebugger.IsCapturing)
                FrameDebugger.RecordDrawAnnotation(cmd, null, material, passIndex, DescribeRenderableForDebug(renderable));
            // Args: 5 uints per submesh; index count comes from the compute pass.
            cmd.DrawIndexedIndirect(owner.GpuVao!, Topology.Triangles, owner.GpuArgs!, (uint)(vg.SubMesh * 5 * sizeof(uint)), true);
            RenderStats.RecordVirtualGeometryDraw(0); // count is GPU-side; record the call only
            return;
        }

        var range = owner.Ranges[vg.SubMesh];
        if (range.Count <= 0) return;

        cmd.UpdateBuffer(scratch, owner.Staging.AsSpan(range.Start, range.Count), (uint)(range.Start * sizeof(uint)));
        if (FrameDebugger.IsCapturing)
            FrameDebugger.RecordDrawAnnotation(cmd, null, material, passIndex, DescribeRenderableForDebug(renderable));
        cmd.DrawIndexed(vao, Topology.Triangles, (uint)range.Count, (uint)range.Start, 0, true);
        RenderStats.RecordVirtualGeometryDraw((uint)range.Count);
    }

    /// <summary>
    /// Draws a specific pass of an instanced renderable with GPU instancing.
    /// Uses DrawIndexedInstanced to draw multiple instances in a single draw call.
    /// The mesh's cached VAO system is used for optimal performance.
    /// </summary>
    private void DrawInstancedRenderablePass(CommandBuffer cmd, IRenderable renderable, Material material,
        Mesh mesh, int passIndex, ViewerData viewer, RenderTexture? currentRT, bool preserveInstanceOrder)
    {
        renderable.GetRenderingData(viewer, out PropertyState sharedProperties, out Mesh _, out Float4x4 __, out InstanceData[]? instanceData);

        if (instanceData == null || instanceData.Length == 0)
            return;

        sharedProperties.SetInt("_RenderingLayerMask", unchecked((int)renderable.GetRenderingLayerMask()));

        // Ensure the shared instance VAO + buffer exist. The actual data upload is
        // encoded into the SAME CommandBuffer as the draw below this matters when
        // multiple instanced batches share the same mesh (e.g. all grass patches of
        // one prototype). Each batch's UpdateBuffer + DrawIndexedInstanced is a pair
        // in the stream, so the executor uploads each batch's data immediately before
        // its draw and they don't clobber each other.
        GraphicsVertexArray vao = mesh.EnsureInstanceVAO(instanceData.Length, out GraphicsBuffer instanceBuf);
        if (vao == null) return;

        int instanceCount = instanceData.Length;
        int indexCount = mesh.IndexCount;
        bool useIndex32 = mesh.IndexFormat == IndexFormat.UInt32;

        material.SetKeyword("HAS_NORMALS", mesh.HasNormals);
        material.SetKeyword("HAS_TANGENTS", mesh.HasTangents);
        material.SetKeyword("HAS_UV", mesh.HasUV);
        material.SetKeyword("HAS_UV2", mesh.HasUV2);
        material.SetKeyword("HAS_COLORS", mesh.HasColors || mesh.HasColors32);
        material.SetKeyword("HAS_BONEINDICES", mesh.HasBoneIndices);
        material.SetKeyword("HAS_BONEWEIGHTS", mesh.HasBoneWeights);
        material.SetKeyword("SKINNED", mesh.HasBoneIndices && mesh.HasBoneWeights);
        // ★ N7 — morph meshes draw instanced through the skinned batch: each instance's
        // CustomData carries (skinRow, morphRow, activeCount) and the shader fetches its
        // weight texels from the batch's per-instance morph palette row.
        material.SetKeyword("BLENDSHAPES", mesh.HasBlendShapes);
        material.SetKeyword("GPU_INSTANCING", true);

        Shaders.ShaderPass pass = material.Shader.GetPass(passIndex);
        if (!pass.TryGetMaterialVariant(material, out ShaderVariant? variantNullable) || variantNullable == null)
        {
            material.SetKeyword("GPU_INSTANCING", false);
            return;
        }

        ShaderVariant variant = variantNullable;

        RenderTexture? grabRT = BeginGrabTexture(cmd, pass, currentRT);
        cmd.SetShader(variant);

        // GlobalUniforms UBO bound by PrepareDraw automatically.

        cmd.SetMaterialProperties(material);

        if (sharedProperties != null)
            cmd.SetInstanceProperties(sharedProperties);

        int subIdx = renderable.GetSubMeshIndex();
        RasterizerState passState = pass.State;
        bool needsWindingSplit = passState.CullFace is not RasterizerState.PolyFace.None and not RasterizerState.PolyFace.FrontAndBack;
        bool firstModelFlipped = false;
        bool mixedModelWinding = false;
        if (needsWindingSplit)
        {
            firstModelFlipped = IsInstanceWindingFlipped(in instanceData[0]);
            for (int i = 1; i < instanceCount; i++)
            {
                if (IsInstanceWindingFlipped(in instanceData[i]) == firstModelFlipped)
                    continue;
                mixedModelWinding = true;
                break;
            }
        }

        if (!mixedModelWinding)
        {
            DrawInstancedSubset(cmd, vao, instanceBuf, mesh, material, subIdx, useIndex32, passState,
                viewer.ProjectionFlipsWinding, firstModelFlipped,
                new ReadOnlySpan<InstanceData>(instanceData, 0, instanceCount), renderable, passIndex);
        }
        else if (preserveInstanceOrder)
        {
            int runStart = 0;
            bool runModelFlipped = firstModelFlipped;
            for (int i = 1; i <= instanceCount; i++)
            {
                bool runEnded = i == instanceCount ||
                    IsInstanceWindingFlipped(in instanceData[i]) != runModelFlipped;
                if (!runEnded)
                    continue;

                DrawInstancedSubset(cmd, vao, instanceBuf, mesh, material, subIdx, useIndex32, passState,
                    viewer.ProjectionFlipsWinding, runModelFlipped,
                    instanceData.AsSpan(runStart, i - runStart), renderable, passIndex);
                if (i < instanceCount)
                {
                    runStart = i;
                    runModelFlipped = IsInstanceWindingFlipped(in instanceData[i]);
                }
            }
        }
        else
        {
            InstanceData[] scratch = ArrayPool<InstanceData>.Shared.Rent(instanceCount);
            try
            {
                for (int windingGroup = 0; windingGroup < 2; windingGroup++)
                {
                    bool modelFlipped = windingGroup != 0;
                    int count = 0;
                    for (int i = 0; i < instanceCount; i++)
                    {
                        if (IsInstanceWindingFlipped(in instanceData[i]) == modelFlipped)
                            scratch[count++] = instanceData[i];
                    }
                    if (count == 0)
                        continue;

                    DrawInstancedSubset(cmd, vao, instanceBuf, mesh, material, subIdx, useIndex32, passState,
                        viewer.ProjectionFlipsWinding, modelFlipped,
                        scratch.AsSpan(0, count), renderable, passIndex);
                }
            }
            finally
            {
                ArrayPool<InstanceData>.Shared.Return(scratch);
            }
        }

        EndGrabTexture(cmd, pass, grabRT);
        material.SetKeyword("GPU_INSTANCING", false);
    }

    private static bool IsInstanceWindingFlipped(in InstanceData instance)
    {
        Float4x4 model = instance.GetMatrix();
        return IsModelWindingFlipped(in model);
    }

    private static void DrawInstancedSubset(CommandBuffer cmd, GraphicsVertexArray vao,
        GraphicsBuffer instanceBuffer, Mesh mesh, Material material, int subMeshIndex, bool useIndex32,
        in RasterizerState passState, bool projectionFlipsWinding, bool modelFlipsWinding,
        ReadOnlySpan<InstanceData> instances, IRenderable renderable, int passIndex)
    {
        if (instances.IsEmpty)
            return;

        RasterizerState rasterState = ResolveRasterStateForWinding(
            passState, projectionFlipsWinding, modelFlipsWinding);
        cmd.SetRasterState(in rasterState);

        // The command buffer copies the span into its transient store while encoding, so pooled
        // winding-partition scratch can be reused immediately after this call.
        cmd.UpdateBuffer(instanceBuffer, instances);

        if (FrameDebugger.IsCapturing)
            FrameDebugger.RecordDrawAnnotation(cmd, mesh, material, passIndex,
                DescribeRenderableForDebug(renderable));

        if (subMeshIndex >= 0 && subMeshIndex < mesh.SubMeshCount)
        {
            var sub = mesh.GetSubMesh(subMeshIndex);
            cmd.DrawIndexedInstanced(vao, sub.Topology, (uint)sub.IndexCount, (uint)instances.Length,
                (uint)sub.IndexStart, 0, useIndex32);
        }
        else
        {
            cmd.DrawIndexedInstanced(vao, Topology.Triangles, (uint)mesh.IndexCount,
                (uint)instances.Length, 0, 0, useIndex32);
        }
    }

    /// <summary>
    /// Draws a GPU-driven mesh using <see cref="CommandBuffer.DrawIndexedIndirect"/>.
    /// Particle SoA / live-list buffers are bound from the renderable's property state.
    /// </summary>
    private void DrawIndirectRenderablePass(CommandBuffer cmd, IRenderable renderable, Material material, Mesh mesh, int passIndex, ViewerData viewer, RenderTexture? currentRT)
    {
        GraphicsBuffer? args = renderable.GetIndirectDrawArgs();
        if (args == null)
            return;

        renderable.GetRenderingData(viewer, out PropertyState properties, out Mesh _, out Float4x4 model, out InstanceData[]? _);

        properties.SetInt("_RenderingLayerMask", unchecked((int)renderable.GetRenderingLayerMask()));

        material.SetKeyword("HAS_NORMALS", mesh.HasNormals);
        material.SetKeyword("HAS_TANGENTS", mesh.HasTangents);
        material.SetKeyword("HAS_UV", mesh.HasUV);
        material.SetKeyword("HAS_UV2", mesh.HasUV2);
        material.SetKeyword("HAS_COLORS", mesh.HasColors || mesh.HasColors32);
        material.SetKeyword("HAS_BONEINDICES", mesh.HasBoneIndices);
        material.SetKeyword("HAS_BONEWEIGHTS", mesh.HasBoneWeights);
        material.SetKeyword("SKINNED", false);
        material.SetKeyword("BLENDSHAPES", false);
        material.SetKeyword("GPU_INSTANCING", false);

        Shaders.ShaderPass pass = material.Shader.GetPass(passIndex);
        if (!pass.TryGetMaterialVariant(material, out ShaderVariant? variantNullable) || variantNullable == null)
            return;

        mesh.Upload();
        if (mesh.VertexArrayObject == null)
            return;

        RenderTexture? grabRT = BeginGrabTexture(cmd, pass, currentRT);
        cmd.SetShader(variantNullable);
        RasterizerState rasterState = ResolveRasterStateForModel(
            pass.State, viewer.ProjectionFlipsWinding, in model);
        cmd.SetRasterState(in rasterState);
        cmd.SetMaterialProperties(material);
        cmd.SetInstanceProperties(properties);
        cmd.SetMatrix("xengine_ObjectToWorld", in model);
        Float4x4 inv = renderable.GetWorldToObjectMatrix(in model);
        cmd.SetMatrix("xengine_WorldToObject", in inv);

        foreach (KeyValuePair<string, GraphicsBuffer> entry in properties._buffers)
        {
            uint binding = 0;
            properties._bufferBindings.TryGetValue(entry.Key, out binding);
            cmd.SetBuffer(entry.Key, entry.Value, binding);
        }

        if (FrameDebugger.IsCapturing)
            FrameDebugger.RecordDrawAnnotation(cmd, mesh, material, passIndex, DescribeRenderableForDebug(renderable));

        bool index32 = mesh.IndexFormat == IndexFormat.UInt32;
        cmd.DrawIndexedIndirect(mesh.VertexArrayObject, mesh.MeshTopology, args, 0, index32);
        EndGrabTexture(cmd, pass, grabRT);
    }

    /// <summary>Owning GameObject name for Frame Debugger draw annotations (capture frame only).</summary>
    private static string? DescribeRenderableForDebug(IRenderable renderable) =>
        renderable is MonoBehaviour mb && mb.GameObject != null ? mb.GameObject.Name : null;
}
