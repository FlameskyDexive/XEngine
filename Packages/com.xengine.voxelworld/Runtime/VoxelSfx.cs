using System;
using System.IO;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.VoxelWorld;

/// <summary>Owned click WAV (not VoxelPlay footsteps). Used for place/break feedback.</summary>
public static class VoxelSfx
{
    public const int SampleRate = 22050;

    public static byte[] CreateClickWav(float seconds = 0.05f, float hertz = 880f)
    {
        int samples = Math.Max(8, (int)(SampleRate * seconds));
        short[] pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)SampleRate;
            double envelope = 1.0 - (i / (double)samples);
            pcm[i] = (short)(Math.Sin(2.0 * Math.PI * hertz * t) * envelope * 12000.0);
        }

        using var stream = new MemoryStream(44 + samples * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (int i = 0; i < samples; i++)
            writer.Write(pcm[i]);
        return stream.ToArray();
    }

    public static bool LooksLikeWav(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 12 &&
        bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
        bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E';

    private static AudioClip? _clickClip;

    public static void PlayClick(GameObject? host)
    {
        if (host == null)
            return;
        try
        {
            var source = host.GetComponent<AudioSource>() ?? host.AddComponent<AudioSource>();
            // cache the clip: per-click instances churn the finalizer + native handle
            // retirement for identical data
            _clickClip ??= new AudioClip(CreateClickWav());
            // never stop a voice mid-playback: the device callback races the stop
            if (source.IsPlaying)
                return;
            source.PlayOneShot(_clickClip);
        }
        catch (Exception)
        {
            // Headless tests and machines without an audio device skip playback.
        }
    }
}
