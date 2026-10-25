// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

using Silk.NET.Core.Native;
using Silk.NET.OpenGL;

using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Backends.OpenGL;

/// <summary>
/// OpenGL <see cref="IGraphicsDevice"/> owning the render-thread FIFO, GL context
/// lifetime, and <see cref="OpenGLCommandExecutor"/>. Preserves the historical
/// <c>Graphics</c> submit / frame-end semantics.
/// </summary>
public sealed unsafe class OpenGLGraphicsDevice : IGraphicsDevice, IPresentationSurfaceLifecycle
{
    private readonly GraphicsDeviceOptions _options;
    private OpenGLCommandExecutor? _executor;
    private readonly BlockingCollection<CBJob> _renderQueue = new();
    private readonly ManualResetEventSlim _frameDone = new(true);

    private IWindowSurface? _surface;
    private readonly PresentationSurfaceRegistry _presentationSurfaceRegistry = new();
    private readonly Dictionary<PresentationSurfaceId, IWindowSurface> _presentationSurfaces = new();
    private PresentationSurfaceId _activePresentationSurface;
    private PresentationSurfaceId _primaryPresentationSurface;
    private Thread? _renderThread;
    private GL? _gl;
    private GraphicsDeviceCapabilities _capabilities = new() { BackendName = "OpenGL" };
    private bool _initialized;
    private bool _threadStarted;
    private bool _shutdown;
    private bool _frameBegun;
    private ulong _fenceValue;
    private float _lastFrameWaitMs;
    private readonly uint[] _timestampQueries = new uint[4];
    private bool _timestampQueriesReady;
    private bool _timestampFrameRequested;
    private bool _timestampFrameStarted;
    private readonly bool[] _timestampFrameSubmitted = new bool[2];
    private int _timestampFrameSlot;
    private ulong _nextTimestampFrameId;
    private readonly ulong[] _timestampFrameIds = new ulong[2];
    private GpuTimestampFrame _lastGpuTimestampFrame;
    private bool _hasGpuTimestampFrame;

    /// <summary>Set by <see cref="Graphics.Initialize"/> so debug follows the facade flag even if the device was created earlier.</summary>
    internal bool PendingDebug { get; set; }

    public OpenGLGraphicsDevice(GraphicsDeviceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public GraphicsBackend Backend => GraphicsBackend.OpenGL;

    public GraphicsDeviceCapabilities Capabilities => _capabilities;

    public bool IsInitialized => _initialized;

    /// <summary>Time the main thread spent blocked in <see cref="EndFrame()"/> last frame.</summary>
    internal float LastFrameWaitMs => _lastFrameWaitMs;

    /// <summary>
    /// Hand the GL context off the calling thread and start the continuous drain loop.
    /// Must run before <see cref="Initialize"/> so Load-time <c>SubmitAndWait</c> has a live drain.
    /// </summary>
    internal void StartRenderThread(IWindowSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (_threadStarted)
            return;
        if (_shutdown)
            throw new ObjectDisposedException(nameof(OpenGLGraphicsDevice));

        _surface = surface;
        // Hand the context off the main thread so the render thread can MakeCurrent.
        surface.ClearCurrent();
        _renderThread = new Thread(RenderThreadLoop)
        {
            IsBackground = true,
            Name = "XEngine GL Render Thread",
        };
        _threadStarted = true;
        _renderThread.Start();
    }

    public void Initialize(IWindowSurface? surface)
    {
        if (_shutdown)
            throw new ObjectDisposedException(nameof(OpenGLGraphicsDevice));

        if (_initialized)
            return;

        if (surface != null)
            _surface = surface;

        if (_surface == null)
            throw new InvalidOperationException("OpenGLGraphicsDevice.Initialize requires an IWindowSurface.");

        IWindowSurface winSurface = _surface;
        Silk.NET.Windowing.IWindow silkWindow = winSurface is SilkWindowSurface silk
            ? silk.Window
            : throw new InvalidOperationException(
                "OpenGLGraphicsDevice currently requires a SilkWindowSurface (Silk.NET IWindow).");

        _gl = GL.GetApi(silkWindow);
        _executor = new OpenGLCommandExecutor(_gl);

        bool debug = PendingDebug || _options.Debug || _options.EnableValidation;
        if (debug && OperatingSystem.IsWindows())
        {
            _gl.DebugMessageCallback(DebugCallback, null);
            _gl.Enable(EnableCap.DebugOutput);
            _gl.Enable(EnableCap.DebugOutputSynchronous);
        }

        _gl.Enable(EnableCap.LineSmooth);

        // Seamless cubemap filtering removes the visible face seams when sampling a
        // cubemap with linear/trilinear filtering. Required for clean reflection-probe
        // and prefiltered-environment sampling.
        _gl.Enable(EnableCap.TextureCubeMapSeamless);

        // Match Vulkan's sRGB swapchain: the Vulkan backend selects B8G8R8A8_SRGB which auto-encodes
        // linear shader output to sRGB on present. Without this, OpenGL displays raw linear values,
        // making midtones darker/desaturated (bluish cast) compared to Vulkan's correct sRGB curve.
        // GL_FRAMEBUFFER_SRGB = 0x8DB9
        _gl.Enable((EnableCap)0x8DB9);

        _capabilities = new GraphicsDeviceCapabilities
        {
            MaxTextureSize = _gl.GetInteger(GLEnum.MaxTextureSize),
            MaxCubeMapTextureSize = _gl.GetInteger(GLEnum.MaxCubeMapTextureSize),
            MaxArrayTextureLayers = _gl.GetInteger(GLEnum.MaxArrayTextureLayers),
            MaxFramebufferColorAttachments = _gl.GetInteger(GLEnum.MaxColorAttachments),
            MaxFramesInFlight = 1,
            SupportsCompute = false,
            SupportsGeometryShader = true,
            HardwareSupportsTimestampQueries = false,
            BackendName = "OpenGL 4.1",
        };

        _initialized = true;
        _primaryPresentationSurface = _presentationSurfaceRegistry.Create(_surface);
        _activePresentationSurface = _primaryPresentationSurface;
        _presentationSurfaces.Add(_activePresentationSurface, _surface);
    }

    public void Shutdown()
    {
        if (_shutdown)
            return;
        _shutdown = true;

        // CompleteAdding makes the render thread's Take throw once the queue is
        // drained, so it finishes any pending work and then exits cleanly.
        try { _renderQueue.CompleteAdding(); } catch { /* already completed */ }
        _renderThread?.Join();
        _renderThread = null;
        _threadStarted = false;

        try { _surface?.MakeCurrent(); } catch { /* context may already be gone */ }

        if (_gl != null && _timestampQueriesReady)
        {
            try { _gl.DeleteQueries(_timestampQueries.AsSpan()); } catch { /* context may already be gone */ }
        }
        _gl?.Dispose();
        _gl = null;
        _executor = null;
        _initialized = false;
        _presentationSurfaces.Clear();
        _presentationSurfaceRegistry.Clear();
        _activePresentationSurface = default;
        _primaryPresentationSurface = default;
        _frameBegun = false;
        _timestampQueriesReady = false;
        _frameDone.Set();
    }

    public bool TryGetPrimaryPresentationSurface(out PresentationSurfaceId surface)
    {
        lock (_presentationSurfaces)
        {
            surface = _primaryPresentationSurface;
            return surface.IsValid && _presentationSurfaces.ContainsKey(surface);
        }
    }

    public PresentationSurfaceId CreatePresentationSurface(IWindowSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (_shutdown || !_initialized || !_threadStarted)
            throw new InvalidOperationException("OpenGL presentation surfaces require an initialized render thread.");
        lock (_presentationSurfaces)
        {
            if (_frameBegun)
                throw new InvalidOperationException("Cannot create an OpenGL presentation surface during a frame.");
            ValidateSharedContext(surface);
            surface.ClearCurrent();
            PresentationSurfaceId id = _presentationSurfaceRegistry.Create(surface);
            _presentationSurfaces.Add(id, surface);
            return id;
        }
    }

    /// <summary>
    /// Runs a window-system operation while the render thread has temporarily released its
    /// current OpenGL context. GLFW/WGL requires the source context to be unbound before a
    /// window can create a context sharing its object namespace. The operation itself remains
    /// on the calling (window/UI) thread; GPU jobs stay queued until the render thread rebinds.
    /// </summary>
    public void ExecuteWithContextReleased(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_shutdown || !_initialized || !_threadStarted)
            throw new InvalidOperationException("OpenGL context release requires an initialized render thread.");

        var lease = new ContextReleaseLease();
        try
        {
            lock (_presentationSurfaces)
            {
                if (_frameBegun)
                    throw new InvalidOperationException("Cannot create a shared OpenGL window during a frame.");

                _renderQueue.Add(new CBJob { ContextReleaseLease = lease });
                lease.Released.Wait();
                ExceptionDispatchInfo? operationError = lease.Error;
                if (operationError == null)
                {
                    try { operation(); }
                    catch (Exception ex) { operationError = ExceptionDispatchInfo.Capture(ex); }
                }
                lease.Resume.Set();
                lease.Resumed.Wait();
                lease.Error?.Throw();
                operationError?.Throw();
            }
        }
        finally { lease.Dispose(); }
    }

    public bool DestroyPresentationSurface(PresentationSurfaceId surface)
    {
        lock (_presentationSurfaces)
        {
            if ((_frameBegun && _activePresentationSurface == surface)
                || !_presentationSurfaces.Remove(surface)
                || !_presentationSurfaceRegistry.Destroy(surface))
                return false;
            if (_primaryPresentationSurface == surface)
                _primaryPresentationSurface = default;
            if (_activePresentationSurface == surface)
                _activePresentationSurface = FirstPresentationSurface();
            return true;
        }
    }

    public bool ResizePresentationSurface(PresentationSurfaceId surface, int width, int height)
    {
        lock (_presentationSurfaces)
            return _presentationSurfaceRegistry.Resize(surface, width, height);
    }

    public bool TryGetPresentationSurface(PresentationSurfaceId surface, out PresentationSurfaceInfo info)
    {
        lock (_presentationSurfaces)
            return _presentationSurfaceRegistry.TryGet(surface, out info);
    }

    public bool BeginFrame(PresentationSurfaceId surface)
    {
        lock (_presentationSurfaces)
        {
            if (!_presentationSurfaceRegistry.Begin(surface))
                return false;
            try
            {
                if (_frameBegun)
                    throw new InvalidOperationException("An OpenGL frame is already active.");
                _activePresentationSurface = surface;
                BeginFrameLocked();
                return true;
            }
            catch
            {
                _presentationSurfaceRegistry.End(surface);
                throw;
            }
        }
    }

    public bool EndFrame(PresentationSurfaceId surface)
    {
        lock (_presentationSurfaces)
        {
            if (_activePresentationSurface != surface || !_frameBegun)
                return false;
            EndFrameLocked(_presentationSurfaces[surface]);
            return _presentationSurfaceRegistry.End(surface);
        }
    }

    public void BeginFrame()
    {
        lock (_presentationSurfaces)
        {
            // Generic frames originate from the application's primary window. Explicit
            // BeginFrame(surface) calls are reserved for detached presentation surfaces.
            if (_primaryPresentationSurface.IsValid && _presentationSurfaces.ContainsKey(_primaryPresentationSurface))
                _activePresentationSurface = _primaryPresentationSurface;
            BeginFrameLocked();
        }
    }

    private void BeginFrameLocked()
    {
        if (_frameBegun)
            throw new InvalidOperationException("An OpenGL frame is already active.");
        _frameBegun = true;
        // Arm the frame-done gate so EndFrame blocks until THIS frame's
        // sentinel is processed. The render thread is always draining, so there's
        // nothing to wake.
        _frameDone.Reset();
        if (_timestampQueriesReady)
        {
            _timestampFrameSlot = (_timestampFrameSlot + 1) & 1;
            _timestampFrameRequested = !_timestampFrameSubmitted[_timestampFrameSlot];
            _timestampFrameStarted = false;
            _timestampFrameIds[_timestampFrameSlot] = ++_nextTimestampFrameId;
        }
    }

    public void EndFrame()
    {
        lock (_presentationSurfaces)
        {
            IWindowSurface surface = _presentationSurfaces.TryGetValue(_activePresentationSurface, out IWindowSurface? active)
                ? active
                : _surface ?? throw new InvalidOperationException("OpenGL presentation surface is unavailable.");
            EndFrameLocked(surface);
        }
    }

    private void EndFrameLocked(IWindowSurface surface)
    {
        if (!_frameBegun)
            return;
        _frameBegun = false;
        _renderQueue.Add(new CBJob { IsFrameEnd = true, PresentationSurface = surface });
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        _frameDone.Wait();
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        _lastFrameWaitMs = (float)(elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        _fenceValue++;
    }

    public void Execute(CommandBuffer commandBuffer, bool wait)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        if (commandBuffer._inPool)
            throw new InvalidOperationException("CommandBuffer has already been submitted (it's in the pool).");
        if (_shutdown || !_threadStarted)
            throw new InvalidOperationException("OpenGLGraphicsDevice render thread is not running.");

        commandBuffer._submitted = true;
        commandBuffer._ownerReleased = true;

        IWindowSurface? presentationSurface = null;
        lock (_presentationSurfaces)
        {
            if (commandBuffer.PresentationSurface.IsValid)
            {
                if (!_frameBegun || _activePresentationSurface != commandBuffer.PresentationSurface)
                    throw new InvalidOperationException(
                        "A command buffer presentation target must be the surface active in BeginFrame(surface).");
                presentationSurface = _presentationSurfaces[_activePresentationSurface];
            }
            else if (_frameBegun && _presentationSurfaces.TryGetValue(_activePresentationSurface, out IWindowSurface? active))
            {
                presentationSurface = active;
            }
        }

        if (!wait)
        {
            _renderQueue.Add(new CBJob { Cmd = commandBuffer, PresentationSurface = presentationSurface });
            return;
        }

        var job = new CBJob
        {
            Cmd = commandBuffer,
            Done = new ManualResetEventSlim(false),
            PresentationSurface = presentationSurface,
        };
        _renderQueue.Add(job);
        job.Done.Wait();
        job.Done.Dispose();
        job.Error?.Throw();
        _fenceValue++;
    }

    public void WaitIdle()
    {
        if (!_threadStarted || _shutdown)
            return;

        var done = new ManualResetEventSlim(false);
        _renderQueue.Add(new CBJob { Done = done });
        done.Wait();
        done.Dispose();
    }

    public ulong GetFenceValue() => _fenceValue;

    public bool TryGetCompletedGpuTimestampFrame(out GpuTimestampFrame frame)
    {
        frame = _lastGpuTimestampFrame;
        return _hasGpuTimestampFrame;
    }

    public void WaitFence(ulong fenceValue)
    {
        // OpenGL path completes work in FIFO order; drain until idle if the caller
        // asks past the last recorded fence (conservative).
        if (fenceValue > _fenceValue)
            WaitIdle();
    }

    public void Dispose() => Shutdown();

    private void RenderThreadLoop()
    {
        IWindowSurface? currentSurface = _surface;
        if (currentSurface == null)
        {
            _frameDone.Set();
            return;
        }

        // Take the GL context once and hold it for the entire run. The frame-end
        // sentinel does SwapBuffers; the context never bounces back to main.
        try { currentSurface.MakeCurrent(); }
        catch (Exception ex)
        {
            Debug.LogError($"Render thread MakeCurrent failed: {ex}");
            _frameDone.Set();
            return;
        }

        TryInitializeTimestampQueries();

        try
        {
            // Single continuous drain loop. Jobs execute in submit order as they
            // arrive, so resource-creation and SubmitAndWait jobs enqueued between
            // frames or from background threads are serviced without waiting for the
            // next BeginFrame. SwapBuffers + frame-done signalling happen only on the
            // frame-end sentinel pushed by EndFrame.
            while (true)
            {
                CBJob job;
                try { job = _renderQueue.Take(); }
                catch (InvalidOperationException) { break; } // CompleteAdding + drained

                if (job.IsFrameEnd)
                {
                    if (job.PresentationSurface != null
                        && !ReferenceEquals(currentSurface, job.PresentationSurface))
                    {
                        currentSurface.ClearCurrent();
                        job.PresentationSurface.MakeCurrent();
                        currentSurface = job.PresentationSurface;
                    }
                    RecordFrameEndTimestamp();
                    try { currentSurface.SwapBuffers(); }
                    catch (Exception ex) { Debug.LogError($"SwapBuffers failed: {ex}"); }
                    finally { _frameDone.Set(); }
                    continue;
                }

                if (job.ContextReleaseLease is ContextReleaseLease lease)
                {
                    try
                    {
                        currentSurface.ClearCurrent();
                        lease.Released.Set();
                        lease.Resume.Wait();
                        currentSurface.MakeCurrent();
                    }
                    catch (Exception ex)
                    {
                        lease.Error = ExceptionDispatchInfo.Capture(ex);
                        lease.Released.Set();
                    }
                    finally
                    {
                        lease.Resumed.Set();
                    }
                    continue;
                }

                if (job.Cmd == null)
                {
                    // Idle / WaitIdle sentinel — no CB to execute.
                    job.Done?.Set();
                    continue;
                }

                var cmd = job.Cmd;
                bool pushed = PushCBDebugGroup(cmd.Name);
                try
                {
                    if (job.PresentationSurface != null
                        && !ReferenceEquals(currentSurface, job.PresentationSurface))
                    {
                        currentSurface.ClearCurrent();
                        job.PresentationSurface.MakeCurrent();
                        currentSurface = job.PresentationSurface;
                    }
                    RecordFrameStartTimestamp();
                    (_executor ?? throw new InvalidOperationException(
                        "OpenGL command execution requires an initialized graphics device."))
                        .Execute(cmd);
                }
                catch (Exception ex)
                {
                    job.Error = ExceptionDispatchInfo.Capture(ex);
                    if (job.Done == null)
                        Debug.LogError($"Render thread CB '{cmd.Name ?? "<?>"}' execute failed: {ex}");
                }
                finally
                {
                    if (pushed) PopCBDebugGroup();
                    CommandBufferPool.Return(cmd);
                    job.Done?.Set();
                }
            }
        }
        finally
        {
            try { currentSurface.ClearCurrent(); } catch { /* ignore */ }
        }
    }

    private void TryInitializeTimestampQueries()
    {
        if (_gl == null || _timestampQueriesReady)
            return;

        try
        {
            _gl.GenQueries(_timestampQueries.AsSpan());
            if (_timestampQueries[0] == 0)
                return;
            _timestampQueriesReady = true;
        }
        catch
        {
            _timestampQueriesReady = false;
        }
    }

    private void RecordFrameStartTimestamp()
    {
        if (!_timestampFrameRequested || _timestampFrameStarted || _gl == null)
            return;
        try
        {
            PublishTimestampFrame(_timestampFrameSlot);
            if (_timestampFrameSubmitted[_timestampFrameSlot])
            {
                _timestampFrameRequested = false;
                return;
            }
            _gl.QueryCounter(_timestampQueries[_timestampFrameSlot * 2], QueryCounterTarget.Timestamp);
            _timestampFrameStarted = true;
            EnableTimestampCapabilities();
        }
        catch
        {
            _timestampFrameRequested = false;
        }
    }

    private void EnableTimestampCapabilities()
    {
        if (_capabilities.SupportsTimestampQueries)
            return;
        _capabilities = new GraphicsDeviceCapabilities
        {
            MaxTextureSize = _capabilities.MaxTextureSize,
            MaxCubeMapTextureSize = _capabilities.MaxCubeMapTextureSize,
            MaxArrayTextureLayers = _capabilities.MaxArrayTextureLayers,
            MaxFramebufferColorAttachments = _capabilities.MaxFramebufferColorAttachments,
            MaxFramesInFlight = _capabilities.MaxFramesInFlight,
            SupportsCompute = _capabilities.SupportsCompute,
            SupportsGeometryShader = _capabilities.SupportsGeometryShader,
            SupportsTimestampQueries = true,
            HardwareSupportsTimestampQueries = true,
            BackendName = _capabilities.BackendName,
            Baseline = _capabilities.Baseline,
        };
    }

    private void RecordFrameEndTimestamp()
    {
        if (!_timestampFrameRequested || _gl == null)
            return;
        if (!_timestampFrameStarted)
            RecordFrameStartTimestamp();
        if (!_timestampFrameStarted)
            return;
        try
        {
            _gl.QueryCounter(_timestampQueries[_timestampFrameSlot * 2 + 1], QueryCounterTarget.Timestamp);
            _timestampFrameSubmitted[_timestampFrameSlot] = true;
            _timestampFrameRequested = false;
            PublishTimestampFrame((_timestampFrameSlot + 1) & 1);
        }
        catch
        {
            _timestampFrameRequested = false;
        }
    }

    private void PublishTimestampFrame(int slot)
    {
        if (!_timestampFrameSubmitted[slot])
            return;
        uint startQuery = _timestampQueries[slot * 2];
        uint endQuery = _timestampQueries[slot * 2 + 1];
        int available = (int)_gl!.GetQueryObject(endQuery, QueryObjectParameterName.ResultAvailable);
        if (available == 0)
            return;

        ulong start = (ulong)_gl.GetQueryObject(startQuery, QueryObjectParameterName.Result);
        ulong end = (ulong)_gl.GetQueryObject(endQuery, QueryObjectParameterName.Result);
        if (end < start)
            return;
        _lastGpuTimestampFrame = new GpuTimestampFrame(_timestampFrameIds[slot],
            (end - start) / 1_000_000f);
        _hasGpuTimestampFrame = true;
        _timestampFrameSubmitted[slot] = false;
    }

#if DEBUG
    private bool PushCBDebugGroup(string? label)
    {
        if (string.IsNullOrEmpty(label) || _gl == null) return false;
        try
        {
            _gl.PushDebugGroup(Silk.NET.OpenGL.DebugSource.DebugSourceApplication, 0, (uint)label.Length, label);
            return true;
        }
        catch { return false; }
    }

    private void PopCBDebugGroup()
    {
        try { _gl?.PopDebugGroup(); } catch { /* ignore */ }
    }
#else
    private static bool PushCBDebugGroup(string? label) => false;
    private static void PopCBDebugGroup() { }
#endif

    private static void DebugCallback(GLEnum source, GLEnum type, int id, GLEnum severity, int length, nint message, nint userParam)
    {
        string? msg = SilkMarshal.PtrToString(message, NativeStringEncoding.UTF8);
        if (type == GLEnum.DebugTypeError || type == GLEnum.DebugTypeUndefinedBehavior)
            Debug.LogError($"OpenGL Error: {msg}");
        else if (type == GLEnum.DebugTypePerformance || type == GLEnum.DebugTypeMarker || type == GLEnum.DebugTypePortability)
            Debug.LogWarning($"OpenGL Warning: {msg}");
    }

    private PresentationSurfaceId FirstPresentationSurface()
    {
        using Dictionary<PresentationSurfaceId, IWindowSurface>.KeyCollection.Enumerator enumerator =
            _presentationSurfaces.Keys.GetEnumerator();
        return enumerator.MoveNext() ? enumerator.Current : default;
    }

    private void ValidateSharedContext(IWindowSurface surface)
    {
        if (_surface is not SilkWindowSurface primary || surface is not SilkWindowSurface candidate)
            throw new NotSupportedException("OpenGL presentation surfaces require SilkWindowSurface instances.");
        if (!ReferenceEquals(candidate.Window.SharedContext, primary.Window.GLContext))
            throw new NotSupportedException(
                "OpenGL presentation surfaces must be created with the primary window GLContext as SharedContext.");
    }

    private sealed class CBJob
    {
        public CommandBuffer? Cmd;
        public ManualResetEventSlim? Done;
        public ExceptionDispatchInfo? Error;
        public bool IsFrameEnd;
        public IWindowSurface? PresentationSurface;
        public ContextReleaseLease? ContextReleaseLease;
    }

    private sealed class ContextReleaseLease : IDisposable
    {
        public ManualResetEventSlim Released { get; } = new(false);
        public ManualResetEventSlim Resume { get; } = new(false);
        public ManualResetEventSlim Resumed { get; } = new(false);
        public ExceptionDispatchInfo? Error;

        public void Dispose()
        {
            Released.Dispose();
            Resume.Dispose();
            Resumed.Dispose();
        }
    }
}
