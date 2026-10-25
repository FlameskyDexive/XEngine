// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#if XENGINE_ANDROID
using System;
using System.Threading;

using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.Audio;
using XEngine.Vector;

namespace XEngine.Runtime.Platforms.Android;

public readonly record struct AndroidPlatformHostStats(
    long RenderedFrames,
    int SurfaceAttachCount,
    int SurfaceDetachCount,
    bool IsSurfaceAttached,
    bool IsResumed);

/// <summary>
/// Serial lifecycle boundary driven by the generated Activity. All methods must be called from the
/// Activity's looper thread so Vulkan surface ownership cannot race frame submission.
/// </summary>
public sealed class AndroidPlatformHost : IDisposable
{
    private const float DefaultDeltaTime = 1f / 60f;
    private const float MaxDeltaTime = 0.25f;
    private static readonly Color s_clearColor = new(0.025f, 0.035f, 0.045f, 1f);

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly AndroidInputHandler _input;
    private readonly AndroidAudioHandler _audio;
    private AndroidWindowSurface? _surface;
    private AndroidInputCoordinateTransform _inputTransform = AndroidInputCoordinateTransform.Create(1, 1, 1, 1);
    private long _lastFrameTimeNanos;
    private long _renderedFrames;
    private int _attachCount;
    private int _detachCount;
    private bool _loaded;
    private bool _resumed;
    private bool _disposed;
    private bool _ownsAudioContext;
    private int _inputViewWidth;
    private int _inputViewHeight;
    private int _framebufferWidth;
    private int _framebufferHeight;
    private WindowSafeAreaInsets _viewSafeAreaInsets;

    public AndroidPlatformHost(
        Func<string>? clipboardGetter = null,
        Action<string>? clipboardSetter = null,
        Action<bool>? softKeyboard = null)
    {
        _input = new AndroidInputHandler(clipboardGetter, clipboardSetter, softKeyboard);
        _audio = new AndroidAudioHandler(AudioContext.SetPlaybackActive, static () => AudioContext.UnderrunCount);
        Input.PushHandler(_input);
    }

    public AndroidInputHandler InputHandler => _input;

    public AndroidAudioHandler AudioHandler => _audio;

    public AndroidPlatformHostStats Stats => new(
        Interlocked.Read(ref _renderedFrames),
        _attachCount,
        _detachCount,
        _surface != null,
        _resumed);

    public void AttachSurface(nint nativeWindow, int width, int height)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surface != null)
            throw new InvalidOperationException("An Android surface is already attached.");

        UpdateFramebufferSize(width, height);
        var surface = new AndroidWindowSurface(nativeWindow, width, height);
        Window.NotifySurfaceCreated(surface, width, height);
        try
        {
            if (Graphics.BackendDevice is VulkanGraphicsDevice { IsInitialized: true } vulkan)
                vulkan.AttachSurface(surface);
            else
                Graphics.Initialize(debug: false);

            if (Graphics.Device?.Capabilities.Baseline is { } baseline)
                Debug.Log($"[Graphics] capability-baseline {baseline.ToStableLogString()}");
            if (Graphics.BackendDevice is VulkanGraphicsDevice pipelineDevice)
                Debug.Log($"[Graphics] pipeline-cache creates={pipelineDevice.PipelineCreateCount} hits={pipelineDevice.PipelineCacheHitCount}");

            _surface = surface;
            _attachCount++;
            _lastFrameTimeNanos = 0;
            if (!_loaded)
            {
                Graphics.BeginFrame();
                try
                {
                    SubmitBootstrapClear();
                    Window.NotifyLoaded();
                }
                finally
                {
                    Graphics.EndFrameAndWait();
                }
                _loaded = true;
            }
        }
        catch
        {
            Window.NotifySurfaceDestroyed();
            throw;
        }
    }

    public void ResizeSurface(int width, int height)
    {
        VerifyThread();
        if (_surface == null)
            return;
        UpdateFramebufferSize(width, height);
        _surface.UpdateSize(width, height);
        Window.NotifySurfaceChanged(width, height);
    }

    /// <summary>Updates the Android view dimensions used to map touch coordinates into framebuffer pixels.</summary>
    public void UpdateInputViewSize(int width, int height)
    {
        VerifyThread();
        _inputViewWidth = Math.Max(1, width);
        _inputViewHeight = Math.Max(1, height);
        UpdateInputTransform();
        PublishSafeArea();
    }

    /// <summary>Publishes system-bar and cutout insets reported in <c>SurfaceView</c> pixels.</summary>
    public void UpdateWindowInsets(int left, int top, int right, int bottom, int viewWidth, int viewHeight)
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

    public void DetachSurface()
    {
        VerifyThread();
        if (_surface == null)
            return;

        if (Graphics.BackendDevice is VulkanGraphicsDevice vulkan)
            vulkan.DetachSurface();
        Window.NotifySurfaceDestroyed();
        _surface = null;
        _lastFrameTimeNanos = 0;
        _detachCount++;
    }

    public void Resume()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _resumed = true;
        _lastFrameTimeNanos = 0;
        _audio.Resume();
        Window.NotifyFocus(true);
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
        Window.NotifyFocus(false);
    }

    public bool InitializeAudio(int sampleRate, int framesPerBuffer)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool wasInitialized = AudioContext.IsInitialized;
        bool initialized = AudioContext.TryInitialize(
            (uint)Math.Max(8000, sampleRate),
            2,
            (uint)Math.Max(64, framesPerBuffer));
        if (initialized)
            _ = AudioContext.SetPlaybackActive(false);
        _ownsAudioContext = !wasInitialized && initialized;
        _audio.SetNativeBackendReady(initialized);
        return initialized;
    }

    public void AudioFocusChanged(int focusChange)
    {
        VerifyThread();
        _audio.FocusChanged(focusChange);
    }

    public void AudioRouteChanged(int deviceType, int sampleRate, int framesPerBuffer)
    {
        VerifyThread();
        _audio.RouteChanged(deviceType, sampleRate, framesPerBuffer);
    }

    public bool HandleKey(int androidKeyCode, bool pressed)
    {
        VerifyThread();
        return _input.HandleKey(androidKeyCode, pressed);
    }

    public bool GamepadConnected(int androidDeviceId)
    {
        VerifyThread();
        return _input.GamepadConnected(androidDeviceId);
    }

    public void GamepadDisconnected(int androidDeviceId)
    {
        VerifyThread();
        _input.GamepadDisconnected(androidDeviceId);
    }

    public bool HandleGamepadKey(int androidDeviceId, int androidKeyCode, bool pressed)
    {
        VerifyThread();
        return _input.HandleGamepadKey(androidDeviceId, androidKeyCode, pressed);
    }

    public bool HandleGamepadMotion(
        int androidDeviceId,
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
        return _input.HandleGamepadMotion(
            androidDeviceId,
            leftX,
            leftY,
            rightX,
            rightY,
            leftTrigger,
            rightTrigger,
            hatX,
            hatY);
    }

    public bool HandleBack()
    {
        VerifyThread();
        _input.HandleBack();
        return Window.NotifyBackRequested();
    }

    public void FocusChanged(bool focused)
    {
        VerifyThread();
        if (!focused)
            _input.FocusLost();
        Window.NotifyFocus(focused);
    }

    public void TouchBegan(int pointerId, float x, float y, float pressure)
    {
        VerifyThread();
        Float2 position = _inputTransform.Map(x, y);
        _input.TouchBegan(pointerId, position.X, position.Y, pressure);
    }

    public void TouchMoved(int pointerId, float x, float y, float pressure)
    {
        VerifyThread();
        Float2 position = _inputTransform.Map(x, y);
        _input.TouchMoved(pointerId, position.X, position.Y, pressure);
    }

    public void TouchHistory(int pointerId, float x, float y, float pressure, long eventTimeMilliseconds)
    {
        VerifyThread();
        Float2 position = _inputTransform.Map(x, y);
        _input.TouchHistory(pointerId, position.X, position.Y, pressure, eventTimeMilliseconds);
    }

    public void TouchEnded(int pointerId, float x, float y, float pressure, bool canceled = false)
    {
        VerifyThread();
        Float2 position = _inputTransform.Map(x, y);
        _input.TouchEnded(pointerId, position.X, position.Y, pressure, canceled);
    }

    [HotPath]
    public void Tick(long frameTimeNanos)
    {
        VerifyThread();
        if (_disposed || !_resumed || _surface == null)
            return;

        float deltaTime = DefaultDeltaTime;
        if (_lastFrameTimeNanos > 0 && frameTimeNanos > _lastFrameTimeNanos)
        {
            deltaTime = (float)((frameTimeNanos - _lastFrameTimeNanos) / 1_000_000_000d);
            deltaTime = Math.Clamp(deltaTime, 0f, MaxDeltaTime);
        }
        _lastFrameTimeNanos = frameTimeNanos;

        Graphics.BeginFrame();
        try
        {
            SubmitBootstrapClear();
            Window.NotifyFrame(deltaTime);
        }
        finally
        {
            try
            {
                Graphics.EndFrameAndWait();
            }
            finally
            {
                _input.EndFrame();
            }
        }
        Interlocked.Increment(ref _renderedFrames);
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed)
            return;
        Pause();
        DetachSurface();
        Window.NotifyClosing();
        if (_ownsAudioContext)
        {
            AudioContext.Deinitialize();
            _ownsAudioContext = false;
        }
        Graphics.Dispose();
        if (ReferenceEquals(Input.Current, _input))
            Input.PopHandler();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [HotPath]
    private static void SubmitBootstrapClear()
    {
        using CommandBuffer command = Graphics.GetCommandBuffer("Android Surface Clear");
        command.SetRenderTarget(null);
        command.SetViewport(
            0,
            0,
            (uint)Window.InternalWindow.FramebufferSize.X,
            (uint)Window.InternalWindow.FramebufferSize.Y);
        command.SetRasterState(new RasterizerState());
        command.ClearRenderTarget(ClearFlags.Color, s_clearColor);
        Graphics.Submit(command);
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
        _inputTransform = AndroidInputCoordinateTransform.Create(
            _inputViewWidth,
            _inputViewHeight,
            framebufferWidth,
            framebufferHeight);
    }

    private void PublishSafeArea() =>
        Window.NotifySafeAreaInsetsChanged(_inputTransform.MapInsets(_viewSafeAreaInsets));

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "AndroidPlatformHost must be driven from the Activity looper thread.");
        }
    }
}
#endif
