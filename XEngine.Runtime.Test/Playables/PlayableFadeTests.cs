// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Playables;

namespace XEngine.Runtime.Test.Playables;

/// <summary>
/// Verifies the <see cref="Playable"/> weight-fade contract: instant snaps, finite-duration fades
/// converge exactly on the target without overshoot, the <see cref="Playable.Weight"/> setter
/// cancels an in-progress fade, and fades work in both directions.
/// </summary>
public class PlayableFadeTests
{
    [Fact]
    public void DefaultWeightIsZero()
    {
        var node = new CountingPlayable();
        Assert.Equal(0f, node.Weight);
        Assert.Equal(0f, node.TargetWeight);
        Assert.False(node.IsFading);
    }

    [Fact]
    public void StartFade_WithZeroDuration_SnapsImmediately()
    {
        var node = new CountingPlayable();

        node.StartFade(targetWeight: 1f, duration: 0f);

        Assert.Equal(1f, node.Weight);
        Assert.Equal(1f, node.TargetWeight);
        Assert.False(node.IsFading);
    }

    [Fact]
    public void StartFade_WithNegativeDuration_SnapsImmediately()
    {
        var node = new CountingPlayable();

        node.StartFade(targetWeight: 0.7f, duration: -1f);

        Assert.Equal(0.7f, node.Weight);
        Assert.False(node.IsFading);
    }

    [Fact]
    public void StartFade_ConvergesOnTargetOverDuration()
    {
        // Fade 0 -> 1 over 1 second. After 0.5s we should be at ~0.5; after 1.0s exactly at 1.0.
        var node = new CountingPlayable();
        node.StartFade(targetWeight: 1f, duration: 1f);

        Assert.True(node.IsFading);

        // Halfway.
        node.PrepareFrame(new PlayableContext(deltaTime: 0.5f, globalTime: 0.5, isScrubbing: false));
        Assert.Equal(0.5f, node.Weight, precision: 5);
        Assert.True(node.IsFading);

        // Complete.
        node.PrepareFrame(new PlayableContext(deltaTime: 0.5f, globalTime: 1.0, isScrubbing: false));
        Assert.Equal(1f, node.Weight, precision: 5);
        Assert.Equal(1f, node.TargetWeight);
        Assert.False(node.IsFading);
    }

    [Fact]
    public void StartFade_CanDeferTheFirstGraphStep()
    {
        var node = new CountingPlayable();
        node.StartFade(targetWeight: 1f, duration: 1f, skipFirstUpdate: true);

        node.PrepareFrame(new PlayableContext(1f / 60f, 1.0 / 60.0, false));
        Assert.Equal(0f, node.Weight);
        Assert.True(node.IsFading);

        node.PrepareFrame(new PlayableContext(1f / 60f, 2.0 / 60.0, false));
        Assert.Equal(1f / 60f, node.Weight, precision: 5);
    }

    [Fact]
    public void StartFade_DoesNotOvershootTarget()
    {
        // Fade 0 -> 1 over 1s, then advance well past the duration: must clamp to target, not exceed.
        var node = new CountingPlayable();
        node.StartFade(targetWeight: 1f, duration: 1f);

        node.PrepareFrame(new PlayableContext(deltaTime: 10f, globalTime: 10.0, isScrubbing: false));

        Assert.Equal(1f, node.Weight, precision: 5);
        Assert.False(node.IsFading);
    }

    [Fact]
    public void StartFade_FadesDownwardToZero()
    {
        var node = new CountingPlayable { Weight = 1f };

        node.StartFade(targetWeight: 0f, duration: 0.5f);
        Assert.True(node.IsFading);

        node.PrepareFrame(new PlayableContext(deltaTime: 0.25f, globalTime: 0.25, isScrubbing: false));
        Assert.Equal(0.5f, node.Weight, precision: 5);

        node.PrepareFrame(new PlayableContext(deltaTime: 0.25f, globalTime: 0.5, isScrubbing: false));
        Assert.Equal(0f, node.Weight, precision: 5);
        Assert.False(node.IsFading);
    }

    [Fact]
    public void Weight_SetterCancelsFade()
    {
        var node = new CountingPlayable();
        node.StartFade(targetWeight: 1f, duration: 1f);
        Assert.True(node.IsFading);

        node.Weight = 0.3f;

        Assert.Equal(0.3f, node.Weight);
        Assert.Equal(0.3f, node.TargetWeight);
        Assert.False(node.IsFading);
    }

    [Fact]
    public void StartFade_ReplacesInProgressFade()
    {
        var node = new CountingPlayable();
        node.StartFade(targetWeight: 1f, duration: 1f);
        node.PrepareFrame(new PlayableContext(0.5f, 0.5, false));
        Assert.Equal(0.5f, node.Weight, precision: 5);

        // Retarget from the current weight (0.5) to 0 over 0.5s.
        node.StartFade(targetWeight: 0f, duration: 0.5f);

        node.PrepareFrame(new PlayableContext(0.5f, 1.0, false));
        Assert.Equal(0f, node.Weight, precision: 5);
        Assert.False(node.IsFading);
    }

    [Theory]
    [InlineData(0f, 1f, 0.5f, 0.5f)]
    [InlineData(0f, 1f, 1f, 1f)]
    [InlineData(1f, 0f, 0.5f, 0.5f)]
    [InlineData(0.2f, 0.8f, 0.8f, 0.8f)]
    public void StartFade_HitsExactTargetAtDuration(float start, float target, float duration, float advance)
    {
        // Advance equals duration in every row: the fade must land exactly on target with no overshoot.
        var node = new CountingPlayable { Weight = start };
        node.StartFade(target, duration);

        node.PrepareFrame(new PlayableContext(advance, advance, false));

        Assert.Equal(target, node.Weight, precision: 5);
    }
}
