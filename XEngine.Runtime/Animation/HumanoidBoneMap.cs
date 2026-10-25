using System;
using System.Collections.Generic;

namespace XEngine.Animation;

/// <summary>★ Gen-P4.2 — Canonical humanoid bones (Unity-style names, Mixamo/VRM aliases).</summary>
public enum HumanoidBone : byte
{
    Hips,
    Spine,
    Chest,
    UpperChest,
    Neck,
    Head,
    LeftShoulder,
    LeftUpperArm,
    LeftLowerArm,
    LeftHand,
    RightShoulder,
    RightUpperArm,
    RightLowerArm,
    RightHand,
    LeftUpperLeg,
    LeftLowerLeg,
    LeftFoot,
    LeftToes,
    RightUpperLeg,
    RightLowerLeg,
    RightFoot,
    RightToes,
}

/// <summary>
/// ★ Gen-P4.2 — Resolve a clip/rig bone name (path, Mixamo prefix, aliases) to a
/// <see cref="HumanoidBone"/>. BCL-only so the Host can compile-include this file.
/// </summary>
public static class HumanoidBoneMap
{
    private static readonly Dictionary<string, HumanoidBone> Aliases = BuildAliases();

    public static string CanonicalName(HumanoidBone bone) => bone.ToString();

    public static bool TryResolve(string? boneName, out HumanoidBone bone)
    {
        bone = default;
        if (string.IsNullOrWhiteSpace(boneName))
            return false;
        string key = Normalize(boneName);
        return Aliases.TryGetValue(key, out bone);
    }

    /// <summary>Last path segment, strip common prefixes, lowercase, drop separators.</summary>
    public static string Normalize(string boneName)
    {
        string leaf = boneName.Trim();
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0 && slash < leaf.Length - 1)
            leaf = leaf[(slash + 1)..];
        int colon = leaf.LastIndexOf(':');
        if (colon >= 0 && colon < leaf.Length - 1)
            leaf = leaf[(colon + 1)..];

        var sb = new System.Text.StringBuilder(leaf.Length);
        foreach (char c in leaf)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static Dictionary<string, HumanoidBone> BuildAliases()
    {
        var map = new Dictionary<string, HumanoidBone>(StringComparer.Ordinal);
        void Add(HumanoidBone bone, params string[] names)
        {
            map[Normalize(bone.ToString())] = bone;
            foreach (string n in names)
                map[Normalize(n)] = bone;
        }

        Add(HumanoidBone.Hips, "hip", "pelvis", "root", "hips", "bip001pelvis");
        Add(HumanoidBone.Spine, "spine", "spine1", "spine01", "bip001spine");
        Add(HumanoidBone.Chest, "chest", "spine2", "spine02", "bip001spine1");
        Add(HumanoidBone.UpperChest, "upperchest", "spine3", "spine03", "bip001spine2");
        Add(HumanoidBone.Neck, "neck", "neck01", "bip001neck");
        Add(HumanoidBone.Head, "head", "bip001head");
        Add(HumanoidBone.LeftShoulder, "leftshoulder", "lshoulder", "claviclel", "bip001lclavicle");
        Add(HumanoidBone.LeftUpperArm, "leftupperarm", "leftarm", "luparm", "upperarml", "bip001lupperarm");
        Add(HumanoidBone.LeftLowerArm, "leftlowerarm", "leftforearm", "leftelbow", "lforearm", "lowerarml", "bip001lforearm");
        Add(HumanoidBone.LeftHand, "lefthand", "leftwrist", "lhand", "handl", "bip001lhand");
        Add(HumanoidBone.RightShoulder, "rightshoulder", "rshoulder", "clavicler", "bip001rclavicle");
        Add(HumanoidBone.RightUpperArm, "rightupperarm", "rightarm", "ruparm", "upperarmr", "bip001rupperarm");
        Add(HumanoidBone.RightLowerArm, "rightlowerarm", "rightforearm", "rightelbow", "rforearm", "lowerarmr", "bip001rforearm");
        Add(HumanoidBone.RightHand, "righthand", "rightwrist", "rhand", "handr", "bip001rhand");
        Add(HumanoidBone.LeftUpperLeg, "leftupperleg", "leftupleg", "leftthigh", "lupleg", "thighl", "bip001lthigh");
        Add(HumanoidBone.LeftLowerLeg, "leftlowerleg", "leftleg", "leftshin", "leftcalf", "lleg", "calfl", "bip001lcalf");
        Add(HumanoidBone.LeftFoot, "leftfoot", "leftankle", "lfoot", "footl", "bip001lfoot");
        Add(HumanoidBone.LeftToes, "lefttoes", "lefttoe", "lefttoebase", "ltoe", "balll", "bip001ltoe0");
        Add(HumanoidBone.RightUpperLeg, "rightupperleg", "rightupleg", "rightthigh", "rupleg", "thighr", "bip001rthigh");
        Add(HumanoidBone.RightLowerLeg, "rightlowerleg", "rightleg", "rightshin", "rightcalf", "rleg", "calfr", "bip001rcalf");
        Add(HumanoidBone.RightFoot, "rightfoot", "rightankle", "rfoot", "footr", "bip001rfoot");
        Add(HumanoidBone.RightToes, "righttoes", "righttoe", "righttoebase", "rtoe", "ballr", "bip001rtoe0");
        return map;
    }
}
