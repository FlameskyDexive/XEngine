// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Threading;

namespace XEngine.Runtime.Video;

/// <summary>
/// Engine-owned abstraction isolating FFmpeg native types from the playback session,
/// importer, and preview layers. Implementations are single-threaded by contract: one
/// instance is opened, read, seeked, and disposed from one thread (the session's decode
/// worker owns it exclusively).
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>
    /// Opens the media and probes its streams. Blocking; must honor cancellation through
    /// the demuxer interrupt callback (also enforcing the no-progress timeout). After a
    /// successful open, <see cref="Metadata"/> is populated.
    /// </summary>
    /// <exception cref="VideoException">On open/probe failure, with a coarse <see cref="VideoErrorCode"/>.</exception>
    void Open(string uri, CancellationToken cancellationToken);

    /// <summary>Probe result from <see cref="Open"/>. Valid only after a successful open.</summary>
    VideoMediaInfo Metadata { get; }

    /// <summary>True when audio output is active (audio stream present and a sink was configured).</summary>
    bool HasAudio { get; }

    /// <summary>
    /// Enables the audio output pipeline: decoded audio is converted to interleaved
    /// float32 at <paramref name="targetSampleRate"/>/<paramref name="targetChannels"/>
    /// and written to <paramref name="sink"/>. Call before <see cref="Open"/>.
    /// </summary>
    void ConfigureAudio(IAudioPcmSink sink, int targetSampleRate, int targetChannels);

    /// <summary>Pitch-preserving playback speed for the audio pipeline (0.5–2.0).</summary>
    void SetAudioSpeed(float speed);

    /// <summary>
    /// Decodes, converts to RGBA8, and returns the first presentable video frame.
    /// Convenience for probes/importers; playback uses <see cref="DecodeNextFrame"/>.
    /// </summary>
    /// <exception cref="VideoException">On decode failure or cancellation.</exception>
    VideoFrame DecodeFirstFrame(CancellationToken cancellationToken);

    /// <summary>
    /// Streaming decode: returns the next presentable video frame with its pixel buffer
    /// rented from <paramref name="pool"/> (caller disposes to return it), or null at end
    /// of stream. Blocking; cancellation aborts via the demuxer interrupt callback.
    /// </summary>
    PooledVideoFrame? DecodeNextFrame(CancellationToken cancellationToken, ArrayPool<byte> pool);

    /// <summary>
    /// Seeks the demuxer (keyframe-accurate) and flushes codec state so the next
    /// <see cref="DecodeNextFrame"/> produces frames at/after <paramref name="seconds"/>.
    /// </summary>
    void Seek(double seconds, CancellationToken cancellationToken);
}
