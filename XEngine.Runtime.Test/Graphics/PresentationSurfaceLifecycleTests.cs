using System;

using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class PresentationSurfaceLifecycleTests
{
    [Fact]
    public void NullDeviceCanCreateResizePresentAndDestroyIndependentSurfaces()
    {
        using var device = new NullGraphicsDevice();
        device.Initialize(null);
        Assert.False(device.TryGetPrimaryPresentationSurface(out PresentationSurfaceId primary));
        Assert.False(primary.IsValid);
        var firstSurface = new TestSurface(640, 480);
        var secondSurface = new TestSurface(320, 240);

        PresentationSurfaceId first = device.CreatePresentationSurface(firstSurface);
        PresentationSurfaceId second = device.CreatePresentationSurface(secondSurface);

        Assert.NotEqual(first, second);
        Assert.False(device.EndFrame(first));
        Assert.True(device.BeginFrame(first));
        Assert.False(device.BeginFrame(first));
        Assert.False(device.DestroyPresentationSurface(first));
        Assert.True(device.EndFrame(first));
        Assert.True(device.ResizePresentationSurface(second, 0, 0));
        Assert.False(device.BeginFrame(second));
        Assert.True(device.DestroyPresentationSurface(first));
        Assert.False(device.TryGetPresentationSurface(first, out _));
        Assert.True(device.TryGetPresentationSurface(second, out PresentationSurfaceInfo info));
        Assert.False(info.IsVisible);
        Assert.Throws<ArgumentOutOfRangeException>(() => device.ResizePresentationSurface(second, -1, 240));
    }

    private sealed class TestSurface : IWindowSurface
    {
        public TestSurface(int width, int height) => FramebufferSize = (width, height);
        public nint NativeHandle => (nint)1;
        public (int Width, int Height) FramebufferSize { get; }
        public bool VSync { get; set; }
        public int SwapInterval { get; set; }
        public void MakeCurrent() { }
        public void ClearCurrent() { }
        public void SwapBuffers() { }
    }
}
