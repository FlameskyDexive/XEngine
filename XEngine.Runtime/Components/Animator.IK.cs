// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Animation;
using XEngine.Animation.Humanoid;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>IK end effectors (Unity AvatarIKGoal parity; layer 0 only, like Unity Playables).</summary>
public enum AvatarIKGoal : byte
{
    LeftFoot = 0,
    RightFoot = 1,
    LeftHand = 2,
    RightHand = 3,
}

/// <summary>IK bend hints (knees / elbows).</summary>
public enum AvatarIKHint : byte
{
    LeftKnee = 0,
    RightKnee = 1,
    LeftElbow = 2,
    RightElbow = 3,
}

public partial class Animator
{
    private struct IkGoal
    {
        public Float3 Position;
        public Quaternion Rotation;
        public float PositionWeight;
        public float RotationWeight;
        public bool HasHint;
        public Float3 HintPosition;
    }

    private static readonly IkGoal[] EmptyGoals = new IkGoal[4];
    private IkGoal[]? _ikGoals;
    private Float3 _lookAtTarget;
    private float _lookAtWeight;

    /// <summary>
    /// M57 — fired at the end of LateUpdate, AFTER the graph applied its pose and BEFORE the IK pass
    /// resolves goals: handlers call <c>SetIKPosition/SetIKRotation/SetLookAt*</c> here (Unity
    /// <c>OnAnimatorIK</c> parity; layer index is always 0, matching Unity's Playables limitation).
    /// </summary>
    [NonSerialized] public Action<int>? OnAnimatorIK;

    /// <summary>
    /// M57 — automatic foot planting (<c>IKOnFeeet</c>): the lower foot samples the ground each frame
    /// and the hips shift so it plants. The ground source is <see cref="GroundSample"/> (probe →
    /// ground point, null = none); wire the physics raycast or a gameplay sampler. Off by default.
    /// </summary>
    public bool FootIK;

    /// <summary>Ground sampler for <see cref="FootIK"/>: (probe position) → ground point, or null.</summary>
    [NonSerialized] public Func<Float3, Float3?>? GroundSample;

    /// <summary>Sets the world-space IK goal position for <paramref name="goal"/>.</summary>
    public void SetIKPosition(AvatarIKGoal goal, Float3 position)
    {
        ref IkGoal g = ref EnsureGoal((int)goal);
        g.Position = position;
    }

    /// <summary>Sets the world-space IK goal rotation for <paramref name="goal"/>.</summary>
    public void SetIKRotation(AvatarIKGoal goal, Quaternion rotation)
    {
        ref IkGoal g = ref EnsureGoal((int)goal);
        g.Rotation = rotation;
    }

    /// <summary>Blends the goal position effect (0 = animated pose, 1 = fully solved).</summary>
    public void SetIKPositionWeight(AvatarIKGoal goal, float weight)
    {
        ref IkGoal g = ref EnsureGoal((int)goal);
        g.PositionWeight = weight;
    }

    /// <summary>Blends the goal rotation effect.</summary>
    public void SetIKRotationWeight(AvatarIKGoal goal, float weight)
    {
        ref IkGoal g = ref EnsureGoal((int)goal);
        g.RotationWeight = weight;
    }

    /// <summary>Sets the world-space bend hint (pole) for the goal's chain (knees→feet, elbows→hands).</summary>
    public void SetIKHintPosition(AvatarIKHint hint, Float3 position)
    {
        AvatarIKGoal goal = hint switch
        {
            AvatarIKHint.LeftKnee => AvatarIKGoal.LeftFoot,
            AvatarIKHint.RightKnee => AvatarIKGoal.RightFoot,
            AvatarIKHint.LeftElbow => AvatarIKGoal.LeftHand,
            _ => AvatarIKGoal.RightHand,
        };
        ref IkGoal g = ref EnsureGoal((int)goal);
        g.HasHint = true;
        g.HintPosition = position;
    }

    /// <summary>Sets the world-space look-at target.</summary>
    public void SetLookAtPosition(Float3 position) => _lookAtTarget = position;

    /// <summary>Blends the look-at effect (0 = animated, 1 = fully aimed).</summary>
    public void SetLookAtWeight(float weight) => _lookAtWeight = weight;

    private ref IkGoal EnsureGoal(int index)
    {
        _ikGoals ??= new IkGoal[4];
        return ref _ikGoals[index];
    }

    /// <summary>
    /// M57 — the IK pass: fires <see cref="OnAnimatorIK"/>, then (with a humanoid avatar bound)
    /// solves hand/foot goals (two-bone chains with optional hints, reusing the M56 solve math on
    /// the applied Transforms), the head look-at, and the foot plant. Runs AFTER the graph applied
    /// and root motion was handled — the renderer's lazy <c>Transform.Version</c> re-skin picks the
    /// edited bones up at draw time.
    /// </summary>
    private void RunIKPass()
    {
        OnAnimatorIK?.Invoke(0);

        XEngine.Animation.Humanoid.Avatar? avatar = Avatar.Res;
        IkGoal[]? goals = _ikGoals;
        bool anyGoal = false;
        if (goals != null)
        {
            for (int i = 0; i < 4; i++)
                if (goals[i].PositionWeight > 0f || goals[i].RotationWeight > 0f)
                    anyGoal = true;
        }

        if (avatar != null && avatar.IsHumanoid)
        {
            if (anyGoal || _bodyPositionWeight > 0f || _bodyRotationWeight > 0f)
            {
                // M58 — the full-body pass runs first so the limb solves anchor to the moved body.
                SolveFullBody(avatar, goals ?? EmptyGoals);
            }
            if (anyGoal)
            {
                SolveGoal(avatar, goals!, AvatarIKGoal.LeftHand, HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand);
                SolveGoal(avatar, goals!, AvatarIKGoal.RightHand, HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand);
                SolveGoal(avatar, goals!, AvatarIKGoal.LeftFoot, HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot);
                SolveGoal(avatar, goals!, AvatarIKGoal.RightFoot, HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot);
            }
            if (_lookAtWeight > 0f)
                SolveLookAt(avatar);
            if (FootIK && GroundSample != null)
                SolveFootPlant(avatar);
        }
    }

    private void SolveGoal(Avatar avatar, IkGoal[] goals, AvatarIKGoal goalSlot, HumanoidBone rootRole, HumanoidBone midRole, HumanoidBone endRole)
    {
        IkGoal goal = goals[(int)goalSlot];
        if (goal.PositionWeight <= 0f && goal.RotationWeight <= 0f)
            return;
        SkeletonBinding? binding = _binding;
        if (binding == null)
            return;
        if (!avatar.TryGetPath(rootRole, out string rootPath)
            || !avatar.TryGetPath(midRole, out string midPath)
            || !avatar.TryGetPath(endRole, out string endPath))
            return;

        int rootIdx = binding.IndexOfBonePath(rootPath);
        int midIdx = binding.IndexOfBonePath(midPath);
        int endIdx = binding.IndexOfBonePath(endPath);
        if (rootIdx < 0 || midIdx < 0 || endIdx < 0)
            return;

        Transform? rootBone = binding.Bones[rootIdx];
        Transform? midBone = binding.Bones[midIdx];
        Transform? endBone = binding.Bones[endIdx];
        if (rootBone == null || midBone == null || endBone == null)
            return;

        // Position solve: two-bone math on the live world transforms. Two iterations converge
        // the coupled root/mid rotations (first pass positions the joint, second tightens the end).
        if (goal.PositionWeight > 0f)
        {
            // Blend the goal ONCE from the animated end position — iterations refine the pose
            // toward this fixed target instead of compounding the weight.
            Float3 animatedEnd = endBone.Position;
            Float3 ikTarget = animatedEnd + (goal.Position - animatedEnd) * Maths.Clamp(goal.PositionWeight, 0f, 1f);
            for (int iteration = 0; iteration < 2; iteration++)
            {
            Float3 rootPos = rootBone.Position;
            Float3 midPos = midBone.Position;
            Float3 endPos = endBone.Position;
            float a = Float3.Distance(rootPos, midPos);
            float b = Float3.Distance(midPos, endPos);
            if (a > 1e-6f && b > 1e-6f)
            {
                Float3 toGoal = ikTarget - rootPos;
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

                Float3 normal = goal.HasHint
                    ? Float3.Cross(rootDir, goal.HintPosition - rootPos)
                    : Float3.Cross(rootDir, midPos - rootPos);
                float normalLen = Float3.Length(normal);
                normal = normalLen > 1e-6f ? normal / normalLen : Float3.Cross(rootDir, Float3.UnitX);
                Float3 bendSide = Float3.Cross(normal, rootDir);
                float bendLen = Float3.Length(bendSide);
                bendSide = bendLen > 1e-6f ? bendSide / bendLen : Float3.UnitX;

                float cosRoot = Maths.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f);
                float sinRoot = MathF.Sqrt(MathF.Max(0f, 1f - cosRoot * cosRoot));
                Float3 desiredMid = rootPos + (rootDir * cosRoot + bendSide * sinRoot) * a;

                RotateTransformToward(rootBone, midPos, desiredMid);

                // Aim the middle joint at the goal ray (recomputed against the moved root).
                Float3 midPos2 = midBone.Position;
                Float3 endPos2 = endBone.Position;
                Float3 desiredEndDir = ikTarget - midPos2;
                if (Float3.LengthSquared(desiredEndDir) > 1e-12f
                    && Float3.LengthSquared(endPos2 - midPos2) > 1e-12f)
                {
                    desiredEndDir = Float3.Normalize(desiredEndDir);
                    Float3 currentDir = Float3.Normalize(endPos2 - midPos2);
                    Quaternion delta = XEngine.Animation.Jobs.TwoBoneIKJob.FromToRotation(currentDir, desiredEndDir);
                    if (delta.X == delta.X)
                        ApplyWorldRotation(midBone, delta * midBone.Rotation);
                }
            }
          }
        }

        // Rotation solve: blend the end bone's world rotation toward the goal rotation.
        if (goal.RotationWeight > 0f)
        {
            Quaternion target = Quaternion.Slerp(endBone.Rotation, goal.Rotation, Maths.Clamp(goal.RotationWeight, 0f, 1f));
            ApplyWorldRotation(endBone, target);
        }
    }

    /// <summary>Rotates <paramref name="bone"/> so the world point <paramref name="currentChild"/>
    /// lands on <paramref name="desiredChild"/> (pole bend for the two-bone solve).</summary>
    private static void RotateTransformToward(Transform bone, Float3 currentChild, Float3 desiredChild)
    {
        Float3 current = currentChild - bone.Position;
        Float3 desired = desiredChild - bone.Position;
        float cl = Float3.Length(current);
        float dl = Float3.Length(desired);
        if (cl < 1e-6f || dl < 1e-6f)
            return;
        Quaternion delta = XEngine.Animation.Jobs.TwoBoneIKJob.FromToRotation(current / cl, desired / dl);
        if (delta.X != delta.X)
            return;
        ApplyWorldRotation(bone, delta * bone.Rotation);
    }

    /// <summary>Sets a world-space rotation by writing the matching local rotation.</summary>
    private static void ApplyWorldRotation(Transform bone, Quaternion worldRotation)
    {
        Transform? parent = bone.Parent;
        Quaternion parentWorld = parent != null ? parent.Rotation : Quaternion.Identity;
        bone.LocalRotation = Quaternion.Inverse(parentWorld) * worldRotation;
    }

    private void SolveLookAt(Avatar avatar)
    {
        SkeletonBinding? binding = _binding;
        if (binding == null)
            return;

        // Chain: Neck (when mapped) then Head.
        if (!avatar.TryGetPath(HumanoidBone.Head, out string headPath))
            return;
        int headIdx = binding.IndexOfBonePath(headPath);
        if (headIdx < 0)
            return;
        Transform? head = binding.Bones[headIdx];
        if (head == null)
            return;

        Float3 forward = head.Rotation * Float3.UnitZ;
        Float3 toTarget = _lookAtTarget - head.Position;
        float dist = Float3.Length(toTarget);
        if (dist < 1e-6f)
            return;
        Quaternion full = XEngine.Animation.Jobs.TwoBoneIKJob.FromToRotation(forward, toTarget / dist);
        if (full.X != full.X)
            return;

        Quaternion blend = Quaternion.Slerp(Quaternion.Identity, full, Maths.Clamp(_lookAtWeight, 0f, 1f));
        Quaternion perBone = XEngine.Animation.Jobs.TwoBoneIKJob.Pow(blend, 0.5f); // Neck + Head.

        if (avatar.TryGetPath(HumanoidBone.Neck, out string neckPath))
        {
            int neckIdx = binding.IndexOfBonePath(neckPath);
            if (neckIdx >= 0 && binding.Bones[neckIdx] != null)
                ApplyWorldRotation(binding.Bones[neckIdx]!, perBone * binding.Bones[neckIdx]!.Rotation);
        }
        ApplyWorldRotation(head, perBone * head.Rotation);
    }

    private void SolveFootPlant(Avatar avatar)
    {
        SkeletonBinding? binding = _binding;
        Func<Float3, Float3?>? sample = GroundSample;
        if (binding == null || sample == null)
            return;
        if (!avatar.TryGetPath(HumanoidBone.Hips, out string hipsPath))
            return;
        int hipsIdx = binding.IndexOfBonePath(hipsPath);
        if (hipsIdx < 0 || binding.Bones[hipsIdx] == null)
            return;

        float deepestPenetration = 0f;
        for (int side = 0; side < 2; side++)
        {
            HumanoidBone footRole = side == 0 ? HumanoidBone.LeftFoot : HumanoidBone.RightFoot;
            if (!avatar.TryGetPath(footRole, out string footPath))
                continue;
            int footIdx = binding.IndexOfBonePath(footPath);
            if (footIdx < 0 || binding.Bones[footIdx] == null)
                continue;
            Transform foot = binding.Bones[footIdx]!;

            Float3? ground = sample(foot.Position + Float3.UnitY * 0.5f);
            if (ground == null)
                continue;
            float penetration = ground.Value.Y - foot.Position.Y;
            if (penetration > deepestPenetration)
                deepestPenetration = penetration;
        }

        if (deepestPenetration > 1e-4f)
        {
            Transform hips = binding.Bones[hipsIdx]!;
            hips.LocalPosition += Float3.UnitY * deepestPenetration; // Raise the body so the sunken foot plants on the ground.
        }
    }

// ---- M58: full-body IK (puppet) ----

    /// <summary>World-space body (hips) target for full-body IK.</summary>
    public void SetBodyPosition(Float3 position) => _bodyPosition = position;

    /// <summary>Blends the hips toward <see cref="SetBodyPosition"/> (0 = animated, 1 = pinned).</summary>
    public void SetBodyPositionWeight(float weight) => _bodyPositionWeight = weight;

    /// <summary>World-space body (hips) orientation target.</summary>
    public void SetBodyRotation(Quaternion rotation) => _bodyRotation = rotation;

    /// <summary>Blends the hips orientation (distributed down the spine chain).</summary>
    public void SetBodyRotationWeight(float weight) => _bodyRotationWeight = weight;

    private Float3 _bodyPosition;
    private Quaternion _bodyRotation = Quaternion.Identity;
    private float _bodyPositionWeight;
    private float _bodyRotationWeight;

    /// <summary>
    /// M58 — full-body pass BEFORE the limb solves: pins/translates the hips toward the body target
    /// (position), drags the whole body toward limb goals that the arm/leg chains alone cannot reach
    /// (puppet pull, scaled by the body weight), and distributes the body orientation down the spine
    /// chain so the torso leans into the pose.
    /// </summary>
    private void SolveFullBody(Avatar avatar, IkGoal[] goals)
    {
        SkeletonBinding? binding = _binding;
        if (binding == null)
            return;
        if (!avatar.TryGetPath(HumanoidBone.Hips, out string hipsPath))
            return;
        int hipsIdx = binding.IndexOfBonePath(hipsPath);
        if (hipsIdx < 0 || binding.Bones[hipsIdx] == null)
            return;
        Transform hips = binding.Bones[hipsIdx]!;

        float bodyW = Maths.Clamp(_bodyPositionWeight, 0f, 1f);

        // Direct body pin.
        if (bodyW > 0f)
        {
            Float3 delta = (_bodyPosition - hips.Position) * bodyW;
            MoveRig(binding, hips, delta);
        }

        // Puppet pull: for each active limb goal the chain cannot reach from its joint, drag the
        // body by the shortfall (scaled by the body weight) so the limb stretches after it.
        if (bodyW > 0f)
        {
            for (int g = 0; g < goals.Length; g++)
            {
                IkGoal goal = goals[g];
                if (goal.PositionWeight <= 0f)
                    continue;
                (HumanoidBone rootRole, HumanoidBone midRole, HumanoidBone endRole) = ChainRoles((AvatarIKGoal)g);
                if (!avatar.TryGetPath(rootRole, out string rootPath)
                    || !avatar.TryGetPath(midRole, out string midPath2)
                    || !avatar.TryGetPath(endRole, out string endPath2))
                    continue;
                int rootIdx = binding.IndexOfBonePath(rootPath);
                int midIdx = binding.IndexOfBonePath(midPath2);
                int endIdx = binding.IndexOfBonePath(endPath2);
                if (rootIdx < 0 || midIdx < 0 || endIdx < 0
                    || binding.Bones[rootIdx] == null || binding.Bones[midIdx] == null || binding.Bones[endIdx] == null)
                    continue;

                Transform root = binding.Bones[rootIdx]!;
                Transform mid = binding.Bones[midIdx]!;
                Transform end = binding.Bones[endIdx]!;
                float a = Float3.Distance(root.Position, mid.Position);
                float b = Float3.Distance(mid.Position, end.Position);
                float reach = Float3.Distance(root.Position, goal.Position);
                float shortfall = reach - (a + b);
                if (shortfall > 1e-4f)
                    MoveRig(binding, hips, Float3.Normalize(goal.Position - root.Position) * shortfall * goal.PositionWeight * bodyW);
            }
        }

        // Body orientation: distribute the blended rotation across the hips + spine chain.
        float rotW = Maths.Clamp(_bodyRotationWeight, 0f, 1f);
        if (rotW > 0f)
        {
            Quaternion delta = Quaternion.Slerp(Quaternion.Identity,
                _bodyRotation * Quaternion.Inverse(hips.Rotation), rotW);
            var chain = new System.Collections.Generic.List<Transform>(5) { hips };
            foreach (HumanoidBone role in new[] { HumanoidBone.Spine, HumanoidBone.Chest, HumanoidBone.UpperChest })
            {
                if (avatar.TryGetPath(role, out string path))
                {
                    int idx = binding.IndexOfBonePath(path);
                    if (idx >= 0 && binding.Bones[idx] != null)
                        chain.Add(binding.Bones[idx]!);
                }
            }
            Quaternion perBone = XEngine.Animation.Jobs.TwoBoneIKJob.Pow(delta, 1f / chain.Count);
            for (int i = 0; i < chain.Count; i++)
                ApplyWorldRotation(chain[i], perBone * chain[i].Rotation);
        }
    }

    private static (HumanoidBone root, HumanoidBone mid, HumanoidBone end) ChainRoles(AvatarIKGoal goal) => goal switch
    {
        AvatarIKGoal.LeftHand => (HumanoidBone.LeftUpperArm, HumanoidBone.LeftLowerArm, HumanoidBone.LeftHand),
        AvatarIKGoal.RightHand => (HumanoidBone.RightUpperArm, HumanoidBone.RightLowerArm, HumanoidBone.RightHand),
        AvatarIKGoal.LeftFoot => (HumanoidBone.LeftUpperLeg, HumanoidBone.LeftLowerLeg, HumanoidBone.LeftFoot),
        _ => (HumanoidBone.RightUpperLeg, HumanoidBone.RightLowerLeg, HumanoidBone.RightFoot),
    };

    /// <summary>Translates the hips by <paramref name="delta"/> in local space (rig follows).</summary>
    private static void MoveRig(SkeletonBinding binding, Transform hips, Float3 delta)
    {
        if (Float3.LengthSquared(delta) < 1e-12f)
            return;
        hips.LocalPosition += delta;
    }

// ---- M61: MatchTarget ----

    private struct ActiveMatch
    {
        public Float3 TargetPosition;
        public Quaternion TargetRotation;
        public float StartNormalized;
        public float TargetNormalized;
        public Float3 StartBodyPosition;   // Captured when the match activated.
        public Quaternion StartBodyRotation;
        public bool PositionOnly;
        public bool Active;
    }

    private ActiveMatch _match;

    /// <summary>
    /// M61 — adjusts this GameObject's transform over the current state's normalized window
    /// [<paramref name="startNormalized"/>, <paramref name="targetNormalized"/>] so the body lines
    /// up with (<paramref name="position"/>, <paramref name="rotation"/>) exactly at the target time
    /// (Unity MatchTarget parity; mask reserved — the whole body matches in v1).
    /// </summary>
    public void MatchTarget(Float3 position, Quaternion rotation, AvatarMask? mask,
        float startNormalized, float targetNormalized)
    {
        _ = mask; // Whole-body v1; per-part masking lands with per-bene match targets (v3).
        _match = new ActiveMatch
        {
            TargetPosition = position,
            TargetRotation = rotation,
            StartNormalized = startNormalized,
            TargetNormalized = targetNormalized,
            StartBodyPosition = Transform.Position,
            StartBodyRotation = Transform.Rotation,
            PositionOnly = false,
            Active = true,
        };
    }

    /// <summary>Cancels an active <see cref="MatchTarget"/>.</summary>
    public void InterruptMatchTarget() => _match.Active = false;

    /// <summary>Advances the active match: blends the body from its captured start toward the
    /// target across the normalized window of the current state.</summary>
    private void UpdateMatchTarget()
    {
        if (!_match.Active)
            return;
        AnimationClipPlayable? state = _current;
        if (state == null || state.Duration <= 0d)
        {
            _match.Active = false;
            return;
        }

        float normalized = state.NormalizedTime;
        if (normalized >= _match.TargetNormalized)
        {
            Transform.Position = _match.TargetPosition;
            if (!_match.PositionOnly)
                Transform.Rotation = _match.TargetRotation;
            _match.Active = false;
            return;
        }
        if (normalized <= _match.StartNormalized)
            return; // Not in the window yet.

        float span = _match.TargetNormalized - _match.StartNormalized;
        float alpha = span > 1e-5f ? (normalized - _match.StartNormalized) / span : 1f;
        Transform.Position = _match.StartBodyPosition
            + (_match.TargetPosition - _match.StartBodyPosition) * alpha;
        if (!_match.PositionOnly)
            Transform.Rotation = Quaternion.Slerp(_match.StartBodyRotation, _match.TargetRotation, alpha);
    }
}
