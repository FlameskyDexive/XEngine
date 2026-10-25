// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

using XEngine;

namespace XEngine.Playables;

/// <summary>
/// Base class for every node in a <see cref="PlayableGraph"/>. A playable owns a local time, a
/// weight, a playback state, and (optionally) a bounded duration. Subclasses implement the two
/// evaluation hooks <see cref="PrepareFrame"/> and <see cref="ProcessFrame"/> to advance time and
/// produce their typed output.
/// </summary>
/// <remarks>
/// <para>
/// This is XEngine's equivalent of Unity's <c>Playable</c> / <c>PlayableBehaviour</c>: a general,
/// type-agnostic graph node. Unlike Unity's handle-based model it is a plain class with direct
/// field references — there is no native handle indirection, no port system, and inputs are managed
/// by subclasses via <see cref="InputCount"/>/<see cref="GetInput"/>.
/// </para>
/// <para>
/// <b>Extensibility:</b> the base class is intentionally <c>public abstract</c> so user code can
/// subclass it (IK jobs, look-at, procedural animation, custom Timeline tracks, ...). Override
/// <see cref="PrepareFrame"/> to advance state, <see cref="ProcessFrame"/> to read child outputs and
/// produce your own. Call <c>base.PrepareFrame(ctx)</c> to keep weight fades running.
/// </para>
/// </remarks>
public abstract class Playable
{
    private double _time;
    private float _weight;
    private float _targetWeight;
    // Absolute fade state: weight = start + (target - start) * easing(elapsed / duration).
    // The elapsed-based form (instead of incremental stepping) is what allows easing curves and
    // FixedSpeed/Normalized durations without drift; Linear + FixedDuration reproduces the
    // historical incremental fade exactly.
    private float _fadeDuration;
    private float _fadeElapsed;
    private float _fadeStartWeight;
    private FadeEasing _fadeEasing;
    private bool _skipNextFadeStep;
    private PlayableState _state;

    /// <summary>Local playback time in seconds.</summary>
    public double Time
    {
        get => _time;
        set => _time = value;
    }

    /// <summary>
    /// Contribution weight in [0,1] to this node's parent mixer. Setting it directly snaps to the
    /// value and cancels any in-progress fade. Use <see cref="StartFade"/> for smooth transitions.
    /// </summary>
    public float Weight
    {
        get => _weight;
        set
        {
            _weight = value;
            _targetWeight = value;
            _fadeDuration = 0f;
            _skipNextFadeStep = false;
        }
    }

    /// <summary>The weight this node is currently fading towards. Equal to <see cref="Weight"/> when no fade is active.</summary>
    public float TargetWeight => _targetWeight;

    /// <summary>Whether a weight fade is in progress.</summary>
    public bool IsFading => _fadeDuration > 0f;

    /// <summary>Playback rate multiplier applied to local time advancement. Defaults to 1.</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>
    /// Bounded duration in seconds, or -1 for an indefinite node (state-machine driven). Subclasses
    /// set this from their source data (e.g. an AnimationClip's length).
    /// </summary>
    public double Duration { get; protected set; } = -1d;

    /// <summary>Current runtime state. See <see cref="PlayableState"/>.</summary>
    public PlayableState State => _state;

    /// <summary>True when <see cref="State"/> is <see cref="PlayableState.Playing"/>.</summary>
    public bool IsPlaying => _state == PlayableState.Playing;

    /// <summary>True when <see cref="State"/> is <see cref="PlayableState.Done"/>.</summary>
    public bool IsDone => _state == PlayableState.Done;

    /// <summary>
    /// Number of child inputs. The base node has no inputs; mixers / layer nodes override this
    /// together with <see cref="GetInput"/>. The graph traverses children via these two members
    /// (indexed access, no enumerator allocation).
    /// </summary>
    public virtual int InputCount => 0;

    /// <summary>Fetches the <paramref name="index"/>-th child input. Override alongside <see cref="InputCount"/>.</summary>
    public virtual Playable GetInput(int index)
    {
        ThrowIndexOutOfRange(index);
        return null!; // unreachable
    }

    /// <summary>Starts advancing local time each frame.</summary>
    public void Play() => _state = PlayableState.Playing;

    /// <summary>Freezes local time at its current value.</summary>
    public void Pause() => _state = PlayableState.Paused;

    /// <summary>Stops playback and resets local time to zero.</summary>
    public virtual void Stop()
    {
        _state = PlayableState.Done;
        _time = 0d;
    }

    /// <summary>Sets the local time directly (scrub within a single node).</summary>
    public virtual void SetTime(double t) => _time = t;

    /// <summary>
    /// Begins (or replaces) a weight fade towards <paramref name="targetWeight"/> over
    /// <paramref name="duration"/> seconds (Animancer <see cref="FadeMode.FixedDuration"/>, linear).
    /// A non-positive duration snaps immediately.
    /// </summary>
    public void StartFade(float targetWeight, float duration, bool skipFirstUpdate = false)
        => StartFade(targetWeight, duration, FadeMode.FixedDuration, FadeEasing.Linear, skipFirstUpdate);

    /// <summary>
    /// Begins (or replaces) a weight fade towards <paramref name="targetWeight"/> with explicit
    /// <paramref name="mode"/> timing and an optional easing curve. See <see cref="FadeMode"/> for the
    /// per-mode duration semantics; FromStart restart handling happens at the <c>Animator</c> level and
    /// is treated here as fixed-duration timing.
    /// </summary>
    public void StartFade(
        float targetWeight,
        float duration,
        FadeMode mode,
        FadeEasing easing = FadeEasing.Linear,
        bool skipFirstUpdate = false)
    {
        float resolved = duration;
        if (mode is FadeMode.NormalizedFixedDuration or FadeMode.NormalizedFixedSpeed or FadeMode.NormalizedFromStart
            && Duration > 0d)
        {
            resolved *= (float)Duration;
        }

        float distance = MathF.Abs(_weight - targetWeight);
        if (mode is FadeMode.FixedSpeed or FadeMode.NormalizedFixedSpeed)
            resolved *= distance; // speed 1/duration → covering `distance` takes distance × duration.

        if (resolved <= 0f || distance <= 1e-5f)
        {
            _weight = targetWeight;
            _targetWeight = targetWeight;
            _fadeDuration = 0f;
            _skipNextFadeStep = false;
            return;
        }

        _targetWeight = targetWeight;
        _fadeDuration = resolved;
        _fadeElapsed = 0f;
        _fadeStartWeight = _weight;
        _fadeEasing = easing;
        _skipNextFadeStep = skipFirstUpdate;
    }

    /// <summary>
    /// Applies a small transition remainder immediately, then defers the regular graph step when
    /// requested. Controller transitions use this for the fraction of the source frame that crossed
    /// ExitTime; the destination clock and both fade weights must see the same fraction.
    /// </summary>
    internal void AdvanceFadeRemainder(float deltaTime, bool deferNextUpdate)
    {
        if (_fadeDuration <= 0f)
        {
            _skipNextFadeStep = false;
            return;
        }

        _skipNextFadeStep = false;
        if (deltaTime > 0f)
            UpdateFade(deltaTime);
        _skipNextFadeStep = deferNextUpdate && _fadeDuration > 0f;
    }

    /// <summary>
    /// Phase 1 hook: advance local time, update weight fades, decide which children are active this
    /// frame. Called top-down (pre-order): the node is updated before its children. The base
    /// implementation advances the weight fade only — subclasses should call <c>base.PrepareFrame</c>
    /// after advancing their own time, or before if the fade must reflect the pre-advance weight.
    /// </summary>
    [HotPath]
    public virtual void PrepareFrame(PlayableContext ctx)
    {
        UpdateFade(ctx.DeltaTime);
    }

    /// <summary>
    /// Phase 2 hook: read child outputs and produce this node's own output. Called bottom-up
    /// (post-order): all children have already been processed when this runs.
    /// </summary>
    [HotPath]
    public virtual void ProcessFrame(PlayableContext ctx)
    {
    }

    /// <summary>Advances the weight fade by <paramref name="dt"/> seconds. No-op when not fading.</summary>
    [HotPath]
    protected void UpdateFade(float dt)
    {
        if (_fadeDuration <= 0f)
            return;

        // Animator transitions are scheduled during the controller evaluation that precedes
        // graph preparation. Unity exposes the newly-created transition at zero elapsed fade
        // time on that same frame, so defer the first weight step while still allowing the
        // playable itself to sample its entry pose.
        if (_skipNextFadeStep)
        {
            _skipNextFadeStep = false;
            return;
        }

        _fadeElapsed += dt;
        if (_fadeElapsed >= _fadeDuration)
        {
            _weight = _targetWeight;
            _fadeDuration = 0f;
            return;
        }

        float progress = FadeEasingMath.Evaluate(_fadeEasing, _fadeElapsed / _fadeDuration);
        _weight = _fadeStartWeight + (_targetWeight - _fadeStartWeight) * progress;
    }

    // Kept off the hot path: exception construction allocates, so it lives in a cold helper.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndexOutOfRange(int index)
        => throw new IndexOutOfRangeException(
            "Playable input index " + index + " is out of range. Override InputCount/GetInput to expose children.");
}
