// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class DeferredPathTests
{
    [Fact]
    public void Forward_StillEnqueuesSevenBuiltInPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        Assert.Equal(RenderingPath.Forward, pipeline.RenderingPath);
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(
        [
            RenderPassEvent.BeforeRendering,
            RenderPassEvent.BeforeRenderingShadows,
            RenderPassEvent.BeforeRenderingOpaques,
            RenderPassEvent.AfterRenderingOpaques,
            RenderPassEvent.BeforeRenderingTransparents,
            RenderPassEvent.BeforeRenderingPostProcessing,
            RenderPassEvent.AfterRendering,
        ], pipeline.GetBuiltInPassEvents());
    }

    [Fact]
    public void Deferred_EnqueuesGBufferAndLighting()
    {
        var pipeline = new UniversalRenderPipeline { RenderingPath = RenderingPath.Deferred };
        Assert.Equal(8, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(
        [
            RenderPassEvent.BeforeRendering,
            RenderPassEvent.BeforeRenderingShadows,
            RenderPassEvent.BeforeRenderingOpaques,
            RenderPassEvent.AfterRenderingGbuffer,
            RenderPassEvent.AfterRenderingOpaques,
            RenderPassEvent.BeforeRenderingTransparents,
            RenderPassEvent.BeforeRenderingPostProcessing,
            RenderPassEvent.AfterRendering,
        ], pipeline.GetBuiltInPassEvents());

        pipeline.CompileRenderGraphForTest(out int culled, out int executed);
        Assert.Equal(2, culled);
        Assert.Equal(8, executed);
    }

    private sealed class EmptyFeature : RendererFeature
    {
        public override void Create() { }

        public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context) { }
    }

    [Fact]
    public void Deferred_EmptyFeature_DoesNotAddPasses()
    {
        var pipeline = new UniversalRenderPipeline { RenderingPath = RenderingPath.Deferred };
        pipeline.AddRendererFeature(new EmptyFeature { Enabled = false });
        Assert.Equal(8, pipeline.EnqueueBuiltInAndFeaturesForTest());
    }

    [Fact]
    public void Overlay_IsSkippedOnDeferred_NotOnForward()
    {
        var overlay = new Camera { RenderType = CameraRenderType.Overlay };
        var bas = new Camera { RenderType = CameraRenderType.Base };

        var forward = new UniversalRenderPipeline();
        Assert.True(forward.ShouldRenderCamera(overlay));
        Assert.True(forward.ShouldRenderCamera(bas));
        Assert.False(forward.UsesDeferredShading);
        Assert.True(forward.UsesClusteredLighting);

        var deferred = new UniversalRenderPipeline { RenderingPath = RenderingPath.Deferred };
        Assert.False(deferred.ShouldRenderCamera(overlay));
        Assert.True(deferred.ShouldRenderCamera(bas));
        Assert.True(deferred.UsesDeferredShading);
        Assert.True(deferred.UsesClusteredLighting);
    }

    [Fact]
    public void ForwardPlus_EnqueuesSevenPasses_UsesClusteredLighting()
    {
        var pipeline = new UniversalRenderPipeline { RenderingPath = RenderingPath.ForwardPlus };
        Assert.False(pipeline.UsesDeferredShading);
        Assert.True(pipeline.UsesClusteredLighting);
        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(
        [
            RenderPassEvent.BeforeRendering,
            RenderPassEvent.BeforeRenderingShadows,
            RenderPassEvent.BeforeRenderingOpaques,
            RenderPassEvent.AfterRenderingOpaques,
            RenderPassEvent.BeforeRenderingTransparents,
            RenderPassEvent.BeforeRenderingPostProcessing,
            RenderPassEvent.AfterRendering,
        ], pipeline.GetBuiltInPassEvents());

        pipeline.CompileRenderGraphForTest(out int culled, out int executed);
        Assert.Equal(2, culled);
        Assert.Equal(7, executed);

        var overlay = new Camera { RenderType = CameraRenderType.Overlay };
        Assert.True(pipeline.ShouldRenderCamera(overlay));
    }

    [Fact]
    public void DeferredPlus_EnqueuesGBufferAndLighting_SkipsOverlay()
    {
        var pipeline = new UniversalRenderPipeline { RenderingPath = RenderingPath.DeferredPlus };
        Assert.True(pipeline.UsesDeferredShading);
        Assert.True(pipeline.UsesClusteredLighting);
        Assert.Equal(8, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(
        [
            RenderPassEvent.BeforeRendering,
            RenderPassEvent.BeforeRenderingShadows,
            RenderPassEvent.BeforeRenderingOpaques,
            RenderPassEvent.AfterRenderingGbuffer,
            RenderPassEvent.AfterRenderingOpaques,
            RenderPassEvent.BeforeRenderingTransparents,
            RenderPassEvent.BeforeRenderingPostProcessing,
            RenderPassEvent.AfterRendering,
        ], pipeline.GetBuiltInPassEvents());

        pipeline.CompileRenderGraphForTest(out int culled, out int executed);
        Assert.Equal(2, culled);
        Assert.Equal(8, executed);

        Assert.False(pipeline.ShouldRenderCamera(new Camera { RenderType = CameraRenderType.Overlay }));
        Assert.True(pipeline.ShouldRenderCamera(new Camera { RenderType = CameraRenderType.Base }));
    }

    [Fact]
    public void EchoRoundTrip_PreservesForwardPlusAndDeferredPlus()
    {
        var plus = Serializer.Deserialize<UniversalRenderData>(
            Serializer.Serialize(typeof(object), new UniversalRenderData { RenderingPath = RenderingPath.ForwardPlus }));
        Assert.Equal(RenderingPath.ForwardPlus, plus!.RenderingPath);

        var deferredPlus = Serializer.Deserialize<UniversalRenderData>(
            Serializer.Serialize(typeof(object), new UniversalRenderData { RenderingPath = RenderingPath.DeferredPlus }));
        Assert.Equal(RenderingPath.DeferredPlus, deferredPlus!.RenderingPath);
    }

    [Fact]
    public void Standard_HasGBufferPass_UnlitDoesNot()
    {
        Shader standard = Shader.LoadDefault(DefaultShader.Standard);
        Assert.True(standard.HasGBufferPass);
        Assert.Equal("GBuffer", standard.GetPass("GBuffer").Name);
        Assert.True(standard.GetPass("GBuffer").HasTag("LightMode", "GBuffer"));

        Shader unlit = Shader.LoadDefault(DefaultShader.Unlit);
        Assert.False(unlit.HasGBufferPass);
    }

    [Fact]
    public void RenderingPath_ReadsRendererDataAsset()
    {
        var renderer = new UniversalRenderData { RenderingPath = RenderingPath.Deferred };
        var pipeline = new UniversalRenderPipeline
        {
            Asset = new UniversalRenderPipelineAsset { Renderer = new AssetRef<UniversalRenderData>(renderer) },
        };
        Assert.Equal(RenderingPath.Deferred, pipeline.RenderingPath);
    }

    [Fact]
    public void EchoRoundTrip_PreservesRenderingPath()
    {
        var original = new UniversalRenderData { DepthPriming = true, RenderingPath = RenderingPath.Deferred };
        var deserialized = Serializer.Deserialize<UniversalRenderData>(Serializer.Serialize(typeof(object), original));
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.DepthPriming);
        Assert.Equal(RenderingPath.Deferred, deserialized.RenderingPath);
    }

    [Fact]
    public void OnValidate_ClampsUnknownRenderingPath()
    {
        var data = new UniversalRenderData { RenderingPath = (RenderingPath)99 };
        data.OnValidate();
        Assert.Equal(RenderingPath.Forward, data.RenderingPath);
    }

    [Fact]
    public void DeferredLighting_ParsesDualSource()
    {
        Shader shader = Shader.LoadDefault(DefaultShader.DeferredLighting);
        Assert.Equal("DeferredLighting", shader.GetPass("DeferredLighting").Name);
    }
}
