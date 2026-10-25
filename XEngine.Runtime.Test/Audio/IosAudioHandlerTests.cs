// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Platforms.iOS;

using Xunit;

namespace XEngine.Runtime.Test.Audio;

public sealed class IosAudioHandlerTests
{
    [Fact]
    public void ResumeStartsPlaybackAndPauseStopsIt()
    {
        var transitions = new List<bool>();
        var audio = new IosAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });

        audio.Resume();
        Assert.Equal([true], transitions);
        Assert.True(audio.IsPlaybackRunning);
        Assert.Equal("AVAudioSessionCategoryPlayback", IosAudioHandler.PlaybackCategory);

        audio.Pause();
        Assert.Equal([true, false], transitions);
        Assert.False(audio.IsPlaybackRunning);
    }

    [Fact]
    public void InterruptionPausesAndShouldResumeRestartsWhenForeground()
    {
        var transitions = new List<bool>();
        var audio = new IosAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });
        audio.Resume();

        audio.InterruptionBegan();
        Assert.False(audio.IsPlaybackRunning);
        Assert.True(audio.Stats.IsInterrupted);

        audio.InterruptionEnded(shouldResume: true);
        Assert.Equal([true, false, true], transitions);
        Assert.True(audio.IsPlaybackRunning);
        Assert.Equal(2, audio.Stats.InterruptCount);
    }

    [Fact]
    public void InterruptionDoesNotRestartWhileBackgrounded()
    {
        var transitions = new List<bool>();
        var audio = new IosAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });
        audio.Resume();
        audio.Pause();
        audio.InterruptionBegan();
        audio.InterruptionEnded(shouldResume: true);

        Assert.Equal([true, false], transitions);
        Assert.False(audio.IsPlaybackRunning);
        Assert.False(audio.Stats.IsAppResumed);
    }

    [Fact]
    public void NativeBackendNotReadyDoesNotStartPlayback()
    {
        var transitions = new List<bool>();
        var audio = new IosAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });
        audio.SetNativeBackendReady(false);
        audio.Resume();
        Assert.Empty(transitions);
        Assert.False(audio.IsPlaybackRunning);

        audio.SetNativeBackendReady(true);
        Assert.Equal([true], transitions);
    }
}
