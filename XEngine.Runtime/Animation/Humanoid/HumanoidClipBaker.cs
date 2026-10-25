// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;
using AnimationCurve = XEngine.Runtime.AnimationCurve;
using XEngine.Runtime.Resources;

namespace XEngine.Animation.Humanoid;

/// <summary>One baked muscle channel: a role + channel index + its float curve.</summary>
public class MuscleCurve
{
    /// <summary>The humanoid role this channel drives.</summary>
    public HumanoidBone Bone;

    /// <summary>0/1/2 = rotation muscle X/Y/Z; 3/4/5 = Hips TDoF translation X/Y/Z (meters).</summary>
    public int Channel;

    /// <summary>The sampled curve (normalized muscle values, or meters for TDoF).</summary>
    public AnimationCurve Curve = new();

    public float Evaluate(float time) => Curve?.Evaluate(time) ?? 0f;
}

/// <summary>
/// M54 — converts an imported clip's mapped humanoid bone curves into muscle curves (Mecanim
/// humanoid-clip bake): each mapped bone's animated local rotation is sampled at its own key times
/// and re-expressed as T-pose-relative normalized angles; Hips also contributes TDoF translation
/// channels. Unmapped bones (weapons, cloth, props), blend shapes, generic curves, events and the
/// source reference pose are preserved untouched; mapped bone curves are removed from
/// <see cref="AnimationClip.Bones"/> (roles replace paths). The result plays on ANY avatar exposing
/// the same roles (M55).
/// </summary>
public static class HumanoidClipBaker
{
    /// <summary>
    /// Bakes <paramref name="clip"/> against <paramref name="avatar"/>. Returns the number of muscle
    /// channels written (0 when no mapped bone was present — the clip is left untouched).
    /// </summary>
    public static int Bake(AnimationClip clip, Avatar avatar)
    {
        if (!avatar.IsHumanoid)
            throw new System.ArgumentException("Humanoid baking requires a Humanoid Avatar.", nameof(avatar));
        var mappings = avatar.Mappings;
        var muscles = new List<MuscleCurve>();
        var mappedBoneNames = new HashSet<string>();

        for (int mi = 0; mi < mappings.Count; mi++)
        {
            Avatar.BoneMapping mapping = mappings[mi];
            // Real imported clips key bones by full path; synthetic/test clips may use bare leaf
            // names — fall back to the leaf when the exact path misses.
            AnimationClip.AnimBone? bone = clip.GetBone(mapping.Path);
            if (bone == null)
            {
                int slash = mapping.Path.LastIndexOf('/');
                if (slash >= 0)
                    bone = clip.GetBone(mapping.Path[(slash + 1)..]);
            }
            if (bone == null)
                continue; // This rig's clip doesn't animate that role.
            mappedBoneNames.Add(bone.BoneName);

            BakeRotation(bone, mapping, muscles);
            if (mapping.Bone == HumanoidBone.Hips)
                BakeHipsTDoF(bone, mapping, muscles);
        }

        if (muscles.Count == 0)
            return 0;

        // Drop the mapped bones; roles replace their curves. Keep everything else verbatim.
        var remaining = new List<AnimationClip.AnimBone>();
        var bones = clip.Bones;
        for (int i = 0; i < bones.Count; i++)
            if (!mappedBoneNames.Contains(bones[i].BoneName))
                remaining.Add(bones[i]);

        clip.Bones.Clear();
        for (int i = 0; i < remaining.Count; i++)
            clip.AddBone(remaining[i]);
        clip.RebuildBoneMap(); // Drop stale name→bone entries for the removed mapped bones.

        clip.MuscleCurves.Clear();
        clip.MuscleCurves.AddRange(muscles);
        clip.IsHumanoid = true;
        clip.HumanoidSourceLegLength = avatar.LegLength; // Retarget TDoF rescale anchor.
        if (!string.IsNullOrEmpty(clip.RootBonePath) && mappedBoneNames.Contains(clip.RootBonePath))
            clip.RootBonePath = null; // Root trajectory now lives in the Hips TDoF channels.
        return muscles.Count;
    }

    private static void BakeRotation(AnimationClip.AnimBone bone, Avatar.BoneMapping mapping, List<MuscleCurve> muscles)
    {
        // Union of the rotation curves' key times, deduped and sorted.
        var times = new List<double>();
        CollectKeyTimes(bone.RotX, times);
        CollectKeyTimes(bone.RotY, times);
        CollectKeyTimes(bone.RotZ, times);
        CollectKeyTimes(bone.RotW, times);
        if (times.Count == 0)
            return;
        times.Sort();

        var x = new List<KeyFrame>();
        var y = new List<KeyFrame>();
        var z = new List<KeyFrame>();
        double last = double.NegativeInfinity;
        for (int i = 0; i < times.Count; i++)
        {
            double t = times[i];
            if (t - last < 1e-6)
                continue; // dedupe near-identical stamps across the component curves.
            last = t;

            Quaternion q = bone.EvaluateRotationAt((float)t);
            Float3 degrees = HumanoidMuscleSpace.RotationMuscles(mapping.LocalRotation, q);
            Float3 muscle = HumanoidMuscleSpace.NormalizeAngles(mapping.Bone, degrees);
            x.Add(new KeyFrame((float)t, muscle.X));
            y.Add(new KeyFrame((float)t, muscle.Y));
            z.Add(new KeyFrame((float)t, muscle.Z));
        }

        AddMuscle(muscles, mapping.Bone, 0, x);
        AddMuscle(muscles, mapping.Bone, 1, y);
        AddMuscle(muscles, mapping.Bone, 2, z);
    }

    private static void BakeHipsTDoF(AnimationClip.AnimBone bone, Avatar.BoneMapping mapping, List<MuscleCurve> muscles)
    {
        if (bone.PosX == null && bone.PosY == null && bone.PosZ == null)
            return;

        var times = new List<double>();
        CollectKeyTimes(bone.PosX, times);
        CollectKeyTimes(bone.PosY, times);
        CollectKeyTimes(bone.PosZ, times);
        if (times.Count == 0)
            return;
        times.Sort();

        var x = new List<KeyFrame>();
        var y = new List<KeyFrame>();
        var z = new List<KeyFrame>();
        double last = double.NegativeInfinity;
        for (int i = 0; i < times.Count; i++)
        {
            double t = times[i];
            if (t - last < 1e-6)
                continue;
            last = t;

            Float3 pos = bone.EvaluatePositionAt((float)t);
            Float3 delta = pos - mapping.LocalPosition; // T-pose-relative translation.
            x.Add(new KeyFrame((float)t, delta.X));
            y.Add(new KeyFrame((float)t, delta.Y));
            z.Add(new KeyFrame((float)t, delta.Z));
        }

        AddMuscle(muscles, HumanoidBone.Hips, 3, x);
        AddMuscle(muscles, HumanoidBone.Hips, 4, y);
        AddMuscle(muscles, HumanoidBone.Hips, 5, z);
    }

    private static void AddMuscle(List<MuscleCurve> muscles, HumanoidBone bone, int channel, List<KeyFrame> keys)
    {
        if (keys.Count == 0)
            return;
        var curve = new AnimationCurve(keys.ToArray());
        curve.SmoothTangents(CurveTangent.Linear);
        muscles.Add(new MuscleCurve { Bone = bone, Channel = channel, Curve = curve });
    }

    private static void CollectKeyTimes(AnimationCurve? curve, List<double> times)
    {
        if (curve == null)
            return;
        int n = curve.Keys.Count;
        for (int i = 0; i < n; i++)
            times.Add(curve.Keys[i].Position);
    }
}
