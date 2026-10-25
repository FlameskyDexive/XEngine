// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Animation; // core animation playables (AnimationPlayable, AnimationClipPlayable, ...)
using XEngine.Playables;
using XEngine.Runtime;
using XEngine;

namespace XEngine.Timeline;

/// <summary>
/// A timeline clip that plays an <see cref="AnimationClip"/> on a bound Animator, scheduled at
/// [Start, Start+Duration) on the timeline with optional blend in/out.
/// </summary>
public class AnimationTrackClip
{
    public AssetRef<AnimationClip> Clip;
    public TimelineClipSchedule Schedule;
}

/// <summary>
/// An animation track: plays its scheduled <see cref="AnimationClip"/>s on a bound Animator via the
/// core <see cref="AnimationClipPlayable"/> / <see cref="AnimationPlayableOutput"/>. At most one clip
/// is active at a time (M11); overlapping clips blend via the mixer weights.
/// </summary>
public class AnimationTrack : TimelineTrack
{
    /// <summary>The clips on this track, in schedule order.</summary>
    public List<AnimationTrackClip> Clips = new();

    public override void BuildInto(PlayableGraph graph, PlayableDirector director)
    {
        Animator? animator = director.GetBinding<Animator>(this);
        if (animator == null || animator.Skeleton == null || Clips.Count == 0)
            return;

        var mixer = new TimelineAnimationMixer();
        mixer.Bind(animator.Skeleton);

        for (int i = 0; i < Clips.Count; i++)
        {
            AnimationClip? clip = Clips[i].Clip.Res;
            if (clip == null) continue;
            var playable = new AnimationClipPlayable(clip);
            playable.Bind(animator.Skeleton!);
            playable.Play();
            playable.Weight = 0f;
            mixer.AddClip(playable, Clips[i].Schedule);
        }

        var output = new AnimationPlayableOutput { Source = mixer, Binding = animator.Skeleton };
        graph.AddOutput(output);
    }
}

/// <summary>
/// Per-track animation mixer: each frame, reads the graph clock time, sets each child clip's local
/// time and weight from its schedule (active / blend in/out), then blends the active children's poses.
/// </summary>
internal sealed class TimelineAnimationMixer : AnimationPlayable
{
    private readonly List<AnimationClipPlayable> _clips = new();
    private readonly List<TimelineClipSchedule> _schedules = new();

    public override int InputCount => _clips.Count;
    public override Playable GetInput(int index) => _clips[index];

    public void Bind(SkeletonBinding binding) { Binding = binding; AllocateOutput(); }

    public void AddClip(AnimationClipPlayable clip, TimelineClipSchedule schedule)
    {
        _clips.Add(clip);
        _schedules.Add(schedule);
    }

    [HotPath]
    public override void PrepareFrame(PlayableContext ctx)
    {
        base.PrepareFrame(ctx);
        double t = ctx.GlobalTime;
        for (int i = 0; i < _clips.Count; i++)
        {
            var sched = _schedules[i];
            var clip = _clips[i];
            if (sched.Contains(t))
            {
                if (clip.State != PlayableState.Playing) clip.Play();
                clip.SetTime(sched.LocalTime(t));
                clip.Weight = sched.Weight(t);
            }
            else
            {
                clip.Weight = 0f;
            }
        }
    }

    [HotPath]
    public override void ProcessFrame(PlayableContext ctx)
    {
        Pose? pose = Output;
        if (pose == null) return;
        pose.Clear();

        float total = 0f;
        for (int i = 0; i < _clips.Count; i++)
            if (_clips[i].Weight > 0f) total += _clips[i].Weight;
        if (total <= 0f) return;

        float inv = 1f / total;
        for (int i = 0; i < _clips.Count; i++)
        {
            if (_clips[i].Weight <= 0f) continue;
            Pose? childPose = _clips[i].Output;
            if (childPose != null) pose.Accumulate(childPose, _clips[i].Weight * inv);
        }
        pose.NormalizeRotations();
    }
}
