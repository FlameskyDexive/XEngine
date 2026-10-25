// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class CopyColorDepthTests
{
    private sealed class ColorGrabFeature : RendererFeature
    {
        public override void Create() { }

        public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context)
            => ConfigureInput(ScriptableRenderPassInput.Color);
    }

    private sealed class DepthGrabFeature : RendererFeature
    {
        public override void Create() { }

        public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context)
            => ConfigureInput(ScriptableRenderPassInput.Depth);
    }

    [Fact]
    public void NoConsumer_DoesNotEnqueueCopyPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.None, pipeline.RequestedInputsForTest);
    }

    [Fact]
    public void ColorConsumer_EnqueuesCopyColor()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ColorGrabFeature());

        int count = pipeline.EnqueueBuiltInAndFeaturesForTest();
        var events = new RenderPassEvent[count];
        pipeline.CopyEnqueuedEventsForTest(events);

        Assert.Equal(8, count);
        Assert.Equal(ScriptableRenderPassInput.Color, pipeline.RequestedInputsForTest);
        Assert.Equal(RenderPassEvent.AfterRenderingOpaques, events[7]);
    }

    [Fact]
    public void DepthConsumer_EnqueuesCopyDepth()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new DepthGrabFeature());

        Assert.Equal(8, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.Depth, pipeline.RequestedInputsForTest);
    }

    [Fact]
    public void ColorAndDepth_EnqueueTwoCopyPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ColorGrabFeature());
        pipeline.AddRendererFeature(new DepthGrabFeature());

        Assert.Equal(9, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(
            ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth,
            pipeline.RequestedInputsForTest);
    }

    [Fact]
    public void ExecutePath_NoConsumer_CullsCopyGraphNodes()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.CompileRenderGraphForTest(out int culled, out int executed);
        Assert.Equal(2, culled);
        Assert.Equal(7, executed);
    }

    [Fact]
    public void MapPassInputs_DepthNormalMotion_MatchPrepassRequirements()
    {
        RenderFeatureRequirements mapped = UniversalRenderPipeline.MapPassInputs(
            ScriptableRenderPassInput.Depth
            | ScriptableRenderPassInput.Normal
            | ScriptableRenderPassInput.Motion);

        Assert.Equal(
            RenderFeatureRequirements.Depth
            | RenderFeatureRequirements.Normals
            | RenderFeatureRequirements.MotionVectors,
            mapped);
        Assert.True(UniversalRenderPipeline.RequiresPrepass(CameraClearFlags.Skybox, mapped));
        Assert.False(UniversalRenderPipeline.RequiresPrepass(
            CameraClearFlags.Skybox, UniversalRenderPipeline.MapPassInputs(ScriptableRenderPassInput.Color)));
    }

    [Fact]
    public void ColorInput_DoesNotForcePrepass()
    {
        Assert.Equal(
            RenderFeatureRequirements.None,
            UniversalRenderPipeline.MapPassInputs(ScriptableRenderPassInput.Color));
    }
}
