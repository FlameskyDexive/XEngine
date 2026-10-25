// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Animation;

/// <summary>
/// Which channels of a <see cref="BonePose"/> are driven by the source(s) contributing to it this
/// frame. A bone that no active clip animates keeps <see cref="None"/> and is skipped on output, so
/// the bone retains whatever local transform gameplay (or a parent layer) last wrote.
/// </summary>
[System.Flags]
public enum PoseChannel : byte
{
    None = 0,
    Position = 1,
    Rotation = 2,
    Scale = 4,
    All = Position | Rotation | Scale,
}

/// <summary>
/// One bone's local transform for a single frame, plus which channels are driven. Value type —
/// stored in a pooled <see cref="Pose"/> array and mutated in place via <c>ref</c>.
/// </summary>
public struct BonePose
{
    public Float3 LocalPosition;
    public Quaternion LocalRotation;
    public Float3 LocalScale;
    public PoseChannel Channels;
}
