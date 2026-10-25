// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using System.Text.Json;

using Xunit;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M26: on-disk AnimationDemo scene + unique sample GUIDs. Full Scene deserialize of
/// PlayableDirector lives in Tools/XEngine.Timeline.SampleGenerator (timeline assembly);
/// Runtime.Test asserts Echo text + .meta so the sample stays loadable without that package.
/// </summary>
public class AnimationDemoSceneTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Samples", "AnimationDemo")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Samples/AnimationDemo).");
    }

    private static string Demo(string fileName) => Path.Combine(RepoRoot(), "Samples", "AnimationDemo", fileName);

    private static string MetaGuid(string assetFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(assetFile + ".meta"));
        return doc.RootElement.GetProperty("guid").GetString()
            ?? throw new InvalidDataException(assetFile + ".meta missing guid");
    }

    [Fact]
    public void CutsceneGuid_DoesNotCollideWithController()
    {
        string controller = MetaGuid(Demo("BananaManLocomotion.controller"));
        string cutscene = MetaGuid(Demo("Cutscene.timeline"));
        Assert.Equal("55555555000000000000000000000005", controller);
        Assert.Equal("66666666000000000000000000000006", cutscene);
        Assert.NotEqual(controller, cutscene);
    }

    [Fact]
    public void Scene_EchoListsCameraLightCharacterDirectorAndBones()
    {
        string path = Demo("AnimationDemo.scene");
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".meta"));
        Assert.Equal("77777777000000000000000000000007", MetaGuid(path));

        string text = File.ReadAllText(path);
        Assert.Contains("Main Camera", text);
        Assert.Contains("Directional Light", text);
        Assert.Contains("BananaMan", text);
        Assert.Contains("Hips", text);
        Assert.Contains("Spine", text);
        Assert.Contains("CutsceneDirector", text);
        Assert.Contains("PlayableDirector", text);
        Assert.Contains("55555555-0000-0000-0000-000000000005", text);
        Assert.Contains("66666666-0000-0000-0000-000000000006", text);
        Assert.Contains("Animator", text);
    }
}
