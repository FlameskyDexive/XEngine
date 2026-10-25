// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Video;

/// <summary>
/// Playback session state machine:
/// <c>Idle → Preparing → Ready → Playing/Paused/Buffering/Seeking → Ended</c>,
/// plus <see cref="Error"/> and <see cref="Disposed"/> terminal states.
/// </summary>
public enum VideoPlaybackState
{
    /// <summary>No media loaded.</summary>
    Idle = 0,

    /// <summary>Opening/probing the source and priming the frame queue.</summary>
    Preparing,

    /// <summary>Media ready; poster frame available; paused at the start position.</summary>
    Ready,

    /// <summary>Presenting frames against the media clock.</summary>
    Playing,

    /// <summary>Playback suspended; media clock frozen.</summary>
    Paused,

    /// <summary>Frame queue starved; media clock frozen until data arrives (audio clock driven in N3).</summary>
    Buffering,

    /// <summary>A seek is in flight; old-generation frames are discarded.</summary>
    Seeking,

    /// <summary>Media played to the end (looping sessions return to Playing).</summary>
    Ended,

    /// <summary>Terminal failure; see the session's error event for details.</summary>
    Error,

    /// <summary>The session was disposed.</summary>
    Disposed,
}
