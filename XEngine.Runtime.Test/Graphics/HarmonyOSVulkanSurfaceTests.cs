// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Platforms.HarmonyOS;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class HarmonyOSVulkanSurfaceTests
{
    [Fact]
    public void Tick_BracketsManagedFrameWithOneGraphicsFrame()
    {
        var graphics = new RecordingGraphicsHost();
        using var host = new HarmonyOSPlatformHost(graphics);
        host.AttachSurface((nint)1, 1280, 720);
        host.Resume();

        host.Tick(1_000_000_000);

        Assert.Equal(1, graphics.BeginFrameCount);
        Assert.Equal(1, graphics.EndFrameCount);
        Assert.Equal((1280, 720), graphics.LastFrameSize);
    }

    [Fact]
    public void Tick_KeepsTheFrameTimestampBaselineMonotonicWhenFramesArriveOutOfOrder()
    {
        using var host = new HarmonyOSPlatformHost();
        host.AttachSurface((nint)1, 1280, 720);
        host.Resume();

        host.Tick(1_000_000_000);
        host.Tick(900_000_000);

        var timestampField = typeof(HarmonyOSPlatformHost).GetField(
            "_lastFrameTimeNanos",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(timestampField);
        Assert.Equal(1_000_000_000L, Assert.IsType<long>(timestampField!.GetValue(host)));
    }

    [Fact]
    public void WindowSurface_DelegatesVulkanCreationAndTracksTheNativeWindowSize()
    {
        nint receivedInstance = 0;
        nint receivedWindow = 0;
        var surface = new HarmonyOSWindowSurface(
            (nint)42,
            1280,
            720,
            (instance, window) =>
            {
                receivedInstance = instance;
                receivedWindow = window;
                return (nint)99;
            });

        surface.UpdateSize(720, 1280);
        nint nativeSurface = surface.CreateVulkanSurface((nint)7);

        Assert.Equal((nint)99, nativeSurface);
        Assert.Equal((nint)7, receivedInstance);
        Assert.Equal((nint)42, receivedWindow);
        Assert.Equal((720, 1280), surface.FramebufferSize);
    }

    [Fact]
    public void WindowSurface_DeclaresTheOhosVulkanInstanceExtension()
    {
        // The surface owns its WSI contract so the device negotiates extensions up front instead
        // of guessing the host OS (plan §4.2). This must match the literal used by
        // VulkanGraphicsDevice.GetRequiredInstanceExtensions.
        var surface = new HarmonyOSWindowSurface((nint)1, 64, 64);

        Assert.Contains(HarmonyOSWindowSurface.VulkanInstanceExtension, surface.RequiredVulkanInstanceExtensions);
        Assert.Equal("VK_OHOS_surface", HarmonyOSWindowSurface.VulkanInstanceExtension);
    }

    [Fact]
    public void SurfaceDetachThenAttach_RecreatesTheSurfaceWithoutCreatingAnotherPlayer()
    {
        using var host = new HarmonyOSPlatformHost();

        host.AttachSurface((nint)1, 1280, 720);
        host.DetachSurface();
        host.AttachSurface((nint)2, 720, 1280);
        host.Resume();
        host.Tick(1_000_000_000);

        HarmonyOSPlatformHostStats stats = host.Stats;
        Assert.Equal(1, stats.PlayerInstanceCount);
        Assert.Equal(2, stats.SurfaceAttachCount);
        Assert.Equal(1, stats.SurfaceDetachCount);
        Assert.True(stats.IsSurfaceAttached);
        Assert.True(stats.IsResumed);
        Assert.Equal(1, stats.RenderedFrames);
    }

    [Fact]
    public void SurfaceLifecycle_ControlsAudioHostReadiness()
    {
        using var host = new HarmonyOSPlatformHost();

        host.AttachSurface((nint)1, 1280, 720);
        host.Resume();
        host.AudioHandler.FocusChanged(true);

        Assert.True(host.AudioHandler.Stats.IsHostReady);
        Assert.True(host.AudioHandler.Stats.IsPlaybackRunning);

        host.DetachSurface();

        Assert.False(host.AudioHandler.Stats.IsHostReady);
        Assert.False(host.AudioHandler.Stats.IsPlaybackRunning);
    }

    [Fact]
    public void ResizeSurface_ForwardsTheNormalizedFramebufferSizeToGraphics()
    {
        var graphics = new RecordingGraphicsHost();
        using var host = new HarmonyOSPlatformHost(graphics);
        host.AttachSurface((nint)1, 1280, 720);

        host.ResizeSurface(0, 1080);

        Assert.Equal(1, graphics.ResizeSurfaceCount);
        Assert.Equal((1, 1080), graphics.LastResizeSize);
    }

    [Fact]
    public void GraphicsHost_ResizeSurface_UsesTheRhiResizePath()
    {
        int resizeCount = 0;
        (int Width, int Height) resized = default;
        using var graphics = new HarmonyOSGraphicsHost((width, height) =>
        {
            resizeCount++;
            resized = (width, height);
        });

        graphics.ResizeSurface(720, 1280);

        Assert.Equal(1, resizeCount);
        Assert.Equal((720, 1280), resized);
    }

    [Fact]
    public void DispatchTouch_MapsNativeActionsToTheInputHandler()
    {
        using var host = new HarmonyOSPlatformHost();

        host.DispatchTouch(7, HarmonyOSTouchAction.Down, 10f, 20f, 0.5f);
        Assert.Equal(TouchPhase.Began, host.InputHandler.GetTouch(0).Phase);
        host.InputHandler.EndFrame();

        host.DispatchTouch(7, HarmonyOSTouchAction.Move, 30f, 40f, 0.75f);
        Assert.Equal(TouchPhase.Moved, host.InputHandler.GetTouch(0).Phase);

        host.DispatchTouch(7, HarmonyOSTouchAction.Up, 50f, 60f, 1f);
        Assert.Equal(TouchPhase.Ended, host.InputHandler.GetTouch(0).Phase);

        host.InputHandler.EndFrame();
        host.DispatchTouch(8, HarmonyOSTouchAction.Down, 70f, 80f, 0.25f);
        host.InputHandler.EndFrame();
        host.DispatchTouch(8, HarmonyOSTouchAction.Cancel, 70f, 80f, 0.25f);
        Assert.Equal(TouchPhase.Canceled, host.InputHandler.GetTouch(0).Phase);
    }

    [Fact]
    public void HostRejectsLifecycleCallsFromAnotherThread()
    {
        using var host = new HarmonyOSPlatformHost();
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                host.Resume();
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        });

        thread.Start();
        thread.Join();

        InvalidOperationException error = Assert.IsType<InvalidOperationException>(captured);
        Assert.Contains("owner thread", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredOwnerBinding_AllowsTheFirstPlayerCallbackThreadToOwnTheHost()
    {
        var host = new HarmonyOSPlatformHost(deferOwnerThreadBinding: true);
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                host.AttachSurface((nint)1, 320, 240);
                host.Resume();
                host.Tick(1_000_000_000);
                host.Dispose();
            }
            catch (Exception exception)
            {
                captured = exception;
            }
        });

        thread.Start();
        thread.Join();

        Assert.Null(captured);
    }

    [Fact]
    public void HandleKeyAndFocusChanged_ForwardPhysicalKeyboardStateToTheInputHandler()
    {
        using var host = new HarmonyOSPlatformHost();

        Assert.True(host.HandleKey(2017, true));
        Assert.True(host.InputHandler.GetKey(KeyCode.A));

        host.FocusChanged(false);

        Assert.False(host.InputHandler.GetKey(KeyCode.A));
        Assert.True(host.InputHandler.GetKeyUp(KeyCode.A));
    }

    [Fact]
    public void HandleMouse_ForwardsPhysicalMouseStateToTheInputHandler()
    {
        using var host = new HarmonyOSPlatformHost();

        Assert.True(host.HandleMouse((int)HarmonyOSMouseAction.Press, 4, 8f, 9f));
        Assert.True(host.InputHandler.GetMouseButton((int)MouseButton.Middle));

        host.FocusChanged(false);

        Assert.False(host.InputHandler.GetMouseButton((int)MouseButton.Middle));
        Assert.True(host.InputHandler.GetMouseButtonUp((int)MouseButton.Middle));
    }

    [Fact]
    public void HandleMouseWheel_ForwardsFiniteWheelDeltaToTheInputHandler()
    {
        using var host = new HarmonyOSPlatformHost();

        Assert.True(host.HandleMouseWheel(2.5f));
        Assert.Equal(2.5f, host.InputHandler.MouseWheelDelta);
        Assert.False(host.HandleMouseWheel(float.PositiveInfinity));
    }

    private sealed class RecordingGraphicsHost : IHarmonyOSGraphicsHost
    {
        public int BeginFrameCount { get; private set; }
        public int EndFrameCount { get; private set; }
        public int ResizeSurfaceCount { get; private set; }
        public (int Width, int Height) LastFrameSize { get; private set; }
        public (int Width, int Height) LastResizeSize { get; private set; }

        public void AttachSurface(HarmonyOSWindowSurface surface) { }
        public void ResizeSurface(int width, int height)
        {
            ResizeSurfaceCount++;
            LastResizeSize = (width, height);
        }
        public void DetachSurface() { }

        public void BeginFrame(int width, int height)
        {
            BeginFrameCount++;
            LastFrameSize = (width, height);
        }

        public void EndFrame() => EndFrameCount++;
        public void Dispose() { }
    }
}
