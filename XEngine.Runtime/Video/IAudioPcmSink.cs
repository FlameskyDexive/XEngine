// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Video;

/// <summary>
/// Receives decoded, speed-adjusted audio PCM from the playback pipeline (float32,
/// interleaved, engine sample rate/channels). Implemented by the session's PCM ring;
/// the decoder pushes chunks on the decode worker.
/// </summary>
public interface IAudioPcmSink
{
    /// <summary>
    /// Writes interleaved PCM. <paramref name="mediaPtsSeconds"/> is the media-timeline
    /// time of the first frame in the chunk; <paramref name="chunkSpeed"/> is the playback
    /// speed the chunk was produced at (media seconds advance chunkSpeed× per output
    /// second inside the chunk). Returns false when the sink is full — the producer should
    /// stop decoding audio until the consumer drains.
    /// </summary>
    bool TryWrite(float[] samples, int count, double mediaPtsSeconds, float chunkSpeed);

    /// <summary>Drops buffered PCM and re-anchors the playhead (seek / loop wrap).</summary>
    void Clear(double startMediaSeconds);
}
