// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Playables;

namespace XEngine.Timeline;

/// <summary>
/// Base for a timeline track: a named lane of clips that the director builds into its playable graph.
/// A subclass implements <see cref="BuildInto"/> to add its output(s) to the graph, typically driving
/// a bound scene target (Animator / AudioSource / GameObject / Camera / ParticleSystem / SignalReceiver).
/// </summary>
public abstract class TimelineTrack
{
    /// <summary>Display name of the track (also shown in the Director window).</summary>
    public string Name = string.Empty;

    /// <summary>
    /// Builds this track's subgraph + output(s) into <paramref name="graph"/>. The director calls this
    /// on <see cref="PlayableDirector.RebuildGraph"/>. Implementations read their clip data and the
    /// director's bindings, then register a <see cref="PlayableOutput"/> whose Apply advances each frame.
    /// </summary>
    public abstract void BuildInto(PlayableGraph graph, PlayableDirector director);
}

/// <summary>
/// A clip's schedule on the timeline: starts at <see cref="Start"/> and runs for <see cref="Duration"/>
/// seconds, with optional blend in/out at the edges. Per-track clip subclasses add their payload
/// (an AnimationClip, an AudioSource target, etc.).
/// </summary>
public struct TimelineClipSchedule
{
    public double Start;
    public double Duration;
    public float BlendIn;
    public float BlendOut;

    /// <summary>The clip's end time on the timeline.</summary>
    public double End => Start + Duration;

    /// <summary>True if this clip is active at timeline time <paramref name="t"/>.</summary>
    public bool Contains(double t) => t >= Start && t < End;

    /// <summary>
    /// The clip-local time for timeline time <paramref name="t"/> (0 at the clip's start), or -1 if
    /// the clip isn't active.
    /// </summary>
    public double LocalTime(double t) => Contains(t) ? t - Start : -1.0;

    /// <summary>
    /// The clip's weight at timeline time <paramref name="t"/>, accounting for BlendIn/Out ramps.
    /// 1 inside the clip (away from the ramps), ramping 0→1 over BlendIn at the start, 1→0 over BlendOut.
    /// </summary>
    public float Weight(double t)
    {
        if (!Contains(t)) return 0f;
        double local = t - Start;
        double remaining = End - t;
        float win = BlendIn <= 0f ? 1f : (float)(local / BlendIn);
        float wout = BlendOut <= 0f ? 1f : (float)(remaining / BlendOut);
        if (win < 0f) win = 0f; else if (win > 1f) win = 1f;
        if (wout < 0f) wout = 0f; else if (wout > 1f) wout = 1f;
        float w = win < wout ? win : wout;
        return w;
    }
}
