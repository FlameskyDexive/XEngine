// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;
using XEngine.Vector;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// Verifies <see cref="SkeletonBinding.Build"/>: bone paths are assigned integer indices by
/// walk-order from the root's children, nested paths use the slash convention, the root itself is
/// excluded, and unresolved paths return -1.
/// </summary>
public class SkeletonBindingTests : RuntimeTestBase
{
    private GameObject BuildArmature()
    {
        var root = CreateGameObject("Armature");
        var hips = CreateGameObject("Hips");
        hips.SetParent(root);
        var spine = CreateGameObject("Spine");
        spine.SetParent(hips);
        var leftArm = CreateGameObject("LeftArm");
        leftArm.SetParent(spine);
        return root;
    }

    [Fact]
    public void Build_AssignsIndicesByWalkOrder()
    {
        var root = BuildArmature();
        var binding = SkeletonBinding.Build(root.Transform);

        Assert.Equal(0, binding.IndexOfBonePath("Hips"));
        Assert.Equal(1, binding.IndexOfBonePath("Hips/Spine"));
        Assert.Equal(2, binding.IndexOfBonePath("Hips/Spine/LeftArm"));
    }

    [Fact]
    public void Build_BonesArrayHoldsResolvedTransforms()
    {
        var root = BuildArmature();
        var binding = SkeletonBinding.Build(root.Transform);

        Assert.Equal(3, binding.BoneCount);
        Assert.NotNull(binding.Bones[0]);
        Assert.Equal("Hips", binding.Bones[0]!.GameObject.Name);
        Assert.Equal("Spine", binding.Bones[1]!.GameObject.Name);
    }

    [Fact]
    public void Build_ReferencePoseDoesNotFollowAnimatedTransformMutations()
    {
        var root = BuildArmature();
        Transform hips = root.Children[0].Transform;
        hips.LocalPosition = new Float3(1f, 2f, 3f);
        hips.LocalRotation = Quaternion.AxisAngle(Float3.UnitY, 0.4f);
        var binding = SkeletonBinding.Build(root.Transform);

        BonePose reference = binding.ReferencePose[0];
        hips.LocalPosition = new Float3(9f, 9f, 9f);
        hips.LocalRotation = Quaternion.Identity;

        Assert.Equal(1f, reference.LocalPosition.X);
        Assert.Equal(2f, reference.LocalPosition.Y);
        Assert.True(Quaternion.Angle(reference.LocalRotation, Quaternion.AxisAngle(Float3.UnitY, 0.4f)) < 1e-4f);
    }

    [Fact]
    public void Build_RootItselfNotInBoneList()
    {
        var root = BuildArmature();
        var binding = SkeletonBinding.Build(root.Transform);

        // The root's own path (empty) is intentionally not registered as a bone.
        Assert.Equal(-1, binding.IndexOfBonePath(""));
        Assert.Equal(-1, binding.IndexOfBonePath("Armature"));
    }

    [Fact]
    public void IndexOfBonePath_UnresolvedPathReturnsMinusOne()
    {
        var root = BuildArmature();
        var binding = SkeletonBinding.Build(root.Transform);

        Assert.Equal(-1, binding.IndexOfBonePath("Hips/Spine/RightArm"));
        Assert.Equal(-1, binding.IndexOfBonePath("Tail"));
    }

    [Fact]
    public void IndexOfBlendShape_UnresolvedReturnsMinusOne()
    {
        var root = BuildArmature();
        var binding = SkeletonBinding.Build(root.Transform);

        // No SkinnedMeshRenderers in this hierarchy → empty blend-shape table.
        Assert.Equal(0, binding.BlendShapeCount);
        Assert.Equal(-1, binding.IndexOfBlendShape("Hips", "Smile"));
    }
}
