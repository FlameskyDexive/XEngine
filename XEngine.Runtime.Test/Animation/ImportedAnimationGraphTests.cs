using System.Reflection;
using XEngine.Animation;
using XEngine.Animation.Humanoid;
using XEngine.Playables;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test.Animation;

public sealed class ImportedAnimationGraphTests : RuntimeTestBase
{
    private sealed class Backend : AssetBackendBase
    {
        public int FreshLoads;
        public void Seed(EngineObject obj) => SetLoaded(obj.AssetID, obj);
        public void Publish(Guid id, EngineObject? replacement)
            => Publish(new Dictionary<Guid, EngineObject?> { [id] = replacement });
        public void Publish(Dictionary<Guid, EngineObject?> replacements)
        {
            foreach (var pair in replacements)
            {
                if (_loadedAssets.TryRemove(pair.Key, out var old) && !ReferenceEquals(old, pair.Value)) old.Dispose();
                if (pair.Value != null) SetLoaded(pair.Key, pair.Value);
            }
            NotifyAssetsPublished(replacements);
        }
        public int Listeners => (typeof(AssetBackendBase).GetField("AssetsPublished",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this) as Delegate)?.GetInvocationList().Length ?? 0;
        protected override EngineObject? LoadFresh(Guid assetId) { FreshLoads++; return null; }
    }

    private readonly Backend _backend = new();
    private readonly AssetBackendBase? _previous = AssetDatabase.Current;
    private readonly List<EngineObject> _assets = new();
    public ImportedAnimationGraphTests() => AssetDatabase.Current = _backend;

    private T Own<T>(T asset, Guid id = default) where T : EngineObject
    {
        asset.AssetID = id == Guid.Empty ? Guid.NewGuid() : id;
        _assets.Add(asset);
        if (id == Guid.Empty) _backend.Seed(asset);
        return asset;
    }
    private AnimationClip Clip(float value, Guid id = default)
        => Own(AnimationTestHelpers.MakeConstantPosClip("Clip", "Bone", value, 4), id);
    private (Animator animator, GameObject bone) Rig()
    {
        var root = CreateGameObject("Root");
        var bone = CreateGameObject("Bone"); bone.SetParent(root);
        var other = CreateGameObject("Other"); other.SetParent(root);
        var animator = new Animator { PlayAutomatically = false }; root.AddComponent(animator);
        return (animator, bone);
    }

    [Fact]
    public void CrossFadeAndFromStartClonesKeepIdentityClockAndFadeProgress()
    {
        var (animator, _) = Rig();
        var clip = Clip(1); clip.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        var main = animator.Play(clip); animator.EvaluateGraph(.2f);
        var clone = animator.Play(clip, 1, FadeMode.FromStart, FadeEasing.CubicInOut);
        animator.EvaluateGraph(.25f);
        double mainTime = main.Time, cloneTime = clone.Time;
        float mainWeight = main.Weight, cloneWeight = clone.Weight;
        var replacement = Clip(2, clip.AssetID); replacement.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        _backend.Publish(clip.AssetID, replacement);
        Assert.Same(replacement, main.Clip); Assert.Same(replacement, clone.Clip);
        Assert.Equal(mainTime, main.Time); Assert.Equal(cloneTime, clone.Time);
        Assert.Equal(mainWeight, main.Weight); Assert.Equal(cloneWeight, clone.Weight);
        Assert.True(main.IsFading); Assert.True(clone.IsFading);
        animator.EvaluateGraph(.25f);
        Assert.InRange(clone.Weight, .4999f, .5001f);
        Assert.InRange(main.Weight, .4999f, .5001f);
        Assert.Same(main, animator.Play(replacement));
    }

    [Fact]
    public void ControllerKeepsFSMParametersAndTransitionAndRefreshesUnvisitedMotions()
    {
        var (animator, _) = Rig();
        var idle = Clip(1); var attack = Clip(2);
        var controller = new AnimatorController { DefaultStateName = "Idle" };
        controller.States.Add(new AnimatorController.State { Name = "Idle", Motion = new(idle) });
        controller.States.Add(new AnimatorController.State { Name = "Attack", Motion = new(attack) });
        Assert.True(animator.TryActivateController(controller));
        animator.EvaluateGraph(.25f);
        var runtime = animator.Runtime;
        animator.Parameters.SetFloat("Speed", 7);
        var newIdle = Clip(3, idle.AssetID); var newAttack = Clip(4, attack.AssetID);
        _backend.Publish(new() { [idle.AssetID] = newIdle, [attack.AssetID] = newAttack });
        Assert.Same(runtime, animator.Runtime);
        Assert.Equal(.25, animator.GetCurrentAnimatorStateInfo().normalizedTime * 4, 5);
        Assert.Equal(7, animator.Parameters.GetFloat("Speed"));
        animator.CrossFade("Attack", 1);
        animator.EvaluateGraph(.2f);
        Assert.Same(newAttack, animator.CurrentClip);
        var another = Clip(5, attack.AssetID);
        float normalized = animator.GetCurrentAnimatorStateInfo().normalizedTime;
        _backend.Publish(attack.AssetID, another);
        Assert.Same(runtime, animator.Runtime);
        Assert.Equal(normalized, animator.GetCurrentAnimatorStateInfo().normalizedTime);
        Assert.True(animator.IsInTransition());
        Assert.Same(another, animator.CurrentClip);
    }

    [Fact]
    public void NestedTreeLeafAndRuntimeEventsRefreshWithoutRestartOrImmediateCallback()
    {
        var (animator, bone) = Rig();
        var clip = Clip(1); clip.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        var leaf = new AnimationClipPlayable(clip); leaf.Bind(SkeletonBinding.Build(animator.Transform));
        leaf.Play(); leaf.SetTime(.25);
        int events = 0; leaf.AddEvent(.25f, () => events++);
        var tree = new BlendTreePlayable(); tree.Bind(leaf.Binding!); tree.AddChild(leaf, 0);
        animator.PlayPlayable(tree); animator.EvaluateGraph(0);
        var replacement = Clip(9, clip.AssetID); replacement.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        _backend.Publish(clip.AssetID, replacement);
        Assert.Equal(0, events); Assert.Same(replacement, leaf.Clip);
        animator.EvaluateGraph(.8f);
        Assert.Equal(1, events); Assert.Equal(9, bone.Transform.LocalPosition.X);
        Assert.Same(tree, animator.LayerMixer!.GetInput(0).GetInput(0));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AvatarReplacementRebindsMuscleMapsAndSupportsKindChangesInPlace(bool startHumanoid)
    {
        var (animator, bone) = Rig();
        var avatar = Own(new Avatar { RigKind = startHumanoid ? AvatarRigKind.Humanoid : AvatarRigKind.Generic });
        avatar.Mappings.Add(new Avatar.BoneMapping { Bone = HumanoidBone.Hips, Path = "Bone", LocalPosition = new(1, 0, 0) });
        var clip = Clip(2); clip.IsHumanoid = true;
        clip.MuscleCurves.Add(new MuscleCurve { Bone = HumanoidBone.Hips, Channel = 3,
            Curve = AnimationTestHelpers.ConstantCurve(0) });
        animator.Avatar = new(avatar);
        var state = animator.Play(clip); state.SetTime(.5); state.Pause();
        animator.EvaluateGraph(0);
        var replacement = Own(new Avatar(), avatar.AssetID);
        replacement.Mappings.Add(new Avatar.BoneMapping { Bone = HumanoidBone.Hips, Path = "Other", LocalPosition = new(8, 0, 0) });
        _backend.Publish(avatar.AssetID, replacement);
        animator.EvaluateGraph(0);
        Assert.Equal(8, animator.Skeleton!.Bones[animator.Skeleton.IndexOfBonePath("Other")].LocalPosition.X);
        Assert.Equal(.5, state.Time); Assert.False(state.IsPlaying);
        Assert.Same(state, animator.Play(clip)); state.SetTime(.5); state.Pause();
        var generic = Own(new Avatar { RigKind = AvatarRigKind.Generic }, avatar.AssetID);
        _backend.Publish(avatar.AssetID, generic);
        animator.EvaluateGraph(0);
        Assert.Equal(2, bone.Transform.LocalPosition.X);
        _backend.Publish(avatar.AssetID, null);
        animator.EvaluateGraph(0); // Deleted Avatar must not retain a muscle sampler referencing it.
        Assert.Null(state.HumanoidSampler);
    }

    [Fact]
    public void ClipKindCanBecomeHumanoidWithoutReplacingBasePlayable()
    {
        var (animator, bone) = Rig();
        var avatar = Own(new Avatar());
        avatar.Mappings.Add(new Avatar.BoneMapping { Bone = HumanoidBone.Hips, Path = "Bone", LocalPosition = new(8, 0, 0) });
        animator.Avatar = new(avatar);
        var clip = Clip(2); var state = animator.Play(clip); state.SetTime(.5); state.Pause();
        var replacement = Clip(3, clip.AssetID); replacement.IsHumanoid = true;
        replacement.MuscleCurves.Add(new MuscleCurve { Bone = HumanoidBone.Hips, Channel = 3,
            Curve = AnimationTestHelpers.ConstantCurve(0) });
        _backend.Publish(clip.AssetID, replacement);
        animator.EvaluateGraph(0);
        Assert.Equal(8, bone.Transform.LocalPosition.X);
        Assert.IsNotType<HumanoidClipPlayable>(state); // Runtime identity is deliberately stable.
        Assert.NotNull(state.HumanoidSampler); Assert.Equal(.5, state.Time);
    }

    [Fact]
    public void RootPolicyAndTrajectoryChangeUsesNewIntervalWithoutTeleport()
    {
        var (animator, _) = Rig(); animator.ApplyRootMotion = true;
        var clip = Own(AnimationTestHelpers.MakeSingleBonePosXClip("Root", "Bone", 4, 4));
        clip.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone; clip.RootBonePath = "Bone";
        var state = animator.Play(clip); state.SetTime(1);
        var replacement = Own(AnimationTestHelpers.MakeSingleBonePosXClip("Root2", "Bone", 40, 4), clip.AssetID);
        replacement.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone; replacement.RootBonePath = "Bone";
        _backend.Publish(clip.AssetID, replacement);
        Float3 delta = default; animator.OnAnimatorMove = (p, _) => delta = p;
        var time = new TimeData { DeltaTime = .1f }; Time.TimeStack.Push(time);
        try { animator.LateUpdate(); } finally { Time.TimeStack.Pop(); }
        Assert.InRange(delta.X, .9999f, 1.0001f);
        replacement = Clip(30, clip.AssetID); replacement.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        _backend.Publish(clip.AssetID, replacement);
        Time.TimeStack.Push(time);
        try { animator.LateUpdate(); } finally { Time.TimeStack.Pop(); }
        Assert.Equal(Float3.Zero, delta);
        Assert.Equal(ClipRootMotionPolicy.Disabled, state.BoundRootMotionPolicy);
    }

    [Fact]
    public void PreviewOwnershipSurvivesIdleSweepAndReleasesOnDisableRebuildAndDispose()
    {
        var (animator, _) = Rig(); var clip = Clip(1);
        animator.Play(clip);
        Assert.Equal(1, _backend.Listeners); Assert.True(AssetDatabase.IsLocked(clip.AssetID));
        AssetDatabase.ForceIdle(clip.AssetID); _backend.ForceIdleSweep(); Assert.False(clip.IsDisposed);
        animator.RebuildGraph(); Assert.Equal(1, _backend.Listeners); Assert.False(AssetDatabase.IsLocked(clip.AssetID));
        animator.Play(clip); animator.OnDisable();
        Assert.Equal(0, _backend.Listeners); Assert.False(AssetDatabase.IsLocked(clip.AssetID));
        animator.Play(clip); animator.Dispose();
        Assert.Equal(0, _backend.Listeners); Assert.False(AssetDatabase.IsLocked(clip.AssetID));
        var replacement = Clip(2, clip.AssetID); _backend.Publish(clip.AssetID, replacement);
    }

    [Fact]
    public void RefreshedHumanoidRootMotionStartsAtNewSampleAndKeepsFirstIncrement()
    {
        var (animator, _) = Rig(); animator.ApplyRootMotion = true;
        var avatar = Own(new Avatar { LegLength = 1 });
        avatar.Mappings.Add(new Avatar.BoneMapping { Bone = HumanoidBone.Hips, Path = "Bone" });
        animator.Avatar = new(avatar);
        var clip = Clip(0); clip.IsHumanoid = true;
        clip.MuscleCurves.Add(new MuscleCurve { Bone = HumanoidBone.Hips, Channel = 3,
            Curve = AnimationTestHelpers.LinearCurve(0, 4, 4) });
        var state = animator.Play(clip); state.SetTime(1);
        var replacement = Clip(0, clip.AssetID); replacement.IsHumanoid = true;
        replacement.MuscleCurves.Add(new MuscleCurve { Bone = HumanoidBone.Hips, Channel = 3,
            Curve = AnimationTestHelpers.LinearCurve(100, 140, 4) });
        _backend.Publish(clip.AssetID, replacement);
        Float3 delta = default; animator.OnAnimatorMove = (p, _) => delta = p;
        Time.TimeStack.Push(new TimeData { DeltaTime = .1f });
        try { animator.LateUpdate(); } finally { Time.TimeStack.Pop(); }
        Assert.InRange(delta.X, .9999f, 1.0001f);
    }

    [Fact]
    public void ShorterClipPreservesSecondsAndDeletedControllerMotionCanReturn()
    {
        var (animator, _) = Rig(); var clip = Clip(1);
        var controller = new AnimatorController { DefaultStateName = "Only" };
        controller.States.Add(new AnimatorController.State { Name = "Only", Motion = new(clip) });
        Assert.True(animator.TryActivateController(controller)); animator.EvaluateGraph(.5f);
        var runtime = animator.Runtime;
        var replacement = Clip(2, clip.AssetID); replacement.Duration = 2;
        _backend.Publish(clip.AssetID, replacement);
        Assert.Equal(.25f, animator.GetCurrentAnimatorStateInfo().normalizedTime);
        _backend.Publish(clip.AssetID, null); Assert.Null(animator.CurrentClip);
        _backend.Publish(clip.AssetID, Clip(3, clip.AssetID));
        Assert.Same(runtime, animator.Runtime);
        Assert.Equal(.125f, animator.GetCurrentAnimatorStateInfo().normalizedTime);
        animator.CrossFade("Only", 0);
        Assert.False(animator.CurrentClip!.IsDisposed);
    }

    [Fact]
    public void TwoOwnersReleaseOnlyTheirOwnPins()
    {
        var (a, _) = Rig(); var (b, _) = Rig(); var clip = Clip(1);
        a.Play(clip); b.Play(clip); a.Dispose();
        Assert.Equal(1, _backend.Listeners); Assert.True(AssetDatabase.IsLocked(clip.AssetID));
        b.Dispose(); Assert.Equal(0, _backend.Listeners); Assert.False(AssetDatabase.IsLocked(clip.AssetID));
    }

    [Fact]
    public void ReplacementKeepsSteadyStateSamplingAllocationFreeAndDoesNotLoadAssets()
    {
        var (animator, _) = Rig(); var clip = Clip(1); animator.Play(clip);
        var replacement = Clip(2, clip.AssetID); _backend.Publish(clip.AssetID, replacement);
        for (int i = 0; i < 100; i++) animator.EvaluateGraph(.01f);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 512; i++) animator.EvaluateGraph(.01f);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated); Assert.Equal(0, _backend.FreshLoads);
    }

    public override void Dispose()
    {
        base.Dispose();
        foreach (var asset in _assets) { asset.Dispose(); AssetDatabase.Forget(asset.AssetID); }
        AssetDatabase.Current = _previous;
    }
}
