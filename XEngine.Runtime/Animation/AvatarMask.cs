// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>Humanoid body-part slots on an <see cref="AvatarMask"/> (authored in the M44 editor; bone-weight runtime still uses <see cref="AvatarMask.Entries"/>).</summary>
public enum AvatarMaskBodyPart : byte
{
    Root = 0,
    Body = 1,
    Head = 2,
    LeftArm = 3,
    RightArm = 4,
    LeftLeg = 5,
    RightLeg = 6,
    LeftFingers = 7,
    RightFingers = 8,
}

/// <summary>
/// A per-bone weight mask selecting which bones a layer affects. Authored as a list of
/// <see cref="Entry"/> items (bone path + weight in [0,1]); an entry on a parent bone propagates to
/// its descendants unless a descendant has its own entry. A layer with a <c>null</c> mask affects the
/// full body. Stored as an Echo asset (<c>.mask</c>).
/// </summary>
[CreateAssetMenu("Animation/Avatar Mask", Extension = ".mask", Order = 92)]
public sealed class AvatarMask : EngineObject
{
    /// <summary>Count of <see cref="AvatarMaskBodyPart"/> slots.</summary>
    public const int HumanoidPartCount = 9;

    /// <summary>The mask entries. An empty list with a non-null mask means "affect nothing".</summary>
    public List<Entry> Entries = new();

    /// <summary>
    /// Per-<see cref="AvatarMaskBodyPart"/> include flags. Empty (legacy .mask files) means every
    /// part is included. First edit expands the list to <see cref="HumanoidPartCount"/> trues.
    /// </summary>
    public List<bool> HumanoidParts = new();

    /// <summary>
    /// M55 — the humanoid avatar the body-part toggles resolve against (role → bone path). When
    /// null, humanoid parts have no runtime effect (path entries still apply).
    /// </summary>
    public Humanoid.Avatar? HumanoidAvatar;

    private static void ApplyHumanoidParts(
        float[] w,
        SkeletonBinding binding,
        Humanoid.Avatar avatar,
        List<bool> parts)
    {
        // Empty list = all included (legacy) — path entries already carry the mask.
        if (parts == null || parts.Count == 0)
            return;

        void ZeroPart(HumanoidBone bone)
        {
            if (!avatar.TryGetPath(bone, out string path))
                return;
            int idx = binding.IndexOfBonePath(path);
            if (idx >= 0)
                w[idx] = 0f;
        }

        for (int p = 0; p < HumanoidPartCount && p < parts.Count; p++)
        {
            if (parts[p])
                continue;
            switch ((AvatarMaskBodyPart)p)
            {
                case AvatarMaskBodyPart.Root: ZeroPart(HumanoidBone.Hips); break;
                case AvatarMaskBodyPart.Body:
                    ZeroPart(HumanoidBone.Spine);
                    ZeroPart(HumanoidBone.Chest);
                    ZeroPart(HumanoidBone.UpperChest);
                    break;
                case AvatarMaskBodyPart.Head:
                    ZeroPart(HumanoidBone.Head);
                    ZeroPart(HumanoidBone.Neck);
                    break;
                case AvatarMaskBodyPart.LeftArm:
                case AvatarMaskBodyPart.LeftFingers:
                    ZeroPart(HumanoidBone.LeftShoulder);
                    ZeroPart(HumanoidBone.LeftUpperArm);
                    ZeroPart(HumanoidBone.LeftLowerArm);
                    ZeroPart(HumanoidBone.LeftHand);
                    break;
                case AvatarMaskBodyPart.RightArm:
                case AvatarMaskBodyPart.RightFingers:
                    ZeroPart(HumanoidBone.RightShoulder);
                    ZeroPart(HumanoidBone.RightUpperArm);
                    ZeroPart(HumanoidBone.RightLowerArm);
                    ZeroPart(HumanoidBone.RightHand);
                    break;
                case AvatarMaskBodyPart.LeftLeg:
                    ZeroPart(HumanoidBone.LeftUpperLeg);
                    ZeroPart(HumanoidBone.LeftLowerLeg);
                    ZeroPart(HumanoidBone.LeftFoot);
                    ZeroPart(HumanoidBone.LeftToes);
                    break;
                case AvatarMaskBodyPart.RightLeg:
                    ZeroPart(HumanoidBone.RightUpperLeg);
                    ZeroPart(HumanoidBone.RightLowerLeg);
                    ZeroPart(HumanoidBone.RightFoot);
                    ZeroPart(HumanoidBone.RightToes);
                    break;
            }
        }
    }

    /// <summary>Whether <paramref name="part"/> is included. Missing/legacy lists count as included.</summary>
    public bool GetHumanoidPart(AvatarMaskBodyPart part)
    {
        int i = (int)part;
        if ((uint)i >= HumanoidPartCount) return true;
        var list = HumanoidParts;
        if (list.Count == 0) return true;
        return i < list.Count && list[i];
    }

    /// <summary>Sets <paramref name="part"/> included/excluded, expanding a legacy empty list first.</summary>
    public void SetHumanoidPart(AvatarMaskBodyPart part, bool enabled)
    {
        EnsureHumanoidParts();
        int i = (int)part;
        if ((uint)i < (uint)HumanoidParts.Count)
            HumanoidParts[i] = enabled;
    }

    void EnsureHumanoidParts()
    {
        var list = HumanoidParts;
        while (list.Count < HumanoidPartCount)
            list.Add(true);
    }

    /// <summary>
    /// Resolves the mask against <paramref name="binding"/> into a per-bone-index weight array. Each
    /// bone inherits its nearest ancestor's entry; bones with no covering entry get weight 0.
    /// </summary>
    public float[] BuildBoneWeights(SkeletonBinding binding)
    {
        int n = binding.BoneCount;
        var w = new float[n];

        // Explicit entries first.
        var entries = Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            int idx = binding.IndexOfBonePath(entries[i].Path);
            if (idx >= 0)
                w[idx] = entries[i].Weight;
        }

        // M55 — humanoid body-part toggles resolve through the avatar's role→bone mapping when one
        // is bound; disabled parts zero their bones (subtree included).
        if (HumanoidAvatar != null)
            ApplyHumanoidParts(w, binding, HumanoidAvatar, HumanoidParts);

        // Subtree propagation: a bone whose path is under an entry's path inherits that entry's weight
        // (unless it already has its own). Lets "Spine"→1 cover "Spine/Neck/Head" automatically.
        for (int i = 0; i < n; i++)
        {
            if (w[i] != 0f)
                continue;
            string path = binding.GetBonePath(i);
            if (string.IsNullOrEmpty(path))
                continue;
            float inherited = 0f;
            for (int e = 0; e < entries.Count; e++)
            {
                string ep = entries[e].Path;
                if (!string.IsNullOrEmpty(ep) && path.StartsWith(ep + "/", System.StringComparison.Ordinal))
                {
                    inherited = entries[e].Weight;
                    break;
                }
            }
            w[i] = inherited;
        }

        return w;
    }

    /// <summary>One bone-path / weight pair on the mask.</summary>
    public class Entry
    {
        /// <summary>Bone path relative to the animation root (same convention as clip bone paths).</summary>
        public string Path = string.Empty;
        /// <summary>Weight in [0,1]: 1 fully included, 0 excluded.</summary>
        public float Weight = 1f;
    }
}
