// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.Video;

/// <summary>
/// Bounded PCM ring bridging the decode worker and the audio device callback
/// (~500ms of media per the module plan). Doubles as the session's master clock: the
/// playhead tracks the media time of the next frame the device consumes, so the media
/// clock advances at exactly the audio consumption rate — and freezes whenever the ring
/// is starved (no silent-clock drift).
///
/// Chunks carry the media timestamp of their first frame (the decoder bakes playback
/// speed into the mapping); the playhead interpolates within a chunk at chunk speed.
/// </summary>
public sealed class VideoPcmRing : IAudioPcmSink
{
    private sealed class Chunk
    {
        public Chunk(double mediaPts, long frames, float speed)
        {
            MediaPts = mediaPts;
            FramesRemaining = frames;
            Speed = speed;
        }

        public double MediaPts { get; }
        public long Frames { get; }
        public long FramesRemaining { get; set; }
        public float Speed { get; }
    }

    private readonly object _lock = new();
    private readonly float[] _buffer;      // interleaved samples
    private readonly int _channels;
    private readonly int _sampleRate;

    private int _writePos;
    private int _readPos;
    private int _sampleCount;

    private readonly Queue<Chunk> _chunks = new();
    private double _playheadSeconds;

    /// <param name="capacitySeconds">Ring duration in seconds of output PCM (0.5 per plan).</param>
    public VideoPcmRing(int sampleRate, int channels, double capacitySeconds = 0.5)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48_000;
        _channels = channels > 0 ? channels : 2;
        _buffer = new float[(int)(_sampleRate * channels * capacitySeconds)];
    }

    public int SampleRate => _sampleRate;

    public int Channels => _channels;

    /// <summary>Buffered frames available to the device.</summary>
    public int BufferedFrames
    {
        get { lock (_lock) return _sampleCount / _channels; }
    }

    /// <summary>
    /// Media time of the next frame the device will consume — the audio master clock
    /// reading. Advances only when PCM is actually delivered to the caller.
    /// </summary>
    public double PlayheadMediaSeconds
    {
        get { lock (_lock) return _playheadSeconds; }
    }

    public bool TryWrite(float[] samples, int count, double mediaPtsSeconds, float chunkSpeed)
    {
        lock (_lock)
        {
            if (samples is null || count <= 0 || count > samples.Length || count > _buffer.Length - _sampleCount)
            {
                return false;
            }

            if (_sampleCount == 0)
            {
                // First chunk after a (re)anchor defines the playhead directly.
                _playheadSeconds = mediaPtsSeconds;
            }

            int tail = (_readPos + _sampleCount) % _buffer.Length;
            for (int i = 0; i < count; i++)
            {
                _buffer[tail] = samples[i];
                tail = tail + 1 == _buffer.Length ? 0 : tail + 1;
            }

            _sampleCount += count;
            var chunk = new Chunk(mediaPtsSeconds, count / _channels, chunkSpeed); _chunks.Enqueue(chunk);
            return true;
        }
    }

    /// <summary>
    /// Device callback: consumes up to <paramref name="frames"/> frames into
    /// <paramref name="destination"/>, advancing the playhead. Returns frames actually
    /// delivered; 0 means starved (caller plays silence, clock freezes).
    /// </summary>
    public int Read(float[] destination, int frames)
    {
        lock (_lock)
        {
            int toRead = Math.Min(frames, _sampleCount / _channels);
            int framesRead = 0;

            while (framesRead < toRead && _chunks.Count > 0)
            {
                Chunk chunk = _chunks.Peek();
                int chunkFrames = (int)Math.Min(chunk.FramesRemaining, toRead - framesRead);
                for (int f = 0; f < chunkFrames; f++)
                {
                    for (int c = 0; c < _channels; c++)
                    {
                        destination[framesRead * _channels + c] = _buffer[_readPos];
                        _readPos = _readPos + 1 == _buffer.Length ? 0 : _readPos + 1;
                        _sampleCount--;
                    }

                    framesRead++;
                }

                chunk.FramesRemaining -= chunkFrames;
                _playheadSeconds = chunk.MediaPts
                    + (chunk.Frames - chunk.FramesRemaining) / (double)_sampleRate * chunk.Speed;

                if (chunk.FramesRemaining <= 0)
                {
                    _chunks.Dequeue();
                }
            }

            return framesRead;
        }
    }

    public void Clear(double startMediaSeconds)
    {
        lock (_lock)
        {
            _readPos = 0;
            _writePos = 0;
            _sampleCount = 0;
            _chunks.Clear();
            _playheadSeconds = startMediaSeconds;
        }
    }
}
