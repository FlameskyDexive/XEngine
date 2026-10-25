// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using global::XEngine.Animation;
using global::XEngine.Animation.Jobs;
using XEngine.Playables;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M56 — the animation job API: AnimationJobPlayable&lt;T&gt; single-input pose post-processing, with the
/// built-in TwoBoneIK (analytic solve), LookAt (chain distribution) and Damping jobs. Verified at
/// the math level and through a full PlayableGraph pipeline, with a zero-allocation contract.
/// </summary>
public class AnimationJobTests : RuntimeTestBase
{
    /// <summary>Armature root → A(0,1,0) → B(0,2,0) → C(0,3,0): a straight +Y three-bone chain.</summary>
    private (GameObject root, SkeletonBinding binding, AnimationClipPlayable clip, int a, int b, int c)
        BuildChain()
    {
        var root = CreateGameObject("Armature");
        var a = CreateGameObject("A");
        a.SetParent(root);
        a.Transform.LocalPosition = new Float3(0f, 1f, 0f);
        var b = CreateGameObject("B");
        b.SetParent(a);
        b.Transform.LocalPosition = new Float3(0f, 1f, 0f);
        var c = CreateGameObject("C");
        c.SetParent(b);
        c.Transform.LocalPosition = new Float3(0f, 1f, 0f);

        var binding = SkeletonBinding.Build(root.Transform);

        // A rest clip that drives all three bones (needed so pose channels are populated).
        var clip = new AnimationClip { Name = "rest", Duration = 1f };
        clip.Wrap = AnimationWrapMode.ClampForever;
        foreach (string name in new[] { "A", "B", "C" })
        {
            var bone = new AnimationClip.AnimBone { BoneName = name };
            bone.PosX = AnimationTestHelpers.ConstantCurve(0f);
            bone.PosY = AnimationTestHelpers.ConstantCurve(name == "A" ? 1f : 1f);
            bone.PosZ = AnimationTestHelpers.ConstantCurve(0f);
            bone.RotX = AnimationTestHelpers.ConstantCurve(0f);
            bone.RotY = AnimationTestHelpers.ConstantCurve(0f);
            bone.RotZ = AnimationTestHelpers.ConstantCurve(0f);
            bone.RotW = AnimationTestHelpers.ConstantCurve(1f);
            clip.AddBone(bone);
        }
        var clipPlayable = new AnimationClipPlayable(clip);
        clipPlayable.Bind(binding);
        clipPlayable.Play();
        clipPlayable.Weight = 1f;

        return (root, binding, clipPlayable, 0, 1, 2);
    }

    private static AnimationJobPlayable<TJob> BuildJobGraph<TJob>(
        SkeletonBinding binding, AnimationClipPlayable clip, TJob job) where TJob : IAnimationPoseJob
    {
        var jobNode = new AnimationJobPlayable<TJob>(job);
        jobNode.Bind(binding);
        jobNode.SetInput(clip);
        jobNode.Weight = 1f;
        jobNode.Play();
        return jobNode;
    }

    private static void Evaluate(PlayableGraph graph, int frames = 1)
    {
        for (int i = 0; i < frames; i++)
            graph.Evaluate(1f / 60f);
    }

    private static Float3 BoneWorld(GameObject root, string name)
    {
        GameObject? node = root;
        foreach (string part in name.Split('/'))
        {
            node = FindChild(node!, part);
            if (node == null)
                return Float3.Zero;
        }
        return node!.Transform.Position;
    }

    private static GameObject? FindChild(GameObject parent, string name)
    {
        int count = parent.Transform.ChildCount;
        for (int i = 0; i < count; i++)
        {
            GameObject child = parent.Transform.GetChild(i).GameObject;
            if (child.Name == name)
                return child;
        }
        return null;
    }

    [Fact]
    public void TwoBoneIK_ReachableTarget_EndLandsOnTarget()
    {
        var (root, binding, clip, a, b, c) = BuildChain();
        var job = new TwoBoneIKJob
        {
            RootBone = a, MidBone = b, EndBone = c,
            TargetPosition = new Float3(1.5f, 1.0f, 0f), // distance 1.8 ≤ 2: reachable.
            Weight = 1f,
        };
        var jobNode = BuildJobGraph(binding, clip, job);
        var graph = new PlayableGraph(new GameClock());
        graph.AddOutput(new AnimationPlayableOutput { Source = jobNode, Binding = binding });
        graph.Root = jobNode;

        Evaluate(graph);

        Float3 end = BoneWorld(root, "A/B/C");
        float drift = Float3.Distance(end, new Float3(1.5f, 1.0f, 0f));
        Assert.True(drift < 0.01f, $"end drifted {drift}");
    }

    [Fact]
    public void TwoBoneIK_UnreachableTarget_StretchesTowardIt()
    {
        var (root, binding, clip, a, b, c) = BuildChain();
        var job = new TwoBoneIKJob
        {
            RootBone = a, MidBone = b, EndBone = c,
            TargetPosition = new Float3(0f, 1.0f, 10f), // Far beyond reach (max 2).
            Weight = 1f,
        };
        var jobNode = BuildJobGraph(binding, clip, job);
        var graph = new PlayableGraph(new GameClock());
        graph.AddOutput(new AnimationPlayableOutput { Source = jobNode, Binding = binding });
        graph.Root = jobNode;

        Evaluate(graph);

        Float3 end = BoneWorld(root, "A/B/C");
        // The chain straightens along the target direction: |end - root| == 2 and direction ≈ +Z.
        Float3 rootPos = BoneWorld(root, "A");
        float length = Float3.Distance(end, rootPos);
        Assert.True(MathF.Abs(length - 2f) < 0.02f, $"chain length {length}");
        Float3 dir = Float3.Normalize(end - rootPos);
        Assert.True(Float3.Dot(dir, Float3.UnitZ) > 0.99f, "chain should point at the target");
    }

    [Fact]
    public void TwoBoneIK_PartialWeight_BlendsEndPosition()
    {
        var (root, binding, clip, a, b, c) = BuildChain();
        Float3 rest = new Float3(0f, 3.0f, 0f); // A at (0,1): rest end = (0,3).
        Float3 target = new Float3(1.5f, 1.0f, 0f);
        var job = new TwoBoneIKJob
        {
            RootBone = a, MidBone = b, EndBone = c,
            TargetPosition = target,
            Weight = 0.5f,
        };
        var jobNode = BuildJobGraph(binding, clip, job);
        var graph = new PlayableGraph(new GameClock());
        graph.AddOutput(new AnimationPlayableOutput { Source = jobNode, Binding = binding });
        graph.Root = jobNode;

        Evaluate(graph);

        Float3 end = BoneWorld(root, "A/B/C");
        Float3 halfway = rest + (target - rest) * 0.5f;
        float drift = Float3.Distance(end, halfway);
        Assert.True(drift < 0.05f, $"halfway blend drifted {drift}: {end} vs {halfway}");
    }

    [Fact]
    public void TwoBoneIK_HintSelectsBendSide()
    {
        var (_, binding, clip, a, b, c) = BuildChain();

        Float3 target = new Float3(1.5f, 1.0f, 0f);
        var jobForward = new TwoBoneIKJob
        { RootBone = a, MidBone = b, EndBone = c, TargetPosition = target, Weight = 1f };
        var nodeForward = BuildJobGraph(binding, clip, jobForward);
        var g1 = new PlayableGraph(new GameClock());
        g1.AddOutput(new AnimationPlayableOutput { Source = nodeForward, Binding = binding });
        g1.Root = nodeForward;
        Evaluate(g1);
        var ctx1 = new AnimationJobContext { Binding = binding };
        ctx1.ComputeBoneWorld(nodeForward.Output!, b, out Float3 midForward, out _);
        g1.Destroy();

        var jobBack = new TwoBoneIKJob
        {
            RootBone = a, MidBone = b, EndBone = c, TargetPosition = target, Weight = 1f,
            HintPosition = new Float3(0f, 1f, -2f), HasHint = true,
        };
        var nodeBack = BuildJobGraph(binding, clip, jobBack);
        var g2 = new PlayableGraph(new GameClock());
        g2.AddOutput(new AnimationPlayableOutput { Source = nodeBack, Binding = binding });
        g2.Root = nodeBack;
        Evaluate(g2);
        var ctx2 = new AnimationJobContext { Binding = binding };
        ctx2.ComputeBoneWorld(nodeBack.Output!, b, out Float3 midBack, out _);
        g2.Destroy();

        // The two bends must land on opposite sides of the root→goal line (opposite Z sides here).
        Assert.True(MathF.Abs(midForward.Z - midBack.Z) > 0.05f,
            $"hint should flip the bend side: {midForward.Z} vs {midBack.Z}");
    }

    [Fact]
    public void LookAt_RotatesHeadTowardTarget()
    {
        var (root, binding, clip, a, b, c) = BuildChain();
        var chain = new[] { a, b, c };
        var job = new LookAtJob
        {
            ChainBones = chain,
            ChainCount = 3,
            Target = new Float3(5f, 1f, 0f), // Look +X from a chain pointing +Y: needs a −90° Z turn.
            Weight = 1f,
        };
        var jobNode = BuildJobGraph(binding, clip, job);
        var graph = new PlayableGraph(new GameClock());
        graph.AddOutput(new AnimationPlayableOutput { Source = jobNode, Binding = binding });
        graph.Root = jobNode;

        Evaluate(graph);

        var ctx = new AnimationJobContext { Binding = binding };
        ctx.ComputeBoneWorld(jobNode.Output!, c, out Float3 headPos, out Quaternion headRot);
        Float3 forward = headRot * Float3.UnitZ;
        Float3 desired = Float3.Normalize(new Float3(5f, 1f, 0f) - headPos); // From the head's own position.
        float angle = MathF.Acos(Maths.Clamp(Float3.Dot(forward, desired), -1f, 1f)) * Maths.Rad2Deg;
        Assert.True(angle < 10f, $"look-at residual angle {angle}°");
    }

    [Fact]
    public void Damping_SmoothsRotationOverTime()
    {
        var (_, binding, clip, a, _, _) = BuildChain();
        var job = new DampingJob { DampingSeconds = 0.25f };
        var jobNode = BuildJobGraph(binding, clip, job);
        var graph = new PlayableGraph(new GameClock());
        graph.AddOutput(new AnimationPlayableOutput { Source = jobNode, Binding = binding });
        graph.Root = jobNode;

        Evaluate(graph, frames: 1); // Initialize the state on the rest pose.

        // Step the input: rotate bone A by 90° about Z.
        AnimationClip.AnimBone boneA = clip.Clip.GetBone("A")!;
        Quaternion spin = Quaternion.FromEuler(0f, 0f, 90f);
        boneA.RotX = AnimationTestHelpers.ConstantCurve(spin.X);
        boneA.RotY = AnimationTestHelpers.ConstantCurve(spin.Y);
        boneA.RotZ = AnimationTestHelpers.ConstantCurve(spin.Z);
        boneA.RotW = AnimationTestHelpers.ConstantCurve(spin.W);

        Evaluate(graph, frames: 1); // One damped frame: far from the full 90°.
        Quaternion afterOne = jobNode.Output!.Bones[a].LocalRotation;
        float angleOne = QuaternionAngle(afterOne, spin);

        Evaluate(graph, frames: 120); // 2 s >> 4× time constant: converged.
        Quaternion converged = jobNode.Output!.Bones[a].LocalRotation;
        float angleEnd = QuaternionAngle(converged, spin);

        Assert.True(angleOne > 20f, $"one frame should lag (residual {angleOne}°)");
        Assert.True(angleEnd < 1f, $"should converge (residual {angleEnd}°)");
    }

    [Fact]
    public void JobGraph_SteadyState_IsAllocationFree()
    {
        var (_, binding, clip, a, b, c) = BuildChain();
        var job = new TwoBoneIKJob
        { RootBone = a, MidBone = b, EndBone = c, TargetPosition = new Float3(1f, 1.5f, 0.3f), Weight = 1f };
        var jobNode = BuildJobGraph(binding, clip, job);
        var graph = new PlayableGraph(new GameClock());
        graph.AddOutput(new AnimationPlayableOutput { Source = jobNode, Binding = binding });
        graph.Root = jobNode;

        Evaluate(graph, frames: 5); // Warm up (FK caches, job state).

        long before = GC.GetAllocatedBytesForCurrentThread();
        Evaluate(graph, frames: 120);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    private static float QuaternionAngle(Quaternion a, Quaternion b)
    {
        Quaternion diff = a * Quaternion.Inverse(b);
        return MathF.Atan2(Float3.Length(new Float3(diff.X, diff.Y, diff.Z)), MathF.Abs(diff.W)) * 2f * Maths.Rad2Deg;
    }
}

