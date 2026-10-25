// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Scribe;

namespace XEngine.Runtime.Test;

/// <summary>
/// Font-clarity node 4: the shared SDF raster-bucket picker. Scribe rasterizes the distance
/// field at the quality's em height regardless of display size, so text drawn far below its
/// raster size must drop to the smallest bucket that still covers the on-screen size —
/// otherwise the field is bilinearly decimated (~3x at 32em shown at ~11px) and turns to mush.
/// </summary>
public class FontQualityBucketTests
{
    [Theory]
    [InlineData(11f, FontQuality.Low)]     // ~11px effective -> 16em bucket
    [InlineData(16f, FontQuality.Low)]
    [InlineData(20f, FontQuality.Normal)]  // 17..32px -> 32em
    [InlineData(32f, FontQuality.Normal)]
    [InlineData(45f, FontQuality.Normal)]  // authored Normal caps: never raised to High
    [InlineData(200f, FontQuality.Normal)] // ditto — adaptation only lowers
    public void PickBucket_SelectsSmallestBucketCoveringEffectiveSize(float effective, FontQuality expected)
        => Assert.Equal(expected, FontQuality.Normal.PickBucket(effective));

    [Fact]
    public void PickBucket_OnlyLowers_NeverRaisesAboveAuthored()
    {
        // Authored High(64) at 11px effective adapts DOWN to 16 (fidelity beats raster size)...
        Assert.Equal(FontQuality.Low, FontQuality.High.PickBucket(11f));
        // ...but authored Low(16) at 100px stays 16 — an explicit coarse choice is respected.
        Assert.Equal(FontQuality.Low, FontQuality.Low.PickBucket(100f));
        // Normal at 80px stays Normal: adaptation never upsizes past what was asked.
        Assert.Equal(FontQuality.Normal, FontQuality.Normal.PickBucket(80f));
    }

    [Fact]
    public void PickBucket_ExactBoundaryUsesLowerBucket()
    {
        // Exactly 16px display fits the 16em bucket (1:1 sampling); 16.1px needs 32em.
        Assert.Equal(FontQuality.Low, FontQuality.Normal.PickBucket(16f));
        Assert.Equal(FontQuality.Normal, FontQuality.Normal.PickBucket(16.1f));
    }
}
