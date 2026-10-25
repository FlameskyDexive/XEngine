// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Timeline;

/// <summary>How a <see cref="PlayableDirector"/> behaves when its playhead reaches the timeline duration.</summary>
public enum DirectorWrapMode : byte
{
    /// <summary>Hold the last frame (clock clamps at the duration).</summary>
    Hold = 0,

    /// <summary>Loop back to the start and keep playing.</summary>
    Loop = 1,

    /// <summary>Stop playing entirely once the end is reached.</summary>
    None = 2,
}
