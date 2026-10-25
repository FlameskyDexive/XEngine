// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Video;

/// <summary>
/// Describes one decoded video frame: 8-bit RGBA pixels, top-down row order, tightly
/// packed (stride == width * 4). Produced by <see cref="IVideoDecoder"/>; consumers upload
/// it to a texture without further conversion.
/// </summary>
public sealed class VideoFrame
{
    /// <summary>RGBA8 pixel data, top-down, stride == Width * 4.</summary>
    public required byte[] Data { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Presentation timestamp in seconds on the media timeline (0 for streams without timestamps).</summary>
    public double PtsSeconds { get; init; }
}

/// <summary>
/// Probe result describing a decoded stream set. Dimensions are the *display* dimensions
/// (sample aspect ratio applied, rotation applied by swapping when 90/270), which is what
/// output textures should be sized to.
/// </summary>
public sealed class VideoMediaInfo
{
    /// <summary>Container/protocol identifier reported by the demuxer (e.g. "mov,mp4,m4a,3gp,3g2,mj2", "http").</summary>
    public required string Container { get; init; }

    /// <summary>Total media duration in seconds (format-level; falls back to the video stream).</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Timeline timestamp of the first frame, in seconds (usually 0; nonzero for some streams).</summary>
    public double StartTimeSeconds { get; init; }

    public long BitRate { get; init; }

    public required VideoStreamInfo Video { get; init; }

    /// <summary>Audio stream, or null when the media is silent.</summary>
    public VideoStreamAudioInfo? Audio { get; init; }

    /// <summary>True when the demuxer can seek (normal byte seekable input and present indexes).</summary>
    public bool CanSeek { get; init; }
}

public sealed class VideoStreamInfo
{
    /// <summary>Display width in pixels: storage width corrected for sample aspect ratio (rotation NOT applied).</summary>
    public required int Width { get; init; }

    /// <summary>Display height in pixels (rotation NOT applied).</summary>
    public required int Height { get; init; }

    public required int CodedWidth { get; init; }

    public required int CodedHeight { get; init; }

    /// <summary>Decoder name (e.g. "h264", "vp9").</summary>
    public required string Codec { get; init; }

    /// <summary>Codec profile string, when reported.</summary>
    public string? Profile { get; init; }

    /// <summary>Average frame rate in frames per second; 0 when unknown.</summary>
    public double Fps { get; init; }

    /// <summary>Rotation metadata in degrees, normalized to one of 0 / 90 / 180 / 270 (0 when absent).</summary>
    public int RotationDegrees { get; init; }

    /// <summary>Sample (pixel) aspect ratio numerator/denominator, 1/1 when square.</summary>
    public int SampleAspectNum { get; init; }

    public int SampleAspectDen { get; init; }

    /// <summary>Display dimensions with rotation applied: swap Width/Height when rotated ±90.</summary>
    public (int Width, int Height) DisplaySize => RotationDegrees is 90 or 270 ? (Height, Width) : (Width, Height);
}

public sealed class VideoStreamAudioInfo
{
    public required string Codec { get; init; }

    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public string? Profile { get; init; }
}
