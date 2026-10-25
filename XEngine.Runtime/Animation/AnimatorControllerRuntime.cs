// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Playables;
using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// Per-instance evaluator for an <see cref="AnimatorControllerConstant"/>. Watches parameters and the
/// current state's normalized time, fires transitions when their conditions (and optional ExitTime)
/// pass, and drives the owning <see cref="global::XEngine.Runtime.Animator"/>'s mixer layer to
/// activate / crossfade the destination state's clip. The animator owns the actual blending; this
/// type only decides <em>what plays</em>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Integration.</b> The owning <see cref="global::XEngine.Runtime.Animator"/> calls
/// <see cref="Evaluate"/> once per frame <em>before</em> advancing the playable graph, so any
/// <see cref="global::XEngine.Runtime.Animator.CrossFade"/> issued here applies in the same frame's
/// graph evaluation. State time for ExitTime checks is read from the current state's clip playable
/// (one frame of lag vs. the just-advanced time, which is imperceptible).
/// </para>
/// <para>
/// <b>Triggers.</b> A trigger parameter stays set until a transition whose conditions include it
/// fires, at which point it's consumed (reset to 0) — matching Unity Mecanim semantics.
/// </para>
/// </remarks>
public sealed class AnimatorControllerRuntime
{
    private readonly AnimatorControllerConstant _c;
    private readonly Guid[] _motionClipIds;
    private readonly Animator _animator;
    private readonly float[] _params;
    private readonly AnimationMixerPlayable? _mixer;
    private readonly int _statesStart;
    private readonly int _statesCount;
    private readonly int _defaultStateIndex;
    private readonly int _anyStart;
    private readonly int _anyCount;
    private readonly int _layerIndex;
    private StateMachineBehaviour?[] _instances = System.Array.Empty<StateMachineBehaviour?>();

    private int _currentState = -1;
    private int _fromState = -1;
    private int _activeRangeStart = -1;
    private int _activeOffsetInRange = -1;
    private TransitionInterruptionSource _interrupt;
    private bool _orderedInterrupt;
    private bool _entered; // whether the default state has been entered at least once.
    private AnimationPlayable? _currentPlayable;
    private int _currentBlendTreeParamIndex = -1;
    private int _currentBlendTreeParamIndexY = -1;
    private float _evaluationDelta;

    public AnimatorControllerConstant Constant => _c;

    /// <summary>Index of the state the FSM is currently in, or -1 before the first Evaluate.</summary>
    public int CurrentStateIndex => _currentState;

    /// <summary>
    /// <see cref="CurrentStateIndex"/> relative to this layer's state slice (0 = first state on the
    /// layer), or -1 before the first Evaluate.
    /// </summary>
    public int CurrentLocalStateIndex
        => _currentState < 0 ? -1 : _currentState - _statesStart;

    /// <summary>True while the current state's playable is still crossfading in.</summary>
    public bool IsInTransition => _currentPlayable != null && _currentPlayable.IsFading;

    /// <summary>Source state index while <see cref="IsInTransition"/>, otherwise -1.</summary>
    public int TransitionFromStateIndex => IsInTransition ? _fromState : -1;

    /// <summary>Fade seconds for a compiled transition given the source clip length.</summary>
    public static float ResolveFadeDuration(in TransitionDef t, float sourceDurationSeconds)
    {
        if (t.HasFixedDuration || sourceDurationSeconds <= 0f)
            return t.Duration;
        return t.Duration * sourceDurationSeconds;
    }

    public AnimatorControllerRuntime(AnimatorControllerConstant constant, Animator animator)
        : this(constant, animator, layerIndex: 0, sharedParameters: null, targetMixer: null)
    {
    }

    public AnimatorControllerRuntime(
        AnimatorControllerConstant constant,
        Animator animator,
        int layerIndex,
        float[]? sharedParameters,
        AnimationMixerPlayable? targetMixer)
    {
        _c = constant;
        _motionClipIds = new Guid[constant.MotionClips.Length];
        for (int i = 0; i < _motionClipIds.Length; i++)
            _motionClipIds[i] = constant.MotionClips[i]?.AssetID ?? Guid.Empty;
        _animator = animator;
        _mixer = targetMixer;
        _params = sharedParameters ?? new float[constant.Parameters.Length];
        if (sharedParameters == null)
            ResetParametersToDefaults();

        if (constant.Layers.Length == 0)
        {
            _layerIndex = 0;
            _statesStart = 0;
            _statesCount = constant.States.Length;
            _defaultStateIndex = constant.DefaultStateIndex;
            _anyStart = constant.AnyStateTransitionsStart;
            _anyCount = constant.AnyStateTransitionsCount;
        }
        else
        {
            int idx = (uint)layerIndex < (uint)constant.Layers.Length ? layerIndex : 0;
            _layerIndex = idx;
            LayerDef layer = constant.Layers[idx];
            _statesStart = layer.StatesStart;
            _statesCount = layer.StatesCount;
            _defaultStateIndex = layer.DefaultStateIndex;
            _anyStart = layer.AnyStateTransitionsStart;
            _anyCount = layer.AnyStateTransitionsCount;
        }

        ReloadBehaviours();
    }

    /// <summary>Resets all parameters to their authored defaults and forgets the current state.</summary>
    public void Reset()
    {
        ResetParametersToDefaults();
        _currentState = -1;
        _fromState = -1;
        _activeRangeStart = -1;
        _activeOffsetInRange = -1;
        _entered = false;
    }

    private void ResetParametersToDefaults()
    {
        ParameterDef[] p = _c.Parameters;
        int n = p.Length;
        for (int i = 0; i < n; i++)
        {
            _params[i] = p[i].Type switch
            {
                AnimatorParameterType.Float => p[i].DefaultFloat,
                AnimatorParameterType.Int => p[i].DefaultInt,
                AnimatorParameterType.Bool => p[i].DefaultBool ? 1f : 0f,
                _ => 0f, // Trigger defaults to 0 (not set).
            };
        }
    }

    // ---- Parameter access (by index) ----

    public float GetFloat(int paramIndex) => _params[paramIndex];
    public void SetFloat(int paramIndex, float value) => _params[paramIndex] = value;
    public int GetInt(int paramIndex) => (int)_params[paramIndex];
    public void SetInt(int paramIndex, int value) => _params[paramIndex] = value;
    public bool GetBool(int paramIndex) => _params[paramIndex] != 0f;
    public void SetBool(int paramIndex, bool value) => _params[paramIndex] = value ? 1f : 0f;

    /// <summary>Sets the trigger parameter (value 1). It is consumed when a transition using it fires.</summary>
    public void SetTrigger(int paramIndex) => _params[paramIndex] = 1f;
    /// <summary>Manually clears a trigger regardless of whether a transition consumed it.</summary>
    public void ResetTrigger(int paramIndex) => _params[paramIndex] = 0f;
    public bool GetTrigger(int paramIndex) => _params[paramIndex] != 0f;

    /// <summary>Looks up a parameter index by name hash. -1 if not found.</summary>
    public int GetParameterIndex(int nameHash) => _c.GetParameterIndex(nameHash);

    /// <summary>
    /// Snaps or crossfades this layer into the named state. Returns false if the name is not on this layer.
    /// </summary>
    public bool Play(string stateName, float fadeDuration = 0f)
        => Play(AnimationNameHash.Hash(stateName), fadeDuration);

    /// <summary>
    /// Snaps or crossfades this layer into the state whose name hashes to <paramref name="stateNameHash"/>.
    /// Returns false if that hash is not on this layer.
    /// </summary>
    public bool Play(int stateNameHash, float fadeDuration = 0f)
    {
        int index = FindStateIndex(stateNameHash);
        if (index < 0) return false;
        _entered = true;
        EnterState(index, fadeDuration, startOffset: 0f);
        return true;
    }

    /// <summary>True if this layer has a state whose name hashes to <paramref name="stateNameHash"/>.</summary>
    public bool HasState(int stateNameHash) => FindStateIndex(stateNameHash) >= 0;

    /// <summary>Current state snapshot for <see cref="global::XEngine.Runtime.Animator.GetCurrentAnimatorStateInfo"/>.</summary>
    public AnimatorStateInfo GetStateInfo()
    {
        var info = default(AnimatorStateInfo);
        if (_currentState < 0 || _currentState >= _c.States.Length) return info;
        StateDef s = _c.States[_currentState];
        info.nameHash = s.NameHash;
        info.isLooping = s.IsLooping;
        Playable? p = _currentPlayable;
        if (p is AnimationClipPlayable cp)
        {
            info.length = (float)cp.Duration;
            info.normalizedTime = cp.NormalizedTime;
        }
        else if (p != null && p.Duration > 0d)
        {
            info.length = (float)p.Duration;
            info.normalizedTime = (float)(p.Time / p.Duration);
        }
        return info;
    }

    private int FindStateIndex(int nameHash)
    {
        StateDef[] s = _c.States;
        int end = _statesStart + _statesCount;
        for (int i = _statesStart; i < end; i++)
        {
            if (s[i].NameHash == nameHash)
                return i;
        }
        return -1;
    }

    // ---- Evaluation ----

    /// <summary>
    /// Advances the FSM one frame: enters the default state on first call, then checks AnyState
    /// transitions and the current state's transitions, firing the first whose conditions (and
    /// ExitTime, if any) are met. Call once per frame before advancing the playable graph.
    /// </summary>
    public void Evaluate(float dt)
    {
        _evaluationDelta = dt;
        if (!_entered)
        {
            _entered = true;
            if (_statesCount == 0) return;
            int start = _defaultStateIndex;
            int end = _statesStart + _statesCount;
            if (start < _statesStart || start >= end) start = _statesStart;
            // Finite default clips advance on the first graph step. Zero-duration marker clips
            // are held by EnterState because they use the entry frame as their authored sample.
            EnterState(start, fadeDuration: 0f, startOffset: 0f, holdEntryFrame: false);
            DispatchUpdateMove();
            return;
        }

        if (_currentState < 0) return;

        if (IsInTransition)
        {
            TryInterruptActiveTransition();
            if (_currentPlayable is BlendTreePlayable fadingBt)
                DriveBlendTree(fadingBt);
            DispatchUpdateMove();
            return;
        }

        // AnyState transitions take precedence.
        if (_anyCount > 0 && TryFireTransitionInRange(_anyStart, _anyCount, anyState: true, orderedLimit: -1))
        {
            DispatchUpdateMove();
            return;
        }

        // Current state's transitions.
        StateDef s = _c.States[_currentState];
        if (s.TransitionsCount > 0 &&
            TryFireTransitionInRange(s.TransitionsStart, s.TransitionsCount, anyState: false, orderedLimit: -1))
        {
            DispatchUpdateMove();
            return;
        }

        // Drive the current state's BlendTree parameter (if any) from the controller parameter table.
        if (_currentPlayable is BlendTreePlayable btp)
            DriveBlendTree(btp);
        ApplyStatePlayback(in s);
        ApplyStateParameterCurves(in s);
        DispatchUpdateMove();
    }

    /// <summary>
    /// M61 — while in this state, each parameter curve overwrites its parameter with the value
    /// sampled at the state's normalized time (Unity state parameter curves; explicit SetFloat
    /// between frames still wins until the next Evaluate).
    /// </summary>
    private void ApplyStateParameterCurves(in StateDef s)
    {
        if (s.CurveParameterIndices.Length == 0 || _currentPlayable == null)
            return;
        Playable p = _currentPlayable;
        float normalized = p.Duration > 0d ? (float)(p.Time / p.Duration) : 0f;
        for (int i = 0; i < s.CurveParameterIndices.Length; i++)
        {
            int idx = s.CurveParameterIndices[i];
            if ((uint)idx < (uint)_params.Length)
                _params[idx] = s.CurveParameterValues[i].Evaluate(normalized);
        }
    }

    private void TryInterruptActiveTransition()
    {
        TransitionInterruptionSource src = _interrupt;
        if (src == TransitionInterruptionSource.None) return;

        bool allowCurrent = src == TransitionInterruptionSource.CurrentState
            || src == TransitionInterruptionSource.CurrentStateThenNextState
            || src == TransitionInterruptionSource.NextStateThenCurrentState;
        bool allowNext = src == TransitionInterruptionSource.NextState
            || src == TransitionInterruptionSource.CurrentStateThenNextState
            || src == TransitionInterruptionSource.NextStateThenCurrentState;
        bool currentFirst = src == TransitionInterruptionSource.CurrentState
            || src == TransitionInterruptionSource.CurrentStateThenNextState;

        if (currentFirst)
        {
            if (allowCurrent && TryInterruptCurrent()) return;
            if (allowNext) TryInterruptNext();
        }
        else
        {
            if (allowNext && TryInterruptNext()) return;
            if (allowCurrent) TryInterruptCurrent();
        }
    }

    private bool TryInterruptCurrent()
    {
        int ordered = OrderedLimitForRange(_anyStart, _anyCount);
        if (_anyCount > 0 && TryFireTransitionInRange(_anyStart, _anyCount, anyState: true, orderedLimit: ordered))
            return true;
        if (_fromState < 0 || _fromState >= _c.States.Length) return false;
        StateDef from = _c.States[_fromState];
        ordered = OrderedLimitForRange(from.TransitionsStart, from.TransitionsCount);
        return from.TransitionsCount > 0
            && TryFireTransitionInRange(from.TransitionsStart, from.TransitionsCount, anyState: false, orderedLimit: ordered);
    }

    private bool TryInterruptNext()
    {
        if (_currentState < 0 || _currentState >= _c.States.Length) return false;
        StateDef next = _c.States[_currentState];
        int ordered = OrderedLimitForRange(next.TransitionsStart, next.TransitionsCount);
        return next.TransitionsCount > 0
            && TryFireTransitionInRange(next.TransitionsStart, next.TransitionsCount, anyState: false, orderedLimit: ordered);
    }

    private int OrderedLimitForRange(int start, int count)
    {
        if (!_orderedInterrupt || _activeRangeStart != start || _activeOffsetInRange < 0)
            return -1;
        if (_activeOffsetInRange >= count) return -1;
        return _activeOffsetInRange;
    }

    private bool TryFireTransitionInRange(int start, int count, bool anyState, int orderedLimit)
    {
        TransitionDef[] trans = _c.Transitions;
        int self = _fromState >= 0 && IsInTransition ? _fromState : _currentState;
        int last = orderedLimit >= 0 ? orderedLimit : count;
        bool anySolo = false;
        for (int i = 0; i < last; i++)
        {
            TransitionDef cand = trans[start + i];
            if (cand.Mute) continue;
            if (cand.Solo)
            {
                anySolo = true;
                break;
            }
        }

        for (int i = 0; i < last; i++)
        {
            TransitionDef t = trans[start + i];
            if (t.Mute) continue;
            if (anySolo && !t.Solo) continue;
            if (anyState && !t.CanTransitionToSelf && t.DestinationStateIndex == self)
                continue;
            if (TransitionReady(t))
            {
                FireTransition(t, start, i);
                return true;
            }
        }
        return false;
    }

    private bool TransitionReady(TransitionDef t)
    {
        // ExitTime gate: only meaningful for clip states (BlendTree states have no single normalized
        // time; their transitions should use conditions, not ExitTime).
        if (t.HasExitTime)
        {
            if (GetCurrentPlayable() is not AnimationClipPlayable cp)
                return false;
            // A zero-duration clip is a valid marker state. Its playable clock advances in
            // seconds and NormalizedTime exposes that clock, so ExitTime remains meaningful.
            if (cp.Duration < 0d)
                return false;

            // Unity treats an authored exitTime of zero as the end of the first cycle,
            // rather than as an immediately satisfied gate. This matters for one-shot
            // controller states such as Icey's Hit clip: the transition is evaluated at
            // the first normalized-time boundary after entering the state.
            bool zeroExitTime = t.ExitTime <= 0f;
            float exitTime = zeroExitTime ? 1f : t.ExitTime;
            // Unity starts a transition on the frame whose graph step crosses ExitTime. The
            // controller is evaluated before the playable graph here, so compare the pending
            // clock (current time plus this evaluation's delta) rather than waiting for the
            // next call. This applies to finite, looping, and zero-duration marker clips; the
            // playable itself still samples the source state at its normal entry/final time.
            float current = (float)cp.Time;
            float pending = current + _evaluationDelta * cp.Speed;
            float predicted = cp.Duration > 0d
                ? pending / (float)cp.Duration
                : pending;
            // Finite clips use the pending graph time. A zero-duration marker keeps its exact
            // boundary sample for one evaluation; finite clips fire on the evaluation that
            // reaches the boundary. The small epsilon absorbs float quantization at authored
            // key times such as 0.9 of a 1.5-second clip.
            float boundaryClock = predicted;
            // The controller clock and authored ExitTime are single-precision values. At
            // boundaries such as 0.9 * 1.5, adding 1/60 can undershoot the same authored
            // boundary by a few ulps even though Unity fires on this graph step. Keep this
            // tolerance for finite clips only; zero-duration marker states intentionally keep
            // their exact-boundary one-evaluation delay below.
            const float epsilon = 1e-5f;
            // Keep explicit first-cycle (ExitTime >= 1) semantics exact: the remainder tests
            // rely on crossing that boundary on the pending step, not one step before it. The
            // extra ulp tolerance is for sub-cycle exits and authored zero exits (which map to
            // the first cycle boundary).
            float boundaryEpsilon = t.ExitTime < 1f ? epsilon : 0f;
            if (cp.Duration == 0d
                ? boundaryClock <= exitTime
                : boundaryClock + boundaryEpsilon < exitTime)
                return false;
        }

        // All conditions must pass.
        int condCount = t.ConditionsCount;
        if (condCount > 0)
        {
            ConditionDef[] conds = _c.Conditions;
            int cs = t.ConditionsStart;
            for (int i = 0; i < condCount; i++)
            {
                if (!EvaluateCondition(conds[cs + i]))
                    return false;
            }
        }
        return true;
    }

    private void FireTransition(TransitionDef t, int rangeStart, int offsetInRange)
    {
        // Consume trigger parameters referenced by an If condition on a trigger.
        int condCount = t.ConditionsCount;
        if (condCount > 0)
        {
            ConditionDef[] conds = _c.Conditions;
            ParameterDef[] parms = _c.Parameters;
            int cs = t.ConditionsStart;
            for (int i = 0; i < condCount; i++)
            {
                ConditionDef c = conds[cs + i];
                if (c.Mode == ConditionMode.If && parms[c.ParamIndex].Type == AnimatorParameterType.Trigger)
                    _params[c.ParamIndex] = 0f;
            }
        }

        int dest = t.DestinationStateIndex;
        if (dest < 0 || dest >= _c.States.Length) return;

        float sourceDur = 0f;
        AnimationClipPlayable? sourcePlayable = GetCurrentPlayable() as AnimationClipPlayable;
        if (sourcePlayable != null)
            sourceDur = (float)sourcePlayable.Duration;
        float fade = ResolveFadeDuration(in t, sourceDur);

        double entryRemainder = GetTransitionRemainder(sourcePlayable, in t);

        _fromState = _currentState;
        _activeRangeStart = rangeStart;
        _activeOffsetInRange = offsetInRange;
        _interrupt = t.InterruptionSource;
        _orderedInterrupt = t.OrderedInterruption;
        // A long finite clip that reaches an explicit ExitTime=1 is observed by Unity one
        // evaluation after its final graph sample; the destination advances during that same
        // evaluation. Short clips and sub-cycle exits enter on the crossing frame and retain
        // their zero-time entry sample.
        bool holdEntryFrame = !(t.HasExitTime && t.ExitTime >= 1f && sourceDur >= 1f);
        if (entryRemainder > 0d)
            holdEntryFrame = true;
        EnterState(dest, fade, t.Offset, holdEntryFrame, skipFirstFadeUpdate: true,
            entryTimeSeconds: entryRemainder > 0d ? entryRemainder : -1d,
            initialFadeDelta: entryRemainder > 0d ? (float)entryRemainder : 0f);
    }

    /// <summary>
    /// Preserves the part of the source graph step that crossed its clip boundary. Unity applies
    /// that remainder to the destination on the same evaluation; dropping it makes long finite
    /// clips arrive one frame early at discrete Spine keyframes.
    /// </summary>
    private double GetTransitionRemainder(AnimationClipPlayable? source, in TransitionDef transition)
    {
        if (source == null || source.Duration <= 0d || !transition.HasExitTime || transition.Offset != 0f)
            return 0d;

        double duration = source.Duration;
        double unwrapped = source.UnwrappedNormalizedTime * duration;
        double exitTime = transition.ExitTime <= 0f ? 1d : transition.ExitTime;
        double threshold = exitTime * duration;
        double candidate = unwrapped + _evaluationDelta * source.Speed;
        // Carry only the portion of the graph step after the authored exit threshold. This is
        // needed for sub-cycle exits as well as a clip-end crossing (for example 0.797 of Hit).
        if (unwrapped > threshold || candidate <= threshold)
            return 0d;
        double remainder = candidate - threshold;
        return remainder > 0d ? remainder : 0d;
    }

    private bool EvaluateCondition(ConditionDef c)
    {
        float v = _params[c.ParamIndex];
        switch (c.Mode)
        {
            case ConditionMode.If: return v != 0f;
            case ConditionMode.IfNot: return v == 0f;
            case ConditionMode.Greater: return v > c.Threshold;
            case ConditionMode.Less: return v < c.Threshold;
            case ConditionMode.Equals: return (int)v == (int)c.Threshold;
            case ConditionMode.NotEqual: return (int)v != (int)c.Threshold;
            default: return false;
        }
    }

    private void EnterState(
        int stateIndex,
        float fadeDuration,
        float startOffset,
        bool holdEntryFrame = true,
        bool skipFirstFadeUpdate = false,
        double entryTimeSeconds = -1d,
        float initialFadeDelta = 0f)
    {
        int previous = _currentState;
        if (previous >= 0)
            Dispatch(previous, BehaviourCb.Exit);

        _currentState = stateIndex;
        StateDef s = _c.States[stateIndex];

        byte kind = (uint)stateIndex < (uint)_c.MotionKind.Length ? _c.MotionKind[stateIndex] : (byte)0;
        if (kind == 1)
        {
            // BlendTree state: build a one-shot BlendTreePlayable and play it directly on the layer.
            BlendTree? bt = (uint)stateIndex < (uint)_c.MotionBlendTrees.Length ? _c.MotionBlendTrees[stateIndex] : null;
            if (bt == null || _animator.Skeleton == null)
            {
                _currentPlayable = null;
                _currentBlendTreeParamIndex = -1;
                _currentBlendTreeParamIndexY = -1;
            }
            else
            {
                BlendTreePlayable btp = BlendTreePlayable.Build(bt, _animator.Skeleton, _animator.AuthoringController);
                btp.Speed = s.Speed;
                _animator.PlayPlayableOnMixer(btp, fadeDuration, MixerOrBase());
                _currentPlayable = btp;
                _currentBlendTreeParamIndex = (uint)stateIndex < (uint)_c.MotionBlendTreeParamIndex.Length
                    ? _c.MotionBlendTreeParamIndex[stateIndex] : -1;
                _currentBlendTreeParamIndexY = (uint)stateIndex < (uint)_c.MotionBlendTreeParamIndexY.Length
                    ? _c.MotionBlendTreeParamIndexY[stateIndex] : -1;
                ApplyStatePlayback(in s);
            }
            Dispatch(stateIndex, BehaviourCb.Enter);
            return;
        }

        // Clip state.
        AnimationClip? clip = ResolveMotionClip(s.MotionIndex);
        if (clip == null)
        {
            _currentPlayable = null;
            _currentBlendTreeParamIndex = -1;
            _currentBlendTreeParamIndexY = -1;
            Dispatch(stateIndex, BehaviourCb.Enter);
            return;
        }

        AnimationClipPlayable p = _animator.PlayClipOnMixer(
            clip, fadeDuration, MixerOrBase(), skipFirstFadeUpdate, holdEntryFrame, initialFadeDelta);
        p.Speed = s.Speed;
        p.Wrap = s.IsLooping ? AnimationWrapMode.Loop : AnimationWrapMode.Once;
        _currentPlayable = p;
        _currentBlendTreeParamIndex = -1;
        _currentBlendTreeParamIndexY = -1;

        bool mirror = ResolveMirror(in s);
        if (p.MirrorBones != mirror)
        {
            p.MirrorBones = mirror;
            if (_animator.Skeleton != null)
                p.Bind(_animator.Skeleton);
        }

        float cycle = s.CycleOffset;
        if (s.CycleOffsetParameterIndex >= 0)
            cycle = GetFloat(s.CycleOffsetParameterIndex);
        float start = startOffset + cycle;
        start -= MathF.Floor(start);
        if (entryTimeSeconds >= 0d)
            p.SetPlaybackTime(entryTimeSeconds);
        else if (start > 0f)
            p.SetPlaybackTime(p.Duration > 0d ? start * p.Duration : p.Duration == 0d ? start : 0d);
        ApplyStatePlayback(in s);
        Dispatch(stateIndex, BehaviourCb.Enter);
    }

    private bool ResolveMirror(in StateDef s)
    {
        if (s.MirrorParameterIndex >= 0)
            return GetFloat(s.MirrorParameterIndex) > 0.5f;
        return s.Mirror;
    }

    private void ApplyStatePlayback(in StateDef s)
    {
        if (_currentPlayable == null) return;
        if (s.TimeParameterIndex >= 0 && _currentPlayable.Duration >= 0d)
        {
            float nt = GetFloat(s.TimeParameterIndex);
            nt -= MathF.Floor(nt);
            if (_currentPlayable is AnimationClipPlayable cp)
                cp.SetPlaybackTime(cp.Duration > 0d ? nt * cp.Duration : nt);
            else
                _currentPlayable.SetTime(_currentPlayable.Duration > 0d ? nt * _currentPlayable.Duration : nt);
        }
        float speed = s.Speed;
        if (s.SpeedMultiplierParameterIndex >= 0)
            speed *= GetFloat(s.SpeedMultiplierParameterIndex);
        _currentPlayable.Speed = speed;
    }

    /// <summary>M35: follow another layer's local state (and optionally its normalized time).</summary>
    public void SyncFrom(AnimatorControllerRuntime source, bool affectTiming)
    {
        int local = source.CurrentLocalStateIndex;
        if (local < 0 || _statesCount <= 0) return;
        if (local >= _statesCount) local = 0;
        int dest = _statesStart + local;
        float srcNorm = affectTiming ? source.CurrentNormalizedTime : 0f;
        if (_currentState != dest)
            EnterState(dest, fadeDuration: 0f, startOffset: srcNorm);
        else if (affectTiming && _currentPlayable != null && _currentPlayable.Duration >= 0d)
            if (_currentPlayable is AnimationClipPlayable cp)
                cp.SetPlaybackTime(cp.Duration > 0d ? srcNorm * cp.Duration : srcNorm);
            else
                _currentPlayable.SetTime(_currentPlayable.Duration > 0d ? srcNorm * _currentPlayable.Duration : srcNorm);
    }

    public float CurrentNormalizedTime
    {
        get
        {
            if (_currentPlayable is AnimationClipPlayable cp)
                return cp.NormalizedTime;
            return 0f;
        }
    }

    private AnimationClip? GetCurrentClip()
        => (_currentState >= 0 && _currentState < _c.States.Length)
            ? ResolveMotionClip(_c.States[_currentState].MotionIndex)
            : null;

    private AnimationPlayable? GetCurrentPlayable() => _currentPlayable;

    private AnimationClip? ResolveMotionClip(int motionIndex)
        => (motionIndex >= 0 && motionIndex < _c.MotionClips.Length) ? _c.MotionClips[motionIndex] : null;

    internal void RefreshPublishedMotions(System.Collections.Generic.IReadOnlyDictionary<Guid, EngineObject?> changes)
    {
        for (int i = 0; i < _motionClipIds.Length; i++)
            if (_motionClipIds[i] != Guid.Empty && changes.TryGetValue(_motionClipIds[i], out var replacement))
                _c.MotionClips[i] = replacement as AnimationClip;
    }

    private AnimationMixerPlayable MixerOrBase() => _mixer ?? _animator.Layer!;

    void DriveBlendTree(BlendTreePlayable btp)
    {
        int px = btp.ParamIndex >= 0 ? btp.ParamIndex : _currentBlendTreeParamIndex;
        if (px >= 0) btp.ParameterValue = GetFloat(px);
        int py = btp.ParamIndexY >= 0 ? btp.ParamIndexY : _currentBlendTreeParamIndexY;
        if (py >= 0) btp.ParameterValueY = GetFloat(py);
        if (btp.Type == BlendType.Direct)
        {
            int n = btp.InputCount;
            for (int i = 0; i < n; i++)
            {
                int pi = btp.DirectParamIndex(i);
                if (pi >= 0) btp.SetDirectWeight(i, GetFloat(pi));
            }
        }
        int inputs = btp.InputCount;
        for (int i = 0; i < inputs; i++)
        {
            if (btp.GetInput(i) is BlendTreePlayable nested)
                DriveBlendTree(nested);
        }
    }

    /// <summary>
    /// Recreates behaviour instances for this layer's states (script reload / tests). Load-time only —
    /// the per-frame dispatch loop never allocates.
    /// </summary>
    public void ReloadBehaviours()
    {
        int[] indices = _c.BehaviourTypeIndices;
        string[] names = _c.BehaviourTypeNames;
        var instances = new StateMachineBehaviour?[indices.Length];
        int end = _statesStart + _statesCount;
        StateDef[] states = _c.States;
        for (int si = _statesStart; si < end && si < states.Length; si++)
        {
            StateDef s = states[si];
            int bEnd = s.BehavioursStart + s.BehavioursCount;
            for (int b = s.BehavioursStart; b < bEnd && b < indices.Length; b++)
            {
                int ti = indices[b];
                if ((uint)ti >= (uint)names.Length) continue;
                instances[b] = CreateBehaviour(names[ti]);
            }
        }
        _instances = instances;
    }

    /// <summary>Behaviour at <paramref name="slot"/> on <paramref name="stateIndex"/>, or null.</summary>
    public StateMachineBehaviour? GetBehaviour(int stateIndex, int slot)
    {
        if ((uint)stateIndex >= (uint)_c.States.Length) return null;
        StateDef s = _c.States[stateIndex];
        if ((uint)slot >= (uint)s.BehavioursCount) return null;
        int i = s.BehavioursStart + slot;
        return (uint)i < (uint)_instances.Length ? _instances[i] : null;
    }

    private enum BehaviourCb : byte { Enter, Update, Exit, Move }

    private void DispatchUpdateMove()
    {
        if (_currentState < 0) return;
        Dispatch(_currentState, BehaviourCb.Update);
        Dispatch(_currentState, BehaviourCb.Move);
    }

    private void Dispatch(int stateIndex, BehaviourCb cb)
    {
        StateMachineBehaviour?[] instances = _instances;
        if (instances.Length == 0) return;
        if ((uint)stateIndex >= (uint)_c.States.Length) return;
        StateDef s = _c.States[stateIndex];
        int count = s.BehavioursCount;
        if (count == 0) return;
        AnimatorStateInfo info = GetStateInfo();
        int start = s.BehavioursStart;
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            if ((uint)idx >= (uint)instances.Length) continue;
            StateMachineBehaviour? b = instances[idx];
            if (b == null) continue;
            switch (cb)
            {
                case BehaviourCb.Enter: b.OnStateEnter(_animator, info, _layerIndex); break;
                case BehaviourCb.Update: b.OnStateUpdate(_animator, info, _layerIndex); break;
                case BehaviourCb.Exit: b.OnStateExit(_animator, info, _layerIndex); break;
                default: b.OnStateMove(_animator, info, _layerIndex); break;
            }
        }
    }

    private static StateMachineBehaviour? CreateBehaviour(string typeName)
    {
        Type? t = RuntimeUtils.FindType(typeName);
        if (t == null || t.IsAbstract || !typeof(StateMachineBehaviour).IsAssignableFrom(t))
            return null;
        try
        {
            return Activator.CreateInstance(t) as StateMachineBehaviour;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
