// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Threading;

using XEngine.Async;
using XEngine.Runtime.Video;

namespace XEngine.Runtime.Test.Video;

/// <summary>
/// Deterministic in-memory decoder for session tests: synthesizes frames at a fixed fps
/// with synthetic PTS values; seek moves the read index. When an audio sink is configured,
/// each decoded video frame also emits one PCM chunk covering the same media span.
/// All counters are thread-safe because the session's decode worker is the only caller.
/// </summary>
internal sealed class FakeVideoDecoder : IVideoDecoder, IDisposable
{
    private readonly object _lock = new();
    private int _nextIndex;

    public int Fps { get; set; } = 30;

    public int FrameCount { get; set; } = 90;

    public int Width { get; set; } = 8;

    public int Height { get; set; } = 8;

    public Exception? OpenException { get; set; }

    public int OpenCount { get; private set; }

    public int SeekCount { get; private set; }

    public int DecodedCount { get; private set; }

    public double? LastSeekSeconds { get; private set; }

    public bool EofSeen { get; private set; }

    public VideoMediaInfo Metadata { get; private set; } = null!;

    // audio synthesis state
    private IAudioPcmSink? _pcmSink;
    private int _targetRate = 48_000;
    private int _targetChannels = 2;
    private float _audioSpeed = 1f;
    private long _pcmSampleCounter;
    private int _pcmChunksDropped;

    public bool HasAudio => _pcmSink is not null;

    public int PcmChunksDropped => _pcmChunksDropped;

    public void ConfigureAudio(IAudioPcmSink sink, int targetSampleRate, int targetChannels)
    {
        _pcmSink = sink;
        _targetRate = targetSampleRate > 0 ? targetSampleRate : 48_000;
        _targetChannels = targetChannels > 0 ? targetChannels : 2;
    }

    public void SetAudioSpeed(float speed)
    {
        _audioSpeed = Math.Clamp(speed, 0.5f, 2f);
    }

    public void Open(string uri, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            OpenCount++;
            if (OpenException is not null)
            {
                Exception ex = OpenException;
                OpenException = null;
                throw ex;
            }

            _nextIndex = 0;
            Metadata = new VideoMediaInfo
            {
                Container = "fake",
                DurationSeconds = FrameCount / (double)Fps,
                StartTimeSeconds = 0,
                BitRate = 0,
                Video = new VideoStreamInfo
                {
                    Width = Width,
                    Height = Height,
                    CodedWidth = Width,
                    CodedHeight = Height,
                    Codec = "fakecode",
                    Profile = null,
                    Fps = Fps,
                    RotationDegrees = 0,
                    SampleAspectNum = 1,
                    SampleAspectDen = 1,
                },
                Audio = _pcmSink is not null
                    ? new VideoStreamAudioInfo { Codec = "fakepcm", SampleRate = _targetRate, Channels = _targetChannels, Profile = null }
                    : null,
                CanSeek = true,
            };
        }
    }

    public VideoFrame DecodeFirstFrame(CancellationToken cancellationToken)
    {
        // Probes are not exercised through the session; provide a minimal frame.
        var data = new byte[Width * Height * 4];
        return new VideoFrame { Data = data, Width = Width, Height = Height, PtsSeconds = 0 };
    }

    public PooledVideoFrame? DecodeNextFrame(CancellationToken cancellationToken, ArrayPool<byte> pool)
    {
        lock (_lock)
        {
            if (_nextIndex >= FrameCount)
            {
                EofSeen = true;
                return null;
            }

            int index = _nextIndex++;
            DecodedCount++;
            int byteCount = Width * Height * 4;
            byte[] data = pool.Rent(byteCount);
            data[0] = (byte)(index & 0xFF);
            data[byteCount - 1] = (byte)(index & 0xFF);
            double pts = index / (double)Fps;

            SynthesizePcm(pts);

            return new PooledVideoFrame(pool, data, byteCount, Width, Height, pts);
        }
    }

    /// <summary>
    /// Emits one PCM chunk per decoded video frame covering the same media span
    /// (mediaPts = frame pts, output duration = media span / speed), a deterministic
    /// ramp keyed off the sample counter.
    /// </summary>
    private void SynthesizePcm(double mediaPts)
    {
        if (_pcmSink is null)
        {
            return;
        }

        int frames = Math.Max(1, (int)(_targetRate / Fps / _audioSpeed));
        int count = frames * _targetChannels;
        var samples = new float[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = ((++_pcmSampleCounter & 0xFF) / 255f) * 2f - 1f;
        }

        if (!_pcmSink.TryWrite(samples, count, mediaPts, _audioSpeed))
        {
            _pcmChunksDropped++;
        }
    }

    public void Dispose()
    {
    }

    public void Seek(double seconds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            SeekCount++;
            LastSeekSeconds = seconds;
            _nextIndex = (int)Math.Round(seconds * Fps);
        }
    }
}

internal sealed class FakeDecoderFactory : IVideoDecoderFactory
{
    public FakeVideoDecoder Decoder { get; } = new();

    public IVideoDecoder Create(IAudioPcmSink? audioSink, int audioSampleRate, int audioChannels)
    {
        if (audioSink is not null)
        {
            Decoder.ConfigureAudio(audioSink, audioSampleRate, audioChannels);
        }

        return Decoder;
    }
}
