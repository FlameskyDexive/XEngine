// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;

namespace XEngine.Runtime.Audio.Effects;

/// <summary>
/// Persists the effect chain of an <see cref="Components.AudioSource"/>: each engine effect is
/// captured as a small parameter compound (a closed type-name → factory map keeps this trim-safe),
/// and the chain is rebuilt on load by constructing fresh effects against the current audio
/// context and restoring the captured parameters. Runtime-only state (delay buffers, filter
/// internals, phaser cursors) is deliberately not persisted.
/// Ported from the upstream preview-4 audio overhaul direction (serializable effect chains), but
/// routed through XEngine's own multi-backend effect types.
/// </summary>
public static class AudioEffectSerialization
{
    /// <summary>Type tag for the built-in effects stored in the "$type" field.</summary>
    private const string DelayTag = "delay";
    private const string FilterTag = "filter";
    private const string PhaserTag = "phaser";
    private const string ReverbTag = "reverb";

    /// <summary>Serializes the chain, or null when there is nothing to persist.</summary>
    public static EchoObject? SerializeEffects(ConcurrentList<IAudioEffect> effects)
    {
        EchoObject? array = null;
        for (int i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            EchoObject? entry = effect switch
            {
                DelayEffect d => CaptureDelay(d),
                FilterEffect f => CaptureFilter(f),
                PhaserEffect p => CapturePhaser(p),
                ReverbEffect r => CaptureReverb(r),
                _ => null, // user effects without a snapshot are skipped rather than guessed at
            };
            if (entry == null) continue;

            array ??= EchoObject.NewList();
            array.ListAdd(entry);
        }
        return array;
    }

    /// <summary>
    /// Replaces the target chain with effects rebuilt from serialized data. Missing/unknown data
    /// clears the chain (legacy assets predate persistent effects and simply start empty).
    /// </summary>
    public static void DeserializeInto(EchoObject? data, ConcurrentList<IAudioEffect> target)
    {
        target.Clear();
        if (data == null || data.TagType != EchoType.List) return;

        for (int i = 0; i < data.Count; i++)
        {
            var entry = data[i];
            var typeTag = entry.Get("$type")?.StringValue;
            var p = entry.Get("params");
            IAudioEffect? effect = typeTag switch
            {
                DelayTag => RestoreDelay(p),
                FilterTag => RestoreFilter(p),
                PhaserTag => RestorePhaser(p),
                ReverbTag => RestoreReverb(p),
                _ => null,
            };
            if (effect != null) target.Add(effect);
        }
    }

    // ---- per-effect capture -------------------------------------------------

    private static EchoObject CaptureDelay(DelayEffect d)
    {
        var p = EchoObject.NewCompound();
        p.Add("delaySeconds", new EchoObject(d.DelayInSeconds));
        p.Add("wet", new EchoObject(d.Wet));
        p.Add("dry", new EchoObject(d.Dry));
        p.Add("decay", new EchoObject(d.Decay));
        return Wrap(DelayTag, p);
    }

    private static EchoObject CaptureFilter(FilterEffect f)
    {
        var p = EchoObject.NewCompound();
        p.Add("type", new EchoObject((int)f.Type));
        p.Add("frequency", new EchoObject(f.Frequency));
        p.Add("q", new EchoObject(f.Q));
        p.Add("gainDb", new EchoObject(f.GainDB));
        return Wrap(FilterTag, p);
    }

    private static EchoObject CapturePhaser(PhaserEffect ph)
    {
        var p = EchoObject.NewCompound();
        p.Add("depth", new EchoObject(ph.Depth));
        p.Add("feedback", new EchoObject(ph.Feedback));
        p.Add("minimum", new EchoObject(ph.Minimum));
        p.Add("maximum", new EchoObject(ph.Maximum));
        p.Add("rate", new EchoObject(ph.Rate));
        return Wrap(PhaserTag, p);
    }

    private static EchoObject CaptureReverb(ReverbEffect r)
    {
        var p = EchoObject.NewCompound();
        p.Add("roomSize", new EchoObject(r.RoomSize));
        p.Add("damping", new EchoObject(r.Damping));
        p.Add("wet", new EchoObject(r.Wet));
        p.Add("dry", new EchoObject(r.Dry));
        p.Add("width", new EchoObject(r.Width));
        p.Add("inputWidth", new EchoObject(r.InputWidth));
        p.Add("mode", new EchoObject(r.Mode));
        return Wrap(ReverbTag, p);
    }

    private static EchoObject Wrap(string typeTag, EchoObject parameters)
    {
        var entry = EchoObject.NewCompound();
        entry.Add("$type", new EchoObject(typeTag));
        entry.Add("params", parameters);
        return entry;
    }

    // ---- per-effect restore -------------------------------------------------

    private static IAudioEffect? RestoreDelay(EchoObject? p)
    {
        if (p == null) return null;
        float sampleRate = AudioContext.SampleRate;
        float seconds = GetFloat(p, "delaySeconds", 0.25f);
        var effect = new DelayEffect((UInt32)sampleRate, 2, seconds < (1f / sampleRate) ? 1u : (UInt32)MathF.Round(seconds * sampleRate), GetFloat(p, "decay", 0f));
        effect.Wet = GetFloat(p, "wet", 1f);
        effect.Dry = GetFloat(p, "dry", 1f);
        effect.Decay = GetFloat(p, "decay", 0f);
        return effect;
    }

    private static IAudioEffect? RestoreFilter(EchoObject? p)
    {
        if (p == null) return null;
        var type = (FilterType)(int)GetFloat(p, "type", 0f);
        return new FilterEffect(
            type,
            GetFloat(p, "frequency", 20000f),
            GetFloat(p, "q", 0.7f),
            GetFloat(p, "gainDb", 0f));
    }

    private static IAudioEffect? RestorePhaser(EchoObject? p)
    {
        if (p == null) return null;
        var effect = new PhaserEffect((UInt32)AudioContext.SampleRate);
        effect.Depth = GetFloat(p, "depth", 1f);
        effect.Feedback = GetFloat(p, "feedback", 0.7f);
        effect.Minimum = GetFloat(p, "minimum", 440f);
        effect.Maximum = GetFloat(p, "maximum", 1600f);
        effect.Rate = GetFloat(p, "rate", 0.5f);
        return effect;
    }

    private static IAudioEffect? RestoreReverb(EchoObject? p)
    {
        if (p == null) return null;
        var effect = new ReverbEffect((UInt32)AudioContext.SampleRate, 2);
        effect.RoomSize = GetFloat(p, "roomSize", 0.5f);
        effect.Damping = GetFloat(p, "damping", 0.5f);
        effect.Wet = GetFloat(p, "wet", 0.3f);
        effect.Dry = GetFloat(p, "dry", 0.7f);
        effect.Width = GetFloat(p, "width", 1f);
        effect.InputWidth = GetFloat(p, "inputWidth", 1f);
        effect.Mode = GetFloat(p, "mode", 0f);
        return effect;
    }

    private static float GetFloat(EchoObject p, string key, float fallback)
    {
        var v = p.Get(key);
        return v != null ? v.FloatValue : fallback;
    }
}
