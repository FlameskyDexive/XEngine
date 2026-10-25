// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

using XEngine.Runtime.Audio.Native;

namespace XEngine.Runtime.Audio.Diagnostics;

/// <summary>Numeric summary of a captured PCM window (interleaved float samples).</summary>
public readonly struct AudioAnalysisResult
{
    /// <summary>Largest absolute sample across all channels.</summary>
    public float Peak { get; init; }
    /// <summary>Root-mean-square across all channels (0 = silence, 1 = full scale).</summary>
    public float Rms { get; init; }
    /// <summary>Mean sample value across all channels; non-zero indicates DC bias.</summary>
    public float DcOffset { get; init; }
    /// <summary>Peak / Rms; inf for silence. Pulse trains have high crest factors, tones ~1.41.</summary>
    public float CrestFactor { get; init; }
    /// <summary>Approximate frequency of the mono-summed signal in Hz via zero crossings; 0 below 20 Hz.</summary>
    public double FrequencyHz { get; init; }
    /// <summary>Per-channel frame count of the analyzed window.</summary>
    public long Frames { get; init; }
    public int Channels { get; init; }
}

/// <summary>
/// Allocation-light analysis math shared by the acceptance harness, mixer metering and the
/// audio diagnostics panels. Pure functions over interleaved float PCM so tests can exercise
/// them without an audio device.
/// </summary>
public static class AudioAnalysis
{
    public const float SilenceRmsThreshold = 1e-5f;

    public static AudioAnalysisResult Analyze(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        if (channels <= 0 || interleaved.IsEmpty)
            return new AudioAnalysisResult();

        double sumSquares = 0, sum = 0, crossings = 0;
        float peak = 0;
        float previousMono = 0;
        long samples = interleaved.Length;
        long frames = samples / channels;

        for (int i = 0; i < interleaved.Length; i++)
        {
            float s = interleaved[i];
            float abs = MathF.Abs(s);
            if (abs > peak) peak = abs;
            sum += s;
            sumSquares += (double)s * s;

            // Zero crossings on the mono sum give a usable frequency estimate for tonal signals.
            if ((i % channels) == channels - 1)
            {
                float mono = s; // last channel of the frame; good enough as a cheap estimator seed
                if (i >= channels)
                {
                    if ((previousMono >= 0f && mono < 0f) || (previousMono < 0f && mono >= 0f))
                        crossings++;
                }
                previousMono = mono;
            }
        }

        double durationSeconds = frames / (double)sampleRate;
        float rms = MathF.Sqrt((float)(sumSquares / samples));
        double frequencyHz = durationSeconds > 0 ? crossings / (2.0 * durationSeconds) : 0;
        if (frequencyHz < 20.0)
            frequencyHz = 0;

        return new AudioAnalysisResult
        {
            Peak = peak,
            Rms = rms,
            DcOffset = (float)(sum / samples),
            CrestFactor = rms > SilenceRmsThreshold ? peak / rms : float.PositiveInfinity,
            FrequencyHz = frequencyHz,
            Frames = frames,
            Channels = channels,
        };
    }
}

/// <summary>
/// Records interleaved device output for acceptance evidence. Subscribe <see cref="DeviceHandler"/>
/// to <see cref="AudioContext.DataProcess"/>, call <see cref="Begin"/> before the window of interest
/// and <see cref="End"/> after; the accumulated PCM can then be written as a 16-bit WAV and analyzed.
/// Capture copies are confined to explicit recording windows, so steady-state operation stays free of
/// per-callback allocations when not recording.
/// </summary>
public sealed class AudioCaptureBuffer
{
    private readonly ConcurrentQueue<float[]> _chunks = new();
    private readonly object _gate = new();
    private long _capturedSamples;
    private int _channels;
    private int _sampleRate;
    private volatile bool _recording;

    public bool IsRecording => _recording;
    public long CapturedFrames => Volatile.Read(ref _capturedSamples) / Math.Max(1, _channels);
    public int Channels => _channels;
    public int SampleRate => _sampleRate;

    /// <summary>Handler shape matches <see cref="AudioContext.DataProcess"/>.</summary>
    public void OnDeviceData(NativeArray<float> data, uint frameCount)
    {
        if (!_recording || data.Length == 0)
            return;

        // One small copy per callback while recording only; diagnostics never run in hot paths.
        float[] copy = new float[data.Length];
        for (int i = 0; i < data.Length; i++)
            copy[i] = data[i];
        _chunks.Enqueue(copy);
        Interlocked.Add(ref _capturedSamples, copy.Length);
    }

    public void Begin(int channels, int sampleRate)
    {
        if (_recording)
            throw new InvalidOperationException("Capture already running; call End first.");
        lock (_gate)
        {
            while (_chunks.TryDequeue(out _)) { }
            _capturedSamples = 0;
            _channels = channels;
            _sampleRate = sampleRate;
            _recording = true;
        }
    }

    public void End() => _recording = false;

    /// <summary>Copies the captured window out and clears the buffer.</summary>
    public float[] ToInterleavedArray()
    {
        lock (_gate)
        {
            long total = Volatile.Read(ref _capturedSamples);
            var result = new float[total];
            int offset = 0;
            while (_chunks.TryDequeue(out var chunk))
            {
                Array.Copy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            Volatile.Write(ref _capturedSamples, 0);
            return result;
        }
    }

    public AudioAnalysisResult AnalyzeAndClear()
    {
        float[] data = ToInterleavedArray();
        var result = AudioAnalysis.Analyze(data, Math.Max(1, _channels), Math.Max(1, _sampleRate));
        return result;
    }

    /// <summary>Writes the captured window as a 16-bit PCM RIFF/WAVE file. Returns false when
    /// nothing was captured or the path cannot be written.</summary>
    public bool WriteWav16(string path)
    {
        float[] data = ToInterleavedArray();
        return TryWriteWav16(path, data, _channels, _sampleRate);
    }

    /// <summary>Writes interleaved float PCM as a 16-bit WAV file.</summary>
    public static bool TryWriteWav16(string path, ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        try
        {
            if (channels <= 0 || sampleRate <= 0)
                return false;

            long sampleCount = interleaved.Length;
            long dataBytes = sampleCount * 2;
            if (dataBytes > int.MaxValue - 44)
                return false;

            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new BinaryWriter(stream);
            writer.Write("RIFF"u8);
            writer.Write((int)(36 + dataBytes));
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);             // PCM
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2); // byte rate
            writer.Write((short)(channels * 2));     // block align
            writer.Write((short)16);                 // bits per sample
            writer.Write("data"u8);
            writer.Write((int)dataBytes);

            Span<byte> bytes = stackalloc byte[2];
            for (int i = 0; i < interleaved.Length; i++)
            {
                float s = Math.Clamp(interleaved[i], -1f, 1f);
                short pcm = (short)(int)MathF.Round(s * 32767f);
                bytes[0] = (byte)(pcm & 0xFF);
                bytes[1] = (byte)((pcm >> 8) & 0xFF);
                writer.Write(bytes);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
