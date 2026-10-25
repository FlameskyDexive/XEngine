using Xunit;

namespace XEngine.Spine38.Tests;

public sealed class Spine38ImporterSniffTests
{
    private const string Spine38Json = """
        {
          "skeleton": { "hash": "s38", "spine": "3.8.55", "width": 8, "height": 8 },
          "bones": [ { "name": "root" } ],
          "slots": [ { "name": "body", "bone": "root" } ]
        }
        """;

    private const string Spine4xJson = """
        {
          "skeleton": { "hash": "s43", "spine": "4.3.75-beta", "width": 8, "height": 8 },
          "bones": [ { "name": "root" } ],
          "slots": [ { "name": "body", "bone": "root" } ]
        }
        """;

    [Fact]
    public void FileLooksLikeSpine38Json_Claims38AndRejects4x()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spine38-sniff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string json38 = Path.Combine(dir, "hero.json");
            string json4x = Path.Combine(dir, "hero4.json");
            string notes = Path.Combine(dir, "notes.json");
            File.WriteAllText(json38, Spine38Json);
            File.WriteAllText(json4x, Spine4xJson);
            File.WriteAllText(notes, """{ "name": "notes" }""");

            Assert.True(SpineAssetPaths.LooksLikeSpineJson(Spine38Json));
            Assert.True(SpineAssetPaths.LooksLikeSpine38Version(Spine38Json));
            Assert.True(SpineAssetPaths.FileLooksLikeSpine38Json(json38));
            Assert.True(SpineAssetPaths.FileLooksLikeSpineJson(json4x));
            Assert.False(SpineAssetPaths.FileLooksLikeSpine38Json(json4x));
            Assert.False(SpineAssetPaths.FileLooksLikeSpine38Json(notes));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void FileLooksLikeSpine38Json_RejectsRepoSpine4xSpineboy()
    {
        string repo = Spine38TestPaths.RepositoryRoot();
        string spine4x = Path.Combine(repo, "Packages", "com.xengine.spine", "Samples~",
            "GettingStarted", "Skeletons", "Spineboy", "spineboy-ess.json");
        Assert.True(File.Exists(spine4x), spine4x);
        Assert.True(SpineAssetPaths.FileLooksLikeSpineJson(spine4x));
        Assert.False(SpineAssetPaths.FileLooksLikeSpine38Json(spine4x));

        string spine38 = Path.Combine(repo, "Packages", "com.xengine.spine38", "Samples~",
            "GettingStarted", "Skeletons", "Spineboy", "spineboy-ess.json");
        Assert.True(SpineAssetPaths.FileLooksLikeSpine38Json(spine38));
    }

}
