// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Threading;

using XEngine.Playables;

namespace XEngine.Runtime.Test.Playables;

/// <summary>
/// Test-only leaf playable: counts PrepareFrame/ProcessFrame calls and records the shared
/// sequence number at which each ran, so tests can assert two-phase traversal order. Writing to
/// these fields allocates nothing, so the same type doubles as the zero-allocation probe.
/// </summary>
internal sealed class CountingPlayable : Playable
{
    public int PrepareFrameCount;
    public int ProcessFrameCount;
    public long PrepareFrameSeq;
    public long ProcessFrameSeq;
    public PlayableContext LastContext;

    public override void PrepareFrame(PlayableContext ctx)
    {
        PrepareFrameCount++;
        PrepareFrameSeq = Interlocked.Increment(ref Sequence.Counter);
        LastContext = ctx;
        base.PrepareFrame(ctx);
    }

    public override void ProcessFrame(PlayableContext ctx)
    {
        ProcessFrameCount++;
        ProcessFrameSeq = Interlocked.Increment(ref Sequence.Counter);
        LastContext = ctx;
    }
}

/// <summary>
/// Test-only branch playable with an explicit list of child inputs, so the graph traversal
/// (<see cref="Playable.InputCount"/> / <see cref="Playable.GetInput"/>) has something to walk.
/// Records the same counters/sequences as <see cref="CountingPlayable"/>.
/// </summary>
internal sealed class GroupPlayable : Playable
{
    private readonly System.Collections.Generic.List<Playable> _inputs = new();

    public int PrepareFrameCount;
    public int ProcessFrameCount;
    public long PrepareFrameSeq;
    public long ProcessFrameSeq;

    public void AddInput(Playable child) => _inputs.Add(child);

    public override int InputCount => _inputs.Count;
    public override Playable GetInput(int index) => _inputs[index];

    public override void PrepareFrame(PlayableContext ctx)
    {
        PrepareFrameCount++;
        PrepareFrameSeq = Interlocked.Increment(ref Sequence.Counter);
        base.PrepareFrame(ctx);
    }

    public override void ProcessFrame(PlayableContext ctx)
    {
        ProcessFrameCount++;
        ProcessFrameSeq = Interlocked.Increment(ref Sequence.Counter);
    }
}

/// <summary>
/// Test-only output that records the number of <see cref="Apply"/> calls and the sequence number
/// of the last one, so tests can assert outputs run after every ProcessFrame.
/// </summary>
internal sealed class RecordingOutput : PlayableOutput
{
    public int ApplyCount;
    public long ApplySeq;

    public override void Apply(PlayableContext ctx)
    {
        ApplyCount++;
        ApplySeq = Interlocked.Increment(ref Sequence.Counter);
    }
}

/// <summary>Shared monotonic counter used to record cross-node call ordering within one test.</summary>
internal static class Sequence
{
    public static long Counter;

    public static void Reset() => Counter = 0;
}
