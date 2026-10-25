// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Playables;

/// <summary>
/// Controls how a <see cref="Clock"/> behaves when its playhead reaches the bounds of its
/// <see cref="Clock.Duration"/>. Has no effect while <see cref="Clock.Duration"/> is unbounded.
/// </summary>
public enum ClockWrapMode
{
    /// <summary>Time stops at the bound and stays there.</summary>
    Clamp,

    /// <summary>Time wraps to the opposite bound and keeps advancing in the same direction.</summary>
    Loop,

    /// <summary>Time reverses direction at each bound (bounces between 0 and Duration).</summary>
    PingPong,
}
