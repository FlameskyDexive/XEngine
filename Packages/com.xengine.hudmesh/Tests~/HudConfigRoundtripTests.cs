using System.Collections.Generic;

using Xunit;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

/// <summary>Echo serialization round-trips for the package configuration (the .hudconfig asset payload).</summary>
public sealed class HudConfigRoundtripTests
{
    [Fact]
    public void Config_RoundTripsThroughEcho()
    {
        HudmeshConfig config = HudmeshConfig.CreateDefault();
        config.SpriteLineGap = 14f;
        config.TextLineGap = 9f;
        config.Blood.BarSize = new Float2(200f, 36f);
        config.Blood.Reverse = true;
        config.Talk.ShowTime = 8f;
        config.Talk.BgSlices = new Float4(50f, 20f, 20f, 36f);
        config.NumberStyles.Clear();
        config.NumberStyles.Add(new HudNumberStyle
        {
            Mode = HudNumberMode.SdfText,
            FontSize = 40,
            OutlineWidth = 3f,
            Gap = 6,
            Prefix = 'c',
        });

        EchoObject echo = Serializer.Serialize(config);
        HudmeshConfig loaded = Serializer.Deserialize<HudmeshConfig>(echo)!;

        Assert.NotNull(loaded);
        Assert.Equal(14f, loaded.SpriteLineGap);
        Assert.Equal(9f, loaded.TextLineGap);
        Assert.Equal(200f, loaded.Blood.BarSize.X);
        Assert.True(loaded.Blood.Reverse);
        Assert.Equal(8f, loaded.Talk.ShowTime);
        Assert.Equal(50f, loaded.Talk.BgSlices.X);
        HudNumberStyle style = Assert.Single(loaded.NumberStyles);
        Assert.Equal(HudNumberMode.SdfText, style.Mode);
        Assert.Equal(40, style.FontSize);
        Assert.Equal(3f, style.OutlineWidth);
        Assert.Equal('c', style.Prefix);
    }

    [Fact]
    public void NumberStyle_CurvesRoundTripWithKeys()
    {
        HudNumberStyle style = HudmeshConfig.CreateDefault().NumberStyles[0];

        EchoObject echo = Serializer.Serialize(style);
        HudNumberStyle loaded = Serializer.Deserialize<HudNumberStyle>(echo)!;

        Assert.Equal(style.Lifetime, loaded.Lifetime, 4);
        Assert.Equal(style.MoveCurve.Keys.Count, loaded.MoveCurve.Keys.Count);
        Assert.Equal(1.5f, loaded.MoveCurve.Evaluate(0f), 3);
        Assert.Equal(2.5f, loaded.MoveCurve.Evaluate(1.2f), 3);
        Assert.Equal(0f, loaded.AlphaCurve.Evaluate(1.2f), 3);
    }

    [Fact]
    public void DefaultConfig_MatchesUnityHUDReferenceValues()
    {
        HudmeshConfig config = HudmeshConfig.CreateDefault();

        Assert.Equal(180f, config.Blood.BarSize.X);
        Assert.Equal(30f, config.Blood.BarSize.Y);
        Assert.Equal(160f, config.Blood.FillSize.X);
        Assert.Equal(13f, config.Blood.FillSize.Y);
        Assert.Equal(6f, config.Talk.ShowTime);
        Assert.Equal(1f, config.Talk.VanishTime);
        Assert.Equal(266f, config.Talk.MaxLineWidth);
        Assert.Equal(1.2f, config.NumberStyles[0].Lifetime, 3);
    }
}
