// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Playables;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies <see cref="BlendTreePlayable"/> 1D blending: at a parameter value between two children's
/// thresholds, the two are linearly interpolated; below the first / above the last, the clamp child wins.
/// </summary>
public class BlendTreePlayableTests : RuntimeTestBase
{
    private static readonly PlayableContext Ctx = new(0f, 0d, false);

    private BlendTreePlayable BuildIdleWalkRun(out SkeletonBinding binding)
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        binding = SkeletonBinding.Build(root.Transform);

        // idle (pos 0) @ threshold 0, walk (pos 5) @ 0.5, run (pos 10) @ 1.
        var bt = new BlendTreePlayable { Type = BlendType.OneD };
        bt.Bind(binding);

        var idle = new AnimationClipPlayable(AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f));
        idle.Bind(binding); idle.Play(); idle.SetTime(0); idle.Weight = 1f;
        var walk = new AnimationClipPlayable(AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", 5f));
        walk.Bind(binding); walk.Play(); walk.SetTime(0); walk.Weight = 1f;
        var run = new AnimationClipPlayable(AnimationTestHelpers.MakeConstantPosClip("run", "Hips", 10f));
        run.Bind(binding); run.Play(); run.SetTime(0); run.Weight = 1f;

        bt.AddChild(idle, 0f);
        bt.AddChild(walk, 0.5f);
        bt.AddChild(run, 1f);
        return bt;
    }

    private static void Process(BlendTreePlayable bt)
    {
        // Children first (post-order), then the blend tree.
        for (int i = 0; i < bt.InputCount; i++)
            bt.GetInput(i).ProcessFrame(Ctx);
        bt.ProcessFrame(Ctx);
    }

    [Theory]
    [InlineData(0f, 0f)]     // at idle threshold → idle only
    [InlineData(0.25f, 2.5f)] // halfway idle↔walk
    [InlineData(0.5f, 5f)]   // at walk threshold → walk only
    [InlineData(0.75f, 7.5f)] // halfway walk↔run
    [InlineData(1f, 10f)]    // at run threshold → run only
    [InlineData(-0.5f, 0f)]  // below first → clamp to idle
    [InlineData(1.5f, 10f)]  // above last → clamp to run
    public void OneD_BlendAtParameter(float parameter, float expectedPosX)
    {
        var bt = BuildIdleWalkRun(out _);
        bt.ParameterValue = parameter;

        Process(bt);

        Assert.Equal(expectedPosX, bt.Output!.Bones[0].LocalPosition.X, precision: 3);
    }

    [Fact]
    public void ParameterChange_ResamplesNextFrame()
    {
        var bt = BuildIdleWalkRun(out _);

        bt.ParameterValue = 0.25f;
        Process(bt);
        Assert.Equal(2.5f, bt.Output!.Bones[0].LocalPosition.X, precision: 3);

        bt.ParameterValue = 0.75f;
        Process(bt);
        Assert.Equal(7.5f, bt.Output!.Bones[0].LocalPosition.X, precision: 3);
    }

    [Fact]
    public void TwoDCartesian_MidpointBlendsEqualWeights()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);

        var bt = new BlendTreePlayable { Type = BlendType.TwoDCartesian };
        bt.Bind(binding);
        var a = new AnimationClipPlayable(AnimationTestHelpers.MakeConstantPosClip("a", "Hips", 0f));
        a.Bind(binding); a.Play(); a.SetTime(0); a.Weight = 1f;
        var b = new AnimationClipPlayable(AnimationTestHelpers.MakeConstantPosClip("b", "Hips", 10f));
        b.Bind(binding); b.Play(); b.SetTime(0); b.Weight = 1f;
        bt.AddChild(a, 0f, new Float2(0f, 0f), -1);
        bt.AddChild(b, 0f, new Float2(1f, 0f), -1);
        bt.ParameterValue = 0.5f;
        bt.ParameterValueY = 0f;
        Process(bt);
        Assert.Equal(5f, bt.Output!.Bones[0].LocalPosition.X, precision: 2);
    }

    [Fact]
    public void Direct_UsesRawWeightsWithoutNormalize()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);
        var bt = new BlendTreePlayable { Type = BlendType.Direct };
        bt.Bind(binding);
        var a = new AnimationClipPlayable(AnimationTestHelpers.MakeConstantPosClip("a", "Hips", 10f));
        a.Bind(binding); a.Play(); a.SetTime(0); a.Weight = 1f;
        var b = new AnimationClipPlayable(AnimationTestHelpers.MakeConstantPosClip("b", "Hips", 0f));
        b.Bind(binding); b.Play(); b.SetTime(0); b.Weight = 1f;
        bt.AddChild(a, 0f, Float2.Zero, -1);
        bt.AddChild(b, 0f, Float2.Zero, -1);
        bt.SetDirectWeight(0, 0.5f);
        bt.SetDirectWeight(1, 0f);
        Process(bt);
        Assert.Equal(5f, bt.Output!.Bones[0].LocalPosition.X, precision: 2);
    }

    [Fact]
    public void Build_AutomaticThresholdsAndNestedTree()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);

        var nested = new BlendTree { Name = "inner", Type = BlendType.OneD };
        nested.Children.Add(new BlendTree.Child
        {
            Clip = new AssetRef<AnimationClip>(AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f)),
            Threshold = 0f,
        });
        nested.Children.Add(new BlendTree.Child
        {
            Clip = new AssetRef<AnimationClip>(AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", 10f)),
            Threshold = 1f,
        });

        var outer = new BlendTree { Name = "outer", Type = BlendType.OneD, UseAutomaticThresholds = true };
        outer.Children.Add(new BlendTree.Child { Tree = new AssetRef<BlendTree>(nested), Threshold = 99f });
        outer.Children.Add(new BlendTree.Child
        {
            Clip = new AssetRef<AnimationClip>(AnimationTestHelpers.MakeConstantPosClip("run", "Hips", 20f)),
            Threshold = 99f,
        });

        var btp = BlendTreePlayable.Build(outer, binding);
        Assert.Equal(2, btp.InputCount);
        Assert.IsType<BlendTreePlayable>(btp.GetInput(0));
        ((BlendTreePlayable)btp.GetInput(0)).ParameterValue = 0.5f;
        btp.ParameterValue = 0f;
        ProcessGraph(btp);
        Assert.Equal(5f, btp.Output!.Bones[0].LocalPosition.X, precision: 2);
    }

    static void ProcessGraph(Playable node)
    {
        for (int i = 0; i < node.InputCount; i++)
            ProcessGraph(node.GetInput(i));
        node.ProcessFrame(Ctx);
    }
}
