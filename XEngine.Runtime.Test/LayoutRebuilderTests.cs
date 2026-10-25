// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class LayoutRebuilderTests : RuntimeTestBase
{
    private const string Sample = "The quick brown fox jumps over the lazy dog.";

    [Fact]
    public void TextReportsPreferredSizeToLayoutUtility()
    {
        TextComponent text = CreateText();
        text.Text = Sample;
        text.Size = 18;
        text.HorizontalOverflow = HorizontalWrapMode.Overflow;
        text.BestFit = false;

        Float2 preferred = text.GetPreferredValues();
        Assert.True(preferred.X > 80f);
        Assert.Equal(preferred.X, text.PreferredWidth, 2);
        Assert.Equal(preferred, UILayoutUtility.GetPreferredSize(text.GameObject));
    }

    [Fact]
    public void ForceRebuildFitsTextToPreferredSize()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject textObject = CreateGameObject("Text");
        textObject.SetParent(canvasObject, worldPositionStays: false);
        textObject.EnsureRectTransform();
        TextComponent text = textObject.AddComponent<TextComponent>();
        UIContentSizeFitter fitter = textObject.AddComponent<UIContentSizeFitter>();
        fitter.HorizontalFit = UIContentSizeFitter.FitMode.PreferredSize;
        fitter.VerticalFit = UIContentSizeFitter.FitMode.PreferredSize;

        text.Text = Sample;
        text.Size = 20;
        text.HorizontalOverflow = HorizontalWrapMode.Overflow;
        text.BestFit = false;
        RectTransform rt = textObject.RectTransform!;
        rt.AnchorMin = new Float2(0.5f, 0.5f);
        rt.AnchorMax = new Float2(0.5f, 0.5f);
        rt.SizeDelta = new Float2(40f, 20f);

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(800f, 450f);
        try
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            Float2 preferred = text.GetPreferredValues();
            Assert.True(rt.SizeDelta.X >= preferred.X - 1f, $"fitted width {rt.SizeDelta.X} should reach preferred {preferred.X}");
            Assert.True(rt.SizeDelta.Y >= preferred.Y - 1f, $"fitted height {rt.SizeDelta.Y} should reach preferred {preferred.Y}");
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void LayoutRebuilder_AcceptanceScreenshot()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        GameObject textObject = CreateGameObject("Text");
        textObject.SetParent(canvasObject, worldPositionStays: false);
        textObject.EnsureRectTransform();
        TextComponent text = textObject.AddComponent<TextComponent>();
        UIContentSizeFitter fitter = textObject.AddComponent<UIContentSizeFitter>();
        fitter.HorizontalFit = UIContentSizeFitter.FitMode.PreferredSize;
        fitter.VerticalFit = UIContentSizeFitter.FitMode.PreferredSize;
        text.Text = Sample;
        text.Size = 22;
        text.HorizontalOverflow = HorizontalWrapMode.Overflow;
        RectTransform rt = textObject.RectTransform!;
        rt.SizeDelta = new Float2(48f, 18f);

        scene.Add(canvasObject);
        scene.Enable();

        Float2 before = rt.SizeDelta;
        LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
        Float2 after = rt.SizeDelta;
        Float2 preferred = text.GetPreferredValues();

        var board = new XGuiAcceptanceScreenshot.Canvas(760, 360, 18, 19, 26);
        board.Fill(24, 24, 712, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 712, 312, 70, 74, 90, 2);
        board.Text(40, 40, "LAYOUT REBUILDER / CONTENT SIZE FITTER", 230, 232, 240, 2);
        board.Text(40, 72, "TEXT PREFERRED SIZE DRIVES THE RECT", 140, 146, 160, 1);

        DrawBox(board, 60, 140, before, [230, 120, 90], "BEFORE");
        DrawBox(board, 280, 140, after, [90, 200, 150], "AFTER");
        board.Text(500, 140, "PREFERRED", 210, 214, 220, 1);
        board.Text(500, 164, $"{Math.Max(1, (int)MathF.Round(preferred.X))}X{Math.Max(1, (int)MathF.Round(preferred.Y))}", 180, 186, 196, 2);
        board.Text(500, 196, "FORCE REBUILD", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-layout-rebuilder-fitter", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
        Assert.True(after.X > before.X);
        Assert.True(after.Y >= before.Y);
    }

    private static void DrawBox(XGuiAcceptanceScreenshot.Canvas board, int x, int y, Float2 size, byte[] color, string label)
    {
        int w = Math.Max(8, Math.Min(180, (int)MathF.Round(size.X * 0.35f)));
        int h = Math.Max(8, Math.Min(80, (int)MathF.Round(size.Y)));
        board.Stroke(x, y, w, h, 90, 94, 110, 2);
        board.Fill(x + 2, y + 2, Math.Max(1, w - 4), Math.Max(1, h - 4), color[0], color[1], color[2], 255);
        board.Text(x, y + 96, label, 210, 214, 220, 1);
        board.Text(x, y + 116, $"{Math.Max(1, (int)MathF.Round(size.X))}X{Math.Max(1, (int)MathF.Round(size.Y))}", 180, 186, 196, 2);
    }

    private TextComponent CreateText()
    {
        GameObject go = CreateGameObject("Text");
        go.EnsureRectTransform();
        return go.AddComponent<TextComponent>();
    }
}
