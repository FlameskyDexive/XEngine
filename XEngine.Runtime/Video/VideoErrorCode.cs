// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Video;

/// <summary>
/// Coarse failure categories for video loading, decoding, and playback. Kept small on
/// purpose: consumers branch on these to decide retry/feedback behavior, and finer
/// diagnostics ride along in <see cref="VideoException.Message"/>.
/// </summary>
public enum VideoErrorCode
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>The FFmpeg native libraries could not be located or loaded.</summary>
    NativeLoadFailed,

    /// <summary>The media could not be opened (missing file, unsupported container, refused connection).</summary>
    OpenFailed,

    /// <summary>The media has no decodable video stream.</summary>
    VideoStreamNotFound,

    /// <summary>Stream data is malformed and cannot be decoded.</summary>
    InvalidData,

    /// <summary>The media uses a stream configuration this build cannot decode.</summary>
    UnsupportedStream,

    /// <summary>Decoding a frame failed.</summary>
    DecodeFailed,

    /// <summary>Open or decode made no progress within the configured timeout.</summary>
    Timeout,

    /// <summary>The operation was canceled (unload, source change, dispose).</summary>
    Canceled,

    /// <summary>The audio device or audio routing rejected the stream.</summary>
    AudioOutputFailed,
}
