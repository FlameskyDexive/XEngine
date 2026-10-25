// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Playables;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies the M5 layer system: <see cref="AvatarMask"/> subtree propagation, the
/// <see cref="AnimationLayerMixerPlayable"/> Override (masked) and Additive blends, and that the
/// Animator's base layer (code-driven / FSM) keeps working unchanged under the new layer-mixer root.
/// </summary>
public class AnimationLayerMixerTests : RuntimeTestBase
{
    private static readonly PlayableContext Ctx = new(0f, 0d, false);

    private static AnimationClip MakeTwoBoneClip(string name, string boneA, float aX, string boneB, float bX, float duration = 1f)
    {
        var clip = new global::XEngine.Runtime.AnimationClip { Name = name };
        clip.Duration = duration;
        clip.Wrap = AnimationWrapMode.Loop;
        var a = new global::XEngine.Runtime.AnimationClip.AnimBone { BoneName = boneA };
        a.PosX = AnimationTestHelpers.ConstantCurve(aX);
        a.PosY = AnimationTestHelpers.ConstantCurve(0f);
        a.PosZ = AnimationTestHelpers.ConstantCurve(0f);
        clip.AddBone(a);
        var b = new global::XEngine.Runtime.AnimationClip.AnimBone { BoneName = boneB };
        b.PosX = AnimationTestHelpers.ConstantCurve(bX);
        b.PosY = AnimationTestHelpers.ConstantCurve(0f);
        b.PosZ = AnimationTestHelpers.ConstantCurve(0f);
        clip.AddBone(b);
        return clip;
    }

    private static AnimationClipPlayable MakePlayable(AnimationClip clip, SkeletonBinding binding)
    {
        var p = new AnimationClipPlayable(clip);
        p.Bind(binding);
        p.Play();
        p.Weight = 1f;
        p.SetTime(0d);
        return p;
    }

    private SkeletonBinding BuildSpineHierarchy()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var spine = CreateGameObject("Spine");
        spine.SetParent(hips);
        var head = CreateGameObject("Head");
        head.SetParent(spine);
        return SkeletonBinding.Build(root.Transform);
    }

    [Fact]
    public void AvatarMask_PropagatesToDescendants()
    {
        var binding = BuildSpineHierarchy(); // Hips(0), Hips/Spine(1), Hips/Spine/Head(2)
        var mask = new AvatarMask();
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips/Spine", Weight = 1f });

        var w = mask.BuildBoneWeights(binding);

        Assert.Equal(0f, w[0]);            // Hips: not covered.
        Assert.Equal(1f, w[1]);            // Spine: explicit entry.
        Assert.Equal(1f, w[2]);            // Head: inherited from Spine.
    }

    [Fact]
    public void OverrideLayer_MaskedBonesOverwrite_UnmaskedKeepBase()
    {
        var binding = BuildSpineHierarchy();
        // Base: Hips=0, Spine=5. Overlay: Spine=10 (only), masked to Spine subtree.
        var baseClip = MakeTwoBoneClip("base", "Hips", 0f, "Hips/Spine", 5f);
        var overlayClip = MakeTwoBoneClip("ov", "Hips/Spine", 10f, "Hips/Spine/Head", 0f);

        var basePlayable = MakePlayable(baseClip, binding);
        var overlayPlayable = MakePlayable(overlayClip, binding);

        var mask = new AvatarMask();
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips/Spine", Weight = 1f });

        var layerMixer = new AnimationLayerMixerPlayable();
        layerMixer.Bind(binding);
        var baseLayer = new AnimationMixerPlayable();
        baseLayer.Bind(binding);
        baseLayer.AddInput(basePlayable);
        var overlayLayer = new AnimationMixerPlayable();
        overlayLayer.Bind(binding);
        overlayLayer.AddInput(overlayPlayable);
        layerMixer.AddLayer(baseLayer, 1f, AnimatorLayerBlendingMode.Override, null);
        layerMixer.AddLayer(overlayLayer, 1f, AnimatorLayerBlendingMode.Override, mask.BuildBoneWeights(binding));

        // Process: children first (post-order), then layer mixer.
        baseLayer.GetInput(0).ProcessFrame(Ctx);
        baseLayer.ProcessFrame(Ctx);
        overlayLayer.GetInput(0).ProcessFrame(Ctx);
        overlayLayer.ProcessFrame(Ctx);
        layerMixer.ProcessFrame(Ctx);

        var pose = layerMixer.Output!;
        // Hips: base only (overlay masked out) → 0.
        Assert.Equal(0f, pose.Bones[0].LocalPosition.X, precision: 3);
        // Spine: override (weight 1) → overlay wins → 10.
        Assert.Equal(10f, pose.Bones[1].LocalPosition.X, precision: 3);
    }

    [Fact]
    public void OverrideLayer_PartialWeight_Lerps()
    {
        var binding = BuildSpineHierarchy();
        var baseClip = MakeTwoBoneClip("base", "Hips", 0f, "Hips/Spine", 5f);
        var overlayClip = MakeTwoBoneClip("ov", "Hips/Spine", 10f, "Hips/Spine/Head", 0f);
        var basePlayable = MakePlayable(baseClip, binding);
        var overlayPlayable = MakePlayable(overlayClip, binding);

        var mask = new AvatarMask();
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips/Spine", Weight = 1f });

        var layerMixer = new AnimationLayerMixerPlayable();
        layerMixer.Bind(binding);
        var baseLayer = new AnimationMixerPlayable(); baseLayer.Bind(binding); baseLayer.AddInput(basePlayable);
        var overlayLayer = new AnimationMixerPlayable(); overlayLayer.Bind(binding); overlayLayer.AddInput(overlayPlayable);
        layerMixer.AddLayer(baseLayer, 1f, AnimatorLayerBlendingMode.Override, null);
        layerMixer.AddLayer(overlayLayer, 0.5f, AnimatorLayerBlendingMode.Override, mask.BuildBoneWeights(binding));

        baseLayer.GetInput(0).ProcessFrame(Ctx); baseLayer.ProcessFrame(Ctx);
        overlayLayer.GetInput(0).ProcessFrame(Ctx); overlayLayer.ProcessFrame(Ctx);
        layerMixer.ProcessFrame(Ctx);

        // Spine: lerp(5, 10, 0.5) = 7.5.
        Assert.Equal(7.5f, layerMixer.Output!.Bones[1].LocalPosition.X, precision: 3);
    }

    [Fact]
    public void AdditiveLayer_AddsDelta()
    {
        var binding = BuildSpineHierarchy();
        // Base Spine=5; additive Spine=+3 (delta clip authored at pos 3).
        var baseClip = MakeTwoBoneClip("base", "Hips", 0f, "Hips/Spine", 5f);
        var deltaClip = MakeTwoBoneClip("d", "Hips/Spine", 3f, "Hips/Spine/Head", 0f);
        var basePlayable = MakePlayable(baseClip, binding);
        var deltaPlayable = MakePlayable(deltaClip, binding);

        var layerMixer = new AnimationLayerMixerPlayable();
        layerMixer.Bind(binding);
        var baseLayer = new AnimationMixerPlayable(); baseLayer.Bind(binding); baseLayer.AddInput(basePlayable);
        var addLayer = new AnimationMixerPlayable(); addLayer.Bind(binding); addLayer.AddInput(deltaPlayable);
        layerMixer.AddLayer(baseLayer, 1f, AnimatorLayerBlendingMode.Override, null);
        layerMixer.AddLayer(addLayer, 1f, AnimatorLayerBlendingMode.Additive, null); // full-body additive

        baseLayer.GetInput(0).ProcessFrame(Ctx); baseLayer.ProcessFrame(Ctx);
        addLayer.GetInput(0).ProcessFrame(Ctx); addLayer.ProcessFrame(Ctx);
        layerMixer.ProcessFrame(Ctx);

        // Spine: 5 + 3*1 = 8.
        Assert.Equal(8f, layerMixer.Output!.Bones[1].LocalPosition.X, precision: 3);
    }

    [Fact]
    public void Animator_BaseLayerStillPlays_UnderLayerMixerRoot()
    {
        // Regression guard: the M0–M4 base-layer path must still drive the skeleton now that the
        // graph root is an AnimationLayerMixerPlayable wrapping the base mixer.
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        scene.Add(root);
        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        animator.Play(AnimationTestHelpers.MakeConstantPosClip("c", "Hips", posX: 7f));

        Update(scene, frames: 2);

        Assert.Equal(7f, root.Children[0].Transform.LocalPosition.X, precision: 3);
    }

    [Fact]
    public void Animator_AddLayer_OverlayBlendsOverBase()
    {
        var scene = CreateScene(enable: true);
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var spine = CreateGameObject("Spine");
        spine.SetParent(hips);
        scene.Add(root);

        var animator = new Animator { PlayAutomatically = false };
        root.AddComponent(animator);
        // Base: Hips=0, Hips/Spine=5.
        animator.Play(MakeTwoBoneClip("base", "Hips", 0f, "Hips/Spine", 5f));

        // Overlay layer masked to Hips/Spine, playing Hips/Spine=10.
        var mask = new AvatarMask();
        mask.Entries.Add(new AvatarMask.Entry { Path = "Hips/Spine", Weight = 1f });
        var overlayMixer = animator.AddLayer(mask, AnimatorLayerBlendingMode.Override, 1f);
        var overlayClip = AnimationTestHelpers.MakeConstantPosClip("ov", "Hips/Spine", posX: 10f);
        var overlayState = new AnimationClipPlayable(overlayClip);
        overlayState.Bind(animator.LayerMixer!.Binding!);
        overlayState.Play();
        overlayState.Weight = 1f;
        overlayMixer.AddInput(overlayState);

        Update(scene, frames: 2);

        // Hips: base only (overlay masked out) → 0. Hips/Spine: override → 10.
        Assert.Equal(0f, root.Children[0].Transform.LocalPosition.X, precision: 3);
        Assert.Equal(10f, root.Children[0].Children[0].Transform.LocalPosition.X, precision: 3);
    }
}
