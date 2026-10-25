using XEngine.Editor.Core;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class GameViewScreenshotColorSpaceTests
{
    [Fact]
    public void PreserveDisplayReferredRgbAndMakeOpaqueInPlace_PreservesRgb()
    {
        byte[] rgba = [64, 32, 128, 73];

        GameViewScreenshotColorSpace.PreserveDisplayReferredRgbAndMakeOpaqueInPlace(rgba);

        Assert.Equal(new byte[] { 64, 32, 128, 255 }, rgba);
    }

    [Fact]
    public void PreserveDisplayReferredRgbAndMakeOpaqueInPlace_MakesTransparentBlackOpaque()
    {
        byte[] rgba = [0, 0, 0, 0];

        GameViewScreenshotColorSpace.PreserveDisplayReferredRgbAndMakeOpaqueInPlace(rgba);

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rgba);
    }

    [Fact]
    public void PreserveDisplayReferredRgbAndMakeOpaqueInPlace_RejectsIncompletePixel()
    {
        Assert.Throws<ArgumentException>(() =>
            GameViewScreenshotColorSpace.PreserveDisplayReferredRgbAndMakeOpaqueInPlace([1, 2, 3]));
    }
}
