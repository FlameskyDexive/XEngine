using System;
using System.IO;
using System.Text;

using XEngine.Spine;

using Xunit;

namespace XEngine.Runtime.Test.Spine;

public sealed class SpineAssetPathsTests
{
    [Fact]
    public void LooksLikeSpineJson_RequiresSkeletonBonesAndSlots()
    {
        Assert.True(SpineAssetPaths.LooksLikeSpineJson("""
            {
              "skeleton": { "spine": "4.2.37" },
              "bones": [ { "name": "root" } ],
              "slots": [ { "name": "body", "bone": "root" } ]
            }
            """));
        Assert.False(SpineAssetPaths.LooksLikeSpineJson("""{ "name": "notes", "items": [] }"""));
        Assert.False(SpineAssetPaths.LooksLikeSpineJson("not json"));
    }

    [Fact]
    public void LooksLikeSpineJson_AcceptsSlotsPastThe8KbBoneBlock()
    {
        // Official exports with many bones push "slots" past 8 KB (raptor ~9.6 KB,
        // mix-and-match ~22 KB). The sniff window must still recognize them.
        var bones = new StringBuilder();
        for (int i = 0; i < 400; i++)
        {
            if (i > 0) bones.Append(',');
            bones.Append("""{ "name": "bone0000000000000000000000000000", "parent": "root", "x": 12.34, "y": 56.78, "rotation": 90.5, "length": 100.0 }""");
        }
        string json = "{\n\"skeleton\": { \"spine\": \"4.2.37\" },\n\"bones\": [" + bones + "],\n\"slots\": [ { \"name\": \"body\", \"bone\": \"root\" } ]\n}";
        Assert.True(bones.Length > 8192);
        Assert.True(SpineAssetPaths.LooksLikeSpineJson(json));

        string tempPath = Path.Combine(Path.GetTempPath(), "spine-sniff-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(tempPath, json);
            Assert.True(SpineAssetPaths.FileLooksLikeSpineJson(tempPath));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void FileNameHelpers_RecognizeOfficialSuffixes()
    {
        Assert.True(SpineAssetPaths.IsAtlasTxtFileName(@"C:\art\hero.atlas.txt"));
        Assert.False(SpineAssetPaths.IsAtlasTxtFileName(@"C:\art\readme.txt"));
        Assert.True(SpineAssetPaths.IsSkelBytesFileName("hero.skel.bytes"));
        Assert.False(SpineAssetPaths.IsSkelBytesFileName("blob.bytes"));
        Assert.Equal("hero", SpineAssetPaths.GetDisplayName("hero.atlas.txt"));
        Assert.Equal("hero", SpineAssetPaths.GetSkeletonStem("hero.skel.bytes"));
    }
}
