using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class TextureAlphaSourceTests
{
    [Fact]
    public void None_MakesPixelsOpaqueWithoutChangingRgb()
    {
        byte[] pixels = { 12, 34, 56, 0, 201, 101, 7, 99 };

        Texture2D.ApplyAlphaSourceToPixels(pixels, TextureAlphaSource.None);

        Assert.Equal(new byte[] { 12, 34, 56, 255, 201, 101, 7, 255 }, pixels);
    }

    [Fact]
    public void FromInput_LeavesPixelsUnchanged()
    {
        byte[] pixels = { 12, 34, 56, 0, 201, 101, 7, 99 };
        byte[] expected = (byte[])pixels.Clone();

        Texture2D.ApplyAlphaSourceToPixels(pixels, TextureAlphaSource.FromInput);

        Assert.Equal(expected, pixels);
    }

    [Fact]
    public void FromGrayScale_UsesUnmodifiedRgbArithmeticMean()
    {
        byte[] pixels = { 255, 0, 0, 17, 0, 255, 0, 17, 0, 0, 255, 17, 1, 2, 3, 17 };

        Texture2D.ApplyAlphaSourceToPixels(pixels, TextureAlphaSource.FromGrayScale);

        Assert.Equal(new byte[]
        {
            255, 0, 0, 85,
            0, 255, 0, 85,
            0, 0, 255, 85,
            1, 2, 3, 2,
        }, pixels);
    }

    [Fact]
    public void ApplyAlphaSource_RejectsIncompletePixelAndUnknownSource()
    {
        Assert.Throws<ArgumentException>(() =>
            Texture2D.ApplyAlphaSourceToPixels(new byte[3], TextureAlphaSource.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Texture2D.ApplyAlphaSourceToPixels(Array.Empty<byte>(), (TextureAlphaSource)99));
    }

    [Fact]
    public void TextureImporter_BumpsVersionAndDefaultsMissingAlphaToInput()
    {
        var importer = new TextureImporter();

        Assert.Equal(6, importer.Version);
        Assert.Equal(TextureAlphaSource.FromInput, TextureImporter.ResolveAlphaSource(null));
        Assert.Equal(TextureAlphaSource.FromInput, TextureImporter.ResolveAlphaSource(EchoObject.NewCompound()));
    }

    [Theory]
    [InlineData(0, TextureAlphaSource.None)]
    [InlineData(1, TextureAlphaSource.FromInput)]
    [InlineData(2, TextureAlphaSource.FromGrayScale)]
    public void TextureImporter_CurrentAlphaSourceWinsOverLegacy(int current, TextureAlphaSource expected)
    {
        EchoObject settings = EchoObject.NewCompound();
        settings["alphaSource"] = new EchoObject(current);
        settings["alphaUsage"] = new EchoObject((int)TextureAlphaSource.FromGrayScale);
        settings["grayscaleToAlpha"] = new EchoObject(true);

        Assert.Equal(expected, TextureImporter.ResolveAlphaSource(settings));
    }

    [Fact]
    public void TextureImporter_ResolvesLegacyNamesWhenCurrentSettingIsMissing()
    {
        EchoObject oldUsage = EchoObject.NewCompound();
        oldUsage["alphaUsage"] = new EchoObject((int)TextureAlphaSource.FromGrayScale);
        Assert.Equal(TextureAlphaSource.FromGrayScale, TextureImporter.ResolveAlphaSource(oldUsage));

        EchoObject camelCaseFlag = EchoObject.NewCompound();
        camelCaseFlag["grayScaleToAlpha"] = new EchoObject(true);
        Assert.Equal(TextureAlphaSource.FromGrayScale, TextureImporter.ResolveAlphaSource(camelCaseFlag));

        EchoObject lowerCaseFlag = EchoObject.NewCompound();
        lowerCaseFlag["grayscaleToAlpha"] = new EchoObject(true);
        Assert.Equal(TextureAlphaSource.FromGrayScale, TextureImporter.ResolveAlphaSource(lowerCaseFlag));
    }
}
