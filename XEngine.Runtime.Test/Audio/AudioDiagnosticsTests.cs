// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Audio.Diagnostics;

using Xunit;

namespace XEngine.Runtime.Test.Audio;

/// <summary>
/// N0 acceptance-harness math: peak/RMS/crest/frequency analysis and 16-bit WAV capture
/// round-trips, exercised on synthetic PCM so the suite needs no audio device.
/// </summary>
public sealed class AudioDiagnosticsTests
{
    private const int SampleRate = 44100;

    private static float[] Sine(float frequency, float amplitude, double seconds, int channels = 1)
    {
        int frames = (int)(seconds * SampleRate);
        var data = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float s = amplitude * MathF.Sin(2 * MathF.PI * frequency * i / SampleRate);
            for (int c = 0; c < channels; c++)
                data[i * channels + c] = s;
        }
        return data;
    }

    [Fact]
    public void Sine440_PeakRmsAndFrequencyMatchExpectation()
    {
        var result = AudioAnalysis.Analyze(Sine(440f, 0.5f, 1.0), 1, SampleRate);

        Assert.Equal(0.5f, result.Peak, 2);
        Assert.Equal(0.3535f, result.Rms, 2);
        Assert.InRange(result.CrestFactor, 1.35f, 1.5f);
        Assert.InRange(result.FrequencyHz, 430, 450);
        Assert.Equal(0f, result.DcOffset, 3);
        Assert.Equal(SampleRate, (int)result.Frames);
    }

    [Fact]
    public void Silence_IsReportedAsSilence()
    {
        var result = AudioAnalysis.Analyze(new float[SampleRate], 1, SampleRate);

        Assert.Equal(0f, result.Peak);
        Assert.Equal(0f, result.Rms);
        Assert.True(float.IsInfinity(result.CrestFactor));
        Assert.Equal(0.0, result.FrequencyHz);
    }

    [Fact]
    public void DcOffset_IsDetectedWithoutFalseFrequency()
    {
        var data = new float[SampleRate];
        Array.Fill(data, 0.25f);

        var result = AudioAnalysis.Analyze(data, 1, SampleRate);

        Assert.Equal(0.25f, result.Peak, 3);
        Assert.Equal(0.25f, result.Rms, 3);
        Assert.Equal(0.25f, result.DcOffset, 3);
        Assert.Equal(0.0, result.FrequencyHz);
    }

    [Fact]
    public void PulseTrain_HasHighCrestFactor()
    {
        // 5 ms bursts every 250 ms: crest factor far above the 1.41 of a pure tone.
        var data = new float[SampleRate];
        for (int i = 0; i < SampleRate; i++)
        {
            double t = i / (double)SampleRate;
            double phase = t % 0.25;
            if (phase < 0.005)
                data[i] = 0.9f * MathF.Exp((float)(-phase / 0.0015));
        }

        var result = AudioAnalysis.Analyze(data, 1, SampleRate);

        Assert.True(result.CrestFactor > 4f, $"crest={result.CrestFactor}");
        Assert.Equal(0.9f, result.Peak, 2);
    }

    [Fact]
    public void Stereo_AnalysisCoversBothChannels()
    {
        var result = AudioAnalysis.Analyze(Sine(440f, 0.5f, 0.5, channels: 2), 2, SampleRate);

        Assert.Equal(2, result.Channels);
        Assert.Equal(SampleRate / 2, (int)result.Frames);
        Assert.Equal(0.5f, result.Peak, 2);
        Assert.Equal(0.3535f, result.Rms, 2);
    }

    [Fact]
    public void CaptureBuffer_CollectsOnlyWhileRecording()
    {
        var capture = new AudioCaptureBuffer();
        var data = Sine(440f, 0.5f, 0.1);

        PushChunk(capture, data);
        Assert.Equal(0, capture.CapturedFrames);

        capture.Begin(1, SampleRate);
        PushChunk(capture, data);
        capture.End();
        PushChunk(capture, data);

        Assert.Equal(data.Length, (long)capture.CapturedFrames);
        var result = capture.AnalyzeAndClear();
        Assert.Equal(0.5f, result.Peak, 2);
        Assert.Equal(0, capture.CapturedFrames);
    }

    [Fact]
    public void Wav16_RoundTripsHeaderAndSamples()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xengine-audio-n0-{Guid.NewGuid():N}.wav");
        try
        {
            const int channels = 2;
            var data = Sine(440f, 0.5f, 0.25, channels);
            Assert.True(AudioCaptureBuffer.TryWriteWav16(path, data, channels, SampleRate));

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + data.Length * 2, bytes.Length);
            Assert.Equal((byte)'R', bytes[0]);
            Assert.Equal((byte)'I', bytes[1]);
            Assert.Equal((byte)'F', bytes[2]);
            Assert.Equal((byte)'F', bytes[3]);
            Assert.Equal(36 + data.Length * 2, BitConverter.ToInt32(bytes, 4));
            Assert.Equal(1, BitConverter.ToInt16(bytes, 20));       // PCM
            Assert.Equal(channels, BitConverter.ToInt16(bytes, 22));
            Assert.Equal(SampleRate, BitConverter.ToInt32(bytes, 24));
            Assert.Equal(data.Length * 2, BitConverter.ToInt32(bytes, 40));

            // Decode a few mid-file samples and compare against the source within 16-bit quantization.
            for (int i = 0; i < 8; i++)
            {
                int offset = 44 + i * 2;
                short pcm = BitConverter.ToInt16(bytes, offset);
                float restored = pcm / 32767f;
                Assert.InRange(Math.Abs(restored - data[i]), 0f, 1.5f / 32767f);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CaptureBuffer_WritesWavFromRecordingWindow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xengine-audio-n0-{Guid.NewGuid():N}.wav");
        try
        {
            var capture = new AudioCaptureBuffer();
            var data = Sine(220f, 0.25f, 0.1);
            capture.Begin(1, SampleRate);
            PushChunk(capture, data);
            capture.End();
            Assert.True(capture.WriteWav16(path));

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(44 + data.Length * 2, bytes.Length);
            capture.AnalyzeAndClear();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Hands one chunk to the capture handler; the fixed pointer only spans the call.</summary>
    private static unsafe void PushChunk(AudioCaptureBuffer capture, float[] source)
    {
        fixed (float* p = source)
        {
            capture.OnDeviceData(new XEngine.Runtime.Audio.Native.NativeArray<float>(p, source.Length), (uint)source.Length);
        }
    }
}
