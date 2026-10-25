// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using global::XEngine.Animation;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M60 — blend-tree time synchronization: Duration sync stretches children to share a cycle length,
/// per-child speed multipliers apply, and StartTime/PhaseFoot drift correction pulls blended
/// children toward the weighted average phase (with authored per-child offsets).
/// </summary>
public class BlendTreeSyncTests : RuntimeTestBase
{
    private static BlendTree MakeTree(float walkDuration, float runDuration, BlendTimeSync sync, float phaseB = 0f)
    {
        var tree = new BlendTree { Type = BlendType.OneD, ParameterName = "Speed" };
        tree.Children.Add(new BlendTree.Child
        {
            Clip = new AssetRef<AnimationClip>(
                AnimationTestHelpers.MakeConstantPosClip("walk", "Hips", 2f, walkDuration)),
            Threshold = 0f,
            TimeSync = sync,
        });
        tree.Children.Add(new BlendTree.Child
        {
            Clip = new AssetRef<AnimationClip>(
                AnimationTestHelpers.MakeConstantPosClip("run", "Hips", 6f, runDuration)),
            Threshold = 1f,
            TimeSync = sync,
            Phase = phaseB,
        });
        return tree;
    }

    private (BlendTreePlayable tree, SkeletonBinding binding) BuildGraph(BlendTree tree, float parameter)
    {
        var root = CreateGameObject("Armature");
        Node(root, "Hips");
        var binding = SkeletonBinding.Build(root.Transform);
        var playable = BlendTreePlayable.Build(tree, binding);
        playable.ParameterValue = parameter;
        return (playable, binding);
    }

    private static void Node(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.SetParent(parent);
    }

    [Fact]
    public void DurationSync_ChildrenShareCycleLength()
    {
        // Walk 1s, run 2s: with Duration sync both take 2s per cycle (run defines the cycle).
        var (tree, _) = BuildGraph(MakeTree(1f, 2f, BlendTimeSync.Duration), parameter: 0.5f);

        var walk = (AnimationClipPlayable)tree.GetInput(0);
        var run = (AnimationClipPlayable)tree.GetInput(1);

        Assert.Equal(2f, walk.Speed, precision: 2); // 2s / 1s.
        Assert.Equal(1f, run.Speed, precision: 2);
    }

    [Fact]
    public void NoSync_ChildSpeedsUntouched()
    {
        var (tree, _) = BuildGraph(MakeTree(1f, 2f, BlendTimeSync.None), parameter: 0.5f);

        Assert.Equal(1f, ((AnimationClipPlayable)tree.GetInput(0)).Speed, precision: 2);
        Assert.Equal(1f, ((AnimationClipPlayable)tree.GetInput(1)).Speed, precision: 2);
    }

    [Fact]
    public void ChildSpeedMultiplier_AppliesAtBuild()
    {
        var tree = MakeTree(1f, 1f, BlendTimeSync.None);
        tree.Children[1].Speed = 1.5f;
        var (built, _) = BuildGraph(tree, parameter: 1f);

        Assert.Equal(1.5f, ((AnimationClipPlayable)built.GetInput(1)).Speed, precision: 2);
    }

    [Fact]
    public void PhaseSync_DriftCorrectsTowardAverage()
    {
        var (tree, _) = BuildGraph(MakeTree(1f, 1f, BlendTimeSync.PhaseFoot, phaseB: 0.5f), parameter: 0.5f);

        var a = (AnimationClipPlayable)tree.GetInput(0);
        var b = (AnimationClipPlayable)tree.GetInput(1);

        // Run the tree through a bare graph so ProcessFrame executes.
        var graph = new XEngine.Playables.PlayableGraph(new XEngine.Playables.GameClock());
        graph.AddOutput(new AnimationPlayableOutput { Source = tree, Binding = tree.Binding! });
        graph.Root = tree;

        a.Time = 0.9; // Perturb: 0.9 vs (0 + phase 0) and (0 + 0.5): the group drifts back together.
        graph.Evaluate(1f / 60f);
        graph.Evaluate(1f / 60f);
        graph.Evaluate(1f / 60f);
        graph.Evaluate(1f / 60f);

        double normA = a.NormalizedTime; // Wrapped near 0.9 + dt.
        double targetB = normA + 0.5;    // B should track A + its 0.5 phase.
        double bNorm = b.NormalizedTime;
        double delta = (bNorm - targetB) % 1.0;
        if (delta > 0.5) delta -= 1.0;
        if (delta < -0.5) delta += 1.0;
        Assert.True(System.Math.Abs(delta) < 0.15,
            $"B should track A+0.5 phase: a={normA:F3} b={bNorm:F3} delta={delta:F3}");
    }

    [Fact]
    public void StartTimeSync_ZeroPhaseChildrenAlign()
    {
        var (tree, _) = BuildGraph(MakeTree(1f, 1f, BlendTimeSync.StartTime), parameter: 0.5f);
        var a = (AnimationClipPlayable)tree.GetInput(0);
        var b = (AnimationClipPlayable)tree.GetInput(1);

        var graph = new XEngine.Playables.PlayableGraph(new XEngine.Playables.GameClock());
        graph.AddOutput(new AnimationPlayableOutput { Source = tree, Binding = tree.Binding! });
        graph.Root = tree;

        b.Time = 0.4; // Perturb far from A.
        graph.Evaluate(1f / 60f);
        graph.Evaluate(1f / 60f);
        graph.Evaluate(1f / 60f);
        graph.Evaluate(1f / 60f);

        double delta = System.Math.Abs(a.NormalizedTime - b.NormalizedTime);
        delta = System.Math.Min(delta, 1.0 - delta);
        Assert.True(delta < 0.15, $"children should re-align: delta={delta:F3}");
    }
}
