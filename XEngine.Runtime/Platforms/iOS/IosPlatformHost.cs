// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

using XEngine.Runtime.Audio;
using XEngine.Vector;

#if XENGINE_IOS
using XEngine.Runtime.Backends.Metal;
#endif

namespace XEngine.Runtime.Platforms.iOS;

/// <summary>Backend boundary between the UIKit host lifecycle and Metal command submission.</summary>
public interface IIosGraphicsHost : IDisposable
{
    void AttachLayer(IosWindowSurface surface);
    void ResizeLayer(int width, int height);
    void DetachLayer();
    void BeginFrame(int width, int height);
    void EndFrame();
}

#if XENGINE_IOS
/// <summary>Graphics backend that binds the Metal device to a <c>CAMetalLayer*</c>.</summary>
public sealed class IosMetalGraphicsHost : IIosGraphicsHost
{
    private readonly Action<int, int> _resizeSurface;
    private bool _ownsGraphics;

    public IosMetalGraphicsHost()
        : this(Graphics.Resize)
    {
    }

    internal IosMetalGraphicsHost(Action<int, int> resizeSurface)
    {
        _resizeSurface = resizeSurface ?? throw new ArgumentNullException(nameof(resizeSurface));
    }

    public void AttachLayer(IosWindowSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (Graphics.Device is MetalGraphicsDevice { IsInitialized: true } metal)
        {
            metal.Initialize(surface);
            return;
        }

        Graphics.Initialize(debug: false);
        _ownsGraphics = true;
        if (Graphics.Device is MetalGraphicsDevice created)
            created.Initialize(surface);
    }

    public void ResizeLayer(int width, int height) => _resizeSurface(width, height);

    public void DetachLayer()
    {
        if (Graphics.Device is MetalGraphicsDevice metal)
            metal.Initialize(null);
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
#endif

public readonly record struct IosPlatformHostStats(
    long RenderedFrames,
    int PlayerInstanceCount,
    int LayerAttachCount,
    int LayerDetachCount,
    bool IsLayerAttached,
    bool IsResumed);

/// <summary>
/// Serial lifecycle boundary driven by the generated <c>UIViewController</c>. All methods must be
/// called from the UIKit main thread so the <c>CAMetalLayer</c> cannot race frame submission.
/// </summary>
public sealed class IosPlatformHost : IDisposable
{
    private const float DefaultDeltaTime = 1f / 60f;
    private const float MaxDeltaTime = 0.25f;
    private const int DefaultPreferredFramesPerSecond = 60;

    private int _ownerThreadId;
    private readonly IIosGraphicsHost _graphics;
    private readonly IosInputHandler _input;
    private readonly IosAudioHandler _audio;
    private readonly IDisposable? _platformServicesRegistration;
    private bool _ownsAudioContext;
    private IosWindowSurface? _surface;
    private IosInputCoordinateTransform _inputTransform = IosInputCoordinateTransform.Create(1, 1, 1, 1);
    private WindowSafeAreaInsets _viewSafeAreaInsets;
    private WindowSafeAreaInsets _publishedSafeAreaInsets;
    private int _inputViewWidth;
    private int _inputViewHeight;
    private int _framebufferWidth;
    private int _framebufferHeight;
    private long _lastFrameTimeNanos;
    private long _renderedFrames;
    private int _attachCount;
    private int _detachCount;
    private int _playerInstanceCount;
    private int _preferredFramesPerSecond = DefaultPreferredFramesPerSecond;
    private bool _resumed;
    private bool _loaded;
    private bool _disposed;

    public IosPlatformHost(
        IIosGraphicsHost? graphics = null,
        Func<string>? clipboardGetter = null,
        Action<string>? clipboardSetter = null,
        Action<bool>? softKeyboard = null,
        IPlatformServices? platformServices = null,
        Func<bool, bool>? setPlaybackActive = null,
        bool deferOwnerThreadBinding = false)
    {
        _ownerThreadId = deferOwnerThreadBinding ? 0 : Environment.CurrentManagedThreadId;
        _graphics = graphics ?? CreateDefaultGraphics();
        _input = new IosInputHandler(clipboardGetter, clipboardSetter, softKeyboard);
        _audio = new IosAudioHandler(setPlaybackActive ?? AudioContext.SetPlaybackActive);
        Input.PushHandler(_input);
        if (platformServices != null)
            _platformServicesRegistration = PlatformServices.Configure(platformServices);
    }

    public int PreferredFramesPerSecond
    {
        get => _preferredFramesPerSecond;
        set => _preferredFramesPerSecond = Math.Clamp(value, 1, 120);
    }

    public IosInputHandler InputHandler => _input;
    public IosAudioHandler AudioHandler => _audio;
    public WindowSafeAreaInsets SafeAreaInsets => _publishedSafeAreaInsets;

    public IosPlatformHostStats Stats => new(
        Interlocked.Read(ref _renderedFrames),
        _playerInstanceCount,
        _attachCount,
        _detachCount,
        _surface != null,
        _resumed);

    public void AttachLayer(nint metalLayer, int width, int height)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surface != null)
            throw new InvalidOperationException("A CAMetalLayer is already attached.");

        var surface = new IosWindowSurface(metalLayer, width, height);
        try
        {
            UpdateFramebufferSize(width, height);
            NotifySurfaceCreated(surface, width, height);
            _graphics.AttachLayer(surface);
            _surface = surface;
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
            try { _graphics.DetachLayer(); }
            finally
            {
                NotifySurfaceDestroyed();
                _surface = null;
                _lastFrameTimeNanos = 0;
            }
            throw;
        }
    }

    public void ResizeLayer(int width, int height)
    {
        VerifyThread();
        if (_surface == null)
            return;
        UpdateFramebufferSize(width, height);
        _surface.UpdateSize(width, height);
        _graphics.ResizeLayer(_surface.FramebufferSize.Width, _surface.FramebufferSize.Height);
        NotifySurfaceChanged(_surface.FramebufferSize.Width, _surface.FramebufferSize.Height);
    }

    /// <summary>Updates the UIView point size used to map touch coordinates into framebuffer pixels.</summary>
    public void UpdateInputViewSize(int width, int height)
    {
        VerifyThread();
        _inputViewWidth = Math.Max(1, width);
        _inputViewHeight = Math.Max(1, height);
        UpdateInputTransform();
        PublishSafeArea();
    }

    /// <summary>Publishes UIKit <c>safeAreaInsets</c> reported in view points.</summary>
    public void UpdateSafeAreaInsets(int left, int top, int right, int bottom, int viewWidth, int viewHeight)
    {
        VerifyThread();
        _inputViewWidth = Math.Max(1, viewWidth);
        _inputViewHeight = Math.Max(1, viewHeight);
        _viewSafeAreaInsets = new WindowSafeAreaInsets(
            Math.Max(0, left),
            Math.Max(0, top),
            Math.Max(0, right),
            Math.Max(0, bottom));
        UpdateInputTransform();
        PublishSafeArea();
    }

    public void DetachLayer()
    {
        VerifyThread();
        if (_surface == null)
            return;
        try
        {
            _graphics.DetachLayer();
        }
        finally
        {
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

    public void Suspend()
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

    /// <summary>
    /// Initializes miniaudio after the generated host has configured
    /// <c>AVAudioSessionCategoryPlayback</c>. Returns false when the ios-arm64
    /// dylib is missing rather than loading a desktop library.
    /// </summary>
    public bool InitializeAudio(int sampleRate = 44100, int periodSizeInFrames = 2048)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool wasInitialized = AudioContext.IsInitialized;
        bool initialized = AudioContext.TryInitialize(
            (uint)Math.Max(8000, sampleRate),
            2,
            (uint)Math.Max(64, periodSizeInFrames));
        if (initialized)
            _ = AudioContext.SetPlaybackActive(false);
        _ownsAudioContext = !wasInitialized && initialized;
        _audio.SetNativeBackendReady(initialized);
        return initialized;
    }

    public void HandleAudioInterruption(bool began, bool shouldResume)
    {
        VerifyThread();
        if (began)
            _audio.InterruptionBegan();
        else
            _audio.InterruptionEnded(shouldResume);
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
        try
        {
            _graphics.BeginFrame(_surface.FramebufferSize.Width, _surface.FramebufferSize.Height);
            frameBegun = true;
            NotifyFrame(deltaTime);
        }
        finally
        {
            try
            {
                if (frameBegun)
                    _graphics.EndFrame();
            }
            finally
            {
                _input.EndFrame();
            }
        }
        Interlocked.Increment(ref _renderedFrames);
    }

    [HotPath]
    public void DispatchTouch(int pointerId, IosTouchAction action, float x, float y, float pressure)
    {
        VerifyThread();
        if (_disposed)
            return;
        Float2 position = _inputTransform.Map(x, y);
        switch (action)
        {
            case IosTouchAction.Began:
                _input.TouchBegan(pointerId, position.X, position.Y, pressure);
                break;
            case IosTouchAction.Moved:
                _input.TouchMoved(pointerId, position.X, position.Y, pressure);
                break;
            case IosTouchAction.Ended:
                _input.TouchEnded(pointerId, position.X, position.Y, pressure);
                break;
            case IosTouchAction.Canceled:
                _input.TouchEnded(pointerId, position.X, position.Y, pressure, canceled: true);
                break;
        }
    }

    public void TouchHistory(int pointerId, float x, float y, float pressure, long eventTimeMilliseconds)
    {
        VerifyThread();
        if (_disposed)
            return;
        Float2 position = _inputTransform.Map(x, y);
        _input.TouchHistory(pointerId, position.X, position.Y, pressure, eventTimeMilliseconds);
    }

    public bool HandleKey(int hidUsage, bool pressed)
    {
        VerifyThread();
        return !_disposed && _input.HandleKey(hidUsage, pressed);
    }

    public bool GamepadConnected(int deviceId)
    {
        VerifyThread();
        return !_disposed && _input.GamepadConnected(deviceId);
    }

    public void GamepadDisconnected(int deviceId)
    {
        VerifyThread();
        if (_disposed)
            return;
        _input.GamepadDisconnected(deviceId);
    }

    public bool HandleGamepadButton(int deviceId, GamepadButton button, bool pressed)
    {
        VerifyThread();
        return !_disposed && _input.HandleGamepadButton(deviceId, button, pressed);
    }

    public bool HandleGamepadMotion(
        int deviceId,
        float leftX,
        float leftY,
        float rightX,
        float rightY,
        float leftTrigger,
        float rightTrigger,
        float hatX,
        float hatY)
    {
        VerifyThread();
        return !_disposed && _input.HandleGamepadMotion(
            deviceId, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger, hatX, hatY);
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed)
            return;
        Suspend();
        DetachLayer();
        NotifyClosing();
        if (_ownsAudioContext)
        {
            AudioContext.Deinitialize();
            _ownsAudioContext = false;
        }
        _graphics.Dispose();
        _platformServicesRegistration?.Dispose();
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
            throw new InvalidOperationException("IosPlatformHost must be driven from its owner thread.");
    }

    private void UpdateFramebufferSize(int width, int height)
    {
        _framebufferWidth = Math.Max(1, width);
        _framebufferHeight = Math.Max(1, height);
        if (_inputViewWidth == 0 || _inputViewHeight == 0)
        {
            _inputViewWidth = _framebufferWidth;
            _inputViewHeight = _framebufferHeight;
        }
        UpdateInputTransform();
        PublishSafeArea();
    }

    private void UpdateInputTransform()
    {
        int framebufferWidth = _framebufferWidth == 0 ? _inputViewWidth : _framebufferWidth;
        int framebufferHeight = _framebufferHeight == 0 ? _inputViewHeight : _framebufferHeight;
        _inputTransform = IosInputCoordinateTransform.Create(
            _inputViewWidth,
            _inputViewHeight,
            framebufferWidth,
            framebufferHeight);
    }

    private void PublishSafeArea()
    {
        _publishedSafeAreaInsets = _inputTransform.MapInsets(_viewSafeAreaInsets);
#if XENGINE_IOS
        Window.NotifySafeAreaInsetsChanged(_publishedSafeAreaInsets);
#endif
    }

    private static IIosGraphicsHost CreateDefaultGraphics()
    {
#if XENGINE_IOS
        return new IosMetalGraphicsHost();
#else
        return NullGraphicsHost.Instance;
#endif
    }

    private static void NotifySurfaceCreated(IosWindowSurface surface, int width, int height)
    {
#if XENGINE_IOS
        Window.NotifySurfaceCreated(surface, width, height);
#endif
    }

    private static void NotifyLoaded()
    {
#if XENGINE_IOS
        Window.NotifyLoaded();
#endif
    }

    private static void NotifySurfaceChanged(int width, int height)
    {
#if XENGINE_IOS
        Window.NotifySurfaceChanged(width, height);
#endif
    }

    private static void NotifySurfaceDestroyed()
    {
#if XENGINE_IOS
        Window.NotifySurfaceDestroyed();
#endif
    }

    [HotPath]
    private static void NotifyFrame(float deltaTime)
    {
#if XENGINE_IOS
        Window.NotifyFrame(deltaTime);
#endif
    }

    private static void NotifyFocus(bool focused)
    {
#if XENGINE_IOS
        Window.NotifyFocus(focused);
#endif
    }

    private static void NotifyClosing()
    {
#if XENGINE_IOS
        Window.NotifyClosing();
#endif
    }

    private sealed class NullGraphicsHost : IIosGraphicsHost
    {
        internal static readonly NullGraphicsHost Instance = new();

        public void AttachLayer(IosWindowSurface surface) { }
        public void ResizeLayer(int width, int height) { }
        public void DetachLayer() { }
        public void BeginFrame(int width, int height) { }
        public void EndFrame() { }
        public void Dispose() { }
    }
}
