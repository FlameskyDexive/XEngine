// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using PAnim = XEngine.Runtime.AnimationClip;

namespace XEngine.Runtime.AssetImporting;

/// <summary>
/// Chooses which animation component a model root gets, based on the Rig-page Animation Type.
/// Generic / Humanoid → <see cref="Animator"/> (controller left empty, <see cref="Animator.Clip"/>
/// is the first imported clip). Legacy → <see cref="AnimationComponent"/> + clip list.
/// Models with no clips get nothing.
/// </summary>
public static class ModelRigBinding
{
    public const int Generic = 0;
    public const int Humanoid = 1;
    public const int Legacy = 2;

    public static int NormalizeAnimationType(int animationType)
        => animationType is Generic or Humanoid or Legacy ? animationType : Generic;

    public static void Attach(GameObject rootGO, List<PAnim> animations, int animationType)
    {
        if (rootGO == null || animations == null || animations.Count == 0)
            return;

        int type = NormalizeAnimationType(animationType);
        var first = new AssetRef<PAnim>(animations[0]);
        if (type == Legacy)
        {
            var anim = rootGO.AddComponent<AnimationComponent>();
            anim.DefaultClip = first;
            var clipRefs = new List<AssetRef<PAnim>>(animations.Count);
            for (int c = 0; c < animations.Count; c++)
                clipRefs.Add(new AssetRef<PAnim>(animations[c]));
            anim.Clips = clipRefs;
            return;
        }

        // Generic and Humanoid both attach Animator. Humanoid clips should be name-remapped
        // via HumanoidRetarget / clip_retarget so bone names match this rig.
        var animator = rootGO.AddComponent<Animator>();
        animator.Clip = first;
    }
}
