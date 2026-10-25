// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

using global::XEngine.Animation;
using global::XEngine.Animation.Transitions;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M49 — event sequence v2: sorted timed events with a distinct end event, forward/backward firing
/// across loop wraps with a per-frame loop cap, deferred invocation (handlers run after the pose is
/// applied), named events routed through the Animator, and transition-owned named events.
/// </summary>
public class AnimationEventSequenceTests : RuntimeTestBase
{
    private (Scene scene, GameObject root, Animator animator) BuildAnimator()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        return (scene, root, animator);
    }

    private static AnimationClip MovingClip() // 1s: x goes 0 → 10 over the clip.
        => AnimationTestHelpers.MakeSingleBonePosXClip("move", "Hips", endX: 10f, duration: 1f);

    [Fact]
    public void TimedEvents_FireInSortedOrderWhenCrossed()
    {
        var (scene, _, animator) = BuildAnimator();
        var state = animator.Play(MovingClip());
        var order = new System.Collections.Generic.List<string>();
        state.AddEvent(0.8f, () => order.Add("late"));
        state.AddEvent(0.2f, () => order.Add("early"));

        Update(scene, frames: 55);            // 0.917s: crosses both 0.2 and 0.8.

        Assert.Equal(new[] { "early", "late" }, order);
    }

    [Fact]
    public void Events_DeferUntilAfterPoseApplied()
    {
        var (scene, root, animator) = BuildAnimator();
        var clip = MovingClip();
        var state = animator.Play(clip);

        float? xWhenFired = null;
        state.AddEvent(0.5f, () => xWhenFired = root.Children[0].Transform.LocalPosition.X);

        Update(scene, frames: 31);            // Fires crossing 0.5; pose sampled at ~0.5 → x ≈ 5.

        Assert.NotNull(xWhenFired);
        Assert.True(MathF.Abs(xWhenFired!.Value - 5f) < 0.05f,
            $"handler should observe the applied pose (~5.0), got {xWhenFired}");
    }

    [Fact]
    public void LoopWrap_FiresTailThenHead_WithinOneFrame()
    {
        var (scene, _, animator) = BuildAnimator();
        var state = animator.Play(MovingClip());
        var hits = new System.Collections.Generic.List<string>();
        state.AddEvent(0.9f, () => hits.Add("tail"));
        state.AddEvent(0.1f, () => hits.Add("head"));

        state.Time = 0.85;                    // Fast playback so ONE frame spans the wrap.
        state.Speed = 20f;                    // 1/60s × 20 ≈ 0.333s per frame.
        Update(scene, frames: 1);             // 0.85 → 1.18 → wraps to 0.18.

        Assert.Equal(new[] { "tail", "head" }, hits);
    }

    [Fact]
    public void EndEvent_FiresOnLoopCompletion_AfterOtherEvents()
    {
        var (scene, _, animator) = BuildAnimator();
        var state = animator.Play(MovingClip());
        var order = new System.Collections.Generic.List<string>();
        state.AddEvent(0.5f, () => order.Add("mid"));
        state.SetEndEvent(() => order.Add("end"));

        Update(scene, frames: 61);            // Wraps past 1.0.

        Assert.Equal(new[] { "mid", "end" }, order);
    }

    [Fact]
    public void BackwardPlayback_FiresEventsInReverse()
    {
        var (scene, _, animator) = BuildAnimator();
        var state = animator.Play(MovingClip());
        var hits = new System.Collections.Generic.List<string>();
        state.AddEvent(0.2f, () => hits.Add("a"));
        state.AddEvent(0.5f, () => hits.Add("b"));

        state.Time = 0.667;                   // Play backward across 0.5 then 0.2.
        state.Speed = -1f;
        Update(scene, frames: 30);

        Assert.Equal(new[] { "b", "a" }, hits);
    }

    [Fact]
    public void NamedEvents_RouteToAnimatorHandlerTable()
    {
        var (scene, _, animator) = BuildAnimator();
        var state = animator.Play(MovingClip());
        int footstep = 0;
        animator.Events.Add("Footstep", () => footstep++);
        state.AddEvent(0.5f, null, "Footstep");

        Update(scene, frames: 31);

        Assert.Equal(1, footstep);
    }

    [Fact]
    public void TransitionEvents_ApplyToState_AndReplaceOnReplay()
    {
        var (scene, _, animator) = BuildAnimator();
        var t = new ClipTransition
        {
            Clip = new AssetRef<AnimationClip>(MovingClip()),
            FadeDuration = 0f,
        };
        t.Events.Add(new TransitionEvent { NormalizedTime = 0.5f, Name = "Hit" });

        int hits = 0;
        animator.Events.Add("Hit", () => hits++);

        animator.Play(t);
        animator.Play(t);                     // Replay must not duplicate the event.
        Update(scene, frames: 31);

        Assert.Equal(1, hits);
        var state = (AnimationClipPlayable)animator.GetState(t)!;
        Assert.Equal(1, state.EventSequence!.Count);
    }

    [Fact]
    public void LoopCap_HugeTimeStep_DoesNotFloodHandlers()
    {
        var (scene, _, animator) = BuildAnimator();
        var state = animator.Play(MovingClip());
        int fired = 0;
        state.AddEvent(0.5f, () => fired++);

        // Simulate a huge catch-up frame spanning many loops: set the clock far ahead by scrubbing.
        state.Time = 0.9;
        Update(scene, frames: 1);             // Settle.
        fired = 0;
        state.Speed = 1000f;                  // One frame advances ~16 loops.
        Update(scene, frames: 1);

        Assert.True(fired <= AnimationEventSequence.MaximumFullLoopCount,
            $"loop cap should bound the burst, got {fired}");
    }

    [Fact]
    public void Sequence_VersionBumpsOnMutation()
    {
        var seq = new AnimationEventSequence();
        int v0 = seq.Version;
        seq.Add(0.5f, null);
        int v1 = seq.Version;
        seq.SetEnd(null);
        int v2 = seq.Version;
        seq.Clear();

        Assert.True(v1 > v0);
        Assert.True(v2 > v1);
        Assert.True(seq.Version > v2);
        Assert.Equal(0, seq.Count);
        Assert.False(seq.HasEndEvent);
    }

    [Fact]
    public void EventFire_SteadyState_IsAllocationFree()
    {
        var (scene, _, animator) = BuildAnimator();
        var state = animator.Play(MovingClip());
        int hits = 0;
        state.AddEvent(0.5f, () => hits++);

        Update(scene, frames: 65);            // Warm up past one fire.

        long before = GC.GetAllocatedBytesForCurrentThread();
        Update(scene, frames: 120);           // Two more loop fires.
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.True(hits >= 3);
    }
}
