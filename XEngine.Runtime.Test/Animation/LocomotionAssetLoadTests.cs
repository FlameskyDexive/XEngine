// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using Xunit;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies the M16 on-disk sample assets round-trip: the generated BananaManLocomotion.controller
/// and its clips load from Echo text and the controller compiles to a well-formed runtime constant.
/// This proves the asset pipeline (author asset → load → compile) works for a real locomotion
/// controller, not just the in-memory construction in <see cref="LocomotionControllerSampleTests"/>.
/// </summary>
public class LocomotionAssetLoadTests
{
    private static string RepoRoot()
    {
        // Walk up from the test bin dir to the repository root (the one holding Samples/).
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Samples", "AnimationDemo")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Samples/AnimationDemo).");
    }

    private static AnimatorController LoadController()
    {
        string path = Path.Combine(RepoRoot(), "Samples", "AnimationDemo", "BananaManLocomotion.controller");
        string text = File.ReadAllText(path);
        EchoObject echo = EchoObject.ReadFromString(text) ?? throw new InvalidDataException("controller parsed null");
        return Serializer.Deserialize<AnimatorController>(echo)
            ?? throw new InvalidDataException("controller deserialized null");
    }

    [Fact]
    public void Controller_LoadsFromDisk_WithExpectedStructure()
    {
        var author = LoadController();

        Assert.Equal("BananaManLocomotion", author.Name);
        Assert.Equal("Locomotion", author.DefaultStateName);
        Assert.Equal(2, author.States.Count);
        Assert.Equal(2, author.Parameters.Count);
        Assert.Contains(author.Parameters, p => p.Name == "Speed");
        Assert.Contains(author.Parameters, p => p.Name == "Jump");
    }

    [Fact]
    public void Controller_CompilesToValidConstant()
    {
        var constant = AnimatorControllerCompiler.Compile(LoadController());

        Assert.Equal(2, constant.States.Length);
        Assert.Equal(1, constant.MotionKind[0]); // Locomotion = BlendTree.
        Assert.Equal(0, constant.MotionKind[1]); // Jump = clip.
        Assert.True(constant.MotionBlendTrees[0] != null);
        Assert.Equal(3, constant.MotionBlendTrees[0]!.Children.Count); // Idle/Walk/Run.
        // Locomotion → Jump (trigger) + Jump → Locomotion (ExitTime).
        Assert.Equal(1, constant.States[0].TransitionsCount);
        Assert.Equal(1, constant.States[1].TransitionsCount);
        Assert.Single(constant.Layers);
        Assert.Equal(0, constant.Layers[0].StatesStart);
        Assert.Equal(2, constant.Layers[0].StatesCount);
        Assert.Equal(AnimationNameHash.Hash("Base Layer"), constant.Layers[0].NameHash);
    }

    [Theory]
    [InlineData("Idle.anim", "Idle")]
    [InlineData("Walk.anim", "Walk")]
    [InlineData("Run.anim",  "Run")]
    [InlineData("Jump.anim", "Jump")]
    public void ClipAssets_LoadWithNameAndBones(string fileName, string expectedName)
    {
        string path = Path.Combine(RepoRoot(), "Samples", "AnimationDemo", fileName);
        var clip = Serializer.Deserialize<AnimationClip>(EchoObject.ReadFromString(File.ReadAllText(path)));

        Assert.NotNull(clip);
        Assert.Equal(expectedName, clip!.Name);
        Assert.True(clip.Duration > 0f);
        Assert.True(clip.Bones.Count >= 2); // Hips + Spine.

        // The clip's GUID lives in its sibling .meta (the asset DB indexes by that), not the asset body.
        Assert.True(File.Exists(path + ".meta"));
        string meta = File.ReadAllText(path + ".meta");
        Assert.Contains("\"importer\"", meta);
        Assert.Contains("\"guid\"", meta);
    }
}
