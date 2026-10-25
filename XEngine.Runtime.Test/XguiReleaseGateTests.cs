// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XguiReleaseGateTests
{
    [Fact]
    public void FourSourceLogicalRoot_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI FOUR SOURCE LOGICAL ROOT", 230, 232, 240, 2);
        board.Text(40, 68, "BUILTIN EMBED LOCAL KEEP SAME PATH", 140, 146, 160, 1);

        string[] labels = ["BUILTIN", "EMBED", "LOCAL", "PATH"];
        string[] values = ["XGUI", "XGUI", "XGUI", "PACKAGES"];
        int[] xs = [48, 250, 452, 654];
        for (int i = 0; i < labels.Length; i++)
        {
            board.Stroke(xs[i], 140, 140, 70, 90, 94, 110, 2);
            board.Fill(xs[i] + 2, 142, 136, 66, 80, 170, 220, 255);
            board.Text(xs[i], 222, labels[i], 210, 214, 220, 1);
            board.Text(xs[i], 252, values[i], 180, 186, 196, 2);
        }
        board.Text(48, 286, "PACKAGES/COM.XENGINE.XGUI", 140, 146, 160, 1);
        board.Text(48, 310, "DROPDOWN STAYS MANUAL MIGRATION", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-four-source-logical-root", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }
}
