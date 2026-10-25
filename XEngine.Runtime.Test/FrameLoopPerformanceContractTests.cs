// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class FrameLoopPerformanceContractTests
{
    [Fact]
    public void ReusableFrustum_MatchesVectorReferenceConstructionAndCulling()
    {
        Float4x4 matrix = Float4x4.CreatePerspectiveFov(Maths.ToRadians(63f), 16f / 9f, 0.1f, 250f);
        Frustum expected = Frustum.FromMatrix(matrix);
        Plane[] planes = new Plane[6];
        Frustum actual = RenderPipeline.CameraSnapshot.CreateFrustum(matrix, planes);

        Assert.Same(planes, actual.Planes);
        for (int i = 0; i < planes.Length; i++)
            Assert.Equal(expected.Planes[i], actual.Planes[i]);

        AABB[] bounds =
        [
            new AABB(new Float3(-0.5f, -0.5f, 1f), new Float3(0.5f, 0.5f, 2f)),
            new AABB(new Float3(50f, 50f, 1f), new Float3(51f, 51f, 2f)),
            new AABB(new Float3(-2f, -2f, 200f), new Float3(2f, 2f, 220f)),
        ];

        for (int i = 0; i < bounds.Length; i++)
            Assert.Equal(expected.Intersects(bounds[i]), RenderPipeline.FrustumIntersectsAabb(actual, bounds[i]));
    }

    [Fact]
    public void MaterialKeywordKey_ReusesStableKeyAndInvalidatesOnChange()
    {
        using var material = new Material(Shader.LoadDefault(DefaultShader.Standard));
        material.SetKeyword("HAS_TANGENTS", true);
        string first = material.KeywordKey;

        material.SetKeyword("HAS_TANGENTS", true);

        Assert.Same(first, material.KeywordKey);

        material.SetKeyword("HAS_TANGENTS", false);

        Assert.NotSame(first, material.KeywordKey);
        Assert.DoesNotContain("HAS_TANGENTS;", material.KeywordKey);
    }

    [Fact]
    public void TemporaryRenderTexturePool_HasZeroSteadyStateAllocation()
    {
        TextureImageFormat[] formats = [TextureImageFormat.Color4b];
        RenderTexture warm = RenderTexture.GetTemporaryRT(32, 32, true, formats);
        RenderTexture.ReleaseTemporaryRT(warm);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
        {
            RenderTexture current = RenderTexture.GetTemporaryRT(32, 32, true, formats);
            RenderTexture.ReleaseTemporaryRT(current);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void GraphicsDispose_InvalidatesTemporaryRenderTexturePool()
    {
        RHI.GraphicsBackend? previousBackend = RHI.GraphicsBackendSelection.Preferred;
        RenderTexture? pooled = null;
        try
        {
            RHI.GraphicsBackendSelection.Preferred = RHI.GraphicsBackend.Null;
            Graphics.Initialize(debug: false);

            pooled = RenderTexture.GetTemporaryRT(32, 32, true, [TextureImageFormat.Color4b]);
            RenderTexture.ReleaseTemporaryRT(pooled);
            Rendering.GlobalUniforms.Initialize();

            Graphics.Dispose();

            Assert.True(pooled.IsDisposed);
            Assert.Null(Rendering.GlobalUniforms.GetBuffer());
            RenderTexture.ReleaseTemporaryRT(pooled);
        }
        finally
        {
            Graphics.Dispose();
            RHI.GraphicsBackendSelection.Preferred = previousBackend;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GraphicsInitialize_InvalidatesPreinitializedTemporaryTargets(bool deviceExists)
    {
        RHI.GraphicsBackend? previousBackend = RHI.GraphicsBackendSelection.Preferred;
        try
        {
            Assert.Null(Graphics.Device);
            RHI.GraphicsBackendSelection.Preferred = RHI.GraphicsBackend.Null;
            if (deviceExists)
                Graphics.EnsureDevice();
            TextureImageFormat[] formats = [TextureImageFormat.Color4b];
            RenderTexture released = RenderTexture.GetTemporaryRT(73, 29, false, formats);
            RenderTexture active = RenderTexture.GetTemporaryRT(73, 29, false, formats);
            RenderTexture.ReleaseTemporaryRT(released);

            Graphics.Initialize(debug: false);

            Assert.True(released.IsDisposed);
            Assert.True(active.IsDisposed);
            RenderTexture current = RenderTexture.GetTemporaryRT(73, 29, false, formats);
            Assert.NotSame(released, current);
            RenderTexture.ReleaseTemporaryRT(current);

            // Calling Initialize again on the ready device must retain normal pool reuse.
            Graphics.Initialize(debug: false);
            RenderTexture reused = RenderTexture.GetTemporaryRT(73, 29, false, formats);
            Assert.Same(current, reused);
            RenderTexture.ReleaseTemporaryRT(reused);
        }
        finally
        {
            Graphics.Dispose();
            RHI.GraphicsBackendSelection.Preferred = previousBackend;
        }
    }
}
