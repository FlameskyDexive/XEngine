using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// ★ Gen-P4.2 — Rename clip bone tracks onto a destination humanoid vocabulary.
/// Curves stay as authored local TRS (no muscle space). Unmapped bones are left unchanged.
/// </summary>
public static class HumanoidRetarget
{
    /// <param name="destNames">Optional HumanoidBone → dest rig name. Null uses Unity canonical names.</param>
    /// <returns>Number of bones whose names changed.</returns>
    public static int RemapBoneNames(AnimationClip clip, IReadOnlyDictionary<HumanoidBone, string>? destNames = null)
    {
        int remapped = 0;
        List<AnimationClip.AnimBone> bones = clip.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            AnimationClip.AnimBone bone = bones[i];
            if (!HumanoidBoneMap.TryResolve(bone.BoneName, out HumanoidBone human))
                continue;
            string next = destNames != null && destNames.TryGetValue(human, out string? dest)
                ? dest
                : HumanoidBoneMap.CanonicalName(human);
            if (string.Equals(next, bone.BoneName, System.StringComparison.Ordinal))
                continue;
            bone.BoneName = next;
            remapped++;
        }

        if (remapped > 0)
            clip.RebuildBoneMap();
        return remapped;
    }

    public static Dictionary<HumanoidBone, string> CollectNames(IEnumerable<string> boneNames)
    {
        var dest = new Dictionary<HumanoidBone, string>();
        foreach (string name in boneNames)
        {
            if (HumanoidBoneMap.TryResolve(name, out HumanoidBone human) && !dest.ContainsKey(human))
                dest[human] = name;
        }

        return dest;
    }
}
