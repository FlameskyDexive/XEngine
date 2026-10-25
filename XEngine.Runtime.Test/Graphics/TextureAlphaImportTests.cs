using ImageMagick;
using XEngine.Echo;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class TextureAlphaImportTests
{
    [Theory]
    [InlineData(TextureAlphaSource.FromInput, 255)]
    [InlineData(TextureAlphaSource.None, 255)]
    [InlineData(TextureAlphaSource.FromGrayScale, 85)]
    public void DecodedPixelsAndFirstCachePayloadUseAuthoredAlpha(TextureAlphaSource source, int expectedAlpha)
    {
        using var image = new MagickImage(MagickColors.Red, 2, 1);
        using var texture = Texture2D.FromImage(image, srgbSampling: true, alphaSource: source);
        byte[] expected = [255, 0, 0, (byte)expectedAlpha, 255, 0, 0, (byte)expectedAlpha];
        Assert.Equal(expected, texture._importedPixels);

        var cache = EchoObject.NewCompound();
        texture.Serialize(ref cache, new SerializationContext());
        Assert.Equal(expected, cache["Data"].ByteArrayValue);
        Assert.Null(texture._importedPixels);
    }
}
