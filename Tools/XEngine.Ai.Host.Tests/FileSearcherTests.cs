// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Proves the find_in_file regex content search: single-file, glob, truncation.</summary>
public class FileSearcherTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public FileSearcherTests(TestSceneFixture fx) => _fx = fx;

    [Fact]
    public void Search_SingleFile_FindsMatches()
    {
        // Write a test .cs file under Assets/Scripts/.
        string dir = Path.Combine(_fx.ProjectRoot, "Assets", "Scripts");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "Player.cs");
        File.WriteAllText(file, "class Player { /* player controller */ }\n// TODO: player\n");

        var result = FileSearcher.Search(_fx.ProjectRoot + "/Assets", "player", path: "Scripts/Player.cs", limit: 10);

        Assert.Equal(1, result.FilesScanned);
        // Two matches: line 1 ("player controller" via the comment, but regex "player" is case-sensitive
        // → only matches lowercase "player"; line 1 has "player controller", line 2 has "player").
        Assert.True(result.Matches.Count >= 1);
        Assert.All(result.Matches, m => Assert.Contains("player", m.Excerpt));
    }

    [Fact]
    public void Search_GlobExtension_ScansAllMatchingFiles()
    {
        string dir = Path.Combine(_fx.ProjectRoot, "Assets", "Glob");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.cs"), "class A { }\n");
        File.WriteAllText(Path.Combine(dir, "b.cs"), "class B { }\n");
        File.WriteAllText(Path.Combine(dir, "c.txt"), "class C { }\n"); // not .cs

        var result = FileSearcher.Search(_fx.ProjectRoot + "/Assets", "class", glob: "Glob/*.cs", limit: 10);

        Assert.Equal(2, result.FilesScanned); // a.cs + b.cs, not c.txt
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public void Search_TruncatesAtLimit_SetsHasMore()
    {
        string dir = Path.Combine(_fx.ProjectRoot, "Assets", "Many");
        Directory.CreateDirectory(dir);
        // Write one file with 10 matching lines.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 10; i++) sb.AppendLine("needle line " + i);
        File.WriteAllText(Path.Combine(dir, "haystack.txt"), sb.ToString());

        var result = FileSearcher.Search(_fx.ProjectRoot + "/Assets", "needle", glob: "Many/haystack.txt", limit: 3);

        Assert.Equal(3, result.Matches.Count);
        Assert.True(result.HasMore);
    }

    [Fact]
    public void Search_NonExistentFile_ReturnsEmpty()
    {
        var result = FileSearcher.Search(_fx.ProjectRoot + "/Assets", "x", path: "nope.cs", limit: 10);
        Assert.Equal(0, result.FilesScanned);
        Assert.Empty(result.Matches);
    }
}
