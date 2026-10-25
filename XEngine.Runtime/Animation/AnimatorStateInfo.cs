// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Animation;

namespace XEngine.Runtime;

/// <summary>
/// Snapshot of the Animator FSM state currently playing on a layer (M25). Mirrors the Unity
/// <c>AnimatorStateInfo</c> fields we need: name hash, clip length, normalized time, looping.
/// </summary>
public struct AnimatorStateInfo
{
    /// <summary>FNV-1a hash of the state name (<see cref="AnimationNameHash"/>).</summary>
    public int nameHash;

    /// <summary>Duration of the state's motion in seconds, or 0 if unknown (BlendTree / empty).</summary>
    public float length;

    /// <summary>Playback time divided by <see cref="length"/> (0 at start; may exceed 1 on looping clips).</summary>
    public float normalizedTime;

    /// <summary>True when the state's motion wraps at the end.</summary>
    public bool isLooping;

    /// <summary>True if this info's name hash matches <paramref name="stateName"/>.</summary>
    public bool IsName(string stateName) => nameHash == AnimationNameHash.Hash(stateName);
}
