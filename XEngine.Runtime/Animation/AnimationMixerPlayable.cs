// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Playables;
using XEngine;

namespace XEngine.Animation;

/// <summary>
/// Blends N <see cref="AnimationPlayable"/> inputs into one output <see cref="Pose"/> by normalized
/// weight. Each child's <see cref="Playable.Weight"/> (driven by <see cref="Playable.StartFade"/>
/// for crossfades) becomes its contribution share. Children that produce no pose this frame or have
/// zero/negative weight are skipped; the rest are renormalized so their weights sum to 1.
/// </summary>
/// <remarks>
/// ProcessFrame runs post-order, so every child's <see cref="AnimationPlayable.Output"/> is already
/// sampled when the mixer blends. Bones no active child drives keep
/// <see cref="PoseChannel.None"/> and are skipped on output (retaining gameplay-written transforms).
/// </remarks>
public sealed class AnimationMixerPlayable : AnimationPlayable
{
    private readonly List<AnimationPlayable> _inputs = new();

    public override int InputCount => _inputs.Count;

    public override Playable GetInput(int index) => _inputs[index];

    /// <summary>Allocates the output pose against <paramref name="binding"/>. Add inputs separately via <see cref="AddInput"/>.</summary>
    public void Bind(SkeletonBinding binding)
    {
        Binding = binding;
        AllocateOutput();
    }

    public void AddInput(AnimationPlayable child) => _inputs.Add(child);

    public void RemoveInput(AnimationPlayable child) => _inputs.Remove(child);

    public void ClearInputs() => _inputs.Clear();

    [HotPath]
    public override void ProcessFrame(PlayableContext ctx)
    {
        Pose? pose = Output;
        if (pose == null)
            return;
        pose.Clear();

        int n = _inputs.Count;
        if (n == 0)
            return;

        // Sum positive-weight children that produced a pose this frame.
        float totalWeight = 0f;
        for (int i = 0; i < n; i++)
        {
            AnimationPlayable c = _inputs[i];
            if (c.Output != null && c.Weight > 0f)
                totalWeight += c.Weight;
        }

        if (totalWeight <= 0f)
            return;

        float invTotal = 1f / totalWeight;
        for (int i = 0; i < n; i++)
        {
            AnimationPlayable c = _inputs[i];
            Pose? childPose = c.Output;
            if (childPose == null || c.Weight <= 0f)
                continue;
            pose.Accumulate(childPose, c.Weight * invTotal);
        }

        pose.NormalizeRotations();
    }
}
