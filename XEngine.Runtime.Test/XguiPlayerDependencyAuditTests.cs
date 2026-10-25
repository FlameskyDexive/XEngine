// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime.UI;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XguiPlayerDependencyAuditScreenshotTests
{
    [Fact]
    public void PlayerAssemblies_MustIncludeXguiRuntimeAndExcludeEditor()
    {
        string[] valid =
        [
            "XEngine.Runtime",
            PlayerXguiAssemblyPolicy.RuntimeAssemblyName,
        ];
        Assert.True(PlayerXguiAssemblyPolicy.TryValidatePlayerAssemblies(valid, out string error), error);

        Assert.False(PlayerXguiAssemblyPolicy.TryValidatePlayerAssemblies(
            ["XEngine.Runtime"], out error));
        Assert.Contains(PlayerXguiAssemblyPolicy.RuntimeAssemblyName, error, StringComparison.Ordinal);

        Assert.False(PlayerXguiAssemblyPolicy.TryValidatePlayerAssemblies(
            [PlayerXguiAssemblyPolicy.RuntimeAssemblyName, PlayerXguiAssemblyPolicy.EditorPackageAssemblyName],
            out error));
        Assert.Contains(PlayerXguiAssemblyPolicy.EditorPackageAssemblyName, error, StringComparison.Ordinal);

        Assert.False(PlayerXguiAssemblyPolicy.TryValidatePlayerAssemblies(
            [PlayerXguiAssemblyPolicy.RuntimeAssemblyName, PlayerXguiAssemblyPolicy.EngineEditorAssemblyName],
            out error));
        Assert.Contains(PlayerXguiAssemblyPolicy.EngineEditorAssemblyName, error, StringComparison.Ordinal);
        Assert.True(PlayerXguiAssemblyPolicy.IsForbiddenEditorAssembly("XEngine.Editor"));
    }

    [Fact]
    public void PlayerDependencyAudit_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI PLAYER BUILD AUDIT", 230, 232, 240, 2);
        board.Text(40, 68, "RUNTIME IN / EDITOR OUT", 140, 146, 160, 1);

        string[] labels = ["WINDOWS", "ANDROID", "HARMONYOS", "AUDIT"];
        string[] values = ["RUNTIME", "RUNTIME", "RUNTIME", "NO EDITOR"];
        int[] xs = [48, 250, 452, 654];
        for (int i = 0; i < labels.Length; i++)
        {
            board.Stroke(xs[i], 140, 140, 70, 90, 94, 110, 2);
            board.Fill(xs[i] + 2, 142, 136, 66, 80, 170, 220, 255);
            board.Text(xs[i], 222, labels[i], 210, 214, 220, 1);
            board.Text(xs[i], 252, values[i], 180, 186, 196, 2);
        }
        board.Text(48, 286, "XENGINE.XGUI.RUNTIME SHIPS IN PLAYERS", 140, 146, 160, 1);
        board.Text(48, 310, "XENGINE.XGUI.EDITOR STAYS EDITOR ONLY", 140, 146, 160, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-player-build-audit", board);
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 200);
        string legacy = XGuiAcceptanceScreenshot.Save("xgui-player-dependency-audit", board);
        Assert.True(File.Exists(legacy));
    }
}
