// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Regression tests for the copy-on-write CollisionMatrix (prowl-sync M2.1, ported from upstream
/// 143cbacc0 + eb7f86b81): broad-phase worker threads read the matrix while the main thread writes
/// it, so every setter must publish a fully-formed snapshot and the hot-path reader must never throw.
/// </summary>
public class CollisionMatrixCowTests
{
    public CollisionMatrixCowTests() => CollisionMatrix.Reset();

    [Fact]
    public void SetLayerCollision_IsSymmetric()
    {
        try
        {
            CollisionMatrix.SetAllCollisions(false);
            CollisionMatrix.SetLayerCollision(3, 7, true);

            Assert.True(CollisionMatrix.GetLayerCollision(3, 7));
            Assert.True(CollisionMatrix.GetLayerCollision(7, 3));
            Assert.False(CollisionMatrix.GetLayerCollision(3, 8));
        }
        finally { CollisionMatrix.Reset(); }
    }

    [Fact]
    public void OutOfRangeLayers_NeverCollide_AndDoNotThrow()
    {
        // Hot path: runs for every broad-phase pair, so out-of-range must be a quiet "no".
        Assert.False(CollisionMatrix.GetLayerCollision(-1, 0));
        Assert.False(CollisionMatrix.GetLayerCollision(0, 32));
        Assert.False(CollisionMatrix.GetLayerCollision(int.MinValue, int.MaxValue));

        var row = CollisionMatrix.GetLayerCollisions(99);
        Assert.Equal(CollisionMatrix.LayerCount, row.Length);
        Assert.All(row, Assert.False);
    }

    [Fact]
    public void SetLayerCollisions_CoversWholeRow_IncludingSelf()
    {
        try
        {
            CollisionMatrix.Reset();
            CollisionMatrix.SetLayerCollisions(5, false);

            for (int other = 0; other < CollisionMatrix.LayerCount; other++)
                Assert.False(CollisionMatrix.GetLayerCollision(5, other));

            CollisionMatrix.SetLayerCollisions(5, true);
            Assert.True(CollisionMatrix.GetLayerCollision(5, 5));
        }
        finally { CollisionMatrix.Reset(); }
    }

    [Fact]
    public void SetRows_ForceSymmetric_SoOrderDoesNotMatter()
    {
        try
        {
            // One direction only: the broad phase may ask (a,b) or (b,a), so an asymmetric
            // input must not make collisions depend on pair order.
            var rows = new uint[CollisionMatrix.LayerCount];
            rows[2] = 1u << 9;

            CollisionMatrix.SetRows(rows);

            Assert.True(CollisionMatrix.GetLayerCollision(2, 9));
            Assert.True(CollisionMatrix.GetLayerCollision(9, 2));
            Assert.False(CollisionMatrix.GetLayerCollision(2, 10));
        }
        finally { CollisionMatrix.Reset(); }
    }

    [Fact]
    public void GetRows_ReturnsSnapshot_CallerMutationDoesNotLeak()
    {
        try
        {
            CollisionMatrix.Reset();
            uint[] rows = CollisionMatrix.GetRows();
            rows[4] = 0u;

            Assert.True(CollisionMatrix.GetLayerCollision(4, 4));
        }
        finally { CollisionMatrix.Reset(); }
    }

    [Fact]
    public void EnsureSymmetric_PreservesState()
    {
        try
        {
            // Every public setter is already symmetric, so EnsureSymmetric must be a state-preserving
            // no-op on anything reachable through the API (kept for matrices assembled externally).
            CollisionMatrix.SetLayerCollision(2, 9, true);
            CollisionMatrix.SetLayerCollision(4, 5, false);
            CollisionMatrix.EnsureSymmetric();

            Assert.True(CollisionMatrix.GetLayerCollision(2, 9));
            Assert.True(CollisionMatrix.GetLayerCollision(9, 2));
            Assert.False(CollisionMatrix.GetLayerCollision(4, 5));
            Assert.False(CollisionMatrix.GetLayerCollision(5, 4));
        }
        finally { CollisionMatrix.Reset(); }
    }
}
