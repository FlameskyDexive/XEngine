// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace XEngine.Runtime.RHI;

/// <summary>
/// Backend-neutral render-thread decorator. Wraps an <see cref="IGraphicsDevice"/> whose
/// <see cref="IGraphicsDevice.Execute"/> translates and submits synchronously on the caller's
/// thread (D3D12, Vulkan, Metal, WebGPU) and moves that work onto one dedicated thread, so the
/// main thread only pays for encoding the byte-stream <see cref="CommandBuffer"/>.
///
/// <para>Ordering: every device operation is marshalled through a single FIFO queue, so the
/// inner device observes exactly the call order it would have seen on a single thread. Frame
/// lifecycle, transient-arena resets and descriptor-pool recycling therefore keep working
/// unchanged; nothing inside the backend needs to know a render thread exists.</para>
///
/// <para>Blocking: calls with a return value (<c>BeginFrame(surface)</c>, <c>Resize</c>,
/// <c>WaitIdle</c>, surface create/destroy) and <c>Execute(wait: true)</c> block until the
/// render thread reaches that job. <c>BeginFrame()</c> and fire-and-forget <c>Execute</c> do
/// not block — that is where the overlap comes from.</para>
///
/// <para>OpenGL is never wrapped: it owns its own render thread because the GL context has
/// hard thread affinity.</para>
/// </summary>
public sealed class RenderThreadGraphicsDevice : IGraphicsDevice, IPresentationSurfaceLifecycle
{
    private enum JobKind : byte
    {
        Execute,
        BeginFrame,
        EndFrame,
        BeginFrameSurface,
        EndFrameSurface,
        Initialize,
        Resize,
        WaitIdle,
        WaitFence,
        CreateSurface,
        DestroySurface,
        ResizeSurface,
        Shutdown,
    }

    private sealed class JobCompletion
    {
        public readonly ManualResetEventSlim Done = new(false);
        public ExceptionDispatchInfo? Error;
        public bool BoolResult;
        public ulong UlongResult;

        public void Rearm()
        {
            Error = null;
            BoolResult = false;
            UlongResult = 0;
            Done.Reset();
        }
    }

    private readonly struct Job
    {
        public readonly JobKind Kind;
        public readonly CommandBuffer? Cmd;
        public readonly JobCompletion? Completion;
        public readonly PresentationSurfaceId Surface;
        public readonly object? Payload;
        public readonly int X;
        public readonly int Y;
        public readonly ulong Fence;

        public Job(
            JobKind kind,
            CommandBuffer? cmd = null,
            JobCompletion? completion = null,
            PresentationSurfaceId surface = default,
            object? payload = null,
            int x = 0,
            int y = 0,
            ulong fence = 0)
        {
            Kind = kind;
            Cmd = cmd;
            Completion = completion;
            Surface = surface;
            Payload = payload;
            X = x;
            Y = y;
            Fence = fence;
        }
    }

    private readonly IGraphicsDevice _inner;
    private readonly IPresentationSurfaceLifecycle? _lifecycle;
    private readonly BlockingCollection<Job> _queue = new();
    private readonly ConcurrentBag<JobCompletion> _completionPool = new();
    private readonly Thread _thread;

    private readonly object _publishGate = new();
    private GpuTimestampFrame _lastTimestampFrame;
    private bool _hasTimestampFrame;
    private readonly GpuCommandTimingBuffer _gpuCommandTimings = new();

    private ExceptionDispatchInfo? _pendingError;
    private volatile bool _threadExited;
    private volatile bool _shutdown;
    private float _lastFrameWaitMs;

    // P7 stage B: when > 0 the main thread returns from EndFrame as soon as the *previous*
    // frame finished, so it records frame N+1 while the render thread still executes frame N.
    private int _maxQueuedFrames;
    private JobCompletion? _queuedFrame;

    /// <summary>Number of command buffers the render thread has translated since creation.</summary>
    private long _executedCommandBuffers;

    public RenderThreadGraphicsDevice(IGraphicsDevice inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (inner is RenderThreadGraphicsDevice)
            throw new ArgumentException("The device is already running on a dedicated render thread.", nameof(inner));
        _lifecycle = inner as IPresentationSurfaceLifecycle;
        _thread = new Thread(RenderThreadLoop)
        {
            IsBackground = true,
            Name = $"XEngine {inner.Backend} Render Thread",
        };
        _thread.Start();
    }

    /// <summary>The wrapped backend device. Diagnostics and platform hosts that need the
    /// concrete backend type unwrap through here instead of casting <c>Graphics.Device</c>.</summary>
    public IGraphicsDevice Inner => _inner;

    /// <inheritdoc />
    public bool SupportsOcclusionQueries =>
        _inner.SupportsOcclusionQueries;

    /// <inheritdoc />
    public bool TryGetOcclusionQueryResult(ulong queryId, out bool passed)
    {
        // The inner device publishes results from its render-thread frame pump; the completed
        // map itself is thread-safe, so a straight pass-through is enough here.
        return _inner.TryGetOcclusionQueryResult(queryId, out passed);
    }

    /// <summary>True when the caller already runs on the render thread (re-entrant submits).</summary>
    public bool IsOnRenderThread => ReferenceEquals(Thread.CurrentThread, _thread);

    /// <summary>Time the main thread spent blocked inside <see cref="EndFrame()"/> last frame.</summary>
    public float LastFrameWaitMs => _lastFrameWaitMs;

    /// <summary>Command buffers translated by the render thread so far (diagnostics / tests).</summary>
    public long ExecutedCommandBuffers => Interlocked.Read(ref _executedCommandBuffers);

    /// <summary>
    /// How many presented frames the main thread may run ahead of the render thread.
    /// <c>0</c> (default) keeps the historical behaviour: <see cref="EndFrame()"/> blocks until
    /// this frame has been submitted and presented. <c>1</c> enables stage-B pipelining:
    /// <see cref="EndFrame()"/> blocks on the <em>previous</em> frame instead, handing the vsync
    /// wait to the render thread and giving the main thread a full frame of budget. Values above
    /// 1 are clamped to 1 (deeper queues only add input latency and unbounded memory).
    /// Must only be changed from the thread that drives the frame loop.
    /// </summary>
    public int MaxQueuedFrames
    {
        get => _maxQueuedFrames;
        set => _maxQueuedFrames = value <= 0 ? 0 : 1;
    }

    /// <summary>True while a presented frame is still owed to the render thread.</summary>
    public bool HasQueuedFrame => _queuedFrame != null;

    // ─────────────────────── Pass-through reads ───────────────────────
    // These are lock-free field reads on every backend, so they never contend with a
    // translate in flight. Routing them through the queue would stall the main thread for
    // the length of the queue, which is exactly what this class exists to avoid.

    public GraphicsBackend Backend => _inner.Backend;

    public GraphicsDeviceCapabilities Capabilities => _inner.Capabilities;

    public bool IsInitialized => _inner.IsInitialized;

    public bool IsDeviceLost => _inner.IsDeviceLost;

    public ulong GetFenceValue() => _inner.GetFenceValue();

    public bool TryGetCompletedGpuTimestampFrame(out GpuTimestampFrame frame)
    {
        lock (_publishGate)
        {
            frame = _lastTimestampFrame;
            return _hasTimestampFrame;
        }
    }

    public bool SupportsGpuCommandTimings => _inner.SupportsGpuCommandTimings;
    public long DroppedGpuCommandTimings => _gpuCommandTimings.Dropped + _inner.DroppedGpuCommandTimings;
    public bool TryDequeueGpuCommandTiming(out GpuCommandTiming timing) => _gpuCommandTimings.TryRead(out timing);

    // ─────────────────────── Frame lifecycle ───────────────────────

    public void Initialize(IWindowSurface? surface)
    {
        if (IsOnRenderThread) { _inner.Initialize(surface); return; }
        RunSync(new Job(JobKind.Initialize, payload: surface));
    }

    public void BeginFrame()
    {
        if (IsOnRenderThread) { _inner.BeginFrame(); return; }
        ThrowPendingError();
        // Fire-and-forget: acquire / frame-fence wait happen on the render thread while the
        // main thread starts encoding this frame's command buffers.
        if (!TryEnqueue(new Job(JobKind.BeginFrame)))
            _inner.BeginFrame();
    }

    public void EndFrame()
    {
        if (IsOnRenderThread) { _inner.EndFrame(); return; }
        if (_shutdown || _threadExited)
        {
            SettleQueuedFrame();
            _inner.EndFrame();
            return;
        }

        JobCompletion completion = RentCompletion();
        if (!TryEnqueue(new Job(JobKind.EndFrame, completion: completion)))
        {
            ReturnCompletion(completion);
            SettleQueuedFrame();
            _inner.EndFrame();
            return;
        }

        // The queue is FIFO, so whichever completion we block on implies every earlier job —
        // including a previously pipelined frame — has already finished.
        JobCompletion? previous = _queuedFrame;
        JobCompletion? waitTarget;
        if (_maxQueuedFrames > 0)
        {
            _queuedFrame = completion;
            waitTarget = previous;
            previous = null;
        }
        else
        {
            _queuedFrame = null;
            waitTarget = completion;
        }

        if (waitTarget == null)
        {
            // First pipelined frame: nothing is owed yet, so the main thread runs straight on.
            _lastFrameWaitMs = 0f;
            return;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        waitTarget.Done.Wait();
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
        _lastFrameWaitMs = (float)(elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        try
        {
            waitTarget.Error?.Throw();
        }
        finally
        {
            ReturnCompletion(waitTarget);
            if (previous != null)
                ReturnCompletion(previous);
        }
    }

    /// <summary>Wait out and retire a pipelined frame before a hard synchronisation point.</summary>
    private void SettleQueuedFrame()
    {
        JobCompletion? queued = _queuedFrame;
        if (queued == null)
            return;
        _queuedFrame = null;
        if (!_threadExited)
            queued.Done.Wait();
        else if (!queued.Done.IsSet)
        {
            ReturnCompletion(queued);
            return;
        }
        try { queued.Error?.Throw(); }
        finally { ReturnCompletion(queued); }
    }

    public void Resize(int width, int height)
    {
        if (IsOnRenderThread) { _inner.Resize(width, height); return; }
        RunSync(new Job(JobKind.Resize, x: width, y: height));
    }

    public void Execute(CommandBuffer commandBuffer, bool wait)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        if (commandBuffer._inPool)
            throw new InvalidOperationException("CommandBuffer has already been submitted (it's in the pool).");
        if (IsOnRenderThread)
        {
            _inner.Execute(commandBuffer, wait);
            return;
        }

        ThrowPendingError();

        // Claim ownership on the calling thread so a `using var cmd = ...` scope closing before
        // the render thread drains the job cannot double-return the buffer to the pool.
        commandBuffer._submitted = true;
        commandBuffer._ownerReleased = true;

        if (_shutdown || _threadExited)
        {
            _inner.Execute(commandBuffer, wait);
            return;
        }

        if (!wait)
        {
            if (!TryEnqueue(new Job(JobKind.Execute, cmd: commandBuffer)))
                _inner.Execute(commandBuffer, wait: false);
            return;
        }

        JobCompletion completion = RentCompletion();
        try
        {
            if (!TryEnqueue(new Job(JobKind.Execute, cmd: commandBuffer, completion: completion, x: 1)))
            {
                _inner.Execute(commandBuffer, wait: true);
                return;
            }
            completion.Done.Wait();
            completion.Error?.Throw();
        }
        finally
        {
            ReturnCompletion(completion);
        }
    }

    public void WaitIdle()
    {
        if (IsOnRenderThread) { _inner.WaitIdle(); return; }
        if (_threadExited) { _inner.WaitIdle(); return; }
        RunSync(new Job(JobKind.WaitIdle));
    }

    public void WaitFence(ulong fenceValue)
    {
        if (IsOnRenderThread) { _inner.WaitFence(fenceValue); return; }
        if (_threadExited) { _inner.WaitFence(fenceValue); return; }
        RunSync(new Job(JobKind.WaitFence, fence: fenceValue));
    }

    // ─────────────────────── Multi-surface lifecycle ───────────────────────

    public bool TryGetPrimaryPresentationSurface(out PresentationSurfaceId surface)
    {
        if (_lifecycle == null) { surface = default; return false; }
        return _lifecycle.TryGetPrimaryPresentationSurface(out surface);
    }

    public bool TryGetPresentationSurface(PresentationSurfaceId surface, out PresentationSurfaceInfo info)
    {
        if (_lifecycle == null) { info = default; return false; }
        return _lifecycle.TryGetPresentationSurface(surface, out info);
    }

    public PresentationSurfaceId CreatePresentationSurface(IWindowSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (_lifecycle == null)
            throw new NotSupportedException($"Backend {_inner.Backend} does not expose presentation surfaces.");
        if (IsOnRenderThread || _threadExited)
            return _lifecycle.CreatePresentationSurface(surface);
        return new PresentationSurfaceId(RunSyncUlong(new Job(JobKind.CreateSurface, payload: surface)));
    }

    public bool DestroyPresentationSurface(PresentationSurfaceId surface)
    {
        if (_lifecycle == null) return false;
        if (IsOnRenderThread || _threadExited)
            return _lifecycle.DestroyPresentationSurface(surface);
        return RunSyncBool(new Job(JobKind.DestroySurface, surface: surface));
    }

    public bool ResizePresentationSurface(PresentationSurfaceId surface, int width, int height)
    {
        if (_lifecycle == null) return false;
        if (IsOnRenderThread || _threadExited)
            return _lifecycle.ResizePresentationSurface(surface, width, height);
        return RunSyncBool(new Job(JobKind.ResizeSurface, surface: surface, x: width, y: height));
    }

    public bool BeginFrame(PresentationSurfaceId surface)
    {
        if (_lifecycle == null) return false;
        if (IsOnRenderThread || _threadExited)
            return _lifecycle.BeginFrame(surface);
        ThrowPendingError();
        return RunSyncBool(new Job(JobKind.BeginFrameSurface, surface: surface));
    }

    public bool EndFrame(PresentationSurfaceId surface)
    {
        if (_lifecycle == null) return false;
        if (IsOnRenderThread || _threadExited)
            return _lifecycle.EndFrame(surface);
        return RunSyncBool(new Job(JobKind.EndFrameSurface, surface: surface));
    }

    // ─────────────────────── Teardown ───────────────────────

    public void Shutdown()
    {
        if (_shutdown)
            return;
        if (IsOnRenderThread || _threadExited)
        {
            _shutdown = true;
            try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { /* already torn down */ }
            _inner.Shutdown();
            return;
        }

        // Drain everything already queued, run Shutdown on the render thread (so the backend
        // tears its resources down on the thread that created them), then retire the thread.
        try { SettleQueuedFrame(); }
        catch (Exception ex) { Debug.LogError($"Pipelined frame failed during shutdown: {ex}"); }
        try { RunSync(new Job(JobKind.Shutdown)); }
        catch (Exception ex) { Debug.LogError($"Render thread shutdown failed: {ex}"); }
        _shutdown = true;
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { /* already torn down */ }
        if (!_thread.Join(TimeSpan.FromSeconds(10)))
            Debug.LogWarning("Render thread did not exit within 10s; abandoning it.");
    }

    public void Dispose()
    {
        Shutdown();
        _inner.Dispose();
        try { _queue.Dispose(); } catch (Exception) { /* racing producers */ }
    }

    // ─────────────────────── Queue plumbing ───────────────────────

    private bool TryEnqueue(Job job)
    {
        try
        {
            _queue.Add(job);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false; // CompleteAdding raced with us.
        }
    }

    private void RunSync(Job template)
    {
        JobCompletion completion = RentCompletion();
        try
        {
            if (!TryEnqueue(WithCompletion(template, completion)))
            {
                RunInline(template);
                return;
            }
            completion.Done.Wait();
            completion.Error?.Throw();
        }
        finally
        {
            ReturnCompletion(completion);
        }
    }

    private bool RunSyncBool(Job template)
    {
        JobCompletion completion = RentCompletion();
        try
        {
            if (!TryEnqueue(WithCompletion(template, completion)))
                return RunInlineBool(template);
            completion.Done.Wait();
            completion.Error?.Throw();
            return completion.BoolResult;
        }
        finally
        {
            ReturnCompletion(completion);
        }
    }

    private ulong RunSyncUlong(Job template)
    {
        JobCompletion completion = RentCompletion();
        try
        {
            if (!TryEnqueue(WithCompletion(template, completion)))
                return RunInlineUlong(template);
            completion.Done.Wait();
            completion.Error?.Throw();
            return completion.UlongResult;
        }
        finally
        {
            ReturnCompletion(completion);
        }
    }

    private static Job WithCompletion(Job job, JobCompletion completion) => new(
        job.Kind, job.Cmd, completion, job.Surface, job.Payload, job.X, job.Y, job.Fence);

    private JobCompletion RentCompletion()
    {
        if (_completionPool.TryTake(out JobCompletion? completion))
        {
            completion.Rearm();
            return completion;
        }
        return new JobCompletion();
    }

    private void ReturnCompletion(JobCompletion completion)
    {
        completion.Error = null;
        _completionPool.Add(completion);
    }

    private void ThrowPendingError()
    {
        ExceptionDispatchInfo? pending = Interlocked.Exchange(ref _pendingError, null);
        pending?.Throw();
    }

    private void RecordAsyncError(Job job, Exception ex)
    {
        Debug.LogError($"Render thread job '{job.Kind}' failed: {ex}");
        // Surface it on the next main-thread entry point so device-loss handling (which catches
        // InvalidOperationException around the render body) still fires instead of the frame
        // silently continuing against a dead device.
        Interlocked.CompareExchange(ref _pendingError, ExceptionDispatchInfo.Capture(ex), null);
    }

    private void RenderThreadLoop()
    {
        try
        {
            while (true)
            {
                Job job;
                try { job = _queue.Take(); }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }   // CompleteAdding + drained

                JobCompletion? completion = job.Completion;
                try
                {
                    RunOnRenderThread(job, completion);
                }
                catch (Exception ex)
                {
                    if (completion != null)
                        completion.Error = ExceptionDispatchInfo.Capture(ex);
                    else
                        RecordAsyncError(job, ex);
                }
                finally
                {
                    completion?.Done.Set();
                }
            }
        }
        catch (Exception fatal)
        {
            Debug.LogError($"Render thread terminated: {fatal}");
        }
        finally
        {
            _threadExited = true;
            DrainAbandonedJobs();
        }
    }

    /// <summary>Release anyone blocked on a job the loop will never reach.</summary>
    private void DrainAbandonedJobs()
    {
        while (_queue.TryTake(out Job job))
        {
            if (job.Cmd != null)
                CommandBufferPool.Return(job.Cmd);
            if (job.Completion != null)
            {
                job.Completion.Error = ExceptionDispatchInfo.Capture(
                    new InvalidOperationException("The graphics render thread exited before this job ran."));
                job.Completion.Done.Set();
            }
        }
    }

    private void RunOnRenderThread(Job job, JobCompletion? completion)
    {
        switch (job.Kind)
        {
            case JobKind.Execute:
                Interlocked.Increment(ref _executedCommandBuffers);
                _inner.Execute(job.Cmd!, wait: job.X != 0);
                break;
            case JobKind.BeginFrame:
                _inner.BeginFrame();
                break;
            case JobKind.EndFrame:
                _inner.EndFrame();
                PublishTimestampFrame();
                break;
            case JobKind.BeginFrameSurface:
                if (completion != null)
                    completion.BoolResult = _lifecycle!.BeginFrame(job.Surface);
                else
                    _lifecycle!.BeginFrame(job.Surface);
                break;
            case JobKind.EndFrameSurface:
                if (completion != null)
                    completion.BoolResult = _lifecycle!.EndFrame(job.Surface);
                else
                    _lifecycle!.EndFrame(job.Surface);
                PublishTimestampFrame();
                break;
            case JobKind.Initialize:
                _inner.Initialize(job.Payload as IWindowSurface);
                break;
            case JobKind.Resize:
                _inner.Resize(job.X, job.Y);
                break;
            case JobKind.WaitIdle:
                _inner.WaitIdle();
                break;
            case JobKind.WaitFence:
                _inner.WaitFence(job.Fence);
                break;
            case JobKind.CreateSurface:
                {
                    PresentationSurfaceId id = _lifecycle!.CreatePresentationSurface((IWindowSurface)job.Payload!);
                    if (completion != null)
                        completion.UlongResult = id.Value;
                    break;
                }
            case JobKind.DestroySurface:
                {
                    bool ok = _lifecycle!.DestroyPresentationSurface(job.Surface);
                    if (completion != null)
                        completion.BoolResult = ok;
                    break;
                }
            case JobKind.ResizeSurface:
                {
                    bool ok = _lifecycle!.ResizePresentationSurface(job.Surface, job.X, job.Y);
                    if (completion != null)
                        completion.BoolResult = ok;
                    break;
                }
            case JobKind.Shutdown:
                _inner.Shutdown();
                break;
        }
    }

    private void PublishTimestampFrame()
    {
        while (_inner.TryDequeueGpuCommandTiming(out GpuCommandTiming timing))
            _gpuCommandTimings.Publish(in timing);
        if (!_inner.TryGetCompletedGpuTimestampFrame(out GpuTimestampFrame frame))
            return;
        lock (_publishGate)
        {
            _lastTimestampFrame = frame;
            _hasTimestampFrame = true;
        }
    }

    // Fallbacks used when the queue is already closed (shutdown races).

    private void RunInline(Job job)
    {
        switch (job.Kind)
        {
            case JobKind.Execute: _inner.Execute(job.Cmd!, wait: job.X != 0); break;
            case JobKind.BeginFrame: _inner.BeginFrame(); break;
            case JobKind.EndFrame: _inner.EndFrame(); break;
            case JobKind.Initialize: _inner.Initialize(job.Payload as IWindowSurface); break;
            case JobKind.Resize: _inner.Resize(job.X, job.Y); break;
            case JobKind.WaitIdle: _inner.WaitIdle(); break;
            case JobKind.WaitFence: _inner.WaitFence(job.Fence); break;
            case JobKind.Shutdown: _inner.Shutdown(); break;
            default: RunInlineBool(job); break;
        }
    }

    private bool RunInlineBool(Job job) => job.Kind switch
    {
        JobKind.BeginFrameSurface => _lifecycle?.BeginFrame(job.Surface) ?? false,
        JobKind.EndFrameSurface => _lifecycle?.EndFrame(job.Surface) ?? false,
        JobKind.DestroySurface => _lifecycle?.DestroyPresentationSurface(job.Surface) ?? false,
        JobKind.ResizeSurface => _lifecycle?.ResizePresentationSurface(job.Surface, job.X, job.Y) ?? false,
        _ => false,
    };

    private ulong RunInlineUlong(Job job) => job.Kind switch
    {
        JobKind.CreateSurface => (_lifecycle?.CreatePresentationSurface((IWindowSurface)job.Payload!) ?? default).Value,
        _ => 0,
    };
}
