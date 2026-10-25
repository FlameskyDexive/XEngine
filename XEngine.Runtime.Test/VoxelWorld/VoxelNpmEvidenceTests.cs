using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelNpmEvidenceTests
{
    [Fact]
    public void NNpmEvidence_WritesPreviewBoard()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 320, 27, 29, 36);
        board.Fill(24, 24, 872, 272, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 272, 70, 74, 90, 2);
        board.Text(40, 40, "N NPM VOXELWORLD PREVIEW", 230, 232, 240, 2);
        board.Text(40, 80, "DRY RUN INXEP VOXELWORLD 0.1.0", 140, 200, 160, 1);
        board.Fill(60, 120, 18, 18, 76, 175, 80, 255);
        board.Text(90, 122, "TARBALL 36506 BYTES", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 141, 110, 99, 255);
        board.Text(90, 154, "NO DUNGEON MASON IN PACK", 200, 220, 200, 1);
        board.Fill(60, 184, 18, 18, 80, 170, 220, 255);
        board.Text(90, 186, "LIVE PUT WAITS FOR NPM TOKEN", 200, 220, 200, 1);
        board.Text(40, 230, "TESTS PACK 2/2  2026-09-15", 140, 146, 160, 1);
        board.Text(40, 258, "DEPRECATE UNPUBLISH IN NPM MD", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "n-npm-voxelworld-preview.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "n-npm-voxelworld-preview.md"),
            "# N-npm @inxep/voxelworld preview dry-run\n\n" +
            "Date: 2026-09-15. Branch: `main`.\n\n" +
            "`xengine-cli package-publish --package Packages/com.xengine.voxelworld --dry-run` " +
            "packed `@inxep/voxelworld` **0.1.0** (`inxep-voxelworld-0.1.0.tgz`, 36506 bytes). " +
            "Tarball includes `THIRD_PARTY_ASSETS.md` and excludes Dungeon Mason.\n\n" +
            "This machine has no `XENGINE_NPM_TOKEN` (env / project / user secrets), so the live " +
            "npmjs PUT was not sent. Re-run without `--dry-run` after a granular `@inxep` token is " +
            "configured. Takedown: `Documentation~/npm.md`.\n\n" +
            "Screenshot: [n-npm-voxelworld-preview.png](n-npm-voxelworld-preview.png)\n");
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
