// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Playables;

/// <summary>
/// Runtime state of a <see cref="Playable"/> node, owned by the node itself.
/// </summary>
public enum PlayableState
{
    /// <summary>The node is advancing its local time each evaluation.</summary>
    Playing,

    /// <summary>The node retains its current time/weight but does not advance.</summary>
    Paused,

    /// <summary>
    /// The node has reached the end of its bounded duration (non-looping) and will not advance
    /// further until explicitly restarted.
    /// </summary>
    Done,
}
