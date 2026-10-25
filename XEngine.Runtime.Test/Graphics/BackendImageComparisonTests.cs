// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

public class BackendImageComparisonTests
{
    [Fact]
    public void Compare_ExactImages_PassWithBlackDifferenceImage()
    {
        byte[] pixels = [10, 20, 30, 255, 40, 50, 60, 255];

        ImageComparisonResult result = BackendImageComparison.Compare(
            new RgbaImage(2, 1, pixels),
            new RgbaImage(2, 1, (byte[])pixels.Clone()),
            ImageComparisonOptions.Exact);

        Assert.True(result.Passed);
        Assert.Equal(0, result.MismatchedPixels);
        Assert.Equal(0, result.MaxChannelDelta);
        Assert.Equal(0d, result.MeanAbsoluteChannelDelta);
        Assert.Equal([0, 0, 0, 255, 0, 0, 0, 255], result.DifferenceRgba);
    }

    [Fact]
    public void Compare_ChannelTolerance_AllowsBackendRoundingNoise()
    {
        ImageComparisonResult result = BackendImageComparison.Compare(
            new RgbaImage(1, 1, [64, 128, 192, 255]),
            new RgbaImage(1, 1, [65, 126, 195, 255]),
            new ImageComparisonOptions(MaxChannelDelta: 3, MaxMismatchRatio: 0d));

        Assert.True(result.Passed, result.ToString());
        Assert.Equal(0, result.MismatchedPixels);
        Assert.Equal(3, result.MaxChannelDelta);
        Assert.Equal(1.5d, result.MeanAbsoluteChannelDelta);
    }

    [Fact]
    public void Compare_VisibleMismatch_FailsAndProducesRedHeatmapPixel()
    {
        ImageComparisonResult result = BackendImageComparison.Compare(
            new RgbaImage(2, 1, [0, 0, 0, 255, 100, 100, 100, 255]),
            new RgbaImage(2, 1, [0, 0, 0, 255, 120, 100, 100, 255]),
            new ImageComparisonOptions(MaxChannelDelta: 3, MaxMismatchRatio: 0d));

        Assert.False(result.Passed);
        Assert.Equal(1, result.MismatchedPixels);
        Assert.Equal(0.5d, result.MismatchRatio);
        Assert.Equal(20, result.MaxChannelDelta);
        Assert.Equal([0, 0, 0, 255, 160, 0, 0, 255], result.DifferenceRgba);
    }

    [Fact]
    public void Compare_DifferentDimensions_ThrowsClearError()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => BackendImageComparison.Compare(
            new RgbaImage(1, 1, [0, 0, 0, 255]),
            new RgbaImage(2, 1, new byte[8]),
            ImageComparisonOptions.Exact));

        Assert.Contains("dimensions differ", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
