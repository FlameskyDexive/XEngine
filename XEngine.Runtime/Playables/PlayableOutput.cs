// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine;

namespace XEngine.Playables;

/// <summary>
/// A typed sink that writes a playable graph's output into a concrete engine target — a set of
/// Transforms (animation), an AudioSource (audio), a GameObject's Enabled flag (activation), a
/// Camera, a ParticleSystem, or a SignalReceiver. The graph owns one or more outputs; each frame,
/// after the two-phase node walk completes, every output's <see cref="Apply"/> runs to flush the
/// graph's computed state into the engine.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors Unity's <c>PlayableOutput</c> / <c>AnimationPlayableOutput</c> / <c>AudioPlayableOutput</c>
/// split. The base contract is intentionally minimal: a source playable and an <see cref="Apply"/>
/// hook. Concrete outputs live with their consumers — <c>AnimationPlayableOutput</c> ships in
/// <c>XEngine.Runtime</c> (the Animator needs it directly), while audio / activation / camera /
/// particle / signal outputs ship in the Timeline package and reference engine subsystems there.
/// </para>
/// </remarks>
public abstract class PlayableOutput
{
    /// <summary>
    /// The root playable feeding this output. May be <c>null</c> (the output then does nothing).
    /// </summary>
    public Playable? Source { get; set; }

    /// <summary>
    /// Overall output weight in [0,1]. Typically mixed into the final contribution alongside the
    /// source playable's own weight. Defaults to 1.
    /// </summary>
    public float Weight { get; set; } = 1f;

    /// <summary>
    /// Flushes the graph's computed state for this output into its engine target. Called once per
    /// frame, after every node's <see cref="Playable.ProcessFrame"/> has run.
    /// </summary>
    [HotPath]
    public abstract void Apply(PlayableContext ctx);
}
