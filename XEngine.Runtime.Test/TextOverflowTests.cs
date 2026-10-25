// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class TextOverflowTests : RuntimeTestBase
{
    private const string Sample = "The quick brown fox jumps over the lazy dog and then keeps running past the fence.";

    [Fact]
    public void WrapPreferredWidthFitsConstraintWhileOverflowDoesNot()
    {
        TextComponent text = CreateText();
        text.Text = Sample;
        text.Size = 18;
        text.BestFit = false;

        text.HorizontalOverflow = HorizontalWrapMode.Wrap;
        Float2 wrapped = text.GetPreferredValues(120f, 400f);
        Assert.True(wrapped.X <= 121f, $"wrap width {wrapped.X} should fit 120");
        Assert.True(wrapped.Y > 18f, "wrapped text should occupy more than one line");

        text.HorizontalOverflow = HorizontalWrapMode.Overflow;
        Float2 overflow = text.GetPreferredValues(120f, 400f);
        Assert.True(overflow.X > wrapped.X, $"overflow width {overflow.X} should exceed wrap width {wrapped.X}");
    }

    [Fact]
    public void BestFitShrinksTextToFitTheRect()
    {
        TextComponent text = CreateText();
        text.Text = Sample;
        text.Size = 36;
        text.HorizontalOverflow = HorizontalWrapMode.Wrap;
        text.VerticalOverflow = VerticalWrapMode.Truncate;
        text.BestFit = false;
        Float2 unfitted = text.GetPreferredValues(160f, 36f);
        Assert.True(unfitted.Y > 36f);

        text.BestFit = true;
        text.BestFitMinSize = 8;
        text.BestFitMaxSize = 36;
        Float2 fitted = text.GetPreferredValues(160f, 36f);
        Assert.True(fitted.Y <= 36.5f, $"best-fit height {fitted.Y} should fit 36");
        Assert.True(fitted.X <= 160.5f, $"best-fit width {fitted.X} should fit 160");
    }

    [Fact]
    public void TextOverflow_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 420, 18, 19, 26);
        board.Fill(24, 24, 812, 372, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 372, 70, 74, 90, 2);
        board.Text(40, 40, "TEXT OVERFLOW / BEST FIT", 230, 232, 240, 2);
        board.Text(40, 68, "WRAP OVERFLOW TRUNCATE BESTFIT ELLIPSIS", 140, 146, 160, 1);

        TextComponent text = CreateText();
        text.Text = Sample;
        text.Size = 28;
        text.BestFitMinSize = 8;
        text.BestFitMaxSize = 28;

        (string label, HorizontalWrapMode h, VerticalWrapMode v, bool best, float boxW, float boxH, byte[] color)[] cases =
        [
            ("WRAP", HorizontalWrapMode.Wrap, VerticalWrapMode.Truncate, false, 120f, 70f, [80, 170, 220]),
            ("OVERFLOW", HorizontalWrapMode.Overflow, VerticalWrapMode.Overflow, false, 120f, 70f, [230, 170, 80]),
            ("TRUNCATE", HorizontalWrapMode.Wrap, VerticalWrapMode.Truncate, false, 120f, 36f, [230, 120, 90]),
            ("BESTFIT", HorizontalWrapMode.Wrap, VerticalWrapMode.Truncate, true, 120f, 36f, [90, 200, 150]),
            ("ELLIPSIS", HorizontalWrapMode.Wrap, VerticalWrapMode.Ellipsis, false, 120f, 36f, [180, 120, 220]),
        ];

        int[] xs = [40, 200, 360, 520, 680];
        for (int i = 0; i < cases.Length; i++)
        {
            var item = cases[i];
            text.HorizontalOverflow = item.h;
            text.VerticalOverflow = item.v;
            text.BestFit = item.best;
            Float2 size = text.GetPreferredValues(item.boxW, item.boxH);
            int boxW = (int)MathF.Round(item.boxW);
            int boxH = (int)MathF.Round(item.boxH);
            int measuredW = Math.Max(1, (int)MathF.Round(size.X));
            int measuredH = Math.Max(1, (int)MathF.Round(size.Y));
            int x = xs[i];
            int y = 170;
            board.Stroke(x, y, boxW, boxH, 90, 94, 110, 2);
            board.Fill(x + 2, y + 2, Math.Min(boxW - 4, measuredW), Math.Min(boxH - 4, measuredH), item.color[0], item.color[1], item.color[2], 255);
            board.Text(x, 260, item.label, 210, 214, 220, 1);
            board.Text(x, 278, $"{measuredW}X{measuredH}", 180, 186, 196, 2);
            board.Text(x, 308, $"BOX {boxW}X{boxH}", 140, 146, 160, 1);
        }

        string path = XGuiAcceptanceScreenshot.Save("xgui-text-overflow-best-fit", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
        Assert.True(text.GetPreferredValues(140f, 70f).X > 0f);
    }

    private TextComponent CreateText()
    {
        GameObject go = CreateGameObject("Text");
        go.EnsureRectTransform();
        return go.AddComponent<TextComponent>();
    }
}
