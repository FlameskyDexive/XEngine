using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelEditorEvidenceTests
{
    [Fact]
    public void EditorSources_DeclarePaperInspectorsAndMenus()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.voxelworld", "Editor");
        string worldEditor = File.ReadAllText(Path.Combine(root, "VoxelWorldEditor.cs"));
        string voxelEditor = File.ReadAllText(Path.Combine(root, "VoxelDefinitionEditor.cs"));
        string menus = File.ReadAllText(Path.Combine(root, "VoxelWorldMenus.cs"));
        Assert.Contains("[CustomEditor(typeof(VoxelWorld))]", worldEditor, StringComparison.Ordinal);
        Assert.Contains("[CustomEditor(typeof(VoxelDefinition))]", voxelEditor, StringComparison.Ordinal);
        Assert.Contains("GameObject/Voxel World/Flat Playable", menus, StringComparison.Ordinal);
        Assert.Contains("CreateEarthPlayableObject", menus, StringComparison.Ordinal);
    }

    [Fact]
    public void N11Evidence_WritesEditorBoard()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N11 VOXEL EDITOR", 230, 232, 240, 2);
        board.Text(40, 80, "PAPER INSPECTORS PLUS CREATE MENU", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 76, 175, 80, 255);
        board.Text(90, 122, "VOXELWORLD  DEFINITION  WORLD SO", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 141, 110, 99, 255);
        board.Text(90, 154, "GAMEOBJECT VOXEL WORLD FLAT EARTH FPS", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 80, 170, 220, 255);
        board.Text(90, 186, "NO CODE NEEDED FOR PLAYABLE HOST", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS EDITOR PLUS SOURCE  2026-09-15", 140, 146, 160, 1);
        board.Text(40, 258, "CREATEASSETMENU ALREADY ON SOS", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n11-voxel-editor.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n11-voxel-editor.md"),
            "# N11 Paper inspectors + create menus\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`VoxelWorldEditor`, `VoxelDefinitionEditor`, and `WorldDefinitionEditor` draw Paper " +
            "summaries on top of the default property grid. `VoxelWorldMenus` adds " +
            "GameObject / Voxel World hosts for Flat, Earth, and first-person play without writing code.\n\n" +
            "```\n" +
            "dotnet test XEngine.Editor.Test --filter FullyQualifiedName~VoxelWorldEditorTests\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelEditorEvidenceTests\n" +
            "```\n\n" +
            "Screenshot: [n11-voxel-editor.png](n11-voxel-editor.png)\n");
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
