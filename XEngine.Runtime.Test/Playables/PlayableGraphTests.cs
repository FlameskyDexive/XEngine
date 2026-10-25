// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Playables;

namespace XEngine.Runtime.Test.Playables;

/// <summary>
/// Verifies the <see cref="PlayableGraph"/> evaluation contract: two-phase traversal order (all
/// PrepareFrame before any ProcessFrame; pre-order within PrepareFrame; post-order within
/// ProcessFrame), output application timing, clock advance vs sample semantics, child traversal,
/// and the steady-state zero-allocation guarantee.
/// </summary>
public class PlayableGraphTests
{
    [Fact]
    public void Evaluate_WithNullRoot_DoesNotThrow()
    {
        var graph = new PlayableGraph();
        var ex = Record.Exception(() => graph.Evaluate(0.016f));
        Assert.Null(ex);
    }

    [Fact]
    public void Evaluate_WithNoOutputs_DoesNotThrow()
    {
        var graph = new PlayableGraph { Root = new CountingPlayable() };
        var ex = Record.Exception(() => graph.Evaluate(0.016f));
        Assert.Null(ex);
    }

    [Fact]
    public void Evaluate_AdvancesClock()
    {
        var graph = new PlayableGraph { Root = new CountingPlayable() };

        graph.Evaluate(0.25f);

        Assert.Equal(0.25, graph.Clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void Evaluate_RespectsTimeScale()
    {
        var graph = new PlayableGraph { Root = new CountingPlayable() };
        graph.Clock.TimeScale = 2f;

        graph.Evaluate(0.5f);

        Assert.Equal(1.0, graph.Clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void Evaluate_PassesScaledDeltaAndGlobalTimeToNodes()
    {
        var leaf = new CountingPlayable();
        var graph = new PlayableGraph { Root = leaf };
        graph.Clock.TimeScale = 0.5f;

        graph.Evaluate(0.2f);

        Assert.Equal(0.1f, leaf.LastContext.DeltaTime, precision: 5);
        Assert.Equal(0.1, leaf.LastContext.GlobalTime, precision: 6);
        Assert.False(leaf.LastContext.IsScrubbing);
    }

    [Fact]
    public void Sample_DoesNotAdvanceClock_AndReportsScrubbing()
    {
        var leaf = new CountingPlayable();
        var graph = new PlayableGraph { Root = leaf };

        graph.Evaluate(1.0f);            // advance to t=1
        double beforeSample = graph.Clock.CurrentTime;

        graph.Sample();

        Assert.Equal(beforeSample, graph.Clock.CurrentTime);
        Assert.True(leaf.LastContext.IsScrubbing);
        Assert.Equal(0f, leaf.LastContext.DeltaTime);
    }

    [Fact]
    public void Evaluate_RunsPrepareFrameBeforeProcessFrame_PerNode()
    {
        Sequence.Reset();
        var leaf = new CountingPlayable();
        var graph = new PlayableGraph { Root = leaf };

        graph.Evaluate(0.016f);

        Assert.Equal(1, leaf.PrepareFrameCount);
        Assert.Equal(1, leaf.ProcessFrameCount);
        Assert.True(leaf.PrepareFrameSeq < leaf.ProcessFrameSeq,
            "PrepareFrame must run before ProcessFrame on the same node.");
    }

    [Fact]
    public void Evaluate_AllPrepareFramesBeforeAnyProcessFrame()
    {
        // The defining property of the two-phase split: every node in the graph finishes
        // PrepareFrame before any node begins ProcessFrame. This is what lets mixers know their
        // children's resolved weights/times for the frame before sampling.
        Sequence.Reset();
        var childA = new CountingPlayable();
        var childB = new CountingPlayable();
        var root = new GroupPlayable();
        root.AddInput(childA);
        root.AddInput(childB);

        var graph = new PlayableGraph { Root = root };
        graph.Evaluate(0.016f);

        long lastPrepare = System.Math.Max(
            System.Math.Max(root.PrepareFrameSeq, childA.PrepareFrameSeq),
            childB.PrepareFrameSeq);
        long firstProcess = System.Math.Min(
            System.Math.Min(root.ProcessFrameSeq, childA.ProcessFrameSeq),
            childB.ProcessFrameSeq);

        Assert.True(lastPrepare < firstProcess,
            "All PrepareFrame calls must complete before any ProcessFrame begins. " +
            $"lastPrepare={lastPrepare} firstProcess={firstProcess}");
    }

    [Fact]
    public void Evaluate_PrepareFrameIsPreOrder_ProcessFrameIsPostOrder()
    {
        Sequence.Reset();
        var leaf = new CountingPlayable();
        var root = new GroupPlayable();
        root.AddInput(leaf);

        var graph = new PlayableGraph { Root = root };
        graph.Evaluate(0.016f);

        // Pre-order: root PrepareFrame before its child.
        Assert.True(root.PrepareFrameSeq < leaf.PrepareFrameSeq,
            "PrepareFrame must be pre-order: parent before child.");
        // Post-order: child ProcessFrame before its parent.
        Assert.True(leaf.ProcessFrameSeq < root.ProcessFrameSeq,
            "ProcessFrame must be post-order: child before parent.");
    }

    [Fact]
    public void Evaluate_VisitsEveryChildExactlyOnce()
    {
        var a = new CountingPlayable();
        var b = new CountingPlayable();
        var c = new CountingPlayable();
        var root = new GroupPlayable();
        root.AddInput(a);
        root.AddInput(b);
        root.AddInput(c);

        var graph = new PlayableGraph { Root = root };
        graph.Evaluate(0.016f);

        Assert.Equal(1, root.PrepareFrameCount);
        Assert.Equal(1, a.PrepareFrameCount);
        Assert.Equal(1, b.PrepareFrameCount);
        Assert.Equal(1, c.PrepareFrameCount);
        Assert.Equal(1, root.ProcessFrameCount);
        Assert.Equal(1, a.ProcessFrameCount);
        Assert.Equal(1, b.ProcessFrameCount);
        Assert.Equal(1, c.ProcessFrameCount);
    }

    [Fact]
    public void Evaluate_RunsOutputsAfterProcessFrame()
    {
        Sequence.Reset();
        var leaf = new CountingPlayable();
        var output = new RecordingOutput { Source = leaf };
        var graph = new PlayableGraph { Root = leaf };
        graph.AddOutput(output);

        graph.Evaluate(0.016f);

        Assert.Equal(1, output.ApplyCount);
        Assert.True(leaf.ProcessFrameSeq < output.ApplySeq,
            "Outputs must apply after every ProcessFrame has completed.");
    }

    [Fact]
    public void Evaluate_RunsMultipleOutputsInInsertionOrder()
    {
        Sequence.Reset();
        var leaf = new CountingPlayable();
        var first = new RecordingOutput { Source = leaf };
        var second = new RecordingOutput { Source = leaf };
        var third = new RecordingOutput { Source = leaf };
        var graph = new PlayableGraph { Root = leaf };
        graph.AddOutput(first);
        graph.AddOutput(second);
        graph.AddOutput(third);

        graph.Evaluate(0.016f);

        Assert.True(first.ApplySeq < second.ApplySeq);
        Assert.True(second.ApplySeq < third.ApplySeq);
    }

    [Fact]
    public void Destroy_ClearsRootAndOutputs()
    {
        var graph = new PlayableGraph
        {
            Root = new CountingPlayable(),
        };
        graph.AddOutput(new RecordingOutput());

        graph.Destroy();

        Assert.Null(graph.Root);
        Assert.Equal(0, graph.OutputCount);
    }

    [Fact]
    public void Evaluate_IsSteadyStateZeroAllocation()
    {
        // The hot-path contract: per-frame evaluation must not allocate on the managed heap.
        // PR0007 enforces this statically for [HotPath] code; this test verifies it dynamically.
        var leaf = new CountingPlayable();
        var childA = new CountingPlayable();
        var childB = new CountingPlayable();
        var root = new GroupPlayable();
        root.AddInput(childA);
        root.AddInput(childB);
        root.AddInput(leaf);
        var output = new RecordingOutput { Source = leaf };
        var graph = new PlayableGraph { Root = root };
        graph.AddOutput(output);

        graph.Evaluate(0.016f); // warm up: JIT / any one-time work.

        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            graph.Evaluate(0.016f);
        }
        long after = System.GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    [Fact]
    public void Evaluate_WalksEachOutputSource_NotJustRoot()
    {
        // The Timeline model: multiple outputs each with their own Source subgraph (no single Root).
        // Every output's Source must be walked each frame so its Apply sees a freshly sampled result.
        Sequence.Reset();
        var trackA = new CountingPlayable();
        var trackB = new CountingPlayable();
        var outA = new RecordingOutput { Source = trackA };
        var outB = new RecordingOutput { Source = trackB };
        var graph = new PlayableGraph { Root = null };
        graph.AddOutput(outA);
        graph.AddOutput(outB);

        graph.Evaluate(0.016f);

        Assert.Equal(1, trackA.PrepareFrameCount);
        Assert.Equal(1, trackA.ProcessFrameCount);
        Assert.Equal(1, trackB.PrepareFrameCount);
        Assert.Equal(1, trackB.ProcessFrameCount);
        Assert.Equal(1, outA.ApplyCount);
        Assert.Equal(1, outB.ApplyCount);
    }
}
