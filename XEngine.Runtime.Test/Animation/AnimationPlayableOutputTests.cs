// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Playables;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies <see cref="AnimationPlayableOutput"/>: driven bones get their local TRS overwritten from
/// the sampled pose, and unanimated bones (Channels None) keep whatever transform gameplay wrote —
/// the property that lets animation compose with IK / look-at / parent layers.
/// </summary>
public class AnimationPlayableOutputTests : RuntimeTestBase
{
    private static readonly PlayableContext Ctx = new(0f, 0d, false);

    private (GameObject hips, GameObject spine, SkeletonBinding binding, AnimationClipPlayable playable)
        BuildHierarchyAndPlayable()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var spine = CreateGameObject("Spine");
        spine.SetParent(hips);
        var binding = SkeletonBinding.Build(root.Transform);

        // Clip animates Hips only (PosX 0 -> 10). Spine is never touched.
        var clip = AnimationTestHelpers.MakeSingleBonePosXClip("c", "Hips", endX: 10f);
        var playable = new AnimationClipPlayable(clip);
        playable.Bind(binding);
        playable.Play();
        playable.Weight = 1f; // actively contributing, so ProcessFrame samples rather than early-outing.
        playable.SetTime(1.0); // sample at end → PosX = 10.
        return (hips, spine, binding, playable);
    }

    [Fact]
    public void Apply_WritesDrivenBoneTransform()
    {
        var (hips, _, binding, playable) = BuildHierarchyAndPlayable();
        var output = new AnimationPlayableOutput { Source = playable, Binding = binding };

        playable.ProcessFrame(Ctx);
        output.Apply(Ctx);

        Assert.Equal(10f, hips.Transform.LocalPosition.X);
    }

    [Fact]
    public void Apply_DoesNotTouchUnanimatedBone()
    {
        var (_, spine, binding, playable) = BuildHierarchyAndPlayable();
        // Gameplay writes a custom transform on Spine; animation must not clobber it.
        spine.Transform.LocalPosition = new Float3(7f, 8f, 9f);

        var output = new AnimationPlayableOutput { Source = playable, Binding = binding };
        playable.ProcessFrame(Ctx);
        output.Apply(Ctx);

        Assert.Equal(7f, spine.Transform.LocalPosition.X);
        Assert.Equal(8f, spine.Transform.LocalPosition.Y);
        Assert.Equal(9f, spine.Transform.LocalPosition.Z);
    }

    [Fact]
    public void Apply_WithNullSource_IsNoOp()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var binding = SkeletonBinding.Build(root.Transform);
        var output = new AnimationPlayableOutput { Source = null, Binding = binding };

        var ex = Record.Exception(() => output.Apply(Ctx));
        Assert.Null(ex);
    }
}
