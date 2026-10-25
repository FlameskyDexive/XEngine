// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Per-camera clustered light list (P3.2): replaces the per-fragment dual-BVH walk in the
/// Standard forward/deferred lighting path with a single cluster lookup + compact light index
/// loop (URP Forward+ style).
///
/// <para>
/// The screen is divided into a fixed <see cref="TilesX"/> x <see cref="TilesY"/> grid in
/// normalized NDC space (resolution / SSAA independent) crossed with <see cref="ZSlices"/>
/// exponential view-depth slices. Per frame and per camera the CPU rasterizes every active
/// local light's bounding sphere into the grid conservatively (world AABB corners through the
/// camera view-projection; any corner behind the eye falls back to full-screen coverage) and
/// builds one compact index list per cluster.
/// </para>
///
/// <para>
/// GPU layout (single RGBA32F square power-of-two texture, texelFetch only):
/// <code>
///   texel c in [0, ClusterCount)      : header for cluster c
///       .x = floatBits(firstEntrySlot)   absolute entry slot (texel*4+channel)
///       .y = floatBits(entryCount)
///   entry slot s (s >= ClusterCount*4): texel s>>2, channel s&amp;3
///       floatBits(encoded)  encoded = slot+1 for the static BVH, -(slot+1) for the dynamic BVH
/// </code>
/// Cluster linear index = (slice * TilesY + tileY) * TilesX + tileX, matching the shader.
/// </para>
///
/// <para>
/// Correctness contract: the per-cluster list is a conservative superset of the BVH result for
/// every world position inside that cluster. Lights outside their range contribute exactly zero
/// radiance (the attenuation window is zero at d &gt;= range), so evaluating the superset yields
/// the same lighting as the BVH walk.
/// </para>
/// </summary>
public sealed class ClusterLightList : IDisposable
{
    public const int TilesX = ShaderConfig.ClusterTilesX;
    public const int TilesY = ShaderConfig.ClusterTilesY;
    public const int ZSlices = ShaderConfig.ClusterZSlices;
    public const int ClusterCount = TilesX * TilesY * ZSlices;

    /// <summary>Smallest texture edge. 128x128 RGBA32F = 256 KB; headers take ClusterCount texels.</summary>
    public const int InitialDimension = 128;

    /// <summary>Hard cap on the list texture edge (4096^2 texels is far beyond any realistic list).</summary>
    public const int MaxDimension = 4096;

    /// <summary>Smallest near plane used for the exponential slice mapping (guards ortho/zero near).</summary>
    public const float MinSliceNear = 0.05f;

    /// <summary>Points sampled on a light's silhouette circle when bounding its screen rect.</summary>
    private const int SilhouetteSamples = 12;

    /// <summary>1 / cos(pi / <see cref="SilhouetteSamples"/>): scales the sampled ring so the
    /// sampled polygon circumscribes the silhouette circle instead of inscribing it.</summary>
    private const float SilhouetteCircumscribe = 1.0353f;

    private Texture2D _texture;
    private float[] _staging = Array.Empty<float>();

    private readonly int[] _counts = new int[ClusterCount];
    private readonly int[] _offsets = new int[ClusterCount];

    private struct Coverage
    {
        public int Encoded;
        public int TxMin, TxMax, TyMin, TyMax, SzMin, SzMax;
    }

    private Coverage[] _coverage = new Coverage[64];
    private int _coverageCount;

    /// <summary>True when the last <see cref="Build"/> produced bindable cluster data.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Total light-index entries emitted by the last build (sum over all clusters).</summary>
    public int EntryCount { get; private set; }

    /// <summary>Clamped near plane the slice mapping used. Shader must use the same value.</summary>
    public float SliceNear { get; private set; }

    /// <summary>Precomputed ZSlices / log2(far / near); 0 collapses everything into slice 0.</summary>
    public float SlicesPerLog2Z { get; private set; }

    /// <summary>Camera forward vector the view-depth mapping used.</summary>
    public Float3 CameraForward { get; private set; }

    /// <summary>The cluster list texture, or null before the first build with lights.</summary>
    public Texture2D Texture => _texture;

    /// <summary>Width = height = power of two, 0 before first allocation.</summary>
    public int TextureSize { get; private set; }

    /// <summary>log2(<see cref="TextureSize"/>).</summary>
    public int TextureShift { get; private set; }

    /// <summary>
    /// Rebuild the cluster lists for one camera. Call after both BVHs are synced (their slot
    /// tables reflect this frame's lights). Cheap when the scene has no local lights.
    /// </summary>
    /// <param name="staticBvh">Static light tree (encoded as +slot+1).</param>
    /// <param name="dynamicBvh">Dynamic light tree (encoded as -(slot+1)).</param>
    /// <param name="viewProjection">projection * view, the same matrix the shaders receive.</param>
    /// <param name="cameraPosition">World-space camera position.</param>
    /// <param name="cameraForward">World-space camera forward (unit length).</param>
    /// <param name="nearClip">Raw camera near plane (may be zero/negative for ortho).</param>
    /// <param name="farClip">Raw camera far plane.</param>
    public void Build(
        LightBVH staticBvh,
        LightBVH dynamicBvh,
        in Float4x4 viewProjection,
        Float3 cameraPosition,
        Float3 cameraForward,
        float nearClip,
        float farClip)
    {
        BuildCore(staticBvh, dynamicBvh, in viewProjection, cameraPosition, cameraForward, nearClip, farClip, useGpu: true);
    }

    /// <summary>CPU-only build for unit tests: fills the staging buffer without touching the GPU.</summary>
    internal void BuildForTest(
        LightBVH staticBvh,
        LightBVH dynamicBvh,
        in Float4x4 viewProjection,
        Float3 cameraPosition,
        Float3 cameraForward,
        float nearClip,
        float farClip)
    {
        BuildCore(staticBvh, dynamicBvh, in viewProjection, cameraPosition, cameraForward, nearClip, farClip, useGpu: false);
    }

    private void BuildCore(
        LightBVH staticBvh,
        LightBVH dynamicBvh,
        in Float4x4 viewProjection,
        Float3 cameraPosition,
        Float3 cameraForward,
        float nearClip,
        float farClip,
        bool useGpu)
    {
        Enabled = false;
        EntryCount = 0;
        _coverageCount = 0;

        int activeLights = (staticBvh?.ActiveLightCount ?? 0) + (dynamicBvh?.ActiveLightCount ?? 0);
        if (activeLights == 0)
        {
            // No local lights: leave Enabled=false. The shader falls back to the BVH walk whose
            // roots are -1, i.e. a no-op. Saves the header upload for the common case.
            return;
        }

        float sliceNear = MathF.Max(nearClip, MinSliceNear);
        float sliceFar = MathF.Max(farClip, sliceNear * 1.001f);
        float logRange = MathF.Log2(sliceFar / sliceNear);
        float slicesPerLog2 = logRange > 1e-5f ? ZSlices / logRange : 0f;

        SliceNear = sliceNear;
        SlicesPerLog2Z = slicesPerLog2;
        CameraForward = cameraForward;

        Array.Clear(_counts, 0, ClusterCount);

        if (staticBvh != null)
            CollectCoverage(staticBvh, encodeSign: +1, in viewProjection, cameraPosition, cameraForward, nearClip, farClip, sliceNear, slicesPerLog2);
        if (dynamicBvh != null)
            CollectCoverage(dynamicBvh, encodeSign: -1, in viewProjection, cameraPosition, cameraForward, nearClip, farClip, sliceNear, slicesPerLog2);

        // Pass 1: per-cluster counts.
        int totalEntries = 0;
        for (int i = 0; i < _coverageCount; i++)
        {
            ref Coverage cov = ref _coverage[i];
            for (int sz = cov.SzMin; sz <= cov.SzMax; sz++)
            {
                int sliceBase = sz * TilesY;
                for (int ty = cov.TyMin; ty <= cov.TyMax; ty++)
                {
                    int rowBase = (sliceBase + ty) * TilesX;
                    for (int tx = cov.TxMin; tx <= cov.TxMax; tx++)
                        _counts[rowBase + tx]++;
                }
            }
            totalEntries += (cov.SzMax - cov.SzMin + 1) * (cov.TyMax - cov.TyMin + 1) * (cov.TxMax - cov.TxMin + 1);
        }

        // Prefix-sum offsets. Entry slots start right after the header texels.
        int entryBaseSlot = ClusterCount * 4;
        int running = entryBaseSlot;
        for (int c = 0; c < ClusterCount; c++)
        {
            _offsets[c] = running;
            running += _counts[c];
        }

        int texelsNeeded = ClusterCount + ((totalEntries + 3) >> 2);
        EnsureCapacity(texelsNeeded, useGpu);

        // Headers (offset, count).
        for (int c = 0; c < ClusterCount; c++)
        {
            int o = c * 4;
            _staging[o + 0] = IntAsFloat(_offsets[c]);
            _staging[o + 1] = IntAsFloat(_counts[c]);
        }

        // Pass 2: fill entries. Reuse _counts as per-cluster write cursors.
        Array.Copy(_offsets, _counts, ClusterCount);
        for (int i = 0; i < _coverageCount; i++)
        {
            ref Coverage cov = ref _coverage[i];
            float encoded = IntAsFloat(cov.Encoded);
            for (int sz = cov.SzMin; sz <= cov.SzMax; sz++)
            {
                int sliceBase = sz * TilesY;
                for (int ty = cov.TyMin; ty <= cov.TyMax; ty++)
                {
                    int rowBase = (sliceBase + ty) * TilesX;
                    for (int tx = cov.TxMin; tx <= cov.TxMax; tx++)
                    {
                        int cluster = rowBase + tx;
                        int slot = _counts[cluster]++;
                        // slot = texel * 4 + channel; staging is 4 floats per texel, so the
                        // linear float index is simply the slot itself.
                        _staging[slot] = encoded;
                    }
                }
            }
        }

        if (useGpu)
            UploadUsedRows(texelsNeeded);

        EntryCount = totalEntries;
        Enabled = true;
    }

    /// <summary>
    /// Bind the cluster uniforms + texture into <paramref name="cmd"/>. Written for both shader
    /// backends: GLSL reads the plain uniforms, the HLSL/Slang path repacks the same globals into
    /// the StandardLighting cbuffer (MaterialUniformPacking.PackStandardLighting).
    /// When the last build produced no data the enabled flag is 0 and the shader falls back to
    /// the BVH walk.
    /// </summary>
    public void BindUniforms(CommandBuffer cmd, Texture2D fallbackTexture)
    {
        bool on = Enabled && _texture != null;
        cmd.SetGlobalTexture("_ClusterLightList", on ? _texture : fallbackTexture);
        cmd.SetGlobalVector("_ClusterGridParams", new Float4(TilesX, TilesY, ZSlices, on ? 1f : 0f));
        cmd.SetGlobalVector("_ClusterZParams", new Float4(SliceNear, SlicesPerLog2Z, TextureSize, TextureShift));
        cmd.SetGlobalVector("_ClusterCameraForward", new Float4(CameraForward.X, CameraForward.Y, CameraForward.Z, 0f));
    }

    /// <summary>Test hook: header (first absolute entry slot, count) of one cluster.</summary>
    internal (int Offset, int Count) HeaderForTest(int cluster)
    {
        int o = cluster * 4;
        return (FloatAsInt(_staging[o + 0]), FloatAsInt(_staging[o + 1]));
    }

    /// <summary>Test hook: decoded entry at an absolute entry slot (+slot+1 static, -(slot+1) dynamic).</summary>
    internal int EntryForTest(int entrySlot) => FloatAsInt(_staging[entrySlot]);

    /// <summary>Mark the list unusable for the next bind (uploads enabled=0, keeps the texture).</summary>
    public void Disable()
    {
        Enabled = false;
        EntryCount = 0;
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
        _staging = Array.Empty<float>();
        TextureSize = 0;
        TextureShift = 0;
        Enabled = false;
        EntryCount = 0;
        _coverageCount = 0;
    }

    // ─────────────────────── coverage ───────────────────────

    private void CollectCoverage(
        LightBVH bvh,
        int encodeSign,
        in Float4x4 viewProjection,
        Float3 cameraPosition,
        Float3 cameraForward,
        float nearClip,
        float farClip,
        float sliceNear,
        float slicesPerLog2)
    {
        ReadOnlySpan<LightBVH.SlotInfo> slots = bvh.Slots;
        for (int s = 0; s < slots.Length; s++)
        {
            ref readonly LightBVH.SlotInfo slot = ref slots[s];
            if (!slot.Active)
                continue;

            float r = MathF.Max(slot.Range, 0.0001f);
            Float3 p = slot.Position;

            // Depth cull/range against the raw camera planes: rasterized fragments always lie in
            // [near, far] along the camera forward axis, so lights entirely outside cannot
            // affect any visible fragment.
            float viewZ = Float3.Dot(p - cameraPosition, cameraForward);
            if (viewZ + r < nearClip || viewZ - r > farClip)
                continue;

            int szMin, szMax;
            if (slicesPerLog2 <= 0f)
            {
                // Degenerate depth range: the shader always computes slice 0.
                szMin = 0;
                szMax = 0;
            }
            else
            {
                // Tiny relative pad absorbs CPU/GPU log2 rounding differences at slice borders.
                float zLo = (viewZ - r) * 0.9999f - 1e-4f;
                float zHi = (viewZ + r) * 1.0001f + 1e-4f;
                szMin = SliceOf(zLo, sliceNear, slicesPerLog2);
                szMax = SliceOf(zHi, sliceNear, slicesPerLog2);
            }

            // Conservative NDC rect from the sphere's true silhouette circle rather than the
            // world AABB corners. The AABB is a cube of half-extent r, whose projection is up to
            // sqrt(3) wider than the sphere per axis (~3x the screen area), which inflated every
            // cluster list and made the shader evaluate lights that could never reach the tile.
            //
            // Seen from the eye at distance d, a sphere of radius r has its silhouette on the
            // circle centered at d - r^2/d along the view ray with radius r*sqrt(1 - r^2/d^2).
            // Projecting a circumscribed N-gon of that circle bounds the projected silhouette
            // exactly (the ring radius is scaled by 1/cos(pi/N) so the polygon contains the
            // circle, not the other way round).
            bool fullRect = false;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            Float3 toCenter = p - cameraPosition;
            float d2 = Float3.Dot(toCenter, toCenter);
            if (d2 <= r * r * 1.0001f)
            {
                // Eye inside (or touching) the sphere: no silhouette, the light can reach any tile.
                fullRect = true;
            }
            else
            {
                float d = MathF.Sqrt(d2);
                Float3 dir = toCenter * (1f / d);
                Float3 ringCenter = cameraPosition + dir * (d - r * r / d);
                float ringRadius = r * MathF.Sqrt(MathF.Max(1f - r * r / d2, 0f)) * SilhouetteCircumscribe;

                // Orthonormal basis perpendicular to the view ray.
                Float3 helper = MathF.Abs(dir.Y) < 0.99f ? Float3.UnitY : Float3.UnitX;
                Float3 t1 = Float3.Normalize(Float3.Cross(helper, dir));
                Float3 t2 = Float3.Cross(dir, t1);

                for (int k = 0; k < SilhouetteSamples; k++)
                {
                    float angle = k * (2f * MathF.PI / SilhouetteSamples);
                    Float3 world = ringCenter + t1 * (ringRadius * MathF.Cos(angle)) + t2 * (ringRadius * MathF.Sin(angle));
                    Float4 clip = viewProjection * new Float4(world.X, world.Y, world.Z, 1f);
                    if (clip.W <= 1e-5f)
                    {
                        fullRect = true;
                        break;
                    }
                    float invW = 1f / clip.W;
                    float nx = clip.X * invW;
                    float ny = clip.Y * invW;
                    if (nx < minX) minX = nx;
                    if (nx > maxX) maxX = nx;
                    if (ny < minY) minY = ny;
                    if (ny > maxY) maxY = ny;
                }
            }

            int txMin, txMax, tyMin, tyMax;
            if (fullRect)
            {
                txMin = 0; txMax = TilesX - 1;
                tyMin = 0; tyMax = TilesY - 1;
            }
            else
            {
                const float pad = 1e-4f;
                minX -= pad; maxX += pad;
                minY -= pad; maxY += pad;
                if (maxX < -1f || minX > 1f || maxY < -1f || minY > 1f)
                    continue; // entirely off-screen
                txMin = TileOf(minX, TilesX);
                txMax = TileOf(maxX, TilesX);
                tyMin = TileOf(minY, TilesY);
                tyMax = TileOf(maxY, TilesY);
            }

            if (_coverageCount == _coverage.Length)
                Array.Resize(ref _coverage, _coverage.Length * 2);
            ref Coverage cov = ref _coverage[_coverageCount++];
            cov.Encoded = encodeSign > 0 ? s + 1 : -(s + 1);
            cov.TxMin = txMin; cov.TxMax = txMax;
            cov.TyMin = tyMin; cov.TyMax = tyMax;
            cov.SzMin = szMin; cov.SzMax = szMax;
        }
    }

    /// <summary>Exponential slice index; must mirror the shader's Cluster lookup exactly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SliceOf(float viewZ, float sliceNear, float slicesPerLog2)
    {
        float rel = MathF.Max(viewZ / sliceNear, 1f);
        int slice = (int)MathF.Floor(MathF.Log2(rel) * slicesPerLog2);
        if (slice < 0) slice = 0;
        if (slice > ZSlices - 1) slice = ZSlices - 1;
        return slice;
    }

    /// <summary>Tile index of an NDC coordinate; must mirror the shader (truncate + clamp).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TileOf(float ndc, int tiles)
    {
        int t = (int)MathF.Floor((ndc * 0.5f + 0.5f) * tiles);
        if (t < 0) t = 0;
        if (t > tiles - 1) t = tiles - 1;
        return t;
    }

    // ─────────────────────── texture ───────────────────────

    private void EnsureCapacity(int texelsNeeded, bool useGpu)
    {
        int dim = TextureSize;
        if (dim == 0) dim = InitialDimension;
        while ((long)dim * dim < texelsNeeded) dim *= 2;
        if (dim > MaxDimension)
            throw new InvalidOperationException($"ClusterLightList texture exceeded MaxDimension ({MaxDimension}). Reduce light count or grid size.");

        if (useGpu && (_texture == null || dim != TextureSize))
        {
            _texture?.Dispose();
            _texture = new Texture2D((uint)dim, (uint)dim, false, TextureImageFormat.Float4);
        }
        TextureSize = dim;
        TextureShift = Log2(dim);

        int floats = dim * dim * 4;
        if (_staging.Length < floats)
            Array.Resize(ref _staging, floats);
    }

    private unsafe void UploadUsedRows(int usedTexels)
    {
        int dim = TextureSize;
        int rows = (usedTexels + dim - 1) / dim;
        if (rows <= 0) return;
        if (rows > dim) rows = dim;
        fixed (float* ptr = _staging)
        {
            _texture.SetDataPtr(ptr, 0, 0, (uint)dim, (uint)rows);
        }
    }

    private static int Log2(int size)
    {
        int n = 0;
        while ((1 << n) < size) n++;
        return n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float IntAsFloat(int i) => *(float*)&i;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int FloatAsInt(float f) => *(int*)&f;
}
