// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class RendererFeatureTests
{
    private sealed class EmptyFeature : RendererFeature
    {
        public int CreateCalls;
        public int AddCalls;

        public override void Create() => CreateCalls++;

        public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context)
            => AddCalls++;
    }

    private sealed class InjectPassFeature : RendererFeature
    {
        private readonly RenderPass _pass;

        public InjectPassFeature(RenderPass pass) => _pass = pass;

        public override void Create() { }

        public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context)
            => renderer.EnqueuePass(_pass);
    }

    private sealed class ProbePass : RenderPass
    {
        private readonly List<string> _log;
        private readonly string _name;

        public ProbePass(RenderPassEvent renderPassEvent, string name, List<string> log)
            : base(renderPassEvent)
        {
            _name = name;
            _log = log;
        }

        public override void Execute(ScriptableRenderContext context) => _log.Add(_name);
    }

    [Fact]
    public void EmptyFeature_DoesNotEnqueueExtraPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        var empty = new EmptyFeature();
        pipeline.AddRendererFeature(empty);

        int count = pipeline.EnqueueBuiltInAndFeaturesForTest();

        Assert.Equal(1, empty.CreateCalls);
        Assert.Equal(1, empty.AddCalls);
        Assert.Equal(7, count);
        Assert.Equal(1, pipeline.RendererFeatureCount);
    }

    [Fact]
    public void DisabledFeature_DoesNotAddPasses()
    {
        var pipeline = new UniversalRenderPipeline();
        var empty = new EmptyFeature { Enabled = false };
        pipeline.AddRendererFeature(empty);

        Assert.Equal(7, pipeline.EnqueueBuiltInAndFeaturesForTest());
        Assert.Equal(1, empty.CreateCalls);
        Assert.Equal(0, empty.AddCalls);
    }

    [Fact]
    public void FeaturePass_InjectedAfterOpaques_RunsBetweenOpaqueAndTransparent()
    {
        var log = new List<string>();
        var renderer = new ScriptableRenderer();
        var ctx = new ScriptableRenderContext();
        var feature = new InjectPassFeature(
            new ProbePass(RenderPassEvent.AfterRenderingOpaques, "feature", log));

        renderer.EnqueuePass(new ProbePass(RenderPassEvent.BeforeRenderingOpaques, "opaque", log));
        renderer.EnqueuePass(new ProbePass(RenderPassEvent.BeforeRenderingTransparents, "transparent", log));
        feature.Create();
        feature.AddRenderPasses(renderer, ctx);
        renderer.Execute(ctx);

        Assert.Equal(new[] { "opaque", "feature", "transparent" }, log);
    }

    [Fact]
    public void InjectedPass_AppearsInDefaultPipelineEnqueueList()
    {
        var pipeline = new UniversalRenderPipeline();
        pipeline.AddRendererFeature(new InjectPassFeature(
            new ProbePass(RenderPassEvent.AfterRenderingOpaques, "feature", new List<string>())));

        Assert.Equal(8, pipeline.EnqueueBuiltInAndFeaturesForTest());

        var events = new RenderPassEvent[8];
        Assert.Equal(8, pipeline.CopyEnqueuedEventsForTest(events));
        Assert.Equal(RenderPassEvent.AfterRenderingOpaques, events[3]);
        Assert.Equal(RenderPassEvent.AfterRenderingOpaques, events[7]);
    }
}
