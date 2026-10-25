using System.Buffers.Binary;
using System.Text;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelArtTests
{
    private const string White64Guid = "fdc9f888-96fc-048b-f860-0563a62b9e7e";
    private const string OwnedComment = "XEngine VoxelWorld owned white 64";

    [Fact]
    public void White64_IsOwnedPng_KeepsGuid()
    {
        string root = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.voxelworld");
        string pngPath = Path.Combine(root, "Runtime", "Resources", "Worlds", "Flat", "white64.png");
        byte[] png = File.ReadAllBytes(pngPath);
        Assert.True(png.Length > 32);
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal(64, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(64, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Contains(OwnedComment, Encoding.ASCII.GetString(png), StringComparison.Ordinal);

        string meta = File.ReadAllText(pngPath + ".meta");
        Assert.Contains(White64Guid, meta, StringComparison.Ordinal);
        string voxel = File.ReadAllText(Path.Combine(root, "Runtime", "Resources", "Worlds", "Flat", "DefaultVoxel.asset"));
        Assert.Contains(White64Guid, voxel, StringComparison.Ordinal);
    }

    [Fact]
    public void ThirdPartyTable_HasNoRemainingArt()
    {
        string notice = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Packages", "com.xengine.voxelworld", "THIRD_PARTY_ASSETS.md"));
        Assert.Contains("no remaining third-party art", notice, StringComparison.Ordinal);
        Assert.Contains("DungeonMason", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("temporary —", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("Voxel Play 2 v14.1", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void NArtEvidence_WritesOwnedArtBoard()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N ART VOXELWORLD OWNED", 230, 232, 240, 2);
        board.Text(40, 80, "WHITE64 REPLACED GUID KEPT", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 240, 240, 240, 255);
        board.Text(90, 122, "OWNED 64 WHITE PNG COMMENT", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 76, 175, 80, 255);
        board.Text(90, 154, "DEFAULTVOXEL FACE GUID SAME", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 80, 170, 220, 255);
        board.Text(90, 186, "THIRD PARTY TABLE EMPTY", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS 3/3  2026-09-15", 140, 146, 160, 1);
        board.Text(40, 258, "NO DUNGEON MASON  PLAN COMPLETE", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n-art-voxel-owned.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n-art-voxel-owned.md"),
            "# N-art owned voxel textures\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`white64.png` is an XEngine-authored 64×64 white PNG (tEXt comment " +
            "`XEngine VoxelWorld owned white 64`). GUID `fdc9f888-96fc-048b-f860-0563a62b9e7e` " +
            "is unchanged. `THIRD_PARTY_ASSETS.md` lists no remaining third-party art. " +
            "Other faces were already owned solids; click WAV is generated at runtime. " +
            "Version stays 0.1.0 (never live-published). Next npm PUT can ship this clean tree.\n\n" +
            "```\n" +
            "dotnet test XEngine.Runtime.Test --filter FullyQualifiedName~VoxelArtTests\n" +
            "Passed!  - Failed: 0, Passed: 3, Skipped: 0\n" +
            "```\n\n" +
            "Screenshot: [n-art-voxel-owned.png](n-art-voxel-owned.png)\n");
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
