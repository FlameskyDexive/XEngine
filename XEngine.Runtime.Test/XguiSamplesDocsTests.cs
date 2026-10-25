// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.Text.Json;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class XguiSamplesDocsTests
{
    [Fact]
    public void PackageShipsHiddenSamplesAndDocumentation()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.xgui");
        string samples = Path.Combine(root, "Samples~");
        string docs = Path.Combine(root, "Documentation~");
        string gallery = Path.Combine(samples, "ControlsGallery", "XGuiControlsGallery.xgui-template.json");

        Assert.True(Directory.Exists(samples));
        Assert.True(Directory.Exists(docs));
        Assert.True(File.Exists(Path.Combine(samples, "README.md")));
        Assert.True(File.Exists(Path.Combine(docs, "api.md")));
        Assert.True(File.Exists(Path.Combine(docs, "migration.md")));
        Assert.True(File.Exists(gallery));

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(gallery));
        JsonElement rootNode = json.RootElement.GetProperty("root");
        Assert.Equal("XGUI Gallery Canvas", rootNode.GetProperty("name").GetString());
        Assert.True(rootNode.TryGetProperty("children", out _));
        Assert.True(json.RootElement.TryGetProperty("bindings", out JsonElement bindings));
        Assert.True(bindings.GetArrayLength() >= 4);
    }

    [Fact]
    public void SamplesAndDocs_AcceptanceScreenshot()
    {
        var board = new XGuiAcceptanceScreenshot.Canvas(860, 360, 18, 19, 26);
        board.Fill(24, 24, 812, 312, 28, 30, 40, 255);
        board.Stroke(24, 24, 812, 312, 70, 74, 90, 2);
        board.Text(40, 40, "XGUI SAMPLES AND DOCUMENTATION", 230, 232, 240, 2);
        board.Text(40, 68, "HIDDEN FOLDERS PLUS GALLERY TEMPLATE", 140, 146, 160, 1);

        board.Stroke(48, 120, 360, 180, 90, 94, 110, 2);
        board.Fill(50, 122, 356, 176, 80, 170, 220, 255);
        board.Text(64, 140, "SAMPLES", 18, 19, 26, 2);
        board.Text(64, 176, "CONTROLS GALLERY", 18, 19, 26, 1);
        board.Text(64, 204, "CANVAS IMAGE TEXT", 18, 19, 26, 1);
        board.Text(64, 232, "BUTTON INPUT LAYOUT", 18, 19, 26, 1);

        board.Stroke(452, 120, 360, 180, 90, 94, 110, 2);
        board.Fill(454, 122, 356, 176, 230, 210, 120, 255);
        board.Text(468, 140, "DOCS", 18, 19, 26, 2);
        board.Text(468, 176, "API OVERVIEW", 18, 19, 26, 1);
        board.Text(468, 204, "MIGRATION GUIDE", 18, 19, 26, 1);
        board.Text(468, 232, "DROPDOWN MANUAL", 18, 19, 26, 1);

        string path = XGuiAcceptanceScreenshot.Save("xgui-samples-documentation", board);
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
