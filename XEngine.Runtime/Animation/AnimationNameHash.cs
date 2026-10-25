// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Animation;

/// <summary>
/// Deterministic 32-bit FNV-1a string hash for animation names (parameters, states, tags). Used to
/// avoid per-frame string comparisons in the FSM runtime. FNV-1a is chosen (over <c>string.GetHashCode</c>,
/// which is randomized per-process) so hashes are stable across runs, platforms, and the
/// <c>.controller</c> asset compiled at import (M4).
/// </summary>
public static class AnimationNameHash
{
    private const uint FnvOffset = 2166136261u;
    private const uint FnvPrime = 16777619u;

    /// <summary>Computes the FNV-1a hash of <paramref name="name"/>. The empty string hashes to 0.</summary>
    public static int Hash(string name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;

        uint h = FnvOffset;
        int n = name.Length;
        for (int i = 0; i < n; i++)
        {
            h ^= name[i];
            h *= FnvPrime;
        }
        return (int)h;
    }
}
