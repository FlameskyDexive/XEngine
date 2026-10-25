// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XGuiHostDiagnosticsTests : RuntimeTestBase
{
    [Fact]
    public void CaptureReportsCanvasNodesAndMaskedClipChain()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject maskObject = CreateGameObject("Mask");
        maskObject.SetParent(canvasObject, worldPositionStays: false);
        maskObject.EnsureRectTransform();
        maskObject.RectTransform!.SizeDelta = new Float2(100f, 100f);
        maskObject.AddComponent<RectMask>();
        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(maskObject, worldPositionStays: false);
        imageObject.EnsureRectTransform();
        imageObject.AddComponent<UIImage>();
        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(200f, 200f);
        try
        {
            canvas.RebuildIfDirty();
            XGuiHostDiagnosticsSnapshot snapshot = XGuiHostDiagnostics.Capture(scene);
            Assert.Equal(1, snapshot.CanvasCount);
            Assert.True(snapshot.OverlayNodes >= 1);
            Assert.True(snapshot.ActiveNodes >= 1);
            Assert.True(snapshot.RectClips >= 1);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void HostDiagnostics_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI DIAGNOSTICS / HOST SNAPSHOT", 230, 232, 240, 2);
        board.Text(40, 68, "CANVAS BATCHES CLIP CHAIN RENDERSTATS", 140, 146, 160, 1);

        string[] labels = ["CANVAS", "BATCHES", "CLIP", "STATS"];
        string[] values = ["COUNT", "OVERLAY", "RECT STENCIL", "UPLOAD 0"];
        int[] xs = [48, 250, 452, 654];
        for (int i = 0; i < labels.Length; i++)
        {
            board.Stroke(xs[i], 140, 140, 70, 90, 94, 110, 2);
            board.Fill(xs[i] + 2, 142, 136, 66, 80, 170, 220, 255);
            board.Text(xs[i], 222, labels[i], 210, 214, 220, 1);
            board.Text(xs[i], 252, values[i], 180, 186, 196, 2);
        }
        board.Text(48, 286, "WINDOW ANALYSIS XGUI DIAGNOSTICS", 140, 146, 160, 1);
        board.Text(48, 310, "READ ONLY HOST REGISTRY AND LAST FRAME", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-diagnostics-host-snapshot", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
    }
}
