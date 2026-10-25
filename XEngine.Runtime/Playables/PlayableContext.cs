// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

namespace XEngine.Playables;

/// <summary>
/// Per-evaluation context passed through the playable graph. Carries everything a node needs to
/// advance itself for one frame. All fields are immutable for the duration of one
/// <see cref="PlayableGraph.Evaluate"/> call.
/// </summary>
/// <remarks>
/// <para>
/// Two time models coexist on purpose:
/// <list type="bullet">
///   <item>State-machine driven nodes (e.g. an Animator's clip) read <see cref="DeltaTime"/> and
///   advance their own local time. They ignore <see cref="GlobalTime"/>.</item>
///   <item>Schedule-driven nodes (e.g. a Timeline clip on a track) read <see cref="GlobalTime"/>
///   and compute their local time from it (localTime = globalTime - clipStart). They ignore
///   <see cref="DeltaTime"/> for time advancement.</item>
/// </list>
/// </para>
/// <para>
/// <see cref="IsScrubbing"/> is set when the graph was sampled at a fixed playhead (the caller set
/// the clock time directly rather than advancing it). Schedule-driven nodes use it to skip
/// side effects for times leapt over; state-machine nodes can ignore it.
/// </para>
/// </remarks>
public readonly struct PlayableContext
{
    /// <summary>
    /// The scaled delta time for this frame (already multiplied by the clock's TimeScale). Zero
    /// during a scrub.
    /// </summary>
    public readonly float DeltaTime;

    /// <summary>The graph's global clock time (post-wrap) for this evaluation.</summary>
    public readonly double GlobalTime;

    /// <summary>
    /// True when the clock was sampled at a fixed playhead rather than advanced. See remarks on
    /// <see cref="PlayableContext"/>.
    /// </summary>
    public readonly bool IsScrubbing;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PlayableContext(float deltaTime, double globalTime, bool isScrubbing)
    {
        DeltaTime = deltaTime;
        GlobalTime = globalTime;
        IsScrubbing = isScrubbing;
    }
}
