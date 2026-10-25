// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Playables;
using XEngine.Runtime;
using XEngine.Runtime.Audio;
using XEngine.Runtime.ParticleSystem;
using XEngine.Runtime.Resources;

namespace XEngine.Timeline;

// ───────────────────────── Audio ─────────────────────────

/// <summary>A scheduled audio clip on a bound <see cref="AudioSource"/>.</summary>
public class AudioTrackClip
{
    public AssetRef<AudioClip> Clip;
    public TimelineClipSchedule Schedule;
    public float Volume = 1f;
}

/// <summary>Drives a bound <see cref="AudioSource"/>: plays the active clip, seeks to its local time, scales volume by the clip weight.</summary>
public class AudioTrack : DirectDriveTrack<AudioSource>
{
    public List<AudioTrackClip> Clips = new();

    internal override void ApplyTarget(PlayableContext ctx, AudioSource? target, double time)
    {
        if (target == null) return;
        for (int i = 0; i < Clips.Count; i++)
        {
            var c = Clips[i];
            if (!c.Schedule.Contains(time)) continue;
            var clip = c.Clip.Res;
            if (clip == null) continue;
            target.Clip = clip;
            target.Volume = c.Volume * c.Schedule.Weight(time);
            if (!target.IsPlaying) target.Play();
            // Cursor is PCM samples; AudioContext.SampleRate is the mixer rate (cheap, no decode).
            int sr = AudioContext.SampleRate;
            if (sr > 0)
            {
                ulong samples = (ulong)(c.Schedule.LocalTime(time) * sr);
                if (target.Cursor != samples) target.Cursor = samples;
            }
            return;
        }
    }
}

// ───────────────────────── Activation ─────────────────────────

/// <summary>A scheduled activation window on a bound <see cref="GameObject"/>.</summary>
public class ActivationTrackClip { public TimelineClipSchedule Schedule; public bool Active = true; }

/// <summary>Toggles a bound <see cref="GameObject"/>'s Enabled to match the active clip; disables when no clip is active.</summary>
public class ActivationTrack : DirectDriveTrack<GameObject>
{
    public List<ActivationTrackClip> Clips = new();

    internal override void ApplyTarget(PlayableContext ctx, GameObject? target, double time)
    {
        if (target == null) return;
        for (int i = 0; i < Clips.Count; i++)
            if (Clips[i].Schedule.Contains(time)) { target.Enabled = Clips[i].Active; return; }
        target.Enabled = false; // no active clip → hide.
    }
}

// ───────────────────────── Camera ─────────────────────────

/// <summary>A scheduled camera shot on a bound <see cref="Camera"/>. M12: cut only (enable/disable); blend is M12.1.</summary>
public class CameraTrackClip { public TimelineClipSchedule Schedule; public AssetRef<Camera> Camera; }

/// <summary>Enables the active clip's bound <see cref="Camera"/>; disables the others when its clip ends.</summary>
public class CameraTrack : DirectDriveTrack<Camera>
{
    public List<CameraTrackClip> Clips = new();

    internal override void ApplyTarget(PlayableContext ctx, Camera? defaultTarget, double time)
    {
        // Each clip references its own camera; enable the active one, disable the rest.
        for (int i = 0; i < Clips.Count; i++)
        {
            var cam = Clips[i].Camera.Res;
            if (cam == null) continue;
            cam.Enabled = Clips[i].Schedule.Contains(time);
        }
    }
}

// ───────────────────────── Particle ─────────────────────────

/// <summary>A scheduled particle burst on a bound <see cref="ParticleSystemComponent"/>.</summary>
public class ParticleTrackClip { public TimelineClipSchedule Schedule; }

/// <summary>Plays a bound <see cref="ParticleSystemComponent"/> while a clip is active; stops it when none is.</summary>
public class ParticleTrack : DirectDriveTrack<ParticleSystemComponent>
{
    public List<ParticleTrackClip> Clips = new();
    private bool _wasActive;

    internal override void ApplyTarget(PlayableContext ctx, ParticleSystemComponent? target, double time)
    {
        if (target == null) return;
        bool active = false;
        for (int i = 0; i < Clips.Count; i++)
            if (Clips[i].Schedule.Contains(time)) { active = true; break; }

        if (active && !_wasActive) target.Play();
        else if (!active && _wasActive) target.Stop();
        _wasActive = active;
    }
}

// ───────────────────────── Base ─────────────────────────

/// <summary>
/// Base for tracks that drive a bound scene target directly from <see cref="PlayableOutput.Apply"/>
/// (no per-track playable subgraph): the output reads the clock time each frame and writes the target.
/// </summary>
public abstract class DirectDriveTrack<T> : TimelineTrack where T : class
{
    /// <summary>The track's single output: holds the bound target and forwards Apply to the subclass.</summary>
    internal sealed class DirectOutput : PlayableOutput
    {
        private readonly DirectDriveTrack<T> _track;
        private readonly T? _target;

        internal DirectOutput(DirectDriveTrack<T> track, T? target)
        {
            _track = track;
            _target = target;
        }

        [XEngine.HotPath]
        public override void Apply(PlayableContext ctx)
            => _track.ApplyTarget(ctx, _target, ctx.GlobalTime);
    }

    public override void BuildInto(PlayableGraph graph, PlayableDirector director)
    {
        T? target = director.GetBinding<T>(this);
        graph.AddOutput(new DirectOutput(this, target));
    }

    /// <summary>Called each frame with the clock time and the bound target (which may be null).</summary>
    internal abstract void ApplyTarget(PlayableContext ctx, T? target, double time);
}
