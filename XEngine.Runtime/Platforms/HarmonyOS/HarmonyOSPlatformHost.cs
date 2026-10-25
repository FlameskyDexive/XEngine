// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

using XEngine.Runtime.Backends.Vulkan;

namespace XEngine.Runtime.Platforms.HarmonyOS;

/// <summary>Backend boundary between the native host lifecycle and Vulkan command submission.</summary>
public interface IHarmonyOSGraphicsHost : IDisposable
{
    void AttachSurface(HarmonyOSWindowSurface surface);
    void ResizeSurface(int width, int height);
    void DetachSurface();
    void BeginFrame(int width, int height);
    void EndFrame();
}

/// <summary>Graphics backend that attaches the existing Vulkan device to a native HarmonyOS surface.</summary>
public sealed class HarmonyOSGraphicsHost : IHarmonyOSGraphicsHost
{
    private readonly Action<int, int> _resizeSurface;
    private bool _ownsGraphics;

    public HarmonyOSGraphicsHost()
        : this(Graphics.Resize)
    {
    }

    internal HarmonyOSGraphicsHost(Action<int, int> resizeSurface)
    {
        _resizeSurface = resizeSurface ?? throw new ArgumentNullException(nameof(resizeSurface));
    }

    public void AttachSurface(HarmonyOSWindowSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (Graphics.BackendDevice is VulkanGraphicsDevice { IsInitialized: true } vulkan)
        {
            vulkan.AttachSurface(surface);
            return;
        }

        Graphics.Initialize(debug: false);
        _ownsGraphics = true;
    }

    public void ResizeSurface(int width, int height) => _resizeSurface(width, height);

    public void DetachSurface()
    {
        if (Graphics.BackendDevice is VulkanGraphicsDevice vulkan)
            vulkan.DetachSurface();
    }

    [HotPath]
    public void BeginFrame(int width, int height) => Graphics.BeginFrame();

    [HotPath]
    public void EndFrame() => Graphics.EndFrameAndWait();

    public void Dispose()
    {
        if (_ownsGraphics)
        {
            Graphics.Dispose();
            _ownsGraphics = false;
        }
    }
}

public readonly record struct HarmonyOSPlatformHostStats(
    long RenderedFrames,
    int PlayerInstanceCount,
    int SurfaceAttachCount,
    int SurfaceDetachCount,
    bool IsSurfaceAttached,
    bool IsResumed);

/// <summary>
/// Single-owner lifecycle coordinator for the generated HarmonyOS native host. The host keeps one
/// managed player instance while XComponent surface objects are detached and recreated.
/// </summary>
public sealed class HarmonyOSPlatformHost : IDisposable
{
    private const float DefaultDeltaTime = 1f / 60f;
    private const float MaxDeltaTime = 0.25f;

    private int _ownerThreadId;
    private readonly IHarmonyOSGraphicsHost _graphics;
    private readonly HarmonyOSInputHandler _input;
    private readonly HarmonyOSAudioHandler _audio;
    private readonly Func<nint, nint, nint>? _createVulkanSurface;
    private HarmonyOSWindowSurface? _surface;
    private long _lastFrameTimeNanos;
    private long _renderedFrames;
    private int _attachCount;
    private int _detachCount;
    private int _playerInstanceCount;
    private bool _resumed;
    private bool _loaded;
    private bool _disposed;

    public HarmonyOSPlatformHost(
        IHarmonyOSGraphicsHost? graphics = null,
        Func<nint, nint, nint>? createVulkanSurface = null,
        Func<string>? clipboardGetter = null,
        Action<string>? clipboardSetter = null,
        Action<bool>? softKeyboard = null,
        bool deferOwnerThreadBinding = false)
    {
        _ownerThreadId = deferOwnerThreadBinding ? 0 : Environment.CurrentManagedThreadId;
        _graphics = graphics ?? NullGraphicsHost.Instance;
        _createVulkanSurface = createVulkanSurface;
        _input = new HarmonyOSInputHandler(clipboardGetter, clipboardSetter, softKeyboard);
        _audio = new HarmonyOSAudioHandler();
        Input.PushHandler(_input);
    }

    public HarmonyOSInputHandler InputHandler => _input;
    public HarmonyOSAudioHandler AudioHandler => _audio;
    public HarmonyOSPlatformHostStats Stats => new(
        Interlocked.Read(ref _renderedFrames),
        _playerInstanceCount,
        _attachCount,
        _detachCount,
        _surface != null,
        _resumed);

    public void AttachSurface(nint nativeWindow, int width, int height)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surface != null)
            throw new InvalidOperationException("A HarmonyOS surface is already attached.");

        var surface = new HarmonyOSWindowSurface(nativeWindow, width, height, _createVulkanSurface);
        try
        {
            NotifySurfaceCreated(surface, width, height);
            _graphics.AttachSurface(surface);
            _surface = surface;
            _audio.SetHostReady(true);
            if (!_loaded)
            {
                NotifyLoaded();
                _loaded = true;
            }
            _attachCount++;
            _lastFrameTimeNanos = 0;
            if (_playerInstanceCount == 0)
                _playerInstanceCount = 1;
        }
        catch
        {
            try { _graphics.DetachSurface(); }
            finally
            {
                _audio.SetHostReady(false);
                NotifySurfaceDestroyed();
                _surface = null;
                _lastFrameTimeNanos = 0;
            }
            throw;
        }
    }

    public void ResizeSurface(int width, int height)
    {
        VerifyThread();
        if (_surface == null)
            return;
        _surface.UpdateSize(width, height);
        _graphics.ResizeSurface(_surface.FramebufferSize.Width, _surface.FramebufferSize.Height);
        NotifySurfaceChanged(_surface.FramebufferSize.Width, _surface.FramebufferSize.Height);
    }

    public void DetachSurface()
    {
        VerifyThread();
        if (_surface == null)
            return;
        try
        {
            _graphics.DetachSurface();
        }
        finally
        {
            _audio.SetHostReady(false);
            NotifySurfaceDestroyed();
            _surface = null;
            _lastFrameTimeNanos = 0;
            _detachCount++;
        }
    }

    public void Resume()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _resumed = true;
        _lastFrameTimeNanos = 0;
        _audio.Resume();
        NotifyFocus(true);
    }

    public void Pause()
    {
        VerifyThread();
        if (_disposed)
            return;
        _resumed = false;
        _lastFrameTimeNanos = 0;
        _audio.Pause();
        _input.FocusLost();
        NotifyFocus(false);
    }

    [HotPath]
    public void DispatchTouch(
        int pointerId,
        HarmonyOSTouchAction action,
        float x,
        float y,
        float pressure)
    {
        VerifyThread();
        if (_disposed)
            return;

        switch (action)
        {
            case HarmonyOSTouchAction.Down:
                _input.TouchBegan(pointerId, x, y, pressure);
                break;
            case HarmonyOSTouchAction.Up:
                _input.TouchEnded(pointerId, x, y, pressure);
                break;
            case HarmonyOSTouchAction.Move:
                _input.TouchMoved(pointerId, x, y, pressure);
                break;
            case HarmonyOSTouchAction.Cancel:
                _input.TouchEnded(pointerId, x, y, pressure, canceled: true);
                break;
        }
    }

    [HotPath]
    public bool HandleKey(int harmonyOsKeyCode, bool pressed)
    {
        VerifyThread();
        if (_disposed)
            return false;
        bool handled = _input.HandleKey(harmonyOsKeyCode, pressed);
        return harmonyOsKeyCode == 2 && pressed ? NotifyBackRequested() || handled : handled;
    }

    [HotPath]
    public bool HandleMouse(int action, int harmonyOsButton, float x, float y)
    {
        VerifyThread();
        if (_disposed)
            return false;
        return _input.HandleMouse((HarmonyOSMouseAction)action, harmonyOsButton, x, y);
    }

    [HotPath]
    public bool HandleMouseWheel(float delta)
    {
        VerifyThread();
        if (_disposed)
            return false;
        return _input.HandleMouseWheel(delta);
    }

    public void FocusChanged(bool focused)
    {
        VerifyThread();
        if (_disposed)
            return;
        if (!focused)
            _input.FocusLost();
        NotifyFocus(focused);
    }

    [HotPath]
    public void Tick(long frameTimeNanos)
    {
        VerifyThread();
        if (_disposed || !_resumed || _surface == null)
            return;

        float deltaTime = DefaultDeltaTime;
        if (frameTimeNanos > _lastFrameTimeNanos)
        {
            if (_lastFrameTimeNanos > 0)
            {
                deltaTime = (float)((frameTimeNanos - _lastFrameTimeNanos) / 1_000_000_000d);
                deltaTime = Math.Clamp(deltaTime, 0f, MaxDeltaTime);
            }
            _lastFrameTimeNanos = frameTimeNanos;
        }

        bool frameBegun = false;
        long phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _graphics.BeginFrame(_surface.FramebufferSize.Width, _surface.FramebufferSize.Height);
            LastBeginFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            frameBegun = true;
            NotifyFrame(deltaTime);
        }
        finally
        {
            try
            {
                if (frameBegun)
                {
                    phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    _graphics.EndFrame();
                    LastEndFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
                }
            }
            finally
            {
                _input.EndFrame();
            }
        }
        Interlocked.Increment(ref _renderedFrames);
    }

    internal static double LastBeginFrameMs { get; private set; }
    internal static double LastEndFrameMs { get; private set; }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed)
            return;
        Pause();
        DetachSurface();
        NotifyClosing();
        _graphics.Dispose();
        if (ReferenceEquals(Input.Current, _input))
            Input.PopHandler();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void VerifyThread()
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        int ownerThreadId = Volatile.Read(ref _ownerThreadId);
        if (ownerThreadId == 0)
        {
            int observed = Interlocked.CompareExchange(ref _ownerThreadId, currentThreadId, 0);
            ownerThreadId = observed == 0 ? currentThreadId : observed;
        }
        if (currentThreadId != ownerThreadId)
            throw new InvalidOperationException("HarmonyOSPlatformHost must be driven from its owner thread.");
    }

    private static void NotifySurfaceCreated(HarmonyOSWindowSurface surface, int width, int height)
    {
#if XENGINE_HARMONYOS
        Window.NotifySurfaceCreated(surface, width, height);
#endif
    }

    private static void NotifyLoaded()
    {
#if XENGINE_HARMONYOS
        Window.NotifyLoaded();
#endif
    }

    private static void NotifySurfaceChanged(int width, int height)
    {
#if XENGINE_HARMONYOS
        Window.NotifySurfaceChanged(width, height);
#endif
    }

    private static void NotifySurfaceDestroyed()
    {
#if XENGINE_HARMONYOS
        Window.NotifySurfaceDestroyed();
#endif
    }

    [HotPath]
    private static void NotifyFrame(float deltaTime)
    {
#if XENGINE_HARMONYOS
        Window.NotifyFrame(deltaTime);
#endif
    }

    private static void NotifyFocus(bool focused)
    {
#if XENGINE_HARMONYOS
        Window.NotifyFocus(focused);
#endif
    }

    private static bool NotifyBackRequested()
    {
#if XENGINE_HARMONYOS
        return Window.NotifyBackRequested();
#else
        return false;
#endif
    }

    private static void NotifyClosing()
    {
#if XENGINE_HARMONYOS
        Window.NotifyClosing();
#endif
    }

    private sealed class NullGraphicsHost : IHarmonyOSGraphicsHost
    {
        internal static readonly NullGraphicsHost Instance = new();

        public void AttachSurface(HarmonyOSWindowSurface surface) { }
        public void ResizeSurface(int width, int height) { }
        public void DetachSurface() { }
        public void BeginFrame(int width, int height) { }
        public void EndFrame() { }
        public void Dispose() { }
    }
}
