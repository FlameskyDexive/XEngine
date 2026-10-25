// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

#if !XENGINE_MOBILE
using XEngine.Runtime.Backends.OpenGL;
#endif
using XEngine.Runtime.RHI;
using XEngine.Runtime.Rendering;

namespace XEngine.Runtime;

/// <summary>
/// Facade over the active <see cref="IGraphicsDevice"/> and the <see cref="CommandBuffer"/>
/// system. Hosts capability constants, resource constructors, and convenience encoders.
/// OpenGL-specific render-thread ownership lives on <see cref="OpenGLGraphicsDevice"/>.
/// </summary>
public static unsafe class Graphics
{
    // Defaulted to conservative real-world minimums so CPU-side validation (e.g. texture size
    // checks) passes before/without a GL context. Initialize() overwrites them with real device
    // limits when a graphics device is present.
    public static int MaxTextureSize { get; internal set; } = 16384;
    public static int MaxCubeMapTextureSize { get; internal set; } = 16384;
    public static int MaxArrayTextureLayers { get; internal set; } = 2048;
    public static int MaxFramebufferColorAttachments { get; internal set; } = 8;

    /// <summary>Active graphics device, or null before creation / after dispose.</summary>
    public static IGraphicsDevice? Device { get; private set; }

    /// <summary>
    /// The concrete backend device, unwrapping <see cref="RenderThreadGraphicsDevice"/> when the
    /// dedicated render thread is active. Platform hosts and diagnostics that need the backend
    /// type must go through here instead of casting <see cref="Device"/>.
    /// </summary>
    public static IGraphicsDevice? BackendDevice =>
        Device is RenderThreadGraphicsDevice threaded ? threaded.Inner : Device;

    /// <summary>
    /// Opt-in for the backend-neutral render thread (P7 stage A). Enabled by the windowed host
    /// (<see cref="Window.Start"/>) for D3D12/Vulkan; left off for tests, headless tools and
    /// mobile hosts so they keep the historical synchronous submit. Must be set before the first
    /// <see cref="EnsureDevice"/> call — after the device exists it has no effect.
    /// </summary>
    public static bool UseDedicatedRenderThread { get; set; }

    /// <summary>True when command translation currently runs on a dedicated render thread.</summary>
    public static bool HasDedicatedRenderThread => Device is RenderThreadGraphicsDevice;

    /// <summary>
    /// P7 stage B — how many presented frames the main thread may run ahead of the render thread.
    /// <c>-1</c> (default) picks the per-host policy: the Player pipelines one frame so the vsync
    /// wait moves off the main thread, the editor stays conservative (0) because interaction
    /// latency and live inspection matter more than throughput there. Set explicitly (or use
    /// <c>--frame-pipelining=on|off</c> / <c>XENGINE_FRAME_PIPELINING</c>) to override.
    /// Requires <see cref="HasDedicatedRenderThread"/>; ignored otherwise.
    /// </summary>
    public static int MaxQueuedFrames { get; set; } = -1;

    private static int? s_framePipeliningOverride;

    /// <summary>
    /// Resolves the effective frame-queue depth for this frame. Frame-debugger captures always
    /// fall back to 0 so the capture and the presented frame describe the same instant.
    /// </summary>
    internal static int ResolveMaxQueuedFrames()
    {
        if (FrameDebugger.IsCapturing)
            return 0;
        if (MaxQueuedFrames >= 0)
            return MaxQueuedFrames;
        s_framePipeliningOverride ??= ParseFramePipeliningOverride();
        if (s_framePipeliningOverride.Value >= 0)
            return s_framePipeliningOverride.Value;
        return Application.IsEditor ? 0 : 1;
    }

    private static int ParseFramePipeliningOverride()
    {
        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--frame-pipelining=", StringComparison.OrdinalIgnoreCase))
                    continue;
                string value = args[i]["--frame-pipelining=".Length..];
                return value.Trim().ToLowerInvariant() switch
                {
                    "0" or "off" or "false" or "no" => 0,
                    "1" or "on" or "true" or "yes" => 1,
                    _ => -1,
                };
            }
            return Environment.GetEnvironmentVariable("XENGINE_FRAME_PIPELINING")?.Trim().ToLowerInvariant() switch
            {
                "0" or "off" or "false" or "no" => 0,
                "1" or "on" or "true" or "yes" => 1,
                _ => -1,
            };
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>
    /// Resolves the render-thread preference from the command line / environment.
    /// <c>--render-thread=off</c> (or <c>XENGINE_RENDER_THREAD=0</c>) forces the legacy
    /// synchronous path; <c>--render-thread=on</c> forces it on. Defaults to
    /// <paramref name="fallback"/>.
    /// </summary>
    public static bool ResolveRenderThreadPreference(bool fallback)
    {
        static bool? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "no" or "disable" or "disabled" => false,
            "1" or "on" or "true" or "yes" or "enable" or "enabled" => true,
            _ => null,
        };

        try
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.StartsWith("--render-thread=", StringComparison.OrdinalIgnoreCase))
                {
                    bool? parsed = Parse(arg["--render-thread=".Length..]);
                    if (parsed.HasValue) return parsed.Value;
                }
                else if (arg.Equals("--no-render-thread", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            bool? env = Parse(Environment.GetEnvironmentVariable("XENGINE_RENDER_THREAD"));
            if (env.HasValue) return env.Value;
        }
        catch (Exception)
        {
            // Hosts with a restricted environment keep the default.
        }
        return fallback;
    }

    /// <summary>Whether the active backend exposes the opt-in multi-surface lifecycle.</summary>
    public static bool SupportsPresentationSurfaces => Device is IPresentationSurfaceLifecycle;

    /// <summary>Gets the surface supplied when the active device was initialized, when it has one.</summary>
    public static bool TryGetPrimaryPresentationSurface(out PresentationSurfaceId surface)
    {
        if (Device is not IPresentationSurfaceLifecycle lifecycle)
        {
            surface = default;
            return false;
        }
        try { return lifecycle.TryGetPrimaryPresentationSurface(out surface); }
        catch (Exception)
        {
            surface = default;
            return false;
        }
    }

    public static bool TryCreatePresentationSurface(
        IWindowSurface surface,
        out PresentationSurfaceId id,
        out string unavailableReason)
    {
        if (Device is not IPresentationSurfaceLifecycle lifecycle)
        {
            id = default;
            unavailableReason = $"Backend {Device?.Capabilities.BackendName ?? "none"} does not expose presentation surfaces.";
            return false;
        }
        try
        {
            id = lifecycle.CreatePresentationSurface(surface);
            unavailableReason = string.Empty;
            return id.IsValid;
        }
        catch (Exception ex)
        {
            id = default;
            unavailableReason = ex.Message;
            return false;
        }
    }

    public static bool TryDestroyPresentationSurface(PresentationSurfaceId id)
    {
        if (Device is not IPresentationSurfaceLifecycle lifecycle)
            return false;
        try { return lifecycle.DestroyPresentationSurface(id); }
        catch (Exception) { return false; }
    }

    public static bool TryResizePresentationSurface(PresentationSurfaceId id, int width, int height)
    {
        if (Device is not IPresentationSurfaceLifecycle lifecycle)
            return false;
        try
        {
            return lifecycle.ResizePresentationSurface(id, width, height);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryGetPresentationSurface(PresentationSurfaceId id, out PresentationSurfaceInfo info)
    {
        if (Device is not IPresentationSurfaceLifecycle lifecycle)
        {
            info = default;
            return false;
        }
        try { return lifecycle.TryGetPresentationSurface(id, out info); }
        catch (Exception)
        {
            info = default;
            return false;
        }
    }

    public static bool TryBeginPresentationFrame(PresentationSurfaceId id)
    {
        if (Device is not IPresentationSurfaceLifecycle lifecycle)
            return false;
        try { return lifecycle.BeginFrame(id); }
        catch (Exception) { return false; }
    }

    public static bool TryEndPresentationFrame(PresentationSurfaceId id)
    {
        if (Device is not IPresentationSurfaceLifecycle lifecycle)
            return false;
        try { return lifecycle.EndFrame(id); }
        catch (Exception) { return false; }
    }

    public static bool IsDeviceLost => Device?.IsDeviceLost == true;

    /// <summary>
    /// True when there is no graphics device (no window / render thread) - a dedicated server or a
    /// build launched with --headless. GPU command submission becomes a no-op so gameplay code that
    /// creates or touches GPU resources (materials, terrain, render textures, etc.) runs without
    /// crashing; read-backs return their default (zeroed) contents.
    /// </summary>
    public static bool IsHeadless =>
        Device == null
        || Device.Backend == GraphicsBackend.Null
        || !Device.IsInitialized;

    public static GraphicsProgram CurrentProgram => GraphicsProgram.currentProgram;

    public static CommandBuffer GetCommandBuffer(string? name = null)
    {
        CommandBuffer cmd = CommandBufferPool.Rent(name);
        // Named buffers get BeginSample/EndSample so RenderDoc / PIX see the same tree as Frame Debugger.
        if (!string.IsNullOrEmpty(name))
            cmd.BeginSample(name);
        return cmd;
    }

    /// <summary>Enqueue a CB for the render thread to execute. Fire-and-forget.</summary>
    public static void Submit(CommandBuffer cmd)
    {
        if (cmd == null) return;
        if (cmd._inPool)
            throw new InvalidOperationException("CommandBuffer has already been submitted (it's in the pool).");
        cmd._fdScopeKind = FrameDebugger.CurrentSubmitScope;
        if (FrameDebugger.IsCapturing)
        {
            if (!string.IsNullOrEmpty(cmd.Name))
                cmd.EndSample();
            cmd.CaptureSubmissionState();
            FrameDebugger.Ingest(cmd);
        }
        else if (!string.IsNullOrEmpty(cmd.Name))
            cmd.EndSample();
        // No graphics device: drop GPU work and recycle the buffer instead of queueing it for a
        // render thread that will never drain it (which would leak the buffer).
        if (IsHeadless || IsDeviceLost)
        {
            cmd._ownerReleased = true;
            CommandBufferPool.Return(cmd);
            return;
        }
        if (!FrameDebugger.IsCapturing)
            cmd.CaptureSubmissionState();
        RenderStats.RecordCommandBufferSubmission(cmd.Name);
        Device!.Execute(cmd, wait: false);
    }

    /// <summary>Enqueue and block until the render thread has finished the CB.
    /// Use for read-backs, shader compile error propagation, and FBO completeness
    /// checks. Render-thread exceptions rethrow on the caller's thread.</summary>
    public static void SubmitAndWait(CommandBuffer cmd)
    {
        if (cmd == null) return;
        if (cmd._inPool)
            throw new InvalidOperationException("CommandBuffer has already been submitted (it's in the pool).");
        cmd._fdScopeKind = FrameDebugger.CurrentSubmitScope;
        if (FrameDebugger.IsCapturing)
        {
            if (!string.IsNullOrEmpty(cmd.Name))
                cmd.EndSample();
            cmd.CaptureSubmissionState();
            FrameDebugger.Ingest(cmd);
        }
        else if (!string.IsNullOrEmpty(cmd.Name))
            cmd.EndSample();
        // No graphics device: nothing executes, so don't block waiting on a render thread. Any
        // read-back this would have filled keeps its default (zeroed) contents.
        if (IsHeadless || IsDeviceLost)
        {
            cmd._ownerReleased = true;
            CommandBufferPool.Return(cmd);
            return;
        }
        if (!FrameDebugger.IsCapturing)
            cmd.CaptureSubmissionState();
        RenderStats.RecordCommandBufferSubmission(cmd.Name);
        Device!.Execute(cmd, wait: true);
    }

    internal static void BeginFrame()
    {
        FrameDebugger.OnBeginFrame();
        if (IsDeviceLost)
            return;
        Device?.BeginFrame();
    }

    /// <summary>Time the main thread spent blocked in <see cref="EndFrameAndWait"/>
    /// last frame. High = render thread is bottleneck. Near-zero = main is.</summary>
    public static float LastFrameWaitMs { get; private set; }

    /// <summary>Last fence-retired GPU timestamp interval, or null when unsupported/unresolved.</summary>
    public static GpuTimestampFrame? LastGpuTimestampFrame { get; private set; }

    internal static void EndFrameAndWait()
    {
        if (Device == null || IsDeviceLost)
        {
            FrameDebugger.OnEndFrame();
            return;
        }
        if (Device is RenderThreadGraphicsDevice pipelined)
            pipelined.MaxQueuedFrames = ResolveMaxQueuedFrames();
        Device.EndFrame();
        if (Device.TryGetCompletedGpuTimestampFrame(out GpuTimestampFrame timestamp))
            LastGpuTimestampFrame = timestamp;
        if (Device is RenderThreadGraphicsDevice threaded)
            LastFrameWaitMs = threaded.LastFrameWaitMs;
#if !XENGINE_MOBILE
        else if (Device is OpenGLGraphicsDevice gl)
            LastFrameWaitMs = gl.LastFrameWaitMs;
#endif
        FrameDebugger.OnEndFrame();
    }

    internal static void Resize(int width, int height) => ResizePrimarySurface(Device, width, height);

    internal static void ResizePrimarySurface(IGraphicsDevice? device, int width, int height)
    {
        // Resize is raised by the primary HWND before BeginFrame restores its surface.
        if (device is IPresentationSurfaceLifecycle surfaces
            && surfaces.TryGetPrimaryPresentationSurface(out var primary))
            surfaces.ResizePresentationSurface(primary, width, height);
        else
            device?.Resize(width, height);
    }

    public static void Initialize(bool debug)
    {
        EnsureDevice(debug);
        LastGpuTimestampFrame = null;

        // Built-ins may have been parsed while no global device was active. Their CPU objects are
        // usable, but embedded textures have zero GPU handles and must be recreated for this device.
        if (!Device!.IsInitialized)
        {
            BuiltInAssets.DisposeCached();
            // Headless startup can also fill the transient pool with targets whose allocation
            // commands were dropped. They cannot become live attachments by being reused.
            Resources.RenderTexture.DisposeTemporaryPool();
        }

#if !XENGINE_MOBILE
        if (BackendDevice is OpenGLGraphicsDevice glPending)
            glPending.PendingDebug = debug;

        IWindowSurface? surface = Window.InternalWindow != null
            ? new SilkWindowSurface(Window.InternalWindow)
            : null;
#else
        IWindowSurface? surface = Window.PlatformSurface;
#endif
        Device.Initialize(surface);

        GraphicsDeviceCapabilities caps = Device.Capabilities;
        MaxTextureSize = caps.MaxTextureSize;
        MaxCubeMapTextureSize = caps.MaxCubeMapTextureSize;
        MaxArrayTextureLayers = caps.MaxArrayTextureLayers;
        MaxFramebufferColorAttachments = caps.MaxFramebufferColorAttachments;
    }

    /// <summary>
    /// Creates the preferred graphics device (if needed) and, for OpenGL, starts its render
    /// thread with the GL context. Must run before <see cref="Initialize"/> so Load-time
    /// SubmitAndWait has a live drain.
    /// </summary>
    public static void StartRenderThread()
    {
        EnsureDevice(debug: false);
#if !XENGINE_MOBILE
        if (BackendDevice is OpenGLGraphicsDevice glDevice)
            glDevice.StartRenderThread(new SilkWindowSurface(Window.InternalWindow));
#endif
    }

    /// <summary>
    /// Create the device for <see cref="GraphicsBackendSelection.Preferred"/> (or CLI)
    /// without starting presentation. Used by tests and explicit host bootstraps.
    /// Windowed Auto selects native Metal on Apple hosts and Vulkan on other desktop hosts.
    /// Use an explicit <c>--graphics=</c> value only to select another backend for diagnostics
    /// or compatibility testing.
    /// </summary>
    public static void EnsureDevice(bool debug = false)
    {
        if (Device != null)
            return;

        GraphicsBackend backend = GraphicsBackendSelection.Preferred
            ?? GraphicsBackendSelection.Parse(Environment.GetCommandLineArgs());

        backend = GraphicsBackendSelection.ResolveWindowedAuto(backend);

        IGraphicsDevice created = GraphicsDeviceFactory.Create(new GraphicsDeviceOptions
        {
            Backend = backend,
            Debug = debug,
            EnableValidation = debug,
            VSync = Window.InternalWindow?.VSync ?? true,
        });

        Device = ShouldRunOnRenderThread(created)
            ? new RenderThreadGraphicsDevice(created)
            : created;
    }

    /// <summary>
    /// OpenGL owns its own render thread (hard context affinity) and the Null device has no work
    /// to move, so neither is wrapped. Everything else translates on the caller's thread and
    /// therefore benefits.
    /// </summary>
    private static bool ShouldRunOnRenderThread(IGraphicsDevice device)
    {
        if (!UseDedicatedRenderThread)
            return false;
        if (device is not IPresentationSurfaceLifecycle)
            return false;
        return device.Backend is GraphicsBackend.Direct3D12 or GraphicsBackend.Vulkan;
    }

    public static void Dispose()
    {
        Rendering.PropertyState.ClearGlobals();
        Rendering.GlobalUniforms.Dispose();
        Rendering.ShadowAtlas.Dispose();
        Rendering.LightCookieAtlas.Dispose();
        Rendering.BRDFLutGenerator.Dispose();
        Rendering.SMAALookupTextures.Dispose();
        Rendering.UniversalRenderPipeline.DisposeDefaultResources();
        Rendering.RTHandle.ReleaseAllTracked();
        Rendering.RenderPipeline.DisposeSharedResources();
        BuiltInAssets.DisposeCached();
        Resources.RenderTexture.DisposeTemporaryPool();
        Device?.WaitIdle();
        Device?.Shutdown();
        Device?.Dispose();
        Device = null;
        LastGpuTimestampFrame = null;
    }

    // ─────────────────────── Resource creation ───────────────────────

    public static GraphicsBuffer CreateBuffer<T>(BufferType bufferType, T[] data, bool dynamic = false) where T : unmanaged
    {
        // Convert the typed array to a byte span. The GraphicsBuffer constructor
        // copies the bytes into a CommandBuffer's transient store so the caller's
        // T[] can be freed/reused immediately after this returns.
        return new GraphicsBuffer(bufferType, System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()), dynamic);
    }

    public static GraphicsVertexArray CreateVertexArray(
        VertexFormat format,
        GraphicsBuffer vertices,
        GraphicsBuffer? indices,
        VertexFormat? instanceFormat = null,
        GraphicsBuffer? instanceBuffer = null)
    {
        return new GraphicsVertexArray(format, vertices, indices, instanceFormat, instanceBuffer);
    }

    public static GraphicsFrameBuffer CreateFramebuffer(GraphicsFrameBuffer.Attachment[] attachments, uint width, uint height)
        => new GraphicsFrameBuffer(attachments, width, height);

    public static GraphicsTexture CreateTexture(TextureType type, TextureImageFormat format)
        => new GraphicsTexture(type, format);

    /// <param name="sampleCount">Multisample count (1 = single-sample); above 1 creates a
    /// render-target-only multisample attachment.</param>
    public static GraphicsTexture CreateTexture(TextureType type, TextureImageFormat format, uint sampleCount)
        => new GraphicsTexture(type, format, sampleCount);

    public static GraphicsProgram CompileProgram(string fragment, string vertex, string geometry)
        => new GraphicsProgram(fragment, vertex, geometry);

    // Resources replaced mid-frame (e.g. an instance buffer that grows) can't be
    // disposed immediately because earlier encoded CBs still reference the old
    // handle. DeferDispose queues them FlushDeferredDisposes runs once per frame
    // after all CBs have executed.
    private static readonly System.Collections.Generic.List<System.IDisposable> s_deferredDisposes = new();

    public static void DeferDispose(System.IDisposable resource)
    {
        if (resource == null) return;
        lock (s_deferredDisposes)
            s_deferredDisposes.Add(resource);
    }

    public static void FlushDeferredDisposes()
    {
        lock (s_deferredDisposes)
        {
            for (int i = 0; i < s_deferredDisposes.Count; i++)
                s_deferredDisposes[i].Dispose();
            s_deferredDisposes.Clear();
        }
    }

    // Convenience encoders for sticky texture state. Each rents a one-op CB and
    // submits it so the mutation runs on the render thread in submit order.

    public static void SetWrapS(GraphicsTexture texture, TextureWrap wrap) => EncodeOneOp(c => c.EncodeSetTextureWrap(texture, 0, wrap), "Texture.SetWrapS");
    public static void SetWrapT(GraphicsTexture texture, TextureWrap wrap) => EncodeOneOp(c => c.EncodeSetTextureWrap(texture, 1, wrap), "Texture.SetWrapT");
    public static void SetWrapR(GraphicsTexture texture, TextureWrap wrap) => EncodeOneOp(c => c.EncodeSetTextureWrap(texture, 2, wrap), "Texture.SetWrapR");
    public static void SetTextureFilters(GraphicsTexture texture, TextureMin min, TextureMag mag) => EncodeOneOp(c => c.EncodeSetTextureFilters(texture, min, mag), "Texture.SetFilters");
    public static void SetTextureCompareMode(GraphicsTexture texture, bool enabled) => EncodeOneOp(c => c.EncodeSetTextureCompareMode(texture, enabled), "Texture.SetCompareMode");
    public static void GenerateMipmap(GraphicsTexture texture) => EncodeOneOp(c => c.GenerateMipmap(texture), "Texture.GenerateMipmap");

    /// <summary>Synchronous texture read-back. Blocks until the destination is filled.</summary>
    public static unsafe void GetTexImage(GraphicsTexture texture, int mip, void* data)
    {
        using var cmd = GetCommandBuffer("Texture.GetTexImage");
        cmd.EncodeGetTextureDataPtr(texture, mip, (nint)data);
        SubmitAndWait(cmd);
    }

    /// <summary>Synchronous read-back of one cubemap face's mip level. Blocks until filled.</summary>
    public static void GetTexImageCubeFace(GraphicsTexture texture, int face, int mip, byte[] destination)
    {
        using var cmd = GetCommandBuffer("Texture.GetTexImageCubeFace");
        cmd.EncodeGetTextureCubeFaceData(texture, face, mip, destination);
        SubmitAndWait(cmd);
    }

    public static unsafe void TexImage2D(GraphicsTexture texture, int mip, uint width, uint height, int border, void* data)
    {
        int size = data != null ? (int)(width * height * BytesPerPixel(texture)) : 0;
        ReadOnlySpan<byte> span = data != null ? new ReadOnlySpan<byte>(data, size) : ReadOnlySpan<byte>.Empty;
        using var cmd = GetCommandBuffer("Texture.TexImage2D");
        cmd.EncodeAllocateTexture2D(texture, mip, width, height, border, span);
        Submit(cmd);
    }

    public static unsafe void TexSubImage2D(GraphicsTexture texture, int mip, int x, int y, uint width, uint height, void* data)
    {
        if (data == null) return;
        int size = (int)(width * height * BytesPerPixel(texture));
        var span = new ReadOnlySpan<byte>(data, size);
        using var cmd = GetCommandBuffer("Texture.TexSubImage2D");
        cmd.EncodeUpdateTexture2D(texture, mip, x, y, width, height, span);
        // HarmonyOS bone palettes (single-row Float4 textures) are updated and drawn
        // in the same Vulkan frame command buffer. Keep imported image uploads synchronous:
        // they can originate on the asset loader while initial mip/material setup is running.
        // UpdateTexture supplies transfer/vertex/fragment barriers and retains staging buffers
        // until the frame fence completes. Waiting here flushes the whole frame per skeleton.
        if (Application.RuntimePlatform == ApplicationRuntimePlatform.HarmonyOS &&
            texture.ImageFormat == TextureImageFormat.Float4 && height == 1 &&
            BackendDevice?.Backend == RHI.GraphicsBackend.Vulkan)
        {
            Submit(cmd);
            return;
        }
        // Large uploads (initial asset texture data) must complete synchronously: the render
        // thread's async retire can free the staging buffer before the GPU executes the copy,
        // silently zeroing the image (the "characters render as black silhouettes" root cause).
        // Other backends remain synchronous: per-frame dynamic uploads (SkinnedMeshRenderer's bone matrix
        // texture, ~7KB) raced the draw submission — two submissions on one queue have no
        // implicit memory dependency, so the vertex shader sampled half-written/wrong-layout
        // texels and skinned meshes shredded. The 1MB async threshold exempted exactly the
        // small per-frame uploads that need ordering the most.
        SubmitAndWait(cmd);
    }

    /// <summary>
    /// Uploads one complete block-compressed mip level. The payload is already encoded in the
    /// texture's Vulkan format; callers must provide the exact tight block payload size.
    /// </summary>
    public static void CompressedTexImage2D(GraphicsTexture texture, int mip, uint width, uint height, ReadOnlySpan<byte> data)
    {
        if (!Resources.Texture.IsCompressedFormat(texture.ImageFormat))
            throw new ArgumentException("CompressedTexImage2D requires a block-compressed texture format.", nameof(texture));
        int expected = Resources.Texture.GetDataSize(texture.ImageFormat, width, height);
        if (data.Length != expected)
            throw new ArgumentException($"Compressed texture upload expected {expected} bytes, got {data.Length}.", nameof(data));
        using var cmd = GetCommandBuffer("Texture.CompressedTexImage2D");
        cmd.EncodeAllocateTexture2D(texture, mip, width, height, 0, data);
        Submit(cmd);
    }

    /// <summary>Allocate (and optionally upload) one face of a cubemap at a mip level.
    /// <paramref name="face"/> is 0..5 in GL order (+X, -X, +Y, -Y, +Z, -Z).</summary>
    public static unsafe void TexImageCubeFace(GraphicsTexture texture, int face, int mip, uint size, void* data)
    {
        int byteSize = data != null ? (int)(size * size * BytesPerPixel(texture)) : 0;
        ReadOnlySpan<byte> span = data != null ? new ReadOnlySpan<byte>(data, byteSize) : ReadOnlySpan<byte>.Empty;
        using var cmd = GetCommandBuffer("Texture.TexImageCubeFace");
        cmd.EncodeAllocateTextureCubeFace(texture, face, mip, size, span);
        Submit(cmd);
    }

    public static unsafe void TexImage3D(GraphicsTexture texture, int level, uint width, uint height, uint depth, void* data)
    {
        int size = data != null ? (int)(width * height * depth * BytesPerPixel(texture)) : 0;
        ReadOnlySpan<byte> span = data != null ? new ReadOnlySpan<byte>(data, size) : ReadOnlySpan<byte>.Empty;
        using var cmd = GetCommandBuffer("Texture.TexImage3D");
        cmd.EncodeAllocateTexture3D(texture, level, width, height, depth, span);
        Submit(cmd);
    }

    public static unsafe void TexSubImage3D(GraphicsTexture texture, int level, int x, int y, int z, uint width, uint height, uint depth, void* data)
    {
        if (data == null) return;
        int size = (int)(width * height * depth * BytesPerPixel(texture));
        var span = new ReadOnlySpan<byte>(data, size);
        using var cmd = GetCommandBuffer("Texture.TexSubImage3D");
        cmd.EncodeUpdateTexture3D(texture, level, x, y, z, width, height, depth, span);
        Submit(cmd);
    }

    /// <summary>Bytes per pixel under tight packing, used to size command-buffer copies.</summary>
    private static int BytesPerPixel(GraphicsTexture texture) =>
        Resources.Texture.GetBytesPerPixel(texture.ImageFormat);

    private static void EncodeOneOp(Action<CommandBuffer> encode, string name)
    {
        using var cmd = GetCommandBuffer(name);
        encode(cmd);
        Submit(cmd);
    }
}
