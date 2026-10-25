// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.Animation.Jobs;

/// <summary>
/// M56 — analytic two-bone IK (root/mid/end, e.g. arm or leg): rotates the two driven bones so the
/// end effector reaches <see cref="TargetPosition"/> (world space). Unreachable targets stretch the
/// chain straight toward them; <see cref="Weight"/> blends between the animated and solved end
/// position; <see cref="HintPosition"/> (world) picks the bend plane like Unity's IK hint.
/// </summary>
public struct TwoBoneIKJob : IAnimationPoseJob
{
    /// <summary>Binding index of the chain root (e.g. LeftUpperArm).</summary>
    public int RootBone;

    /// <summary>Binding index of the middle joint (e.g. LeftLowerArm).</summary>
    public int MidBone;

    /// <summary>Binding index of the end effector (e.g. LeftHand).</summary>
    public int EndBone;

    /// <summary>World-space position the end effector should reach.</summary>
    public Float3 TargetPosition;

    /// <summary>World-space bend hint (pole); only used when <see cref="HasHint"/>.</summary>
    public Float3 HintPosition;

    /// <summary>Whether <see cref="HintPosition"/> drives the bend plane.</summary>
    public bool HasHint;

    /// <summary>Blend between the animated pose (0) and the solved pose (1).</summary>
    public float Weight;

    public void ProcessPose(Pose pose, in AnimationJobContext ctx)
    {
        if (Weight <= 0f)
            return;
        float w = Weight > 1f ? 1f : Weight;

        ctx.ComputeBoneWorld(pose, RootBone, out Float3 rootPos, out Quaternion rootRot);
        ctx.ComputeBoneWorld(pose, MidBone, out Float3 midPos, out _);
        ctx.ComputeBoneWorld(pose, EndBone, out Float3 endPos, out _);

        float a = Float3.Distance(rootPos, midPos);  // root→mid rest length (scales keep ~constant).
        float b = Float3.Distance(midPos, endPos);   // mid→end.
        if (a < 1e-6f || b < 1e-6f)
            return;

        // Blend the goal by weight, then clamp it into the reachable annulus around the root.
        Float3 goal = endPos + (TargetPosition - endPos) * w;
        Float3 toGoal = goal - rootPos;
        float d = Float3.Length(toGoal);
        float maxReach = a + b - 1e-4f;
        float minReach = MathF.Abs(a - b) + 1e-4f;
        if (d > maxReach)
        {
            toGoal = toGoal * (maxReach / d);
            d = maxReach;
        }
        else if (d < minReach)
        {
            toGoal = d > 1e-6f ? toGoal * (minReach / d) : Float3.UnitY * minReach;
            d = minReach;
        }
        Float3 rootDir = d > 1e-6f ? toGoal / d : Float3.UnitY;

        // Bend plane normal: from the hint when given, else preserve the current bend.
        Float3 normal;
        if (HasHint)
            normal = Float3.Cross(rootDir, HintPosition - rootPos);
        else
            normal = Float3.Cross(rootDir, midPos - rootPos);
        float normalLen = Float3.Length(normal);
        normal = normalLen > 1e-6f ? normal / normalLen : Float3.Cross(rootDir, Float3.UnitX);

        Float3 bendSide = Float3.Cross(normal, rootDir);
        float bendLen = Float3.Length(bendSide);
        bendSide = bendLen > 1e-6f ? bendSide / bendLen : Float3.UnitX;

        // Law of cosines at the root joint: direction toward the solved middle joint.
        float cosRoot = Maths.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
        float sinRoot = MathF.Sqrt(MathF.Max(0f, 1f - cosRoot * cosRoot));
        Float3 midDir = rootDir * cosRoot + bendSide * sinRoot;
        Float3 desiredMid = rootPos + midDir * a;

        // Rotate the root so its child direction points at the solved middle joint.
        if (!RotateBoneToward(pose, ctx, RootBone, rootPos, midPos, desiredMid))
            return;

        // Rotate the middle joint so the end effector lands on the goal ray.
        ctx.ComputeBoneWorld(pose, MidBone, out Float3 midPos2, out Quaternion midRot2);
        ctx.ComputeBoneWorld(pose, EndBone, out Float3 endPos2, out _);
        Float3 desiredEndDir = goal - midPos2;
        if (Float3.LengthSquared(desiredEndDir) < 1e-12f)
            return;
        desiredEndDir = Float3.Normalize(desiredEndDir);
        Float3 currentEndDir = endPos2 - midPos2;
        if (Float3.LengthSquared(currentEndDir) < 1e-12f)
            return;
        currentEndDir = Float3.Normalize(currentEndDir);

        Quaternion midDelta = FromToRotation(currentEndDir, desiredEndDir);
        if (!IsFinite(midDelta))
            return;
        Quaternion newMidWorld = midDelta * midRot2;
        ctx.ComputeParentWorld(pose, MidBone, out Quaternion midParentWorld);
        pose.Bones[MidBone].LocalRotation = Quaternion.Inverse(midParentWorld) * newMidWorld;
        pose.Bones[MidBone].Channels |= PoseChannel.Rotation;
    }

    /// <summary>Rotates <paramref name="boneIndex"/> so the point <paramref name="currentChild"/>
    /// moves to <paramref name="desiredChild"/> (both world). False when the solve is degenerate.</summary>
    private static bool RotateBoneToward(
        Pose pose, in AnimationJobContext ctx,
        int boneIndex, Float3 bonePos, Float3 currentChild, Float3 desiredChild)
    {
        ctx.ComputeBoneWorld(pose, boneIndex, out _, out Quaternion worldRot);
        Float3 current = currentChild - bonePos;
        Float3 desired = desiredChild - bonePos;
        float cl = Float3.Length(current);
        float dl = Float3.Length(desired);
        if (cl < 1e-6f || dl < 1e-6f)
            return false;
        Quaternion delta = FromToRotation(current / cl, desired / dl);
        if (!IsFinite(delta))
            return false;
        Quaternion newWorld = delta * worldRot;
        ctx.ComputeParentWorld(pose, boneIndex, out Quaternion parentWorld);
        pose.Bones[boneIndex].LocalRotation = Quaternion.Inverse(parentWorld) * newWorld;
        pose.Bones[boneIndex].Channels |= PoseChannel.Rotation;
        return true;
    }

    /// <summary>Shortest-arc rotation from one unit vector to another.</summary>
    public static Quaternion FromToRotation(Float3 from, Float3 to)
    {
        float dot = Float3.Dot(from, to);
        if (dot > 1f - 1e-6f)
            return Quaternion.Identity;
        if (dot < -1f + 1e-6f)
        {
            Float3 axis = MathF.Abs(from.X) < 0.9f ? Float3.UnitX : Float3.UnitY;
            Float3 perp = Float3.Normalize(Float3.Cross(from, axis));
            return new Quaternion(perp.X, perp.Y, perp.Z, 0f); // 180° about a perpendicular axis.
        }
        Float3 cross = Float3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 1f + dot));
    }

    /// <summary>Quaternion power (unit quaternions): q^t via axis-angle.</summary>
    public static Quaternion Pow(Quaternion q, float t)
    {
        q = Quaternion.Normalize(q);
        float angle = 2f * MathF.Atan2(MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z), MathF.Abs(q.W));
        if (angle < 1e-6f)
            return Quaternion.Identity;
        float half = angle * 0.5f * t;
        float s = MathF.Sin(half) / MathF.Sin(angle * 0.5f);
        float sign = q.W < 0f ? -1f : 1f;
        return new Quaternion(q.X * s, q.Y * s, q.Z * s, MathF.Cos(half) * sign);
    }

    private static bool IsFinite(Quaternion q)
        => q.X == q.X && q.Y == q.Y && q.Z == q.Z && q.W == q.W && MathF.Abs(q.W) < 1f + 1e-3f;
}

/// <summary>
/// M56 — distributes a look-at rotation across a bone chain (head/neck): each bone applies the
/// weight-blended rotation's n-th root, composing (top-down) to aim the head's forward axis (+Z)
/// at <see cref="Target"/>.
/// </summary>
public struct LookAtJob : IAnimationPoseJob
{
    /// <summary>Chain bone indices, outermost (head) LAST; pooled by the caller.</summary>
    public int[] ChainBones;

    /// <summary>Chain length in <see cref="ChainBones"/> (allows oversized pooled arrays).</summary>
    public int ChainCount;

    /// <summary>World-space look target.</summary>
    public Float3 Target;

    /// <summary>Blend weight.</summary>
    public float Weight;

    public void ProcessPose(Pose pose, in AnimationJobContext ctx)
    {
        int n = ChainCount;
        if (n <= 0 || Weight <= 0f)
            return;
        float w = Weight > 1f ? 1f : Weight;

        int head = ChainBones[n - 1];
        ctx.ComputeBoneWorld(pose, head, out Float3 headPos, out Quaternion headRot);

        Float3 forward = headRot * Float3.UnitZ;
        Float3 toTarget = Target - headPos;
        float dist = Float3.Length(toTarget);
        if (dist < 1e-6f)
            return;
        Quaternion full = TwoBoneIKJob.FromToRotation(forward, toTarget / dist);
        if (full.X != full.X)
            return;

        Quaternion perBone = TwoBoneIKJob.Pow(Quaternion.Slerp(Quaternion.Identity, full, w), 1f / n);

        for (int i = 0; i < n; i++)
        {
            int idx = ChainBones[i];
            ctx.ComputeBoneWorld(pose, idx, out _, out Quaternion worldRot);
            Quaternion newWorld = perBone * worldRot;
            ctx.ComputeParentWorld(pose, idx, out Quaternion parentWorld);
            pose.Bones[idx].LocalRotation = Quaternion.Inverse(parentWorld) * newWorld;
            pose.Bones[idx].Channels |= PoseChannel.Rotation;
        }
    }
}

/// <summary>
/// M56 — critically-damped smoothing of bone local rotations (Unity's Damping job equivalent):
/// rotations ease toward the input with time constant <see cref="DampingSeconds"/>. Stateful job;
/// buffers scale on first use and are reused afterwards (zero-alloc steady state).
/// </summary>
public sealed class DampingJob : IAnimationPoseJob
{
    /// <summary>Time constant in seconds; larger = softer.</summary>
    public float DampingSeconds = 0.1f;

    private Quaternion[] _smoothed = Array.Empty<Quaternion>();
    private bool[] _initialized = Array.Empty<bool>();

    public void ProcessPose(Pose pose, in AnimationJobContext context)
    {
        BonePose[] bones = pose.Bones;
        int n = bones.Length;
        if (_smoothed.Length < n)
        {
            _smoothed = new Quaternion[n];
            _initialized = new bool[n];
        }

        float alpha = DampingSeconds > 1e-5f
            ? 1f - MathF.Exp(-context.DeltaTime / DampingSeconds)
            : 1f;

        for (int i = 0; i < n; i++)
        {
            ref BonePose bp = ref bones[i];
            if ((bp.Channels & PoseChannel.Rotation) == 0)
                continue;
            if (!_initialized[i])
            {
                _smoothed[i] = bp.LocalRotation;
                _initialized[i] = true;
                continue;
            }
            _smoothed[i] = Quaternion.Slerp(_smoothed[i], bp.LocalRotation, alpha);
            bp.LocalRotation = _smoothed[i];
        }
    }
}
