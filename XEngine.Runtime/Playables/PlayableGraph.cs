// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine;

namespace XEngine.Playables;

/// <summary>
/// A directed acyclic graph of <see cref="Playable"/> nodes evaluated against a <see cref="Clock"/>.
/// One graph feeds one or more typed <see cref="PlayableOutput"/>s. Each evaluation runs in two
/// phases — a pre-order <see cref="Playable.PrepareFrame"/> pass (advance time/weight, decide which
/// children are active) followed by a post-order <see cref="Playable.ProcessFrame"/> pass (read
/// child outputs, produce own output) — then applies every output to its engine target.
/// </summary>
/// <remarks>
/// <para>
/// Two entry points cover the two consumers:
/// <list type="bullet">
///   <item><see cref="Evaluate"/> — advance the clock by a delta and evaluate. Used by the Animator
///   (continuous, game-time driven) and the Timeline director in play mode.</item>
///   <item><see cref="Sample"/> — evaluate at the clock's current time without advancing it. Used by
///   the Timeline director while scrubbing.</item>
/// </list>
/// </para>
/// <para>
/// <b>Hot path.</b> <see cref="Evaluate"/> and <see cref="Sample"/> are per-frame and must stay
/// allocation-free. The node walk uses indexed <see cref="Playable.InputCount"/> /
/// <see cref="Playable.GetInput"/> access (no enumerator), the context is a stack-allocated struct,
/// and the outputs list is a retained field iterated by index. PR0002–PR0007 enforce this.
/// </para>
/// </remarks>
public sealed class PlayableGraph
{
    private readonly List<PlayableOutput> _outputs = new();
    private Clock _clock;

    /// <summary>The clock driving this graph. Never <c>null</c>.</summary>
    public Clock Clock => _clock;

    /// <summary>The root playable, or <c>null</c> for an empty graph.</summary>
    public Playable? Root { get; set; }

    /// <summary>Number of outputs attached to this graph.</summary>
    public int OutputCount => _outputs.Count;

    /// <summary>Creates a graph with a default <see cref="GameClock"/>.</summary>
    public PlayableGraph() : this(new GameClock()) { }

    /// <summary>Creates a graph driven by the given clock.</summary>
    public PlayableGraph(Clock clock)
    {
        _clock = clock ?? ThrowClockNull();
    }

    /// <summary>Fetches the output at <paramref name="index"/>.</summary>
    public PlayableOutput GetOutput(int index) => _outputs[index];

    /// <summary>Appends an output. Outputs apply in insertion order each frame.</summary>
    public void AddOutput(PlayableOutput output) => _outputs.Add(output);

    /// <summary>Removes the first occurrence of <paramref name="output"/>. Returns true if removed.</summary>
    public bool RemoveOutput(PlayableOutput output) => _outputs.Remove(output);

    /// <summary>Removes all outputs.</summary>
    public void ClearOutputs() => _outputs.Clear();

    /// <summary>Replaces the clock. The previous clock's state is discarded.</summary>
    public void SetClock(Clock clock) => _clock = clock ?? ThrowClockNull();

    /// <summary>
    /// Advances the clock by <paramref name="rawDelta"/> seconds and evaluates the graph. This is
    /// the per-frame entry point for continuous playback (Animator / Timeline play mode).
    /// </summary>
    [HotPath]
    public void Evaluate(float rawDelta)
    {
        float scaled = _clock.Advance(rawDelta);
        var ctx = new PlayableContext(scaled, _clock.CurrentTime, isScrubbing: false);
        EvaluateInternal(ctx);
    }

    /// <summary>
    /// Evaluates the graph at the clock's current time without advancing it. The produced context
    /// carries <see cref="PlayableContext.IsScrubbing"/>=true so schedule-driven nodes can resync.
    /// This is the scrub entry point for the Timeline director.
    /// </summary>
    [HotPath]
    public void Sample()
    {
        var ctx = new PlayableContext(0f, _clock.CurrentTime, isScrubbing: true);
        EvaluateInternal(ctx);
    }

    private void EvaluateInternal(PlayableContext ctx)
    {
        // Walk the root's subtree (the Animator's single-root case), then any output Source that isn't
        // already under the root (Timeline's multi-track case, where each track is its own subgraph
        // feeding its own output). Outputs whose Source is the root aren't re-walked.
        Playable? root = Root;
        if (root != null)
        {
            PropagatePrepareFrame(root, ctx);
            PropagateProcessFrame(root, ctx);
        }

        int count = _outputs.Count;
        for (int i = 0; i < count; i++)
        {
            Playable? src = _outputs[i].Source;
            if (src != null && src != root)
            {
                PropagatePrepareFrame(src, ctx);
                PropagateProcessFrame(src, ctx);
            }
        }

        for (int i = 0; i < count; i++)
        {
            _outputs[i].Apply(ctx);
        }

        // M49 — deferred events flush after outputs applied. Handlers see the settled frame.
        PlayableEventQueue.Shared.Flush(PlayableEventQueue.Shared.NamedRouter);
    }

    // Pre-order: parent before children. Time/weight decisions propagate downwards so children see
    // their parent's resolved state for the frame.
    private static void PropagatePrepareFrame(Playable node, PlayableContext ctx)
    {
        node.PrepareFrame(ctx);
        int count = node.InputCount;
        for (int i = 0; i < count; i++)
        {
            PropagatePrepareFrame(node.GetInput(i), ctx);
        }
    }

    // Post-order: children before parent. Each node reads already-resolved child outputs when its
    // own ProcessFrame runs.
    private static void PropagateProcessFrame(Playable node, PlayableContext ctx)
    {
        int count = node.InputCount;
        for (int i = 0; i < count; i++)
        {
            PropagateProcessFrame(node.GetInput(i), ctx);
        }
        node.ProcessFrame(ctx);
    }

    /// <summary>
    /// Tears down the graph: drops the root and clears outputs. Owned playables are not explicitly
    /// disposed (they have no native resources in M0); they become eligible for GC once unreferenced.
    /// </summary>
    public void Destroy()
    {
        Root = null;
        _outputs.Clear();
    }

    // Cold helper: exception construction allocates, so it stays out of the constructor's inlined path.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Clock ThrowClockNull()
        => throw new System.ArgumentNullException("clock", "A PlayableGraph requires a non-null Clock.");
}
