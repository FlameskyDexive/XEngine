// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Platforms.Android;

using Xunit;

namespace XEngine.Runtime.Test.Audio;

public sealed class AndroidAudioHandlerTests
{
    [Fact]
    public void PlaybackRequiresResumeBackendAndGrantedFocus()
    {
        var transitions = new List<bool>();
        var audio = new AndroidAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });

        audio.Resume();
        audio.SetNativeBackendReady(true);
        Assert.Empty(transitions);

        audio.FocusChanged(AndroidAudioHandler.FocusGain);
        Assert.Equal([true], transitions);
        Assert.True(audio.Stats.IsPlaybackRunning);

        audio.Pause();
        Assert.Equal([true, false], transitions);
        Assert.False(audio.Stats.IsPlaybackRunning);
    }

    [Theory]
    [InlineData(AndroidAudioHandler.FocusLoss)]
    [InlineData(AndroidAudioHandler.FocusLossTransient)]
    [InlineData(AndroidAudioHandler.FocusLossTransientCanDuck)]
    public void FocusLossStopsAndGainRestartsPlayback(int focusLoss)
    {
        var transitions = new List<bool>();
        var audio = new AndroidAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });
        audio.SetNativeBackendReady(true);
        audio.Resume();
        audio.FocusChanged(AndroidAudioHandler.FocusGain);

        audio.FocusChanged(focusLoss);
        audio.FocusChanged(AndroidAudioHandler.FocusGain);

        Assert.Equal([true, false, true], transitions);
        Assert.Equal(3, audio.Stats.FocusChangeCount);
    }

    [Fact]
    public void RouteChangeRestartsActiveBackendAndCapturesFormat()
    {
        var transitions = new List<bool>();
        var audio = new AndroidAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });
        audio.SetNativeBackendReady(true);
        audio.Resume();
        audio.FocusChanged(AndroidAudioHandler.FocusGain);

        audio.RouteChanged(8, 48000, 192);

        Assert.Equal([true, false, true], transitions);
        Assert.Equal(AndroidAudioRoute.Bluetooth, audio.Stats.Route);
        Assert.Equal(48000, audio.Stats.SampleRate);
        Assert.Equal(192, audio.Stats.FramesPerBuffer);
        Assert.Equal(1, audio.Stats.RouteRestartCount);
    }

    [Fact]
    public void MissingNativeBackendReportsCapabilityAndNeverClaimsPlayback()
    {
        var audio = new AndroidAudioHandler(static active => !active);
        audio.Resume();
        audio.FocusChanged(AndroidAudioHandler.FocusGain);
        audio.SetNativeBackendReady(true);

        Assert.False(audio.Stats.IsPlaybackRunning);
        Assert.True(audio.Stats.IsNativeBackendReady);
        Assert.Equal(0, audio.Stats.PlaybackStartCount);
    }

    [Theory]
    [InlineData(2, AndroidAudioRoute.BuiltIn)]
    [InlineData(3, AndroidAudioRoute.Wired)]
    [InlineData(8, AndroidAudioRoute.Bluetooth)]
    [InlineData(22, AndroidAudioRoute.Usb)]
    [InlineData(9, AndroidAudioRoute.Hdmi)]
    [InlineData(999, AndroidAudioRoute.Other)]
    public void DeviceTypesMapToStableRouteFamilies(int deviceType, AndroidAudioRoute expected)
    {
        Assert.Equal(expected, AndroidAudioHandler.MapRoute(deviceType));
    }
}
