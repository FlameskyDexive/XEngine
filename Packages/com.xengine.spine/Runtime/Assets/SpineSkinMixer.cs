using System;
using Spine;

namespace XEngine.Spine;

/// <summary>Combine authored skins (same skeleton, new look) without regenerating bones.</summary>
public static class SpineSkinMixer
{
    public static Skin Combine(SkeletonData data, params string[] skinNames)
    {
        ArgumentNullException.ThrowIfNull(data);
        var combined = new Skin("combined");
        if (skinNames == null)
            return combined;

        for (int i = 0; i < skinNames.Length; i++)
        {
            string name = skinNames[i];
            if (string.IsNullOrEmpty(name))
                continue;
            Skin? skin = data.FindSkin(name);
            if (skin != null)
                combined.AddSkin(skin);
        }

        return combined;
    }

    public static void Apply(Skeleton skeleton, params string[] skinNames)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        skeleton.SetSkin(Combine(skeleton.Data, skinNames));
        skeleton.SetupPoseSlots();
    }
}
