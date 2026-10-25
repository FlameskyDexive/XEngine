// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

using XEngine.Playables;
using XEngine.Vector;

namespace XEngine.Animation.Jobs;

/// <summary>
/// The per-frame context handed to a pose job: the skeleton the pose belongs to and the frame's
/// delta time, plus FK helpers that resolve bone world transforms from the pose being processed
/// (ancestors outside the pose's driven channels read the live Transforms).
/// </summary>
public readonly struct AnimationJobContext
{
    public SkeletonBinding Binding { get; init; }
    public float DeltaTime { get; init; }

    /// <summary>
    /// World position/rotation of <paramref name="boneIndex"/> as the pose would place it: walks the
    /// ancestor chain multiplying pose local TRS (falling back to live Transform values for bones
    /// the pose does not drive), anchored at the skeleton root's current world transform.
    /// </summary>
    public void ComputeBoneWorld(Pose pose, int boneIndex, out Float3 position, out Quaternion rotation)
    {
        Transform?[] bones = Binding.Bones;
        if ((uint)boneIndex >= (uint)bones.Length)
        {
            position = Float3.Zero;
            rotation = Quaternion.Identity;
            return;
        }

        // Collect the chain root→bone into the shared scratch (main-thread jobs only).
        int[] chain = SharedChain;
        int depth = 0;
        int current = boneIndex;
        while (current >= 0 && depth < MaxChainDepth)
        {
            chain[depth++] = current;
            current = ParentIndexOf(current);
        }

        // Seed with the first ancestor outside the binding (or the root itself), then walk down.
        Transform? top = depth > 0 ? bones[chain[depth - 1]]?.Parent : null;
        Quaternion worldRotation = top?.Rotation ?? Quaternion.Identity;
        Float3 worldPosition = top?.Position ?? Float3.Zero;

        for (int i = depth - 1; i >= 0; i--)
        {
            int idx = chain[i];
            ref BonePose bp = ref pose.Bones[idx];
            bool driven = (bp.Channels & PoseChannel.Rotation) != 0;
            Quaternion localRot = driven ? bp.LocalRotation : bones[idx]!.LocalRotation;
            bool drivenPos = (bp.Channels & PoseChannel.Position) != 0;
            Float3 localPos = drivenPos ? bp.LocalPosition : bones[idx]!.LocalPosition;

            // The local position lives in the PARENT's frame — transform before folding the bone's
            // own rotation into the accumulator.
            worldPosition = worldPosition + worldRotation * localPos;
            worldRotation = worldRotation * localRot;
        }

        position = worldPosition;
        rotation = worldRotation;
    }

    /// <summary>World rotation of <paramref name="boneIndex"/>'s PARENT (for local↔world conversion).</summary>
    public void ComputeParentWorld(Pose pose, int boneIndex, out Quaternion parentRotation)
    {
        int parent = ParentIndexOf(boneIndex);
        if (parent < 0)
        {
            Transform? ancestor = Binding.Bones[boneIndex]?.Parent;
            parentRotation = ancestor?.Rotation ?? Quaternion.Identity;
            return;
        }
        ComputeBoneWorld(pose, parent, out _, out parentRotation);
    }

    private int ParentIndexOf(int boneIndex)
    {
        int[] parents = Parents;
        return parents[boneIndex];
    }

    internal int[] Parents => AnimationJobFk.GetParentMap(Binding);

    private const int MaxChainDepth = 96;
    [ThreadStatic] private static int[]? _sharedChain;
    internal int[] SharedChain => _sharedChain ??= new int[MaxChainDepth];
}

/// <summary>One procedural pose pass: mutate the pose in place after the input produced it.</summary>
public interface IAnimationPoseJob
{
    /// <summary>Processes <paramref name="pose"/> (already copied from the input's output). Hot path.</summary>
    void ProcessPose(Pose pose, in AnimationJobContext context);
}

/// <summary>
/// M56 — a single-input pose post-processing node (Unity <c>AnimationScriptPlayable</c> equivalent):
/// the child's evaluated pose is copied into this node's output, then the job mutates it. Compose
/// chains like clip → job → mixer/output; jobs themselves are structs or classes with pooled state
/// (steady state must be allocation-free).
/// </summary>
public sealed class AnimationJobPlayable<TJob> : AnimationPlayable where TJob : IAnimationPoseJob
{
    /// <summary>The job instance (set up before playing; mutated fields apply next frame).</summary>
    public TJob Job;

    private AnimationPlayable? _input;

    public AnimationJobPlayable(TJob job) => Job = job;

    public override int InputCount => _input != null ? 1 : 0;

    public override Playable GetInput(int index) => index == 0 && _input != null ? _input : base.GetInput(index);

    /// <summary>Sets (or replaces) the single input whose output this job processes.</summary>
    public void SetInput(AnimationPlayable input) => _input = input;

    /// <summary>Allocates the output pose against <paramref name="binding"/>.</summary>
    public void Bind(SkeletonBinding binding)
    {
        Binding = binding;
        AllocateOutput();
    }

    [HotPath]
    public override void ProcessFrame(PlayableContext ctx)
    {
        Pose? output = Output;
        Pose? inputPose = _input?.Output;
        if (output == null || inputPose == null)
            return;

        output.CopyFrom(inputPose);
        Job.ProcessPose(output, new AnimationJobContext { Binding = Binding!, DeltaTime = ctx.DeltaTime });
        output.NormalizeRotations();
    }
}

/// <summary>Lazy per-binding parent-index maps for the FK walks (main-thread jobs).</summary>
internal static class AnimationJobFk
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SkeletonBinding, int[]> Cache = new();

    public static int[] GetParentMap(SkeletonBinding binding)
    {
        return Cache.GetValue(binding, static b =>
        {
            var bones = b.Bones;
            var parents = new int[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                parents[i] = -1;
                Transform? p = bones[i]?.Parent;
                if (p == null)
                    continue;
                for (int j = 0; j < bones.Length; j++)
                {
                    if (j != i && ReferenceEquals(bones[j], p))
                    {
                        parents[i] = j;
                        break;
                    }
                }
            }
            return parents;
        });
    }
}
