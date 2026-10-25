// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XguiDefaultResourcesTests
{
    [Fact]
    public void PackageShipsDefaultSpriteAndFontFiles()
    {
        string root = FindRepositoryRoot();
        string resources = Path.Combine(root, "Packages", "com.xengine.xgui", "Runtime", "Resources");
        string sprite = Path.Combine(resources, "DefaultSprite.png");
        string font = Path.Combine(resources, "DefaultFont.ttf");

        Assert.True(File.Exists(sprite), sprite);
        Assert.True(File.Exists(font), font);
        Assert.True(new FileInfo(sprite).Length > 1000);
        Assert.True(new FileInfo(font).Length > 1000);
        Assert.True(File.Exists(sprite + ".meta"));
        Assert.True(File.Exists(font + ".meta"));
        Assert.True(File.Exists(resources + ".meta"));
    }

    [Fact]
    public void DefaultResources_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI PACKAGE DEFAULT RESOURCES", 230, 232, 240, 2);
        board.Text(40, 68, "SPRITE PLUS FONT SHIP WITH STABLE GUIDS", 140, 146, 160, 1);

        board.Stroke(48, 120, 360, 180, 90, 94, 110, 2);
        board.Fill(50, 122, 356, 176, 80, 170, 220, 255);
        board.Text(64, 140, "DEFAULT SPRITE", 18, 19, 26, 2);
        board.Text(64, 176, "256X256 SLICED PANEL", 18, 19, 26, 1);
        board.Text(64, 204, "BORDER 32 / PPU 100", 18, 19, 26, 1);
        board.Text(64, 232, "FALLBACK UIPANEL", 18, 19, 26, 1);

        board.Stroke(452, 120, 360, 180, 90, 94, 110, 2);
        board.Fill(454, 122, 356, 176, 230, 210, 120, 255);
        board.Text(468, 140, "DEFAULT FONT", 18, 19, 26, 2);
        board.Text(468, 176, "GEIST REGULAR TTF", 18, 19, 26, 1);
        board.Text(468, 204, "IME READY FALLBACK", 18, 19, 26, 1);
        board.Text(468, 232, "STABLE PACKAGE GUID", 18, 19, 26, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-package-default-resources", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
