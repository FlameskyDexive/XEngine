using XEngine.Animation;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test.Animation;

public sealed class ModelRootMotionTests : RuntimeTestBase
{
    [Fact]
    public void ExplicitModelRootExtractsIncrementWithoutOverwritingScenePlacement()
    {
        var (animator, child) = Rig();
        animator.Transform.LocalPosition = new Float3(10, 20, 30);
        animator.Transform.LocalRotation = Quaternion.FromEuler(0, 90, 0);
        animator.Transform.LocalScale = new Float3(2, 3, 4);
        Float3 expected = animator.Transform.Position + animator.Transform.TransformVector(new Float3(1, 0, 0));
        using var clip = RootClip();
        animator.Play(clip);
        Tick(animator, .5f);
        Near(expected, animator.Transform.Position);
        Near(new Float3(2, 3, 4), animator.Transform.LocalScale);
        SameRotation(Quaternion.FromEuler(0, 90, 0), animator.Transform.LocalRotation);
        Near(new Float3(2, 0, 0), child.LocalPosition);
        Assert.Equal(-1, animator.Skeleton!.IndexOfBonePath(string.Empty));
        Assert.Equal(-1, animator.Skeleton.IndexOfBonePath("."));
        Assert.DoesNotContain(animator.Transform, animator.Skeleton.Bones);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InPlaceAndPreviewDoNotWriteAnyRootTRS(bool preview)
    {
        var (animator, child) = Rig();
        animator.ApplyRootMotion = preview;
        animator.ApplyRootMotionRotation = true;
        animator.Transform.LocalPosition = new Float3(10, 20, 30);
        animator.Transform.LocalRotation = Quaternion.FromEuler(25, 40, 15);
        animator.Transform.LocalScale = new Float3(2, 3, 4);
        using var clip = RootClip();
        SetRotation(clip.Bones[0], Quaternion.Identity, Quaternion.FromEuler(0, 90, 0));
        SetScale(clip.Bones[0], new Float3(8, 9, 10));
        animator.Play(clip);
        if (preview) animator.EvaluateGraph(.5f); else Tick(animator, .5f);
        Near(new Float3(10, 20, 30), animator.Transform.LocalPosition);
        SameRotation(Quaternion.FromEuler(25, 40, 15), animator.Transform.LocalRotation);
        Near(new Float3(2, 3, 4), animator.Transform.LocalScale);
        Near(new Float3(2, 0, 0), child.LocalPosition);
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(77)]
    public void OnlyExplicitModelRootConsumesTheEmptyPath(int policy)
    {
        var (animator, _) = Rig();
        using var clip = RootClip(); clip.RootMotionPolicy = (ClipRootMotionPolicy)policy;
        // All other paths are unbound, so this asserts the root track itself stays inactive.
        clip.Bones.RemoveAt(1);
        animator.Play(clip); Tick(animator, .5f);
        Near(Float3.Zero, animator.Transform.Position);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("partial-position")]
    [InlineData("partial-rotation")] [InlineData("no-keys")] [InlineData("scale-only")]
    [InlineData("zero-scale")] [InlineData("nan-scale")] [InlineData("infinite-rotation")]
    [InlineData("zero-rotation")] [InlineData("dot-track")]
    public void UnusableModelRootNeverSelectsAChild(string defect)
    {
        var (animator, child) = Rig();
        using var clip = RootClip(); var root = clip.Bones[0];
        switch (defect)
        {
            case "missing": clip.Bones.RemoveAt(0); break;
            case "duplicate": clip.Bones.Add(root); break;
            case "partial-position": root.PosZ = null!; break;
            case "partial-rotation": root.RotX = AnimationTestHelpers.ConstantCurve(0); break;
            case "no-keys": root.PosX = new XEngine.Runtime.AnimationCurve(); root.PosX.Keys.Clear(); break;
            case "scale-only": root.PosX = root.PosY = root.PosZ = null!; SetScale(root, Float3.One); break;
            case "zero-scale": root.HasSourceReferencePose = true; root.SourceScale = new Float3(0, 1, 1); break;
            case "nan-scale": root.HasSourceReferencePose = true; root.SourceScale = new Float3(1, float.NaN, 1); break;
            case "infinite-rotation": root.HasSourceReferencePose = true; root.SourceRotation = new Quaternion(float.PositiveInfinity, 0, 0, 1); break;
            case "zero-rotation": root.HasSourceReferencePose = true; root.SourceRotation = default; break;
            case "dot-track": root.BoneName = "."; break;
        }
        animator.Play(clip); Tick(animator, .5f);
        Near(Float3.Zero, animator.Transform.Position);
        Near(new Float3(2, 0, 0), child.LocalPosition);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SourceReferenceAndCurrentActorFramesAreAppliedExactlyOnce(bool planar)
    {
        var (animator, _) = Rig();
        GameObject parent = CreateGameObject("Parent");
        parent.Transform.LocalPosition = new Float3(2, 3, 5);
        parent.Transform.LocalRotation = Quaternion.FromEuler(20, 35, 10);
        parent.Transform.LocalScale = new Float3(1.5f, 2, 3);
        animator.GameObject.SetParent(parent, worldPositionStays: false);
        animator.Transform.LocalPosition = new Float3(10, 20, 30);
        animator.Transform.LocalRotation = Quaternion.FromEuler(15, 80, 25);
        animator.Transform.LocalScale = new Float3(2, 3, 4);
        animator.RootMotionPlanar = planar;
        using var clip = RootClip(); var root = clip.Bones[0];
        root.HasSourceReferencePose = true;
        root.SourceRotation = Quaternion.FromEuler(15, 65, 20);
        root.SourceScale = new Float3(2, 3, 4);
        root.SourcePosition = new Float3(70, 80, 90);
        root.PosX = AnimationTestHelpers.LinearCurve(70, 72, 1);
        root.PosY = AnimationTestHelpers.LinearCurve(80, 84, 1);
        root.PosZ = AnimationTestHelpers.LinearCurve(90, 96, 1);
        Float3 delta = (Quaternion.Inverse(root.SourceRotation) * new Float3(1, 2, 3)) / root.SourceScale;
        if (planar) delta.Y = 0;
        Float3 expected = animator.Transform.Position + animator.Transform.TransformVector(delta);
        animator.Play(clip); Tick(animator, .5f);
        Near(expected, animator.Transform.Position);
    }

    [Fact]
    public void SourceRootRotationIsConvertedToCurrentActorAxes()
    {
        var (animator, _) = Rig(); animator.ApplyRootMotionRotation = true;
        Quaternion reference = Quaternion.FromEuler(35, 80, 20);
        Quaternion placement = Quaternion.FromEuler(60, 25, 45);
        Quaternion turn = Quaternion.FromEuler(10, 40, 15);
        animator.Transform.LocalRotation = placement;
        using var clip = RootClip(); var root = clip.Bones[0];
        root.HasSourceReferencePose = true; root.SourceRotation = reference;
        SetRotation(root, reference, reference * turn);
        animator.Play(clip); Tick(animator, 1);
        SameRotation(placement * turn, animator.Transform.Rotation);
    }

    [Fact]
    public void SeparateSkeletonRootUsesItsCurrentFrameWithoutDirectTRSWrites()
    {
        var (animator, _) = Rig();
        GameObject skeleton = CreateGameObject("Model"); skeleton.SetParent(animator.GameObject, worldPositionStays: false);
        skeleton.Transform.LocalPosition = new Float3(4, 5, 6);
        skeleton.Transform.LocalRotation = Quaternion.FromEuler(0, 90, 0);
        skeleton.Transform.LocalScale = new Float3(2, 3, 4);
        animator.SkeletonRoot = skeleton.Transform; animator.RebuildGraph();
        using var clip = RootClip();
        Float3 expected = skeleton.Transform.TransformVector(new Float3(1, 0, 0));
        animator.Play(clip); Tick(animator, .5f);
        Near(expected, animator.Transform.Position);
        Near(new Float3(4, 5, 6), skeleton.Transform.LocalPosition);
        Near(new Float3(2, 3, 4), skeleton.Transform.LocalScale);
        SameRotation(Quaternion.FromEuler(0, 90, 0), skeleton.Transform.LocalRotation);
    }

    [Theory]
    [InlineData(15)] [InlineData(30)] [InlineData(60)] [InlineData(120)]
    public void LoopingAndReversePlaybackUseIncrementsWithoutStartOffsetCatchUp(int fps)
    {
        var (animator, _) = Rig();
        using var clip = RootClip();
        clip.Bones[0].PosX = AnimationTestHelpers.LinearCurve(100, 102, 1);
        var state = animator.Play(clip); state.Wrap = AnimationWrapMode.Loop;
        for (int i = 0; i < fps * 3; i++) Tick(animator, 1f / fps);
        Near(new Float3(6, 0, 0), animator.Transform.Position);
        state.Speed = -1;
        for (int i = 0; i < fps; i++) Tick(animator, 1f / fps);
        Near(new Float3(4, 0, 0), animator.Transform.Position);
        state.SetTime(50.25); Tick(animator, 0);
        Near(new Float3(4, 0, 0), animator.Transform.Position);
        Tick(animator, .25f);
        Near(new Float3(3.5f, 0, 0), animator.Transform.Position);
    }

    [Fact]
    public void TurningMultiLoopCatchUpMatchesPerLoopComposition()
    {
        var (a, _) = Rig(); var (b, _) = Rig();
        a.ApplyRootMotionRotation = b.ApplyRootMotionRotation = true;
        using var clip = RootClip();
        SetRotation(clip.Bones[0], Quaternion.Identity, Quaternion.FromEuler(15, 70, 25));
        a.Play(clip).Wrap = b.Play(clip).Wrap = AnimationWrapMode.Loop;
        Tick(a, 3); Tick(b, 1); Tick(b, 1); Tick(b, 1);
        Near(a.Transform.Position, b.Transform.Position);
        SameRotation(a.Transform.Rotation, b.Transform.Rotation);
    }

    [Fact]
    public void MixedRootTrajectoryDoesNotPinDisabledChildAnimation()
    {
        var (animator, child) = Rig();
        using var root = RootClip(); using var disabled = RootClip();
        disabled.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        var a = animator.Play(root); var b = animator.Play(disabled);
        a.Weight = b.Weight = .5f;
        Tick(animator, .5f);
        Near(new Float3(.5f, 0, 0), animator.Transform.Position);
        Near(new Float3(2, 0, 0), child.LocalPosition);
    }

    [Fact]
    public void NestedBlendTreeWeightsModelRootIncrementsIndependently()
    {
        var (animator, child) = Rig();
        using var root = RootClip(); using var disabled = RootClip();
        disabled.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        var tree = new BlendTreePlayable { ParameterValue = .75f }; tree.Bind(animator.Skeleton!);
        var a = new AnimationClipPlayable(root); a.Bind(animator.Skeleton!); a.Play();
        var b = new AnimationClipPlayable(disabled); b.Bind(animator.Skeleton!); b.Play();
        tree.AddChild(a, 0); tree.AddChild(b, 1); animator.PlayPlayable(tree);
        Tick(animator, .5f);
        Near(new Float3(.25f, 0, 0), animator.Transform.Position);
        Near(new Float3(2, 0, 0), child.LocalPosition);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RotationOnlyRootRespectsRotationToggleAndPause(bool rotate)
    {
        var (animator, child) = Rig(); animator.ApplyRootMotionRotation = rotate;
        using var clip = RootClip(); var root = clip.Bones[0];
        root.PosX = root.PosY = root.PosZ = null!;
        SetRotation(root, Quaternion.Identity, Quaternion.FromEuler(0, 90, 0));
        var state = animator.Play(clip); Tick(animator, 1);
        SameRotation(rotate ? Quaternion.FromEuler(0, 90, 0) : Quaternion.Identity, animator.Transform.Rotation);
        Near(Float3.Zero, animator.Transform.Position);
        Near(new Float3(4, 0, 0), child.LocalPosition);
        state.Speed = 0; Tick(animator, 1);
        SameRotation(rotate ? Quaternion.FromEuler(0, 90, 0) : Quaternion.Identity, animator.Transform.Rotation);
    }

    [Fact]
    public void CallbackReceivesBodySpaceIncrementAndPreventsAutomaticMovement()
    {
        var (animator, _) = Rig();
        animator.Transform.LocalPosition = new Float3(10, 20, 30);
        animator.Transform.LocalRotation = Quaternion.FromEuler(0, 90, 0);
        animator.Transform.LocalScale = new Float3(2, 3, 4);
        Float3 reported = default; int calls = 0;
        animator.OnAnimatorMove = (position, rotation) => { reported = position; calls++; };
        using var clip = RootClip(); animator.Play(clip); Tick(animator, .5f);
        Assert.Equal(1, calls); Near(new Float3(1, 0, 0), reported);
        Near(new Float3(10, 20, 30), animator.Transform.LocalPosition);
    }

    [Fact]
    public void RebindingModelThenChildThenDisabledClearsTrajectoryState()
    {
        var (animator, child) = Rig();
        using var clip = RootClip(); var state = animator.Play(clip);
        Tick(animator, .25f); Near(new Float3(.5f, 0, 0), animator.Transform.Position);
        clip.RootBonePath = "Carrier"; state.Bind(animator.Skeleton!);
        Tick(animator, .25f); Near(new Float3(1.5f, 0, 0), animator.Transform.Position);
        Near(Float3.Zero, child.LocalPosition);
        clip.RootMotionPolicy = ClipRootMotionPolicy.Disabled; state.Bind(animator.Skeleton!);
        Tick(animator, .25f); Near(new Float3(1.5f, 0, 0), animator.Transform.Position);
        Near(new Float3(3, 0, 0), child.LocalPosition);
        clip.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone; clip.RootBonePath = ".";
        state.Bind(animator.Skeleton!); Tick(animator, .25f);
        Near(new Float3(2, 0, 0), animator.Transform.Position);
        Near(new Float3(4, 0, 0), child.LocalPosition);
    }

    [Fact]
    public void StableModelRootPlaybackAllocatesZeroBytes()
    {
        var (animator, _) = Rig(); using var clip = RootClip();
        animator.Play(clip).Wrap = AnimationWrapMode.Loop;
        Time.TimeStack.Push(new TimeData { DeltaTime = 1f / 60 });
        try
        {
            for (int i = 0; i < 2048; i++) animator.LateUpdate();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) animator.LateUpdate();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        finally { Time.TimeStack.Pop(); }
    }

    private static void SetRotation(AnimationClip.AnimBone root, Quaternion from, Quaternion to)
    {
        root.RotX = AnimationTestHelpers.LinearCurve(from.X, to.X, 1);
        root.RotY = AnimationTestHelpers.LinearCurve(from.Y, to.Y, 1);
        root.RotZ = AnimationTestHelpers.LinearCurve(from.Z, to.Z, 1);
        root.RotW = AnimationTestHelpers.LinearCurve(from.W, to.W, 1);
    }
    private static void SetScale(AnimationClip.AnimBone root, Float3 scale)
    {
        root.ScaleX = AnimationTestHelpers.ConstantCurve(scale.X);
        root.ScaleY = AnimationTestHelpers.ConstantCurve(scale.Y);
        root.ScaleZ = AnimationTestHelpers.ConstantCurve(scale.Z);
    }

    private (Animator animator, Transform child) Rig()
    {
        Scene scene = CreateScene(enable: true);
        GameObject actor = CreateGameObject("Actor");
        GameObject child = CreateGameObject("Carrier"); child.SetParent(actor, worldPositionStays: false);
        scene.Add(actor);
        var animator = new Animator { PlayAutomatically = false, ApplyRootMotion = true,
            ApplyRootMotionRotation = false, Wrap = AnimationWrapMode.Once };
        actor.AddComponent(animator);
        return (animator, child.Transform);
    }

    private static AnimationClip RootClip()
    {
        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("ModelRoot", string.Empty, 2);
        clip.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone;
        clip.RootBonePath = ".";
        clip.AddBone(new AnimationClip.AnimBone { BoneName = "Carrier",
            PosX = AnimationTestHelpers.LinearCurve(0, 4, 1),
            PosY = AnimationTestHelpers.ConstantCurve(0), PosZ = AnimationTestHelpers.ConstantCurve(0) });
        return clip;
    }

    private static void Tick(Animator animator, float dt)
    {
        Time.TimeStack.Push(new TimeData { DeltaTime = dt });
        try { animator.LateUpdate(); } finally { Time.TimeStack.Pop(); }
    }
    private static void Near(Float3 expected, Float3 actual)
        => Assert.True(Float3.Length(expected - actual) < 3e-4f, $"expected {expected}; actual {actual}");
    private static void SameRotation(Quaternion expected, Quaternion actual)
        => Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > .9999f, $"expected {expected}; actual {actual}");
}
