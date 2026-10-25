// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class ScriptableRendererTests
{
    private sealed class ProbePass : RenderPass
    {
        public readonly List<int> Log;
        public readonly int Id;

        public ProbePass(RenderPassEvent renderPassEvent, int id, List<int> log)
            : base(renderPassEvent)
        {
            Id = id;
            Log = log;
        }

        public override void Execute(ScriptableRenderContext context) => Log.Add(Id);
    }

    [Fact]
    public void Execute_SortsByEvent_AndKeepsStableOrderForTies()
    {
        var log = new List<int>();
        var renderer = new ScriptableRenderer();
        var ctx = new ScriptableRenderContext();

        renderer.EnqueuePass(new ProbePass(RenderPassEvent.AfterRendering, 3, log));
        renderer.EnqueuePass(new ProbePass(RenderPassEvent.BeforeRendering, 1, log));
        renderer.EnqueuePass(new ProbePass(RenderPassEvent.AfterRenderingOpaques, 2, log));
        renderer.EnqueuePass(new ProbePass(RenderPassEvent.AfterRenderingOpaques, 4, log));

        renderer.Execute(ctx);

        Assert.Equal(new[] { 1, 2, 4, 3 }, log);
    }

    [Fact]
    public void EnqueuePass_SkipsNullAndDisabled()
    {
        var renderer = new ScriptableRenderer();
        renderer.EnqueuePass(null);
        var disabled = new ProbePass(RenderPassEvent.BeforeRendering, 1, new List<int>()) { Enabled = false };
        renderer.EnqueuePass(disabled);
        Assert.Equal(0, renderer.EnqueuedCount);
    }

    [Fact]
    public void DefaultForwardPipeline_EnqueuesSubmitBoundaryPasses()
    {
        RenderPassEvent[] events = UniversalRenderPipeline.Default.GetBuiltInPassEvents();
        Assert.Equal(
        [
            RenderPassEvent.BeforeRendering,
            RenderPassEvent.BeforeRenderingShadows,
            RenderPassEvent.BeforeRenderingOpaques,
            RenderPassEvent.AfterRenderingOpaques,
            RenderPassEvent.BeforeRenderingTransparents,
            RenderPassEvent.BeforeRenderingPostProcessing,
            RenderPassEvent.AfterRendering,
        ], events);
    }
}
