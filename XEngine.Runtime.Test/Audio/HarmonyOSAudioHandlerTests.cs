// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Platforms.HarmonyOS;

using Xunit;

namespace XEngine.Runtime.Test.Audio;

public sealed class HarmonyOSAudioHandlerTests
{
    [Fact]
    public void PlaybackRequiresResumeHostReadinessAndFocus()
    {
        var transitions = new List<bool>();
        var audio = new HarmonyOSAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });

        audio.Resume();
        audio.SetHostReady(true);
        Assert.Empty(transitions);

        audio.FocusChanged(true);

        Assert.Equal([true], transitions);
        Assert.True(audio.Stats.IsPlaybackRunning);
    }

    [Fact]
    public void FocusLossStopsPlaybackAndGainRestartsIt()
    {
        var transitions = new List<bool>();
        var audio = new HarmonyOSAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });
        audio.SetHostReady(true);
        audio.Resume();
        audio.FocusChanged(true);

        audio.FocusChanged(false);
        audio.FocusChanged(true);

        Assert.Equal([true, false, true], transitions);
        Assert.Equal(3, audio.Stats.FocusChangeCount);
    }

    [Fact]
    public void RouteChangeRestartsActiveBackendWithNewFormat()
    {
        var transitions = new List<bool>();
        var audio = new HarmonyOSAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });
        audio.SetHostReady(true);
        audio.Resume();
        audio.FocusChanged(true);

        audio.RouteChanged(HarmonyOSAudioRoute.Bluetooth, 48000, 192);

        Assert.Equal([true, false, true], transitions);
        Assert.Equal(HarmonyOSAudioRoute.Bluetooth, audio.Stats.Route);
        Assert.Equal(48000, audio.Stats.SampleRate);
        Assert.Equal(192, audio.Stats.FramesPerBuffer);
        Assert.Equal(1, audio.Stats.RouteRestartCount);
    }
}
