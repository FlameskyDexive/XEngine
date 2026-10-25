using XEngine.Animation;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Runtime.Test.Animation;

/// <summary>★ Gen-P4.2 — Mixamo aliases remap onto Unity humanoid names (or a dest vocabulary).</summary>
public sealed class HumanoidRetargetTests : RuntimeTestBase
{
    [Fact]
    public void TryResolve_MixamoAndPathAliases()
    {
        Assert.True(HumanoidBoneMap.TryResolve("mixamorig:Hips", out HumanoidBone hips));
        Assert.Equal(HumanoidBone.Hips, hips);
        Assert.True(HumanoidBoneMap.TryResolve("Armature/mixamorig:LeftArm", out HumanoidBone arm));
        Assert.Equal(HumanoidBone.LeftUpperArm, arm);
        Assert.False(HumanoidBoneMap.TryResolve("Weapon_Socket", out _));
    }

    [Fact]
    public void Remap_MixamoToCanonical_AndLookupWorks()
    {
        var clip = new AnimationClip { Name = "Walk", Duration = 1f };
        clip.AddBone(new AnimationClip.AnimBone { BoneName = "mixamorig:Hips" });
        clip.AddBone(new AnimationClip.AnimBone { BoneName = "mixamorig:LeftArm" });
        clip.AddBone(new AnimationClip.AnimBone { BoneName = "Weapon_Socket" });

        int n = HumanoidRetarget.RemapBoneNames(clip);
        Assert.Equal(2, n);
        Assert.NotNull(clip.GetBone("Hips"));
        Assert.NotNull(clip.GetBone("LeftUpperArm"));
        Assert.NotNull(clip.GetBone("Weapon_Socket"));
        Assert.Null(clip.GetBone("mixamorig:Hips"));
    }

    [Fact]
    public void Remap_LikeDestVocabulary()
    {
        var clip = new AnimationClip { Name = "Walk", Duration = 1f };
        clip.AddBone(new AnimationClip.AnimBone { BoneName = "Hips" });
        clip.AddBone(new AnimationClip.AnimBone { BoneName = "LeftUpperArm" });

        var dest = HumanoidRetarget.CollectNames(new[] { "pelvis", "LeftArm" });
        int n = HumanoidRetarget.RemapBoneNames(clip, dest);
        Assert.Equal(2, n);
        Assert.NotNull(clip.GetBone("pelvis"));
        Assert.NotNull(clip.GetBone("LeftArm"));
    }
}
