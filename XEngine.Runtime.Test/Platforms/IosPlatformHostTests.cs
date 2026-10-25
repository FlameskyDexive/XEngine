// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Platforms.iOS;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class IosPlatformHostTests
{
    [Fact]
    public void Tick_BracketsManagedFrameWithOneGraphicsFrame()
    {
        var graphics = new RecordingGraphicsHost();
        using var host = new IosPlatformHost(graphics);
        host.AttachLayer((nint)1, 1280, 720);
        host.Resume();

        host.Tick(1_000_000_000);

        Assert.Equal(1, graphics.BeginFrameCount);
        Assert.Equal(1, graphics.EndFrameCount);
        Assert.Equal((1280, 720), graphics.LastFrameSize);
        Assert.Equal(60, host.PreferredFramesPerSecond);
    }

    [Fact]
    public void LayerDetachThenAttach_RecreatesTheSurfaceWithoutCreatingAnotherPlayer()
    {
        using var host = new IosPlatformHost();
        host.AttachLayer((nint)1, 1280, 720);
        host.DetachLayer();
        host.AttachLayer((nint)2, 720, 1280);
        host.Resume();
        host.Tick(1_000_000_000);

        IosPlatformHostStats stats = host.Stats;
        Assert.Equal(1, stats.PlayerInstanceCount);
        Assert.Equal(2, stats.LayerAttachCount);
        Assert.Equal(1, stats.LayerDetachCount);
        Assert.True(stats.IsLayerAttached);
        Assert.True(stats.IsResumed);
        Assert.Equal(1, stats.RenderedFrames);
    }

    [Fact]
    public void ResizeLayer_ForwardsTheNormalizedFramebufferSizeToGraphics()
    {
        var graphics = new RecordingGraphicsHost();
        using var host = new IosPlatformHost(graphics);
        host.AttachLayer((nint)1, 1280, 720);

        host.ResizeLayer(0, 1080);

        Assert.Equal(1, graphics.ResizeLayerCount);
        Assert.Equal((1, 1080), graphics.LastResizeSize);
    }

    [Fact]
    public void Suspend_StopsTicksUntilResume()
    {
        var graphics = new RecordingGraphicsHost();
        using var host = new IosPlatformHost(graphics);
        host.AttachLayer((nint)1, 64, 64);
        host.Resume();
        host.Tick(1_000_000_000);
        host.Suspend();
        host.Tick(2_000_000_000);

        Assert.Equal(1, graphics.BeginFrameCount);
        Assert.False(host.Stats.IsResumed);
        Assert.False(host.AudioHandler.IsPlaybackRunning);
        Assert.False(host.AudioHandler.IsAppResumed);
    }

    [Fact]
    public void DispatchTouch_MapsViewPointsToFramebufferPixels()
    {
        using var host = new IosPlatformHost();
        host.AttachLayer((nint)1, 1080, 2400);
        host.UpdateInputViewSize(540, 1200);
        host.DispatchTouch(7, IosTouchAction.Began, 270, 600, 0.5f);

        Assert.Equal(1, host.InputHandler.TouchCount);
        Assert.Equal(new XEngine.Vector.Float2(540, 1200), host.InputHandler.GetTouch(0).Position);
        Assert.Equal(TouchPhase.Began, host.InputHandler.GetTouch(0).Phase);
    }

    [Fact]
    public void UpdateSafeAreaInsets_PublishesFramebufferPixelInsets()
    {
        using var host = new IosPlatformHost();
        host.AttachLayer((nint)1, 1080, 2400);
        host.UpdateSafeAreaInsets(15, 50, 20, 30, 540, 1200);

        Assert.Equal(new WindowSafeAreaInsets(30, 100, 40, 60), host.SafeAreaInsets);
    }

    [Fact]
    public void Suspend_ClearsTouchesAndPausesAudio()
    {
        bool playback = false;
        using var host = new IosPlatformHost(setPlaybackActive: value =>
        {
            playback = value;
            return true;
        });
        host.AttachLayer((nint)1, 64, 64);
        host.Resume();
        Assert.True(playback);
        host.DispatchTouch(1, IosTouchAction.Began, 8, 8, 1f);
        host.Suspend();

        Assert.Equal(0, host.InputHandler.TouchCount);
        Assert.False(host.AudioHandler.IsPlaybackRunning);
        Assert.False(playback);
    }

    [Fact]
    public void HandleAudioInterruption_PausesAndResumesPlayback()
    {
        var transitions = new List<bool>();
        using var host = new IosPlatformHost(setPlaybackActive: value =>
        {
            transitions.Add(value);
            return true;
        });
        host.AttachLayer((nint)1, 64, 64);
        host.Resume();
        host.HandleAudioInterruption(began: true, shouldResume: false);
        host.HandleAudioInterruption(began: false, shouldResume: true);

        Assert.Equal([true, false, true], transitions);
        Assert.False(host.AudioHandler.Stats.IsInterrupted);
        Assert.True(host.AudioHandler.IsPlaybackRunning);
    }

    [Fact]
    public void WindowSurface_ExposesMetalLayerHandle()
    {
        var surface = new IosWindowSurface((nint)42, 1280, 720);
        surface.UpdateSize(720, 1280);

        Assert.Equal((nint)42, surface.NativeHandle);
        Assert.Equal((nint)42, surface.MetalLayer);
        Assert.Equal((720, 1280), surface.FramebufferSize);
        IWindowSurface rhi = surface;
        Assert.Empty(rhi.RequiredVulkanInstanceExtensions);
        Assert.Equal(nint.Zero, rhi.CreateVulkanSurface((nint)7));
    }

    [Fact]
    public void WindowSurface_RejectsZeroLayerHandle()
    {
        Assert.Throws<ArgumentException>(() => new IosWindowSurface(nint.Zero, 64, 64));
    }

    [Fact]
    public void PreferredFramesPerSecond_ClampsToDisplayLinkRange()
    {
        using var host = new IosPlatformHost();
        host.PreferredFramesPerSecond = 240;
        Assert.Equal(120, host.PreferredFramesPerSecond);
        host.PreferredFramesPerSecond = 0;
        Assert.Equal(1, host.PreferredFramesPerSecond);
    }

    private sealed class RecordingGraphicsHost : IIosGraphicsHost
    {
        public int BeginFrameCount { get; private set; }
        public int EndFrameCount { get; private set; }
        public int ResizeLayerCount { get; private set; }
        public (int Width, int Height) LastFrameSize { get; private set; }
        public (int Width, int Height) LastResizeSize { get; private set; }

        public void AttachLayer(IosWindowSurface surface) { }
        public void ResizeLayer(int width, int height)
        {
            ResizeLayerCount++;
            LastResizeSize = (width, height);
        }
        public void DetachLayer() { }

        public void BeginFrame(int width, int height)
        {
            BeginFrameCount++;
            LastFrameSize = (width, height);
        }
        public void EndFrame() => EndFrameCount++;
        public void Dispose() { }
    }
}

public sealed class IosMetalLayerSurfaceTests
{
    [Fact]
    public void DefaultWindowSurface_MetalLayerIsZero()
    {
        IWindowSurface surface = new StubWindowSurface(nativeHandle: 11);
        Assert.Equal(0, surface.MetalLayer);
        Assert.Equal(11, surface.NativeHandle);
    }

    private sealed class StubWindowSurface : IWindowSurface
    {
        public StubWindowSurface(nint nativeHandle) => NativeHandle = nativeHandle;
        public nint NativeHandle { get; }
        public (int Width, int Height) FramebufferSize => (64, 64);
        public bool VSync { get; set; }
        public int SwapInterval { get; set; }
        public void MakeCurrent() { }
        public void ClearCurrent() { }
        public void SwapBuffers() { }
    }
}
