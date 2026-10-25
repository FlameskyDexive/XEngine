using XEngine.Runtime.RHI;
using XEngine.Runtime.Rendering;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class DefaultBlitOrientationTests
{
    // Default intermediate blits must not receive the swapchain presentation conversion
    // used by standalone output. Final Metal camera-target compensation is tested below.
    [Theory]
    [InlineData(GraphicsBackend.Vulkan, false, 0f)]
    [InlineData(GraphicsBackend.Vulkan, true, 1f)]
    [InlineData(GraphicsBackend.Metal, false, 0f)]
    [InlineData(GraphicsBackend.Metal, true, 0f)]
    [InlineData(GraphicsBackend.OpenGL, false, 1f)]
    [InlineData(GraphicsBackend.OpenGL, true, 1f)]
    public void DefaultBlit_SeparatesPresentationFromIntermediateCopies(GraphicsBackend backend,
        bool presenting, float expectedFlip)
    {
        Assert.Equal(expectedFlip, CommandBuffer.ResolveDefaultBlitFlipY(backend, presenting));
    }

    [Theory]
    [InlineData(GraphicsBackend.Metal, 1f)]
    [InlineData(GraphicsBackend.Vulkan, 0f)]
    [InlineData(GraphicsBackend.OpenGL, 0f)]
    [InlineData(GraphicsBackend.Direct3D12, 0f)]
    public void FinalCameraTargetBlit_UndoesOnlyMetalProjectionReflection(GraphicsBackend backend,
        float expectedFlip)
    {
        Assert.Equal(expectedFlip, UniversalRenderPipeline.ResolveOffscreenCameraPresentationFlipY(backend));
    }

    [Theory]
    [InlineData(false, false, RasterizerState.WindingOrder.CW)]
    [InlineData(true, false, RasterizerState.WindingOrder.CCW)]
    [InlineData(false, true, RasterizerState.WindingOrder.CCW)]
    [InlineData(true, true, RasterizerState.WindingOrder.CW)]
    public void RasterWinding_CombinesProjectionAndModelReflections(
        bool projectionFlipsWinding, bool modelFlipsWinding, RasterizerState.WindingOrder expected)
    {
        RasterizerState state = new()
        {
            CullFace = RasterizerState.PolyFace.Back,
            Winding = RasterizerState.WindingOrder.CW,
        };
        Float4x4 model = Float4x4.CreateScale(modelFlipsWinding ? -1f : 1f, 1f, 1f);

        Assert.Equal(modelFlipsWinding, RenderPipeline.IsModelWindingFlipped(in model));
        Assert.Equal(expected,
            RenderPipeline.ResolveRasterStateForModel(state, projectionFlipsWinding, in model).Winding);
    }

    [Fact]
    public void RasterWinding_DoesNotChangeWhenCullingIsDisabled()
    {
        RasterizerState state = new()
        {
            CullFace = RasterizerState.PolyFace.None,
            Winding = RasterizerState.WindingOrder.CW,
        };
        Float4x4 model = Float4x4.CreateScale(-1f, 1f, 1f);

        Assert.Equal(RasterizerState.WindingOrder.CW,
            RenderPipeline.ResolveRasterStateForModel(state, true, in model).Winding);
    }
}
