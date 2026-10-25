// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class RenderGraphTests
{
    private sealed class ProbePass : RenderPass
    {
        public int ExecuteCount;

        public ProbePass(RenderPassEvent renderPassEvent) : base(renderPassEvent) { }

        public override void Execute(ScriptableRenderContext context) => ExecuteCount++;
    }

    [Fact]
    public void WriterWithoutReader_IsCulled()
    {
        var graph = new RenderGraph();
        var writer = new ProbePass(RenderPassEvent.AfterRenderingOpaques);
        var present = new ProbePass(RenderPassEvent.AfterRendering);

        RenderGraphResource color = graph.Import(RenderGraphImportedTexture.CameraOpaqueColor);
        graph.BeginPass(writer, allowCulling: true);
        graph.Write(color);
        graph.EndPass();
        graph.BeginPass(present, allowCulling: false);
        graph.EndPass();

        Assert.Equal(1, graph.Compile());
        Assert.False(graph.IsLive(0));
        Assert.True(graph.IsLive(1));

        var context = new ScriptableRenderContext();
        graph.Execute(context);
        Assert.Equal(0, writer.ExecuteCount);
        Assert.Equal(1, present.ExecuteCount);
    }

    [Fact]
    public void ReaderKeepsWriterLive()
    {
        var graph = new RenderGraph();
        var writer = new ProbePass(RenderPassEvent.AfterRenderingOpaques);
        var reader = new ProbePass(RenderPassEvent.AfterRendering);

        RenderGraphResource color = graph.Import(RenderGraphImportedTexture.CameraOpaqueColor);
        graph.BeginPass(writer, allowCulling: true);
        graph.Write(color);
        graph.EndPass();
        graph.BeginPass(reader, allowCulling: false);
        graph.Read(color);
        graph.EndPass();

        Assert.Equal(0, graph.Compile());
        Assert.True(graph.IsLive(0));
        Assert.True(graph.IsLive(1));

        graph.Execute(new ScriptableRenderContext());
        Assert.Equal(1, writer.ExecuteCount);
        Assert.Equal(1, reader.ExecuteCount);
    }

    [Fact]
    public void ForcedRead_KeepsWriterLive()
    {
        var graph = new RenderGraph();
        var writer = new ProbePass(RenderPassEvent.AfterRenderingOpaques);

        RenderGraphResource depth = graph.Import(RenderGraphImportedTexture.CameraDepth);
        graph.ForceRead(depth);
        graph.BeginPass(writer, allowCulling: true);
        graph.Write(depth);
        graph.EndPass();

        Assert.Equal(0, graph.Compile());
        graph.Execute(new ScriptableRenderContext());
        Assert.Equal(1, writer.ExecuteCount);
    }

    [Fact]
    public void BeginPass_DefaultsToRaster_RecordsComputeAndUnsafe()
    {
        var graph = new RenderGraph();
        var raster = new ProbePass(RenderPassEvent.BeforeRenderingOpaques);
        var compute = new ProbePass(RenderPassEvent.AfterRenderingOpaques);
        var unsafePass = new ProbePass(RenderPassEvent.AfterRendering);

        graph.BeginPass(raster, allowCulling: false);
        graph.EndPass();
        graph.BeginPass(compute, allowCulling: false, RenderGraphPassKind.Compute);
        graph.EndPass();
        graph.BeginPass(unsafePass, allowCulling: false, RenderGraphPassKind.Unsafe);
        graph.EndPass();

        Assert.Equal(RenderGraphPassKind.Raster, graph.GetPassKind(0));
        Assert.Equal(RenderGraphPassKind.Compute, graph.GetPassKind(1));
        Assert.Equal(RenderGraphPassKind.Unsafe, graph.GetPassKind(2));
        Assert.Equal(0, graph.Compile());
        graph.Execute(new ScriptableRenderContext());
        Assert.Equal(1, raster.ExecuteCount);
        Assert.Equal(1, compute.ExecuteCount);
        Assert.Equal(1, unsafePass.ExecuteCount);
    }

    [Fact]
    public void ComputeWriterWithoutReader_IsCulled()
    {
        var graph = new RenderGraph();
        var compute = new ProbePass(RenderPassEvent.AfterRenderingOpaques);
        var present = new ProbePass(RenderPassEvent.AfterRendering);

        RenderGraphResource color = graph.Import(RenderGraphImportedTexture.CameraOpaqueColor);
        graph.BeginPass(compute, allowCulling: true, RenderGraphPassKind.Compute);
        graph.Write(color);
        graph.EndPass();
        graph.BeginPass(present, allowCulling: false, RenderGraphPassKind.Raster);
        graph.EndPass();

        Assert.Equal(1, graph.Compile());
        Assert.False(graph.IsLive(0));
        Assert.Equal(RenderGraphPassKind.Compute, graph.GetPassKind(0));
    }

    [Fact]
    public void NoConsumer_PipelineGraphCullsBothCopies()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.CompileRenderGraphForTest(out int culled, out int executed);
        Assert.Equal(2, culled);
        Assert.Equal(7, executed);
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

    [Fact]
    public void DepthConsumer_PipelineGraphKeepsCopyDepth()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new DepthGrabFeature());
        pipeline.CompileRenderGraphForTest(out int culled, out int executed);
        Assert.Equal(1, culled);
        Assert.Equal(8, executed);
        Assert.Equal(8, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(ScriptableRenderPassInput.Depth, pipeline.RequestedInputsForTest);
    }

    [Fact]
    public void ColorAndDepth_PipelineGraphKeepsBothCopies()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ColorGrabFeature());
        pipeline.AddRendererFeature(new DepthGrabFeature());
        pipeline.CompileRenderGraphForTest(out int culled, out int executed);
        Assert.Equal(0, culled);
        Assert.Equal(9, executed);
        Assert.Equal(9, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

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
}
