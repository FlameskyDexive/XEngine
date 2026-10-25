// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ShadowAtlasResizeTests : IDisposable
{
    private readonly GraphicsBackend? _previousBackend = GraphicsBackendSelection.Preferred;
    private readonly int _previousPreferredSize = ShadowAtlas.PreferredSize;
    private readonly int _previousMaxTextureSize = Graphics.MaxTextureSize;

    public ShadowAtlasResizeTests()
    {
        GraphicsBackendSelection.Preferred = GraphicsBackend.Null;
        Graphics.Initialize(debug: false);
        ShadowAtlas.Dispose();
    }

    public void Dispose()
    {
        Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
        ShadowAtlas.PreferredSize = _previousPreferredSize;
        Graphics.MaxTextureSize = _previousMaxTextureSize;
    }

    [Fact]
    public void QualityChangeWithoutShadowWork_DoesNotCreateAtlas()
    {
        ShadowAtlas.PreferredSize = 2048;
        ShadowAtlas.Clear();
        ShadowAtlas.PreferredSize = 4096;
        ShadowAtlas.Clear();

        Assert.False(ShadowAtlas.IsInitialized);
        Assert.Null(ShadowAtlas.GetAtlas());
        Assert.Null(ShadowAtlas.GetStaticCacheAtlas());
        Assert.Equal(0, ShadowAtlas.GetSize());
    }

    [Theory]
    [InlineData(2048, 4096)]
    [InlineData(4096, 2048)]
    [InlineData(2048, 1024)]
    public void Resize_RetiresOldTargetsAndInvalidatesBothCachesAndPacking(int initialSize, int nextSize)
    {
        ShadowAtlas.PreferredSize = initialSize;
        ShadowAtlas.TryInitialize();
        RenderTexture original = ShadowAtlas.GetAtlas()!;
        Texture2D originalDepth = original.InternalDepth;
        GraphicsFrameBuffer originalFramebuffer = original.frameBuffer;
        RenderTexture originalStatic = ShadowAtlas.EnsureStaticCache();
        Texture2D originalStaticDepth = originalStatic.InternalDepth;
        GraphicsFrameBuffer originalStaticFramebuffer = originalStatic.frameBuffer;
        Assert.Equal(new Int2(0, 0), ShadowAtlas.ReserveTiles(initialSize, initialSize, 1));
        ShadowAtlas.MarkShadowCacheValid(42);
        ShadowAtlas.MarkStaticCacheValid(43);

        ShadowAtlas.PreferredSize = nextSize;
        // Quality edits only request a resize; creation remains in the shadow work path.
        Assert.Same(original, ShadowAtlas.GetAtlas());
        ShadowAtlas.TryInitialize();

        RenderTexture resized = ShadowAtlas.GetAtlas()!;
        Assert.NotSame(original, resized);
        Assert.Equal(nextSize, ShadowAtlas.GetSize());
        Assert.Equal(nextSize, resized.Width);
        Assert.Equal(nextSize, resized.Height);
        Assert.True(original.IsDisposed);
        Assert.True(originalDepth.IsDisposed);
        Assert.True(originalFramebuffer.IsDisposed);
        Assert.True(originalStatic.IsDisposed);
        Assert.True(originalStaticDepth.IsDisposed);
        Assert.True(originalStaticFramebuffer.IsDisposed);
        Assert.Null(ShadowAtlas.GetStaticCacheAtlas());
        Assert.False(ShadowAtlas.IsShadowCacheValid(42));
        Assert.False(ShadowAtlas.IsStaticCacheValid(43));
        Assert.Equal(new Int2(0, 0), ShadowAtlas.ReserveTiles(nextSize, nextSize, 2));
        Assert.Null(ShadowAtlas.ReserveTiles(32, 32, 3));

        RenderTexture resizedStatic = ShadowAtlas.EnsureStaticCache();
        Assert.Equal(nextSize, resizedStatic.Width);
        Assert.Equal(nextSize, resizedStatic.Height);
        Assert.False(ShadowAtlas.IsStaticCacheValid(43));
    }

    [Fact]
    public void UnchangedEffectiveSize_PreservesTargetsCachesAndPackingWithoutAllocating()
    {
        Graphics.MaxTextureSize = 2048;
        ShadowAtlas.PreferredSize = 4096;
        ShadowAtlas.TryInitialize();
        RenderTexture original = ShadowAtlas.GetAtlas()!;
        RenderTexture originalStatic = ShadowAtlas.EnsureStaticCache();
        Assert.Equal(new Int2(0, 0), ShadowAtlas.ReserveTiles(2048, 2048, 1));
        ShadowAtlas.MarkShadowCacheValid(42);
        ShadowAtlas.MarkStaticCacheValid(43);
        // Both requests resolve to the same device limit, so neither requires GPU work.
        ShadowAtlas.PreferredSize = 8192;
        for (int i = 0; i < 100; i++)
            ShadowAtlas.TryInitialize();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            ShadowAtlas.TryInitialize();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Same(original, ShadowAtlas.GetAtlas());
        Assert.Same(originalStatic, ShadowAtlas.GetStaticCacheAtlas());
        Assert.False(original.IsDisposed);
        Assert.False(originalStatic.IsDisposed);
        Assert.True(ShadowAtlas.IsShadowCacheValid(42));
        Assert.True(ShadowAtlas.IsStaticCacheValid(43));
        Assert.Null(ShadowAtlas.ReserveTiles(32, 32, 2));
    }

    [Fact]
    public void DisposedAtlas_IsRecreatedInsteadOfReusingDisposedReference()
    {
        ShadowAtlas.PreferredSize = 2048;
        ShadowAtlas.TryInitialize();
        RenderTexture original = ShadowAtlas.GetAtlas()!;
        RenderTexture originalStatic = ShadowAtlas.EnsureStaticCache();
        ShadowAtlas.MarkShadowCacheValid(42);
        ShadowAtlas.MarkStaticCacheValid(43);
        original.Dispose();

        ShadowAtlas.TryInitialize();

        Assert.True(ShadowAtlas.IsInitialized);
        Assert.NotSame(original, ShadowAtlas.GetAtlas());
        Assert.True(originalStatic.IsDisposed);
        Assert.Null(ShadowAtlas.GetStaticCacheAtlas());
        Assert.False(ShadowAtlas.IsShadowCacheValid(42));
        Assert.False(ShadowAtlas.IsStaticCacheValid(43));
        Assert.Equal(new Int2(0, 0), ShadowAtlas.ReserveTiles(2048, 2048, 1));
    }
}
