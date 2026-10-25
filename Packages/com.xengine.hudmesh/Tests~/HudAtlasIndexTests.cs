using Xunit;

using XEngine.Runtime.Resources;

namespace XEngine.Hudmesh.Tests;

public sealed class HudAtlasIndexTests
{
    [Fact]
    public void RegisterAtlas_ParsesSubRectsWithBottomUpY()
    {
        var atlas = new Texture2D(256u, 256u);
        HudSpriteTable table = new();
        string index =
            "# comment line\n" +
            "atlas hud_atlas1.png 256 256\n" +
            "sprite Blood 10 20 100 30 0 0 0 0\n" +
            "sprite b5 120 0 20 28 0 0 0 0\n";

        int count = table.RegisterAtlas(atlas, index);

        Assert.Equal(2, count);
        HudSpriteInfo? blood = table.Get("Blood");
        Assert.NotNull(blood);
        Assert.Equal(100f, blood!.Width);
        Assert.Equal(30f, blood.Height);
        // v = 0 is the bottom row: uv vMin = y/256, vMax = (y+h)/256.
        Assert.Equal(10f / 256f, blood.UvMin.X, 6);
        Assert.Equal(20f / 256f, blood.UvMin.Y, 6);
        Assert.Equal(110f / 256f, blood.UvMax.X, 6);
        Assert.Equal(50f / 256f, blood.UvMax.Y, 6);

        HudSpriteInfo? b5 = table.Get("b5");
        Assert.NotNull(b5);
        Assert.Equal(20f, b5!.Width);
        Assert.Equal(28f, b5.Height);
    }
}
