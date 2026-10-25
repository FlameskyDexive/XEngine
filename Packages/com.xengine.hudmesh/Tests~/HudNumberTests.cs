using System.Collections.Generic;

using Xunit;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

public sealed class HudNumberTests
{
    private static HudSpriteTable MakeDigitTable()
    {
        HudSpriteTable table = new();
        // "b" style: 10 px wide, 14 px high digit art; slot 0.
        foreach (string c in new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "+", "-" })
        {
            HudSpriteInfo info = table.Register("b" + c, new Texture2D());
            info.Width = 10f;
            info.Height = 14f;
            info.MaterialSlot = 0;
        }
        return table;
    }

    private static HudNumberStyle MakeStyle()
        => new()
        {
            Mode = HudNumberMode.DigitAtlas,
            Prefix = 'b',
            Gap = 4f > 0 ? 4 : 4,
            Sign = true,
            Align = HudAlignment.Left,
            MoveCurve = new XEngine.Runtime.AnimationCurve([new XEngine.Runtime.KeyFrame(0f, 0f), new XEngine.Runtime.KeyFrame(1f, 100f)]),
            AlphaCurve = new XEngine.Runtime.AnimationCurve([new XEngine.Runtime.KeyFrame(0f, 1f), new XEngine.Runtime.KeyFrame(1f, 0f)]),
            ScaleCurve = new XEngine.Runtime.AnimationCurve([new XEngine.Runtime.KeyFrame(0f, 1f)]),
        };

    [Fact]
    public void Push_LaysOutSignedDigitsWithGap()
    {
        HudNumberWidget widget = new();
        HudSpriteTable table = MakeDigitTable();
        HudNumberStyle style = MakeStyle();

        widget.Push(style, -128, table, Float3.Zero, Float3.Zero, now: 10f);

        // "-128" → 4 digits, each 10 px + 4 px gap (last gap removed) = 52 px.
        Assert.Equal(52f, widget.Size.X, 5);
        Assert.Equal(14f, widget.Size.Y, 5);
        Assert.True(widget.IsValid);
    }

    [Fact]
    public void Tick_AdvancesAlphaMoveScaleAndExpires()
    {
        HudNumberWidget widget = new();
        HudNumberStyle style = MakeStyle();
        widget.Push(style, 99, MakeDigitTable(), Float3.Zero, Float3.Zero, now: 0f);

        widget.Tick(0.5f);
        Assert.True(widget.IsValid);
        Assert.Equal(1f, widget.Scale, 4);
        Assert.Equal(5000f, widget.Offset.Y, 4); // 50 units x 100 px-per-unit
        // Alpha is internal; expiry is the observable contract:
        widget.Tick(2f);
        Assert.False(widget.IsValid);
    }

    [Fact]
    public void Fill_AtlasMode_WritesTintedQuads()
    {
        HudMeshData target = new();
        List<HudMeshData?> targets = [target];

        HudNumberWidget widget = new();
        widget.Push(MakeStyle(), 7, MakeDigitTable(), Float3.Zero, Float3.Zero, now: 0f);
        widget.Fill(targets, new HudFillContext
        {
            RightStep = new Float3(1f, 0f, 0f),
            UpStep = new Float3(0f, 1f, 0f),
        });

        Assert.Equal(8, target.VertexCount); // sign + digit = two quads
        widget.Tick(0.5f);
        target.BeginFrame();
        widget.Fill(targets, new HudFillContext
        {
            RightStep = new Float3(1f, 0f, 0f),
            UpStep = new Float3(0f, 1f, 0f),
        });
        Assert.Equal(8, target.VertexCount); // still two quads after the animation advance
    }

    [Fact]
    public void Lifetime_IsMaxCurveEndTime()
    {
        HudNumberStyle style = MakeStyle();
        Assert.Equal(1f, style.Lifetime, 4);
    }
}
