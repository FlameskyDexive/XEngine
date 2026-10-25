// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class RenderingDebuggerTests : RuntimeTestBase
{
    public RenderingDebuggerTests()
    {
        VolumeManager.Instance.ResetForTests();
    }

    public override void Dispose()
    {
        VolumeManager.Instance.ResetForTests();
        base.Dispose();
    }

    [Fact]
    public void Capture_EmptyPipeline_HasSevenPassesAndCullsBothCopies()
    {
        var pipeline = new UniversalRenderPipeline();
        RenderingDebugSnapshot snapshot = RenderingDebugger.Capture(pipeline);

        Assert.Equal(7, snapshot.PassEvents.Length);
        Assert.Equal(7, snapshot.EnqueuedPasses);
        Assert.Equal(7, snapshot.ExecutedPasses);
        Assert.Equal(2, snapshot.CulledGraphPasses);
        Assert.Empty(snapshot.Features);
        Assert.Equal(RenderPassEvent.BeforeRendering.ToString(), snapshot.PassEvents[0]);
        Assert.Equal(RenderPassEvent.AfterRendering.ToString(), snapshot.PassEvents[6]);
    }

    [Fact]
    public void Capture_ListsEnabledAndDisabledFeatures()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new ScreenSpaceDecalRendererFeature());
        pipeline.AddRendererFeature(new ScreenSpaceShadowsRendererFeature { Enabled = false });

        RenderingDebugSnapshot snapshot = RenderingDebugger.Capture(pipeline);
        Assert.Equal(2, snapshot.Features.Length);
        Assert.Equal("ScreenSpaceDecalRendererFeature", snapshot.Features[0]);
        Assert.Equal("ScreenSpaceShadowsRendererFeature disabled", snapshot.Features[1]);
        Assert.Equal(2, snapshot.CulledGraphPasses);
        Assert.Equal(7, snapshot.ExecutedPasses);

        var shadows = new UniversalRenderPipeline();
        shadows.AddRendererFeature(new ScreenSpaceShadowsRendererFeature());
        RenderingDebugSnapshot depth = RenderingDebugger.Capture(shadows);
        Assert.Equal(1, depth.CulledGraphPasses);
        Assert.Equal(8, depth.ExecutedPasses);
    }

    [Fact]
    public void Capture_ListsRegisteredVolumes()
    {
        GameObject go = CreateGameObject("GlobalFog");
        var volume = go.AddComponent<Volume>();
        volume.IsGlobal = true;
        volume.Weight = 0.75f;
        volume.Priority = 3;
        VolumeManager.Instance.Register(volume);

        RenderingDebugSnapshot snapshot = RenderingDebugger.Capture(new UniversalRenderPipeline());
        Assert.Single(snapshot.Volumes);
        Assert.Equal("GlobalFog", snapshot.Volumes[0].Name);
        Assert.True(snapshot.Volumes[0].IsGlobal);
        Assert.Equal(0.75f, snapshot.Volumes[0].Weight);
        Assert.Equal(3, snapshot.Volumes[0].Priority);
        Assert.True(snapshot.Volumes[0].Enabled);
    }

    [Fact]
    public void OverdrawProxy_IsExtraDrawsBeyondObjects()
    {
        RenderStats.BeginFrame();
        RenderStats.AddRenderables(4, 1, 3);
        RenderStats.RecordDraw(Topology.Triangles, 3);
        RenderStats.RecordDraw(Topology.Triangles, 3);
        RenderStats.RecordDraw(Topology.Triangles, 3);
        RenderStats.RecordDraw(Topology.Triangles, 3);
        RenderStats.RecordDraw(Topology.Triangles, 3);
        RenderStats.EndFrame();

        Assert.Equal(5, RenderStats.Last.DrawCalls);
        Assert.Equal(3, RenderStats.Last.RenderablesDrawn);
        Assert.Equal(2, RenderStats.Last.OverdrawProxy);
        Assert.Equal(2, RenderingDebugger.Capture(new UniversalRenderPipeline()).OverdrawProxy);
    }

    [Fact]
    public void ShadowCascades_RecordOnFrame()
    {
        RenderStats.BeginFrame();
        RenderStats.RecordShadowCascades(4);
        RenderStats.EndFrame();
        Assert.Equal(4, RenderStats.Last.ShadowCascades);
        Assert.Equal(4, RenderingDebugger.Capture(new UniversalRenderPipeline()).ShadowCascades);
    }
}
