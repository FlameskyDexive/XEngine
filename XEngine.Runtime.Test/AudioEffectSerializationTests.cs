// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime.Audio;
using XEngine.Runtime.Audio.Effects;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Regression tests for effect-chain persistence (prowl-sync M6.1, from the upstream preview-4
/// audio overhaul direction): effect parameters survive capture/restore round-trips, runtime
/// buffers are rebuilt, and unknown or missing data degrades to an empty chain instead of failing.
/// </summary>
public class AudioEffectSerializationTests
{
    private static float SampleRate => AudioContext.SampleRate;

    [Fact]
    public void DelayEffect_ParamsRoundTrip()
    {
        var original = new DelayEffect((UInt32)SampleRate, 2, 0.35f, 0.4f)
        {
            Wet = 0.8f,
            Dry = 0.6f,
        };

        var data = AudioEffectSerialization.SerializeEffects(Wrap(original));
        var restored = new ConcurrentList<IAudioEffect>();
        AudioEffectSerialization.DeserializeInto(data, restored);

        Assert.Equal(1, restored.Count);
        var delay = Assert.IsType<DelayEffect>(restored[0]);
        Assert.Equal(0.8f, delay.Wet, 3);
        Assert.Equal(0.6f, delay.Dry, 3);
        Assert.Equal(0.4f, delay.Decay, 3);
        Assert.InRange(delay.DelayInSeconds, 0.30f, 0.40f);
    }

    [Fact]
    public void FilterEffect_ParamsRoundTrip()
    {
        var original = new FilterEffect(FilterType.Lowpass, 800f, 1.3f, 2f);

        var data = AudioEffectSerialization.SerializeEffects(Wrap(original));
        var restored = new ConcurrentList<IAudioEffect>();
        AudioEffectSerialization.DeserializeInto(data, restored);

        Assert.Equal(1, restored.Count);
        var filter = Assert.IsType<FilterEffect>(restored[0]);
        Assert.Equal(FilterType.Lowpass, filter.Type);
        Assert.Equal(800f, filter.Frequency, 1);
        Assert.Equal(1.3f, filter.Q, 3);
        Assert.Equal(2f, filter.GainDB, 3);
    }

    [Fact]
    public void PhaserEffect_ParamsRoundTrip()
    {
        var original = new PhaserEffect((UInt32)SampleRate)
        {
            Depth = 0.5f,
            Feedback = 0.3f,
            Minimum = 220f,
            Maximum = 1800f,
            Rate = 0.8f,
        };

        var data = AudioEffectSerialization.SerializeEffects(Wrap(original));
        var restored = new ConcurrentList<IAudioEffect>();
        AudioEffectSerialization.DeserializeInto(data, restored);

        Assert.Equal(1, restored.Count);
        var phaser = Assert.IsType<PhaserEffect>(restored[0]);
        Assert.Equal(0.5f, phaser.Depth, 3);
        Assert.Equal(0.3f, phaser.Feedback, 3);
        Assert.Equal(220f, phaser.Minimum, 1);
        Assert.Equal(1800f, phaser.Maximum, 1);
        Assert.Equal(0.8f, phaser.Rate, 3);
    }

    [Fact]
    public void ReverbEffect_ParamsRoundTrip()
    {
        var original = new ReverbEffect((UInt32)SampleRate, 2)
        {
            RoomSize = 0.7f,
            Damping = 0.3f,
            Wet = 0.4f,
            Dry = 0.9f,
            Width = 0.8f,
            InputWidth = 0.6f,
            Mode = 1f,
        };

        var data = AudioEffectSerialization.SerializeEffects(Wrap(original));
        var restored = new ConcurrentList<IAudioEffect>();
        AudioEffectSerialization.DeserializeInto(data, restored);

        Assert.Equal(1, restored.Count);
        var reverb = Assert.IsType<ReverbEffect>(restored[0]);
        Assert.Equal(0.7f, reverb.RoomSize, 3);
        Assert.Equal(0.3f, reverb.Damping, 3);
        Assert.Equal(0.4f, reverb.Wet, 3);
        Assert.Equal(0.9f, reverb.Dry, 3);
        Assert.Equal(0.8f, reverb.Width, 3);
        Assert.Equal(0.6f, reverb.InputWidth, 3);
        Assert.Equal(1f, reverb.Mode, 3);
    }

    [Fact]
    public void Chain_OrderAndMultipleEffects_Preserved()
    {
        var chain = new ConcurrentList<IAudioEffect>();
        chain.Add(new DelayEffect((UInt32)SampleRate, 2, 0.1f, 0f));
        chain.Add(new FilterEffect(FilterType.Highpass, 120f, 0.7f, 0f));
        chain.Add(new PhaserEffect((UInt32)SampleRate));

        var data = AudioEffectSerialization.SerializeEffects(chain);
        var restored = new ConcurrentList<IAudioEffect>();
        AudioEffectSerialization.DeserializeInto(data, restored);

        Assert.Equal(3, restored.Count);
        Assert.IsType<DelayEffect>(restored[0]);
        Assert.IsType<FilterEffect>(restored[1]);
        Assert.IsType<PhaserEffect>(restored[2]);
    }

    [Fact]
    public void NullOrMissingData_YieldsEmptyChain()
    {
        var restored = new ConcurrentList<IAudioEffect>();
        AudioEffectSerialization.DeserializeInto(null, restored);
        Assert.Equal(0, restored.Count);

        AudioEffectSerialization.DeserializeInto(EchoObject.NewCompound(), restored);
        Assert.Equal(0, restored.Count);
    }

    private static ConcurrentList<IAudioEffect> Wrap(IAudioEffect effect)
    {
        var list = new ConcurrentList<IAudioEffect>();
        list.Add(effect);
        return list;
    }
}
