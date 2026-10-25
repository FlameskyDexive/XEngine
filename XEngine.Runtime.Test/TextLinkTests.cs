// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class TextLinkTests : RuntimeTestBase
{
    [Fact]
    public void LinkInfo_ExposesQuotedAndAnchorHrefs()
    {
        TextComponent text = CreateCanvasText();
        text.RichTextEnabled = true;
        text.Text = "Go <link=\"alpha\">here</link> or <a href=\"beta\">there</a>.";
        text.Alignment = TextAlignment.TopLeft;
        text.Size = 20;

        TextLinkInfo[] links = text.linkInfo;
        Assert.Equal(2, links.Length);
        Assert.Equal("alpha", links[0].GetLinkID());
        Assert.Equal(3, links[0].LinkTextFirstCharacterIndex);
        Assert.Equal(4, links[0].LinkTextLength);
        Assert.Equal("beta", links[1].GetLinkID());
    }

    [Fact]
    public void FindIntersectingLink_HitsGlyphUnderDesignPoint()
    {
        TextComponent text = CreateCanvasText();
        text.RichTextEnabled = true;
        text.Text = "xx<link=id>LINK</link>yy";
        text.Alignment = TextAlignment.TopLeft;
        text.Size = 24;
        text.HorizontalOverflow = HorizontalWrapMode.Overflow;

        RectTransform rt = text.GameObject.RectTransform!;
        Rect rect = rt.ComputedRect;
        float prefix = text.MeasureWidth("xx");
        // First line is near the top of the rect; sample the middle of the LINK run.
        Float2 design = new Float2(rect.Min.X + prefix + 8f, rect.Max.Y - 12f);

        Assert.Equal(0, text.FindIntersectingLink(design));
        Assert.Equal("id", text.HitLink(design));
        Assert.Equal(-1, text.FindIntersectingLink(new Float2(rect.Min.X + 2f, design.Y)));
    }

    [Fact]
    public void OnPointerClick_RaisesOnLinkClicked()
    {
        TextComponent text = CreateCanvasText();
        text.RichTextEnabled = true;
        text.Text = "<link=clicked>OK</link>";
        text.Alignment = TextAlignment.TopLeft;
        text.Size = 24;

        string? received = null;
        text.OnLinkClicked += id => received = id;

        Rect rect = text.GameObject.RectTransform!.ComputedRect;
        var e = new PointerEventData { Button = MouseButton.Left, DesignPosition = new Float2(rect.Min.X + 6f, rect.Max.Y - 12f) };
        text.OnPointerClick(e);

        Assert.Equal("clicked", received);
        Assert.True(e.Used);
    }

    [Fact]
    public void TextLinks_AcceptanceScreenshot()
    {
        TextComponent text = CreateCanvasText();
        text.RichTextEnabled = true;
        text.Text = "Go <link=\"shop\">here</link> or <a href=\"help\">there</a>.";
        text.Alignment = TextAlignment.TopLeft;
        text.Size = 20;

        TextLinkInfo[] links = text.linkInfo;
        Assert.Equal(2, links.Length);

        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "TEXT RICH LINKS / TEXTINFO", 230, 232, 240, 2);
        board.Text(40, 68, "FINDINTERSECTINGLINK ONLINKCLICKED", 140, 146, 160, 1);

        board.Text(40, 120, $"LINKS {links.Length}", 210, 214, 220, 1);
        board.Text(40, 148, $"0 {links[0].GetLinkID()} @{links[0].LinkTextFirstCharacterIndex}+{links[0].LinkTextLength}", 180, 186, 196, 2);
        board.Text(40, 176, $"1 {links[1].GetLinkID()} @{links[1].LinkTextFirstCharacterIndex}+{links[1].LinkTextLength}", 180, 186, 196, 2);

        TextInfo info = text.textInfo;
        board.Text(40, 220, $"CHARS {info.characterCount}  LINKCOUNT {info.linkCount}", 140, 146, 160, 1);
        board.Fill(40, 260, 200, 28, 80, 170, 220, 255);
        board.Text(48, 266, "HIT LINK SHOP", 18, 19, 26, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-text-rich-links", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    private TextComponent CreateCanvasText()
    {
        Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        GameObject textObject = CreateGameObject("Text");
        textObject.SetParent(canvasObject, worldPositionStays: false);
        textObject.EnsureRectTransform();
        TextComponent text = textObject.AddComponent<TextComponent>();

        RectTransform rt = textObject.RectTransform!;
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.Zero;
        rt.Pivot = new Float2(0f, 1f);
        rt.AnchoredPosition = new Float2(20f, 400f);
        rt.SizeDelta = new Float2(400f, 80f);

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(800f, 450f);
        canvasObject.GetComponent<GameCanvas>()!.RebuildIfDirty();
        return text;
    }
}
