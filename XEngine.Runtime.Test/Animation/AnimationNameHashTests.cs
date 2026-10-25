// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies <see cref="AnimationNameHash"/> is deterministic, stable across calls, and distinguishes
/// distinct names — the contract the FSM runtime and the future .controller importer rely on for
/// name→index resolution without per-frame string comparison.
/// </summary>
public class AnimationNameHashTests
{
    [Fact]
    public void EmptyString_HashesToZero()
    {
        Assert.Equal(0, AnimationNameHash.Hash(""));
    }

    [Fact]
    public void NullString_HashesToZero()
    {
        Assert.Equal(0, AnimationNameHash.Hash(null!));
    }

    [Fact]
    public void SameName_ProducesSameHash_AcrossCalls()
    {
        int a = AnimationNameHash.Hash("Speed");
        int b = AnimationNameHash.Hash("Speed");

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentNames_ProduceDifferentHashes()
    {
        Assert.NotEqual(AnimationNameHash.Hash("Speed"), AnimationNameHash.Hash("Health"));
        Assert.NotEqual(AnimationNameHash.Hash("Idle"), AnimationNameHash.Hash("Attack"));
    }

    [Fact]
    public void Hash_NonEmpty_IsNonZero()
    {
        // A non-empty name must never collide with the empty-string sentinel (0).
        Assert.NotEqual(0, AnimationNameHash.Hash("Idle"));
        Assert.NotEqual(0, AnimationNameHash.Hash("Speed"));
    }
}
