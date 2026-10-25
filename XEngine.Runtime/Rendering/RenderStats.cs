// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Per-frame rendering counters captured by the pipeline and the low-level <see cref="Graphics"/>
/// draw calls. The pipeline calls <see cref="BeginFrame"/> before its first draw and
/// <see cref="EndFrame"/> after the last one; the editor reads the snapshot exposed in
/// <see cref="Last"/>. Counters are global because both <see cref="Graphics"/> and the active
/// pipeline write to the same accumulator; multi-camera renders are summed into one frame.
/// </summary>
public static class RenderStats
{
    public struct Frame
    {
        // ── Geometry (color pass) ────────────────────────────
        public int DrawCalls;
        public int InstancedDrawCalls;
        public long Triangles;
        public long Vertices;
        public int Batches;
        public long MeshVertexUploadBytes;
        public long MeshVertexBytesSaved;

        // ── Culling ──────────────────────────────────────────
        public int RenderablesCollected;
        public int RenderablesCulled;
        public int RenderablesDrawn;

        // ── Shadows ──────────────────────────────────────────
        public int ShadowDrawCalls;
        public int ShadowInstancedDrawCalls;
        public long ShadowTriangles;
        public long ShadowVertices;
        public int ShadowBatches;
        public int ShadowPasses;
        public int ShadowRenderablesCollected;
        public int ShadowRenderablesCulled;
        public int ShadowRenderablesDrawn;

        // ── Lighting ─────────────────────────────────────────
        public int Lights;
        public int DirectionalLights;
        public int PointLights;
        public int SpotLights;
        public int ShadowCasters;

        // ── Image Effects / Post-Processing ──────────────────
        public int ImageEffects;
        public int ImageEffectPasses;

        // ── Cameras ──────────────────────────────────────────
        public int Cameras;

        // ── Shadow atlas / pipeline mode (idle-budget diagnostics) ──
        /// <summary>How many times this frame submitted a full shadow-atlas depth clear.</summary>
        public int ShadowAtlasClears;
        /// <summary>Resolved atlas edge length (0 if never initialized).</summary>
        public int ShadowAtlasSize;
        /// <summary>How many times this frame cleared the local light-cookie atlas.</summary>
        public int CookieAtlasClears;
        /// <summary>Cookie atlas edge length (0 if never initialized).</summary>
        public int CookieAtlasSize;
        /// <summary>Cameras that skipped the shadow atlas pass entirely.</summary>
        public int ShadowPassesSkipped;
        public int ShadowPassesCached;
        /// <summary>Quality tier used while this snapshot was recorded.</summary>
        public QualitySettings.QualityTier QualityTier;
        /// <summary>Whether image effects were enabled for this snapshot.</summary>
        public bool ImageEffectsEnabled;
        /// <summary>Image effects omitted by the active quality tier.</summary>
        public int ImageEffectsSkipped;

        // ── Submission / bandwidth observability ─────────────────
        /// <summary>Command buffers submitted while this frame was active.</summary>
        public int CommandBufferSubmissions;
        public int ColorPassSubmissions;
        public int ShadowPassSubmissions;
        public int PostFxSubmissions;
        public int FinalPassSubmissions;
        public int UiSubmissions;
        public int ClearOperations;
        public int ColorClears;
        public int DepthClears;
        public int StencilClears;
        public int BlitOperations;
        /// <summary>Destination pixels touched by framebuffer blits, a bandwidth proxy.</summary>
        public long BlitPixels;
        /// <summary>Estimated bytes of transient color/depth attachments requested this frame.</summary>
        public long TransientRenderTargetBytes;
        public int TransientRenderTargetCount;
        public int PrepassSubmissions;
        public int PrepassSkipped;
        /// <summary>Passes accepted into the <see cref="ScriptableRenderer"/> queue this frame.</summary>
        public int EnqueuedPasses;
        /// <summary>Passes whose <see cref="RenderPass.Execute"/> ran this frame.</summary>
        public int ExecutedPasses;
        /// <summary>RenderGraph nodes culled because no live pass reads their writes (M18).</summary>
        public int CulledGraphPasses;
        /// <summary>On-demand opaque color copies (M7).</summary>
        public int CopyColorPasses;
        /// <summary>On-demand depth binds/copies (M7).</summary>
        public int CopyDepthPasses;
        /// <summary>Real-time GI albedo-capture geometry passes (forward path, realtime GI active).</summary>
        public int GiAlbedoPasses;
        /// <summary>Real-time GI fullscreen passes submitted (SSGI gather/temporal/blur/composite).</summary>
        public int GiPasses;
        /// <summary>Probe tiles refreshed by the dynamic irradiance volume this frame.</summary>
        public int GiProbesUpdated;
        /// <summary>RTHandle allocations that constructed or adopted a render target.</summary>
        public int RTHandleAllocations;
        /// <summary>RTHandle allocs that reused the existing target (stable size/format).</summary>
        public int RTHandleReuses;
        /// <summary>RTHandle releases (resize, unused prepass, or device shutdown).</summary>
        public int RTHandleReleases;
        /// <summary>CPU overdraw proxy: extra color draw calls beyond objects drawn (M20).</summary>
        public int OverdrawProxy;
        /// <summary>Directional shadow cascade count uploaded this frame (0 if no directional shadow).</summary>
        public int ShadowCascades;
        /// <summary>
        /// Sample count of the camera color target actually allocated this frame (1 = no MSAA).
        /// This is the effective value after device-capability clamping and the single-sample
        /// fallbacks, not the requested quality tier — use it to confirm MSAA really engaged.
        /// </summary>
        public int MsaaSamples;
        /// <summary>Multisample resolve operations recorded this frame.</summary>
        public int MsaaResolves;

        // Retained-mode UI.
        public int CanvasCount;
        public int ActiveGraphics;
        public int DirtyLayoutRoots;
        public int LayoutRebuilds;
        public int GraphicRebuilds;
        public int MaterialUpdates;
        public int MeshUploads;
        public long UploadedBytes;
        public long UIVertices;
        public long UIIndices;
        public int UIDrawCalls;
        public int UIBatches;
        public int ClipStateChanges;
        public int StencilPasses;

        // ── 2D sprites / atlases ─────────────────────────────
        public int SpritesVisible;
        public int SpritesCulled;
        public int SpriteInstances;
        public int SpriteBatches;
        public int SpriteDraws;
        public long SpriteUploadBytes;
        public int SpriteAtlasSwitches;

        // ── Virtual geometry ─────────────────────────────────
        /// <summary>Clusters selected across all renderers and views this frame.</summary>
        public int VgVisibleClusters;
        /// <summary>Triangles of selected clusters (pre-GPU, includes all views).</summary>
        public long VgTriangles;
        /// <summary>Virtual geometry draw calls (one per renderer-submesh-view-pass).</summary>
        public int VgDrawCalls;
        /// <summary>Index bytes re-uploaded into scratch index buffers this frame.</summary>
        public long VgScratchUploadBytes;
        /// <summary>Bytes resident in virtual geometry vertex buffers (gauge, not per-frame delta).</summary>
        public long VgResidentBytes;

        // ── Clustered light lists (P3.2) ─────────────────────
        /// <summary>CPU ms spent building per-camera cluster light lists this frame (all cameras).</summary>
        public float LightClusterBuildMs;
        /// <summary>Cluster light-index entries emitted this frame (all cameras).</summary>
        public int LightClusterEntries;

        // ── Timing (ms) ──────────────────────────────────────
        // CPU wall-clock on the encoding thread how long the pipeline spent
        // building the pass's CommandBuffers, not actual GPU execution time.
        public float FrameTimeMs;
        public float ColorPassMs;
        public float ShadowPassMs;
        public float PostFxMs;
        public float UIPassCpuMs;
    }

    private static Frame s_current;
    private static Frame s_last;
    private static bool s_inShadowPass;
    private static bool s_frameActive;
    private static long s_sectionStart;
    private static long s_uiPassStart;

    // Frame time history for graphs
    private static readonly float[] s_frameTimeHistory = new float[120];
    private static int s_frameTimeIndex;

    /// <summary>Snapshot of the most recently completed frame. Safe for UI to read.</summary>
    public static Frame Last => s_last;

    [HotPath] public static void RecordMeshVertexUpload(int bytes, int savedBytes)
    {
        s_current.MeshVertexUploadBytes += bytes;
        s_current.MeshVertexBytesSaved += savedBytes;
    }

    /// <summary>Ring buffer of recent frame times (ms) for plotting. Length = 120.</summary>
    public static float[] FrameTimeHistory => s_frameTimeHistory;

    /// <summary>Current write index in the history ring buffer.</summary>
    public static int FrameTimeIndex => s_frameTimeIndex;

    /// <summary>True while a frame's counters are being recorded.</summary>
    public static bool IsFrameActive => s_frameActive;

    /// <summary>Reset all counters. Called by the pipeline at the start of a frame.</summary>
    [HotPath]
    public static void BeginFrame()
    {
        s_current = default;
        s_inShadowPass = false;
        s_frameActive = true;
        s_uiPassStart = 0;
        s_current.QualityTier = QualitySettings.Tier;
        s_current.ImageEffectsEnabled = QualitySettings.ImageEffectsEnabled;
    }

    /// <summary>Promote the in-progress counters to <see cref="Last"/>. Called by the pipeline at the end of a frame.</summary>
    [HotPath]
    public static void EndFrame()
    {
        s_current.FrameTimeMs = (float)Time.UnscaledDeltaTime * 1000f;
        int extraDraws = s_current.DrawCalls - s_current.RenderablesDrawn;
        s_current.OverdrawProxy = extraDraws > 0 ? extraDraws : 0;
        s_last = s_current;
        s_frameActive = false;

        s_frameTimeHistory[s_frameTimeIndex] = s_last.FrameTimeMs;
        s_frameTimeIndex = (s_frameTimeIndex + 1) % s_frameTimeHistory.Length;
    }

    // Per-section CPU stopwatch on the encoding thread. Post-fx accumulates
    // across both BeginPostFx / EndPostFx cycles (AfterOpaques + PostProcess).

    [HotPath] public static void BeginShadowPass()
    {
        s_inShadowPass = true;
        s_current.ShadowPasses++;
        s_sectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    [HotPath] public static void EndShadowPass()
    {
        s_current.ShadowPassMs += ElapsedMs();
        s_inShadowPass = false;
    }

    [HotPath] public static void BeginColorPass() => s_sectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
    [HotPath] public static void EndColorPass() => s_current.ColorPassMs += ElapsedMs();

    [HotPath] public static void BeginPostFx() => s_sectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
    [HotPath] public static void EndPostFx() => s_current.PostFxMs += ElapsedMs();

    /// <summary>Starts timing one retained-mode UI collection and submission.</summary>
    [HotPath]
    public static void BeginUIPass()
    {
        if (!s_frameActive) return;
        s_uiPassStart = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>Accumulates CPU time spent collecting and encoding retained-mode UI.</summary>
    [HotPath]
    public static void EndUIPass()
    {
        if (!s_frameActive || s_uiPassStart == 0) return;
        s_current.UIPassCpuMs += (float)((System.Diagnostics.Stopwatch.GetTimestamp() - s_uiPassStart)
            * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        s_uiPassStart = 0;
    }

    private static float ElapsedMs()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        return (float)((now - s_sectionStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    [HotPath] public static void AddCamera() => s_current.Cameras++;

    /// <summary>Record a command buffer submission and classify the stable pipeline labels.</summary>
    [HotPath]
    public static void RecordCommandBufferSubmission(string? name)
    {
        if (!s_frameActive) return;
        s_current.CommandBufferSubmissions++;
        if (name == "ShadowAtlasClear"
            || name?.StartsWith("DirectionalLightCascade", StringComparison.Ordinal) == true
            || name?.StartsWith("PointLightFace", StringComparison.Ordinal) == true
            || name?.StartsWith("SpotLightShadow", StringComparison.Ordinal) == true)
        {
            s_current.ShadowPassSubmissions++;
            return;
        }

        switch (name)
        {
            case "ColorPass":
            case "Transparents": s_current.ColorPassSubmissions++; break;
            case "FinalBlit": s_current.FinalPassSubmissions++; break;
            case "UI": s_current.UiSubmissions++; break;
            case "Bloom":
            case "Tonemapper":
            case "TAA":
            case "SMAA":
            case "FXAA":
            case "SSR":
            case "GTAO":
            case "MotionBlur":
            case "VolumetricFog":
            case "BokehDoF":
            case "AutoExposure":
            case "Cinematic":
            case "MsaaResolve": s_current.PostFxSubmissions++; break;
        }
    }

    [HotPath]
    public static void RecordClear(ClearFlags flags)
    {
        if (!s_frameActive) return;
        s_current.ClearOperations++;
        if ((flags & ClearFlags.Color) != 0) s_current.ColorClears++;
        if ((flags & ClearFlags.Depth) != 0) s_current.DepthClears++;
        if ((flags & ClearFlags.Stencil) != 0) s_current.StencilClears++;
    }

    [HotPath]
    public static void RecordBlit(int destinationWidth, int destinationHeight)
    {
        if (!s_frameActive) return;
        s_current.BlitOperations++;
        s_current.BlitPixels += (long)Math.Max(0, destinationWidth) * Math.Max(0, destinationHeight);
    }

    /// <summary>Estimate requested transient attachment storage from format byte sizes.</summary>
    [HotPath]
    public static void RecordTransientRenderTarget(int width, int height, bool hasDepth,
        ReadOnlySpan<TextureImageFormat> formats)
    {
        if (!s_frameActive) return;
        long pixels = (long)Math.Max(0, width) * Math.Max(0, height);
        long bytesPerPixel = hasDepth ? Texture.GetBytesPerPixel(TextureImageFormat.Depth24f) : 0;
        for (int i = 0; i < formats.Length; i++)
            bytesPerPixel += Texture.GetBytesPerPixel(formats[i]);
        s_current.TransientRenderTargetCount++;
        s_current.TransientRenderTargetBytes += pixels * bytesPerPixel;
    }

    [HotPath] public static void RecordPrepassSubmitted() => s_current.PrepassSubmissions++;
    [HotPath] public static void RecordPrepassSkipped() => s_current.PrepassSkipped++;
    [HotPath] public static void RecordPassEnqueued() => s_current.EnqueuedPasses++;
    [HotPath] public static void RecordPassExecuted() => s_current.ExecutedPasses++;
    [HotPath] public static void RecordCulledGraphPass() => s_current.CulledGraphPasses++;
    [HotPath] public static void RecordShadowCascades(int count)
    {
        if (!s_frameActive) return;
        s_current.ShadowCascades = count < 0 ? 0 : count;
    }
    [HotPath] public static void RecordCopyColor() => s_current.CopyColorPasses++;
    [HotPath] public static void RecordCopyDepth() => s_current.CopyDepthPasses++;
    [HotPath] public static void RecordGiAlbedoPass() => s_current.GiAlbedoPasses++;
    [HotPath] public static void RecordGiPass() => s_current.GiPasses++;
    [HotPath] public static void RecordGiProbesUpdated(int count) => s_current.GiProbesUpdated += count;
    [HotPath] public static void RecordRTHandleAlloc() => s_current.RTHandleAllocations++;
    [HotPath] public static void RecordRTHandleReuse() => s_current.RTHandleReuses++;
    [HotPath] public static void RecordRTHandleRelease() => s_current.RTHandleReleases++;
    [HotPath] public static void RecordMsaaSamples(int samples)
    {
        if (!s_frameActive) return;
        s_current.MsaaSamples = samples < 1 ? 1 : samples;
    }
    [HotPath] public static void RecordMsaaResolve() => s_current.MsaaResolves++;

    /// <summary>Record a full shadow-atlas depth clear submission.</summary>
    [HotPath]
    public static void RecordShadowAtlasClear(int atlasSize)
    {
        s_current.ShadowAtlasClears++;
        s_current.ShadowAtlasSize = atlasSize;
    }

    [HotPath]
    public static void RecordCookieAtlasClear(int atlasSize)
    {
        s_current.CookieAtlasClears++;
        s_current.CookieAtlasSize = atlasSize;
    }

    /// <summary>Record that a camera skipped the shadow atlas pass (no shadow lights).</summary>
    [HotPath] public static void RecordLightClusterBuild(float milliseconds, int entries)
    {
        s_current.LightClusterBuildMs += milliseconds;
        s_current.LightClusterEntries += entries;
    }

    [HotPath] public static void RecordShadowPassSkipped() => s_current.ShadowPassesSkipped++;
    [HotPath] public static void RecordShadowPassCached() => s_current.ShadowPassesCached++;

    [HotPath] public static void RecordImageEffectsSkipped(int count) => s_current.ImageEffectsSkipped += count;

    [HotPath] public static void AddBatch() => s_current.Batches++;

    [HotPath]
    public static void RecordVirtualGeometry(int visibleClusters, int visibleTriangles)
    {
        if (!s_frameActive) return;
        s_current.VgVisibleClusters += visibleClusters;
        s_current.VgTriangles += visibleTriangles;
    }

    [HotPath]
    public static void RecordVirtualGeometryDraw(uint indexCount)
    {
        if (!s_frameActive) return;
        s_current.VgDrawCalls++;
        s_current.VgScratchUploadBytes += (long)indexCount * 4;
    }

    [HotPath]
    public static void SetVirtualGeometryResidentBytes(long bytes)
    {
        if (!s_frameActive) return;
        s_current.VgResidentBytes = bytes;
    }

    [HotPath] public static void RecordCanvasCollection(int activeGraphics)
    {
        if (!s_frameActive) return;
        s_current.CanvasCount++;
        if (activeGraphics > 0) s_current.ActiveGraphics += activeGraphics;
    }

    [HotPath]
    public static void RecordDirtyLayoutRoot(int count = 1)
    {
        if (!s_frameActive || count <= 0) return;
        s_current.DirtyLayoutRoots += count;
    }

    [HotPath] public static void RecordLayoutRebuild() { if (s_frameActive) s_current.LayoutRebuilds++; }
    [HotPath] public static void RecordGraphicRebuild() { if (s_frameActive) s_current.GraphicRebuilds++; }
    [HotPath] public static void RecordMaterialUpdate() { if (s_frameActive) s_current.MaterialUpdates++; }

    /// <summary>Records a UI mesh upload and its estimated vertex/index payload size.</summary>
    [HotPath]
    public static void RecordMeshUpload(int vertexCount, int indexCount, long uploadedBytes)
    {
        if (!s_frameActive) return;
        s_current.MeshUploads++;
        if (uploadedBytes > 0) s_current.UploadedBytes += uploadedBytes;
    }

    [HotPath]
    public static void RecordUIDraw(int vertexCount, int indexCount)
    {
        if (!s_frameActive) return;
        s_current.UIDrawCalls++;
        if (vertexCount > 0) s_current.UIVertices += vertexCount;
        if (indexCount > 0) s_current.UIIndices += indexCount;
    }

    [HotPath] public static void RecordUIBatch() { if (s_frameActive) s_current.UIBatches++; }

    [HotPath]
    public static void RecordClipStateChange(bool usesStencil)
    {
        if (!s_frameActive) return;
        s_current.ClipStateChanges++;
        if (usesStencil) s_current.StencilPasses++;
    }

    [HotPath]
    public static void RecordSprites(int visible, int culled, int instances, int batches, int draws, long uploadBytes, int atlasSwitches)
    {
        if (!s_frameActive) return;
        if (visible > 0) s_current.SpritesVisible += visible;
        if (culled > 0) s_current.SpritesCulled += culled;
        if (instances > 0) s_current.SpriteInstances += instances;
        if (batches > 0) s_current.SpriteBatches += batches;
        if (draws > 0) s_current.SpriteDraws += draws;
        if (uploadBytes > 0) s_current.SpriteUploadBytes += uploadBytes;
        if (atlasSwitches > 0) s_current.SpriteAtlasSwitches += atlasSwitches;
    }

    [HotPath]
    public static void AddRenderables(int collected, int culled, int drawn)
    {
        if (s_inShadowPass)
        {
            s_current.ShadowRenderablesCollected += collected;
            s_current.ShadowRenderablesCulled += culled;
            s_current.ShadowRenderablesDrawn += drawn;
        }
        else
        {
            s_current.RenderablesCollected += collected;
            s_current.RenderablesCulled += culled;
            s_current.RenderablesDrawn += drawn;
        }
    }

    public static void AddLightCounts(int total, int directional, int point, int spot, int shadowCasters)
    {
        s_current.Lights += total;
        s_current.DirectionalLights += directional;
        s_current.PointLights += point;
        s_current.SpotLights += spot;
        s_current.ShadowCasters += shadowCasters;
    }

    /// <summary>Record an image effect execution.</summary>
    public static void AddImageEffect(int passes = 1)
    {
        s_current.ImageEffects++;
        s_current.ImageEffectPasses += passes;
    }

    /// <summary>
    /// Record a draw call. Called from <see cref="Graphics"/>. <paramref name="indexCount"/> is
    /// the number of indices and <paramref name="topology"/> determines the triangle count.
    /// </summary>
    public static void RecordDraw(Topology topology, uint indexCount, uint instances = 1)
    {
        long primCount = topology switch
        {
            Topology.Triangles => indexCount / 3,
            Topology.TriangleStrip => indexCount >= 2 ? indexCount - 2 : 0,
            Topology.TriangleFan => indexCount >= 2 ? indexCount - 2 : 0,
            _ => 0
        };

        long tris = primCount * instances;
        long verts = (long)indexCount * instances;

        if (s_inShadowPass)
        {
            s_current.ShadowDrawCalls++;
            if (instances > 1) s_current.ShadowInstancedDrawCalls++;
            s_current.ShadowTriangles += tris;
            s_current.ShadowVertices += verts;
        }
        else
        {
            s_current.DrawCalls++;
            if (instances > 1) s_current.InstancedDrawCalls++;
        }

        // Always count total geometry
        s_current.Triangles += tris;
        s_current.Vertices += verts;
    }
}
