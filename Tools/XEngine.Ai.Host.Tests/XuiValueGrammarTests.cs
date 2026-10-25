using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The scalar grammars (<c>size</c>, <c>pos</c>, <c>padding</c>, <c>color</c>) and the tag catalog's
/// naming rules. These decide what an agent is allowed to type, so each accepted and each rejected
/// form is pinned explicitly rather than left to the parser's mood.
/// </summary>
public sealed class XuiValueGrammarTests
{
    [Theory]
    [InlineData("480x360", 480f, 360f)]
    [InlineData("0x0", 0f, 0f)]
    [InlineData(" 400 x 44 ", 400f, 44f)]
    public void ParsesFullySpecifiedSizes(string text, float width, float height)
    {
        Assert.True(XuiValueParser.TryParseSize(text, out XuiSize size));
        Assert.Equal(width, size.Width);
        Assert.Equal(height, size.Height);
    }

    [Fact]
    public void UnderscoreLeavesAnAxisToTheLayoutSystem()
    {
        Assert.True(XuiValueParser.TryParseSize("_x44", out XuiSize height));
        Assert.Null(height.Width);
        Assert.Equal(44f, height.Height);

        Assert.True(XuiValueParser.TryParseSize("400x_", out XuiSize width));
        Assert.Equal(400f, width.Width);
        Assert.Null(width.Height);
    }

    [Theory]
    [InlineData("480")]          // a bare number must not silently mean "square"
    [InlineData("480x360x2")]
    [InlineData("x")]
    [InlineData("")]
    [InlineData("480,360")]
    public void RejectsMalformedSizes(string text)
        => Assert.False(XuiValueParser.TryParseSize(text, out _));

    [Theory]
    [InlineData("12,-8", 12f, -8f)]
    [InlineData(" 0 , 0 ", 0f, 0f)]
    public void ParsesFloat2(string text, float x, float y)
    {
        Assert.True(XuiValueParser.TryParseFloat2(text, out XuiFloat2 value));
        Assert.Equal(x, value.X);
        Assert.Equal(y, value.Y);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("1,2,3")]
    public void RejectsMalformedFloat2(string text)
        => Assert.False(XuiValueParser.TryParseFloat2(text, out _));

    [Fact]
    public void UniformInsetAppliesToEveryEdge()
    {
        Assert.True(XuiValueParser.TryParseInsets("24", out float t, out float r, out float b, out float l));
        Assert.Equal(24f, t);
        Assert.Equal(24f, r);
        Assert.Equal(24f, b);
        Assert.Equal(24f, l);
    }

    [Fact]
    public void FourPartInsetIsTopRightBottomLeft()
    {
        Assert.True(XuiValueParser.TryParseInsets("1,2,3,4", out float t, out float r, out float b, out float l));
        Assert.Equal(1f, t);
        Assert.Equal(2f, r);
        Assert.Equal(3f, b);
        Assert.Equal(4f, l);
    }

    [Fact]
    public void RejectsThreePartInset()
        => Assert.False(XuiValueParser.TryParseInsets("1,2,3", out _, out _, out _, out _));

    [Theory]
    [InlineData("#fff", 1f, 1f, 1f, 1f)]
    [InlineData("#000000", 0f, 0f, 0f, 1f)]
    [InlineData("#FF0000", 1f, 0f, 0f, 1f)]
    [InlineData("#00000080", 0f, 0f, 0f, 128f / 255f)]
    [InlineData("white", 1f, 1f, 1f, 1f)]
    [InlineData("transparent", 0f, 0f, 0f, 0f)]
    public void ParsesColorLiterals(string text, float r, float g, float b, float a)
    {
        Assert.True(XuiColorParser.TryParseLiteral(text, out XuiColor color));
        Assert.False(color.IsToken);
        Assert.Equal(r, color.R, 3);
        Assert.Equal(g, color.G, 3);
        Assert.Equal(b, color.B, 3);
        Assert.Equal(a, color.A, 3);
    }

    [Theory]
    [InlineData("#ff")]
    [InlineData("#fffff")]
    [InlineData("#gggggg")]
    public void RejectsMalformedHex(string text)
        => Assert.False(XuiColorParser.TryParseLiteral(text, out _));

    [Fact]
    public void UnknownNameBecomesAThemeTokenRatherThanAnError()
    {
        XuiColor color = XuiColorParser.Parse("panel-bg");

        Assert.True(color.IsToken);
        Assert.Equal("panel-bg", color.Token);
    }

    [Fact]
    public void ShortHexRoundTripsThroughToString()
    {
        XuiColor color = XuiColorParser.Parse("#4C8DFF");
        Assert.Equal("#4C8DFF", color.ToString());
    }

    // ---- tag catalog / naming ----

    [Fact]
    public void EveryBuiltinTagDeclaresAPrefixAndBindingType()
    {
        foreach (XuiTagInfo tag in XuiTagLibrary.Tags)
        {
            Assert.False(string.IsNullOrEmpty(tag.IdPrefix), tag.Tag + " has no id prefix");
            Assert.False(string.IsNullOrEmpty(tag.BindingType), tag.Tag + " has no binding type");
            Assert.StartsWith("X", tag.IdPrefix, System.StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("XBtnLogin", "BtnLogin", "_btnLogin")]
    [InlineData("XTxtTitle", "TxtTitle", "_txtTitle")]
    [InlineData("XImgAvatar", "ImgAvatar", "_imgAvatar")]
    public void IdDerivesPropertyAndFieldNames(string id, string property, string field)
    {
        Assert.Equal(property, XuiTagLibrary.PropertyNameFor(id));
        Assert.Equal(field, XuiTagLibrary.FieldNameFor(id));
    }

    [Theory]
    [InlineData("Btn", "Confirm", "XBtnConfirm")]
    [InlineData("Btn", "XBtnConfirm", "XBtnConfirm")]
    [InlineData("Text", "title", "XTxtTitle")]
    [InlineData("VStack", "Panel", "XVsPanel")]
    public void ShortIdsAreCompletedToTheCanonicalPrefixedForm(string tag, string id, string expected)
        => Assert.Equal(expected, XuiTagLibrary.CanonicalId(tag, id));

    [Fact]
    public void UnknownTagLeavesTheIdAlone()
        => Assert.Equal("Whatever", XuiTagLibrary.CanonicalId("NotATag", "Whatever"));

    [Fact]
    public void LayoutGroupsAreFlaggedAsSuch()
    {
        Assert.True(XuiTagLibrary.Find("VStack")!.IsLayoutGroup);
        Assert.True(XuiTagLibrary.Find("HStack")!.IsLayoutGroup);
        Assert.True(XuiTagLibrary.Find("Grid")!.IsLayoutGroup);
        Assert.False(XuiTagLibrary.Find("Frame")!.IsLayoutGroup);
    }
}
