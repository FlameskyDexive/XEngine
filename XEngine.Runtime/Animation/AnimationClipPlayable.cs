// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Playables;
using XEngine.Runtime;
using XEngine.Vector;
using XEngine;

namespace XEngine.Animation;

/// <summary>
/// Leaf animation node that samples a single <see cref="AnimationClip"/> into its
/// <see cref="AnimationPlayable.Output"/> pose each frame. Resolves the clip's bone paths and
/// blend-shape keys to skeleton indices once at <see cref="Bind"/> time, so per-frame sampling is
/// pure integer-indexed curve evaluation.
/// </summary>
public partial class AnimationClipPlayable : AnimationPlayable
{
    /// <summary>The clip to sample. May be <c>null</c> while streaming in.</summary>
    public AnimationClip? Clip;

    // Retain identity across a temporarily missing import output, without retaining the old clip.
    internal Guid BoundClipAssetId { get; private set; }
    internal bool IsCodeClipState;
    internal bool IsWeightlessClone;

    /// <summary>When true, <see cref="Bind"/> remaps clip bones through <see cref="AnimationMirrorNames"/>.</summary>
    public bool MirrorBones;

    /// <summary>
    /// When set, the next graph evaluation samples the newly entered state at its entry time
    /// without advancing its clock. Unity's Animator keeps a clip at its authored entry time
    /// (normally zero) on the frame in which Play/transition enters it; the outgoing playable
    /// still advances normally.
    /// </summary>
    internal bool HoldEntryFrame;

    /// <summary>
    /// Time used by the most recent graph sample.
    /// </summary>
    public float SampleTime => Duration > 0d ? (float)_sampleTime : 0f;

    /// <summary>Wrap mode applied when <see cref="Playable.Time"/> passes the clip's duration.</summary>
    public AnimationWrapMode Wrap = AnimationWrapMode.Loop;

    /// <summary>
    /// Current playback position as a fraction of the clip's duration. A zero-duration Unity
    /// animation still owns a state clock: its normalized time advances in seconds while the
    /// authored pose is sampled at time zero. This is required for ExitTime transitions on marker
    /// clips such as Icey's Shopping state.
    /// </summary>
    public float NormalizedTime => Duration > 0d ? (float)(Time / Duration)
        : Duration == 0d ? (float)Time : 0f;

    /// <summary>Raw normalized state clock used by controller ExitTime checks.</summary>
    internal float TransitionNormalizedTime => Duration > 0d
        ? (float)(Time / Duration)
        : Duration == 0d ? (float)_frameUnwrappedTime : 0f;

    /// <summary>
    /// Normalized clock before a finite once/clamp playable applies its end clamp. The controller
    /// uses this to distinguish an exact end sample from a later evaluation after the boundary.
    /// </summary>
    public float UnwrappedNormalizedTime => Duration > 0d
        ? (float)(_frameUnwrappedTime / Duration)
        : Duration == 0d ? (float)_frameUnwrappedTime : 0f;

    /// <summary>True if at least one normalized-time event is registered (playable or clip-authored).</summary>
    public bool HasEvents =>
        (_sequence != null && (_sequence.Count > 0 || _sequence.HasEndEvent))
        || (_clipEvents != null && _clipEvents.Length > 0);

    // Per-clip-bone index into the skeleton (-1 if the path didn't resolve). Parallel to Clip.Bones.
    private int[]? _boneMap;
    // Per-clip-blendshape index into the skeleton (-1 if unresolved). Parallel to Clip.BlendShapes.
    private int[]? _blendShapeMap;
    private GenericCurveBinder.Slot[]? _genericSlots;
    private AnimationClipEvent[]? _clipEvents;
    private TravelProjection _travelProjection;
    internal ClipRootMotionPolicy BoundRootMotionPolicy { get; private set; }
    private int _legacyInPlaceRoot = -1;

    // M49 — runtime normalized-time events (sorted sequence + distinct end event). Lazily allocated
    // (most clips have none); fired through the deferred AnimationEventQueue, unlike the clip-authored
    // events above which keep their historical inline invocation.
    private AnimationEventSequence? _sequence;

    // The [prev,cur] local-time interval advanced THIS frame, captured in PrepareFrame and consumed
    // in ProcessFrame so events fire exactly for the times crossed (with loop-wrap handling).
    private double _framePrevTime;
    private double _frameCurTime;
    private double _sampleTime;

    public AnimationClipPlayable() { }

    public AnimationClipPlayable(AnimationClip clip) { Clip = clip; }

    /// <summary>
    /// Sets the clip clock for direct inspector/test sampling as well as graph-driven playback.
    /// Keep the cached sample interval in sync so a ProcessFrame call made without a preceding
    /// PrepareFrame samples the requested time instead of a stale entry value.
    /// </summary>
    public override void SetTime(double time)
    {
        float clock = (float)time;
        base.SetTime(clock);
        _framePrevTime = clock;
        _frameCurTime = clock;
        _frameUnwrappedTime = clock;
        _sampleTime = clock;
    }

    /// <summary>Resets a reused clip state's clock and entry-frame bookkeeping.</summary>
    internal void ResetPlaybackClock(double time = 0d)
    {
        float clock = (float)time;
        SetTime(clock);
        _framePrevTime = clock;
        _frameCurTime = clock;
        _frameUnwrappedTime = clock;
        _sampleTime = clock;
    }

    /// <summary>Sets a clip clock and keeps the unwrapped/last-sample clocks coherent.</summary>
    internal void SetPlaybackTime(double time)
    {
        float clock = (float)time;
        SetTime(clock);
        _framePrevTime = clock;
        _frameCurTime = clock;
        _frameUnwrappedTime = clock;
        _sampleTime = clock;
    }

    /// <summary>Binds this playable to a skeleton: allocates the output pose and builds the bone/blendshape index maps.</summary>
    public virtual void Bind(SkeletonBinding binding)
    {
        if (Clip != null) BoundClipAssetId = Clip.AssetID;
        Binding = binding;
        AllocateOutput();
        Duration = -1d;
        _boneMap = null;
        _blendShapeMap = null;
        _genericSlots = null;
        _clipEvents = null;
        _travelProjection = default;
        _rootMotionClipBone = -1;
        _rootMotionVisualBone = -1;
        _rootMotionPinProjection = default;
        ResetModelRootMotionBinding();
        _legacyInPlaceRoot = -1;
        BoundRootMotionPolicy = Clip?.RootMotionPolicy ?? ClipRootMotionPolicy.LegacyAutomatic;
        if (Clip != null)
            BuildMaps(Clip, binding);
        BindHumanoidSampler(binding);
    }

    internal bool RefreshImportedAssets(IReadOnlyDictionary<Guid, EngineObject?> changes,
        Humanoid.Avatar? avatar, bool avatarChanged)
    {
        bool clipChanged = BoundClipAssetId != Guid.Empty && changes.ContainsKey(BoundClipAssetId);
        bool sampledAvatarChanged = SamplingAvatarAssetId != Guid.Empty && changes.ContainsKey(SamplingAvatarAssetId);
        if (!clipChanged && !avatarChanged && !sampledAvatarChanged) return false;
        if (clipChanged) Clip = changes[BoundClipAssetId] as AnimationClip;
        if (avatarChanged) ConfigureHumanoidSampler(avatar);
        else if (sampledAvatarChanged)
            ConfigureHumanoidSampler(changes[SamplingAvatarAssetId] as Humanoid.Avatar);
        if (Binding != null) Bind(Binding);
        // The clock, fades, state, speed, wrap, entry frame and runtime events stay on this node.
        // Reset only the last sampled interval so a publication cannot synthesize events/deltas.
        _framePrevTime = _frameCurTime = _frameUnwrappedTime = _sampleTime = Time;
        HumanoidSampler?.PrimeRootMotion(Duration > 0d ? (float)AdvanceTime(Time, Duration, Wrap) : 0f);
        return true;
    }

    /// <summary>Assigns a new clip and (re)binds. The previous maps are rebuilt against the current binding.</summary>
    public void SetClip(AnimationClip clip, SkeletonBinding binding)
    {
        Clip = clip;
        Time = 0d;
        Bind(binding);
    }

    private void BuildMaps(AnimationClip clip, SkeletonBinding binding)
    {
        // Preserve an authored zero duration. It is a valid state-clock marker, not an unresolved
        // indefinite playable; PrepareFrame advances its clock while ProcessFrame samples t=0.
        Duration = clip.Duration;

        List<AnimationClip.AnimBone> bones = clip.Bones;
        _boneMap = new int[bones.Count];
        for (int i = 0; i < bones.Count; i++)
        {
            string boneName = bones[i].BoneName;
            int idx = -1;
            if (MirrorBones)
            {
                string mirrored = AnimationMirrorNames.Mirror(boneName);
                if (mirrored != boneName)
                    idx = binding.IndexOfBonePath(mirrored);
            }
            if (idx < 0)
                idx = binding.IndexOfBonePath(boneName);
            _boneMap[i] = idx;
        }
        CaptureRetargetOffsets(clip, binding);
        CaptureTravelProjection(clip, binding);
        CaptureRootMotionBinding(clip, binding);
        CaptureRootMotionProjection(clip, binding);
        if (BoundRootMotionPolicy == ClipRootMotionPolicy.LegacyAutomatic)
        {
            _legacyInPlaceRoot = string.IsNullOrEmpty(clip.RootBonePath) ? -1 : binding.IndexOfBonePath(clip.RootBonePath);
            // Binding order is parent-first; the first descendant is the historical topmost fallback.
            if (_legacyInPlaceRoot < 0 && binding.BoneCount > 0) _legacyInPlaceRoot = 0;
        }

        List<AnimationClip.BlendShapeAnim> bss = clip.BlendShapes;
        _blendShapeMap = new int[bss.Count];
        for (int j = 0; j < bss.Count; j++)
            _blendShapeMap[j] = binding.IndexOfBlendShape(bss[j].Path, bss[j].ShapeName);

        _genericSlots = GenericCurveBinder.Bind(clip, binding.Root);
        _clipEvents = AnimationEventBinder.Bind(clip, binding.Root);
    }

    /// <summary>
    /// Target-parent-space directions that carry authored locomotion for one bound bone. Built
    /// once with the retarget maps so the Animator can remove travel without also erasing small
    /// orthogonal bob/sway, including while this state is fading out.
    /// </summary>
    internal readonly struct TravelProjection
    {
        internal readonly int BoneIndex;
        internal readonly byte AxisCount;
        internal readonly Float3 Axis0;
        internal readonly Float3 Axis1;
        internal readonly Float3 Axis2;

        internal TravelProjection(int boneIndex, byte axisCount, Float3 axis0, Float3 axis1, Float3 axis2)
        {
            BoneIndex = boneIndex;
            AxisCount = axisCount;
            Axis0 = axis0;
            Axis1 = axis1;
            Axis2 = axis2;
        }

        internal Float3 GetAxis(int index) => index switch
        {
            0 => Axis0,
            1 => Axis1,
            _ => Axis2,
        };
    }

    internal bool TryGetTravelProjection(out TravelProjection projection)
    {
        projection = _travelProjection;
        return projection.AxisCount != 0;
    }

    // ── Source-reference-pose retargeting ──────────────────────────────────
    // A clip's time-zero sample is authored motion, not its source rig's bind/reference pose.
    // Using frame zero as the old G-chain did collapses every clip's real first frame to the
    // destination bind pose and compounds the wrong correction down the hierarchy. Imported
    // clips now carry each source bone's static local TRS separately.
    //
    // With column-vector local transforms, let F_i map destination-bone coordinates to source-
    // bone coordinates. For source local C_i and destination local S_i:
    //   S_i(t) = inverse(F_parent) * C_i(t) * F_i
    // and the source/destination reference poses solve top-down as:
    //   F_i = inverse(C_ref_i) * F_parent * S_ref_i.
    // Matching rigs reduce to F=identity. Position deltas are rotated by inverse(F_parent) and
    // scale is applied relative to the two reference poses. All maps are built once at Bind;
    // ProcessFrame remains allocation-free and integer-indexed.
    private Quaternion[]? _targetToSourceBasis;
    private Quaternion[]? _sourceParentToTargetBasis;
    private int[]? _clipParent;
    private bool[]? _basisComputed;
    private bool[]? _retarget;
    private BonePose[]? _targetReferencePose;

    private void CaptureRetargetOffsets(AnimationClip clip, SkeletonBinding binding)
    {
        List<AnimationClip.AnimBone> bones = clip.Bones;
        int n = bones.Count;

        if (_targetToSourceBasis == null || _targetToSourceBasis.Length != n)
        {
            _targetToSourceBasis = new Quaternion[n];
            _sourceParentToTargetBasis = new Quaternion[n];
            _clipParent = new int[n];
            _basisComputed = new bool[n];
            _retarget = new bool[n];
            _targetReferencePose = new BonePose[n];
        }

        Array.Clear(_basisComputed!, 0, n);
        for (int i = 0; i < n; i++)
        {
            _clipParent[i] = FindClipParent(bones, i);
            int idx = _boneMap![i];
            bool canRetarget = idx >= 0 && bones[i].HasSourceReferencePose;
            _retarget![i] = canRetarget;
            _targetReferencePose![i] = canRetarget
                ? binding.ReferencePose[idx]
                : default;
        }

        for (int i = 0; i < n; i++)
            ComputeBasis(i, bones);
    }

    private const float TravelThreshold = 0.5f;

    /// <summary>
    /// Detects large source translation during the clip-bind cold path and rotates its source
    /// axes through the same parent basis used by position retargeting. The descriptor therefore
    /// lives in the exact destination local space sampled by <see cref="ProcessFrame"/>.
    /// </summary>
    private void CaptureTravelProjection(AnimationClip clip, SkeletonBinding binding)
    {
        List<AnimationClip.AnimBone> bones = clip.Bones;
        int bestDepth = int.MaxValue;
        TravelProjection best = default;

        for (int i = 0; i < bones.Count; i++)
        {
            int boneIndex = _boneMap![i];
            if ((uint)boneIndex >= (uint)binding.BoneCount)
                continue;

            AnimationClip.AnimBone bone = bones[i];
            Transform? transform = binding.Bones[boneIndex];
            Transform? motionRoot = binding.MotionRoot;
            if (motionRoot != null && (transform == null
                || !SkeletonBinding.IsAncestorOf(transform, motionRoot)))
                continue;
            Float3 sourceReference = bone.HasSourceReferencePose
                ? bone.SourcePosition : binding.ReferencePose[boneIndex].LocalPosition;
            bool travelX = CarriesTravel(bone.PosX, sourceReference.X, motionRoot != null);
            bool travelY = CarriesTravel(bone.PosY, sourceReference.Y, motionRoot != null);
            bool travelZ = CarriesTravel(bone.PosZ, sourceReference.Z, motionRoot != null);
            if (!(travelX || travelY || travelZ))
                continue;

            string path = binding.Paths[boneIndex];
            int depth = 0;
            for (int c = 0; c < path.Length; c++)
                if (path[c] == '/') depth++;
            if (depth >= bestDepth)
                continue;

            Quaternion sourceParentToTarget = _retarget![i]
                ? _sourceParentToTargetBasis![i]
                : Quaternion.Identity;
            Float3 axis0 = default;
            Float3 axis1 = default;
            Float3 axis2 = default;
            byte axisCount = 0;
            if (travelX) AddTravelAxis(sourceParentToTarget * Float3.UnitX, ref axisCount, ref axis0, ref axis1, ref axis2);
            if (travelY) AddTravelAxis(sourceParentToTarget * Float3.UnitY, ref axisCount, ref axis0, ref axis1, ref axis2);
            if (travelZ) AddTravelAxis(sourceParentToTarget * Float3.UnitZ, ref axisCount, ref axis0, ref axis1, ref axis2);

            bestDepth = depth;
            best = new TravelProjection(boneIndex, axisCount, axis0, axis1, axis2);
        }

        _travelProjection = best;
    }

    private static void AddTravelAxis(
        Float3 axis,
        ref byte axisCount,
        ref Float3 axis0,
        ref Float3 axis1,
        ref Float3 axis2)
    {
        axis = Float3.Normalize(axis);
        if (Float3.LengthSquared(axis) <= 1e-8f)
            return;
        if (axisCount == 0) axis0 = axis;
        else if (axisCount == 1) axis1 = axis;
        else axis2 = axis;
        axisCount++;
    }

    private static float CurveRange(XEngine.Runtime.AnimationCurve? curve)
    {
        if (curve == null || curve.Keys.Count == 0)
            return 0f;
        float minimum = float.MaxValue;
        float maximum = float.MinValue;
        CurveKeyCollection keys = curve.Keys;
        for (int i = 0; i < keys.Count; i++)
        {
            float value = keys[i].Value;
            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;
        }
        return maximum - minimum;
    }

    private static bool CarriesTravel(XEngine.Runtime.AnimationCurve? curve, float reference, bool hasSkinRoot)
    {
        if (CurveRange(curve) > TravelThreshold) return true;
        // End/recovery clips often hold the final travelled position with zero range. They must
        // remain in place too. Restrict offset detection to the skin-root chain, never weapons.
        return hasSkinRoot && curve != null && curve.Keys.Count > 0
            && MathF.Abs(curve.Keys[0].Value - reference) > TravelThreshold;
    }

    private Quaternion ComputeBasis(int i, List<AnimationClip.AnimBone> bones)
    {
        if (_basisComputed![i]) return _targetToSourceBasis![i];
        _basisComputed[i] = true;

        if (!_retarget![i])
        {
            _targetToSourceBasis![i] = Quaternion.Identity;
            _sourceParentToTargetBasis![i] = Quaternion.Identity;
            return Quaternion.Identity;
        }

        int parent = _clipParent![i];
        Quaternion parentBasis = parent >= 0 ? ComputeBasis(parent, bones) : Quaternion.Identity;
        Quaternion sourceReference = Quaternion.NormalizeSafe(bones[i].SourceRotation);
        Quaternion targetReference = Quaternion.NormalizeSafe(_targetReferencePose![i].LocalRotation);
        Quaternion basis = Quaternion.NormalizeSafe(
            Quaternion.Inverse(sourceReference) * (parentBasis * targetReference));
        _targetToSourceBasis![i] = basis;
        _sourceParentToTargetBasis![i] = Quaternion.Inverse(parentBasis);
        return basis;
    }

    private int FindClipParent(List<AnimationClip.AnimBone> bones, int i)
    {
        string path = bones[i].BoneName;
        int slash = path.LastIndexOf('/');
        if (slash < 0) return -1;
        string parentPath = path[..slash];
        for (int j = 0; j < bones.Count; j++)
            if (bones[j].BoneName == parentPath)
                return j;
        return -1;
    }

    /// <summary>Registers a callback fired when playback crosses <paramref name="normalizedTime"/> (0..1).</summary>
    public void AddEvent(float normalizedTime, Action callback)
    {
        _sequence ??= new AnimationEventSequence();
        _sequence.Add(normalizedTime, callback);
    }

    /// <summary>
    /// M49 — registers a (optionally named) timed event. A named event also fires the Animator's
    /// named-handler table for <paramref name="name"/>; the per-event callback may be null when only
    /// the named handler matters.
    /// </summary>
    public void AddEvent(float normalizedTime, Action? callback, string name)
    {
        _sequence ??= new AnimationEventSequence();
        _sequence.Add(normalizedTime, callback,
            string.IsNullOrEmpty(name) ? 0 : AnimationNameHash.Hash(name));
    }

    /// <summary>Registers a callback fired when the clip reaches its end (distinct end event).</summary>
    public void SetEndEvent(Action callback)
    {
        _sequence ??= new AnimationEventSequence();
        _sequence.SetEnd(callback);
    }

    /// <summary>The runtime event sequence (null when no runtime events were registered).</summary>
    public AnimationEventSequence? EventSequence => _sequence;

    /// <summary>Removes all registered events.</summary>
    public void ClearEvents() => _sequence?.Clear();

    [HotPath]
    public override void PrepareFrame(PlayableContext ctx)
    {
        _framePrevTime = Time;
        _frameUnwrappedTime = Time;
        base.PrepareFrame(ctx); // advance weight fade

        if (Clip == null || State != PlayableState.Playing)
        {
            _sampleTime = Time;
            _frameCurTime = Time;
            return;
        }

        if (HoldEntryFrame)
        {
            HoldEntryFrame = false;
            // SetPlaybackTime may have carried the sub-frame remainder from the source
            // transition. Preserve that authored entry time for the first pose sample while
            // keeping the destination clock stationary for this evaluation.
            _sampleTime = Time;
            _frameCurTime = Time;
            return;
        }

        // Unity's Animator accumulates clip time in a float. Keeping the local clock at float
        // precision is observable at long finite clips: a double clock reaches an exact 2.0 s
        // boundary one frame before Unity's 1.9999988 s sample.
        float current = (float)Time;
        float t = current + ctx.DeltaTime * Speed;
        _frameUnwrappedTime = t;
        Time = (float)AdvanceTime(t, (float)Duration, Wrap);
        _sampleTime = Time;
        _frameCurTime = Time;
    }

    [HotPath]
    public override void ProcessFrame(PlayableContext ctx)
    {
        // States not contributing this frame (and not mid-fade) skip sampling; the mixer ignores them
        // when blending (Weight <= 0), so leaving their pose stale is safe and saves per-bone work.
        if (Weight <= 0f && !IsFading)
        {
            FireEvents();
            return;
        }

        Pose? pose = Output;
        if (pose == null)
        {
            FireEvents();
            return;
        }
        pose.Clear();

        AnimationClip? clip = Clip;
        if (clip == null || _boneMap == null)
        {
            FireEvents();
            return;
        }

        // Zero-duration marker clips advance their state clock but have no time span to sample.
        float time = Duration > 0d ? (float)_sampleTime : 0f;
        BonePose[] dst = pose.Bones;
        List<AnimationClip.AnimBone> bones = clip.Bones;
        int[] map = _boneMap;
        int n = map.Length;

        for (int i = 0; i < n; i++)
        {
            int idx = map[i];
            if (idx < 0)
                continue;

            AnimationClip.AnimBone cb = bones[i];
            ref BonePose bp = ref dst[idx];

            PoseChannel ch = PoseChannel.None;
            if (cb.PosX != null)
            {
                ch |= PoseChannel.Position;
                Float3 position = cb.EvaluatePositionAt(time);
                if (_retarget![i])
                {
                    position = _targetReferencePose![i].LocalPosition
                        + _sourceParentToTargetBasis![i] * (position - cb.SourcePosition);
                }
                bp.LocalPosition = position;
            }
            if (cb.RotX != null)
            {
                ch |= PoseChannel.Rotation;
                Quaternion rot = cb.EvaluateRotationAt(time);
                if (_retarget![i])
                {
                    rot = _sourceParentToTargetBasis![i]
                        * (rot * _targetToSourceBasis![i]);
                }
                bp.LocalRotation = rot;
            }
            if (cb.ScaleX != null)
            {
                ch |= PoseChannel.Scale;
                Float3 scale = cb.EvaluateScaleAt(time);
                if (_retarget![i])
                    scale = RetargetScale(scale, cb.SourceScale, _targetReferencePose![i].LocalScale);
                bp.LocalScale = scale;
            }
            bp.Channels = ch;
        }

        PinRootMotionContribution();

        // Blend shapes.
        int[]? bsMap = _blendShapeMap;
        if (bsMap != null && bsMap.Length > 0)
        {
            float[] weights = pose.BlendShapeWeights;
            List<AnimationClip.BlendShapeAnim> bss = clip.BlendShapes;
            int m = bsMap.Length;
            for (int j = 0; j < m; j++)
            {
                int idx = bsMap[j];
                if (idx >= 0)
                    weights[idx] = bss[j].EvaluateAt(time);
            }
        }

        GenericCurveBinder.Slot[]? gen = _genericSlots;
        if (gen != null && gen.Length > 0)
            GenericCurveBinder.Apply(gen, time);

        FireEvents();
        HumanoidSampler?.ApplyMuscles(pose, time);
    }

    private static Float3 RetargetScale(Float3 source, Float3 sourceReference, Float3 targetReference)
    {
        return new Float3(
            RetargetScaleComponent(source.X, sourceReference.X, targetReference.X),
            RetargetScaleComponent(source.Y, sourceReference.Y, targetReference.Y),
            RetargetScaleComponent(source.Z, sourceReference.Z, targetReference.Z));
    }

    private static float RetargetScaleComponent(float source, float sourceReference, float targetReference)
        => MathF.Abs(sourceReference) > 1e-7f
            ? targetReference * (source / sourceReference)
            : source;

    /// <summary>
    /// Fires every registered event whose normalized time was crossed this frame, using the interval
    /// captured in <see cref="PrepareFrame"/>. Handles loop wrap (fires the tail [prev,1] then the
    /// head [0,cur] when the playhead wrapped past the duration) and true backward playback
    /// (negative speed — sequence events fire in reverse order). Clip-authored events invoke inline
    /// (historical behaviour); runtime sequence events enqueue into the deferred
    /// <see cref="PlayableEventQueue"/> and fire after the pose is applied, capped at
    /// <see cref="AnimationEventSequence.MaximumFullLoopCount"/> loops per frame.
    /// </summary>
    protected void FireEvents()
    {
        bool hasSequence = _sequence != null && (_sequence.Count > 0 || _sequence.HasEndEvent);
        if (!hasSequence && (_clipEvents == null || _clipEvents.Length == 0))
            return;
        if (Clip == null || Duration < 0d)
            return;
        // Unity uses a one-second normalized state clock for a zero-duration marker clip.
        double duration = Duration == 0d ? 1d : Duration;

        double prevNorm = _framePrevTime / duration;
        double curNorm = _frameCurTime / duration;

        if (curNorm >= prevNorm)
        {
            FireInRange(_clipEvents, prevNorm, curNorm);
            if (hasSequence)
                _sequence!.CollectInRange(prevNorm, curNorm, PlayableEventQueue.Shared);
        }
        else if (_frameUnwrappedTime < _framePrevTime)
        {
            // True backward playback (negative speed): sequence events fire in reverse order.
            // Authored clip events keep their historical forward-only behaviour.
            if (hasSequence)
                _sequence!.CollectInRange(prevNorm, curNorm, PlayableEventQueue.Shared);
        }
        else
        {
            // Wrapped forward past 1.0 (loop). The two-segment dispatch covers one wrap; a span
            // larger than the loop cap (huge catch-up dt) fires only the last cycle's head so a
            // burst of wrapped events cannot flood handlers.
            double spanCycles = (1.0 - prevNorm) + curNorm;
            FireInRange(_clipEvents, prevNorm, 1.0);
            if (hasSequence && spanCycles <= AnimationEventSequence.MaximumFullLoopCount)
                _sequence!.CollectInRange(prevNorm, 1.0, PlayableEventQueue.Shared);
            FireInRange(_clipEvents, 0.0, curNorm);
            if (hasSequence)
                _sequence!.CollectInRange(0.0, curNorm, PlayableEventQueue.Shared);
        }
    }

    private static void FireInRange(AnimationClipEvent[]? events, double fromNorm, double toNorm)
    {
        if (events == null || events.Length == 0)
            return;
        int n = events.Length;
        const double boundaryEpsilon = 1e-5;
        for (int i = 0; i < n; i++)
        {
            AnimationClipEvent e = events[i];
            if (e.NormalizedTime > fromNorm + boundaryEpsilon
                && e.NormalizedTime <= toNorm + boundaryEpsilon)
                e.Callback?.Invoke();
        }
    }

    /// <summary>
    /// Advances clip-local time <paramref name="t"/> past the duration, applying the wrap mode.
    /// Mirrors the legacy AnimationComponent wrap handling.
    /// </summary>
    internal static double AdvanceTime(double t, double duration, AnimationWrapMode wrap)
    {
        if (duration <= 0d)
            return t;

        switch (wrap)
        {
            case AnimationWrapMode.Loop:
                // Multi-wrap in one step (large catch-up deltas).
                while (t >= duration) t -= duration;
                while (t < 0d) t += duration;
                return t;

            case AnimationWrapMode.PingPong:
                {
                    // Reflect at each bound. Bounded iterations avoid pathological spin on huge deltas.
                    double d = duration;
                    for (int i = 0; i < 8 && (t < 0d || t >= d); i++)
                    {
                        if (t >= d) t = d + (d - t);
                        else if (t < 0d) t = -t;
                    }
                    if (t < 0d) t = 0d;
                    else if (t > d) t = d;
                    return t;
                }

            case AnimationWrapMode.Once:
                if (t >= duration)
                    return duration;
                if (t < 0d)
                    return 0d;
                return t;

            case AnimationWrapMode.ClampForever:
            default:
                if (t >= duration)
                    return duration;
                if (t < 0d)
                    return 0d;
                return t;
        }
    }
}
