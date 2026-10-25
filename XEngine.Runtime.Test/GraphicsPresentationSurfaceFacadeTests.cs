using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class GraphicsPresentationSurfaceFacadeTests
{
    [Fact]
    public void UnavailableBackendReturnsAnExplicitReason()
    {
        bool created = Graphics.TryCreatePresentationSurface(new TestSurface(), out PresentationSurfaceId id,
            out string reason);

        Assert.False(created);
        Assert.False(id.IsValid);
        Assert.Contains("does not expose presentation surfaces", reason);
        Assert.False(Graphics.TryGetPrimaryPresentationSurface(out PresentationSurfaceId primary));
        Assert.False(primary.IsValid);
        Assert.False(Graphics.TryGetPresentationSurface(new PresentationSurfaceId(1), out _));
    }

    private sealed class TestSurface : IWindowSurface
    {
        public nint NativeHandle => (nint)1;
        public (int Width, int Height) FramebufferSize => (640, 480);
        public bool VSync { get; set; }
        public int SwapInterval { get; set; }
        public void MakeCurrent() { }
        public void ClearCurrent() { }
        public void SwapBuffers() { }
    }
}
