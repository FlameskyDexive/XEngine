// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Audio;

/// <summary>Fade curve shapes for snapshot transitions and volume automation (plan N3).</summary>
public enum AudioFadeCurve
{
    /// <summary>Constant slope in dB.</summary>
    Linear = 0,
    /// <summary>Equal-power: sin-shaped rise in linear space, no perceived level dip.</summary>
    EqualPower = 1,
    /// <summary>Smoothstep ease-in-out in dB.</summary>
    Smooth = 2,
}

/// <summary>Evaluates fade progress t∈[0,1] for the supported curves.</summary>
public static class AudioFade
{
    public static float Evaluate(AudioFadeCurve curve, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return curve switch
        {
            AudioFadeCurve.EqualPower => MathF.Sin(t * MathF.PI * 0.5f),
            AudioFadeCurve.Smooth => t * t * (3f - 2f * t),
            _ => t,
        };
    }
}

/// <summary>
/// A request that ducks a group while a voice (or any system) holds it (plan N3).
/// Handles are generation-checked: using a released handle is a safe no-op. Acquiring the same
/// group again stacks holders and takes the deeper duck; the group tweens back over its release
/// time after the last holder releases.
/// </summary>
public struct AudioDuckHandle : IEquatable<AudioDuckHandle>
{
    internal int Generation;
    internal int Id;

    internal AudioDuckHandle(int id, int generation) { Id = id; Generation = generation; }

    public bool IsValid => Id != 0;

    public bool Equals(AudioDuckHandle other) => Id == other.Id && Generation == other.Generation;
    public override bool Equals(object? obj) => obj is AudioDuckHandle other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Id, Generation);
    public static bool operator ==(AudioDuckHandle a, AudioDuckHandle b) => a.Equals(b);
    public static bool operator !=(AudioDuckHandle a, AudioDuckHandle b) => !a.Equals(b);
}

/// <summary>
/// Per-instance automation (plan N3): snapshot transition tweens and duck envelopes. Fixed
/// capacity, zero allocation in steady state; ticked from the instance's main-thread update.
/// <para>
/// Effective volume layering per group: base (asset default or snapshot override) is offset by
/// the transition tween while one runs, minus the deepest duck envelope reduction. Every layer
/// lands in the same effective values N1/N2 already consume, so fades and ducks propagate to
/// playing sources and native bus nodes automatically.
/// </para>
/// </summary>
public sealed class AudioMixerAutomation
{
    private const int MaxDucks = 16;

    private readonly AudioMixerInstance _owner;

    // Snapshot transition: one active tween across all groups.
    private float[] _transitionFrom = Array.Empty<float>();
    private float[] _transitionTo = Array.Empty<float>();
    private float _transitionElapsed;
    private float _transitionDuration = 1f;
    private AudioFadeCurve _transitionCurve;
    private string _transitionTargetSnapshot = "";
    private bool _transitionActive;

    // Ducks: fixed pool; generation counters invalidate released handles. A slot stays visible
    // (used=false, level>0) while its release envelope plays out.
    private readonly int[] _duckGroups = new int[MaxDucks];
    private readonly float[] _duckDepthDB = new float[MaxDucks];
    private readonly float[] _duckAttack = new float[MaxDucks];
    private readonly float[] _duckRelease = new float[MaxDucks];
    private readonly float[] _duckLevel = new float[MaxDucks];  // 0=full volume, 1=fully ducked
    private readonly int[] _duckHolders = new int[MaxDucks];
    private readonly int[] _duckGeneration = new int[MaxDucks];
    private readonly bool[] _duckHeld = new bool[MaxDucks];

    /// <summary>True while a snapshot transition is playing.</summary>
    public bool IsTransitioning => _transitionActive;

    /// <summary>The snapshot the active transition is moving to ("" when idle).</summary>
    public string TransitionTargetSnapshot => _transitionTargetSnapshot;

    public AudioMixerAutomation(AudioMixerInstance owner) => _owner = owner;

    /// <summary>Starts (or replaces) a transition to a snapshot over <paramref name="seconds"/>.
    /// Groups the snapshot does not list tween back to their asset defaults. Returns false when
    /// the snapshot does not exist. Cancelling = starting another transition, or
    /// <see cref="CancelTransition"/> to freeze in place.</summary>
    public bool TransitionToSnapshot(string snapshotName, float seconds, AudioFadeCurve curve = AudioFadeCurve.Smooth)
    {
        int count = _owner.GroupCount;
        if (!_owner.HasSnapshot(snapshotName))
            return false;

        // From = the audible values right now (possibly mid-transition); to = the snapshot's
        // final base values. The base layer is applied immediately and masked by the tween
        // layer until it completes.
        EnsureCapacity(count);
        for (int i = 0; i < count; i++)
        {
            _transitionFrom[i] = _owner.GetEffectiveVolumeDB(i);
        }
        _owner.ApplySnapshot(snapshotName);
        for (int i = 0; i < count; i++)
        {
            _transitionTo[i] = _owner.GetBaseVolumeDB(i);
        }

        _transitionDuration = MathF.Max(0.0001f, seconds);
        _transitionElapsed = 0;
        _transitionCurve = curve;
        _transitionTargetSnapshot = snapshotName;
        // Activate the tween layer at the start values immediately: without this there would be
        // a one-frame jump to the snapshot level before the first tick.
        for (int i = 0; i < count; i++)
            _owner.SetTransitionDB(i, _transitionFrom[i]);
        _transitionActive = true;
        return true;
    }

    /// <summary>Stops the active transition where it is: the tween layer freezes at the current
    /// value until replaced. Effective volumes do not jump.</summary>
    public void CancelTransition()
    {
        if (!_transitionActive) return;
        for (int i = 0; i < _owner.GroupCount && i < _transitionTo.Length; i++)
            _owner.SetTransitionDB(i, _owner.GetTweenDB(i));
        _transitionActive = false;
        _transitionTargetSnapshot = "";
    }

    /// <summary>Acquires a duck on a group: while held, the group's effective gain is reduced by
    /// <paramref name="duckDB"/> (attack seconds), tweening back over
    /// <paramref name="releaseSeconds"/> after the last release.</summary>
    public AudioDuckHandle AcquireDuck(int groupIndex, float duckDB, float attackSeconds, float releaseSeconds)
    {
        if (groupIndex < 0 || groupIndex >= _owner.GroupCount)
            return default;

        for (int i = 0; i < MaxDucks; i++)
        {
            if (_duckHeld[i] && _duckGroups[i] == groupIndex)
            {
                _duckHolders[i]++;
                // Depth is a positive magnitude (dB of reduction); stacked ducks take the deepest.
                _duckDepthDB[i] = MathF.Max(_duckDepthDB[i], MathF.Abs(duckDB));
                return new AudioDuckHandle(i + 1, _duckGeneration[i]);
            }
        }

        for (int i = 0; i < MaxDucks; i++)
        {
            if (_duckHeld[i]) continue;
            _duckHeld[i] = true;
            _duckGroups[i] = groupIndex;
            _duckDepthDB[i] = MathF.Abs(duckDB);
            _duckAttack[i] = MathF.Max(0f, attackSeconds);
            _duckRelease[i] = MathF.Max(0f, releaseSeconds);
            _duckLevel[i] = 0f;
            _duckHolders[i] = 1;
            return new AudioDuckHandle(i + 1, _duckGeneration[i]);
        }
        return default; // pool exhausted
    }

    /// <summary>Releases one hold. The duck tweens back over its release time once the last
    /// holder releases. Stale handles are safe no-ops returning false.</summary>
    public bool ReleaseDuck(ref AudioDuckHandle handle)
    {
        if (!handle.IsValid) return false;
        int i = handle.Id - 1;
        if ((uint)i >= (uint)MaxDucks || !_duckHeld[i] || _duckGeneration[i] != handle.Generation)
        {
            handle = default;
            return false;
        }

        if (--_duckHolders[i] <= 0)
        {
            _duckHolders[i] = 0;
            _duckHeld[i] = false;
            _duckGeneration[i]++;
        }
        handle = default;
        return true;
    }

    /// <summary>Duck state summary for a group: current reduction in dB (deepest envelope) and
    /// the number of live holders (UI shows the ducking source).</summary>
    public (float ReductionDB, int Holders) GetDuckInfo(int groupIndex)
    {
        float reduction = 0;
        int holders = 0;
        for (int i = 0; i < MaxDucks; i++)
        {
            if (!_duckHeld[i] && _duckLevel[i] <= 0f) continue;
            if (_duckGroups[i] != groupIndex) continue;
            reduction = MathF.Max(reduction, _duckDepthDB[i] * Math.Clamp(_duckLevel[i], 0f, 1f));
            holders += _duckHeld[i] ? Math.Max(1, _duckHolders[i]) : 0;
        }
        return (reduction, holders);
    }

    /// <summary>Advances the transition tween and duck envelopes, rewriting effective volumes.
    /// Zero allocation; a no-op frame writes nothing.</summary>
    public void Tick(float deltaSeconds)
    {
        bool changed = false;

        if (_transitionActive)
        {
            _transitionElapsed += deltaSeconds;
            float t = Math.Clamp(_transitionElapsed / _transitionDuration, 0f, 1f);
            float eased = AudioFade.Evaluate(_transitionCurve, t);
            int count = Math.Min(_owner.GroupCount, _transitionTo.Length);
            for (int i = 0; i < count; i++)
                _owner.SetTransitionDB(i, _transitionFrom[i] + (_transitionTo[i] - _transitionFrom[i]) * eased);
            changed = true;
            if (t >= 1f)
            {
                // Tween reached the base layer: drop the mask so future base edits show through.
                _owner.ClearTransitionLayer();
                _transitionActive = false;
                _transitionTargetSnapshot = "";
            }
        }

        for (int i = 0; i < MaxDucks; i++)
        {
            bool releasing = !_duckHeld[i] && _duckLevel[i] > 0f;
            if (!_duckHeld[i] && !releasing) continue;

            float target = _duckHeld[i] ? 1f : 0f;
            float speed = _duckHeld[i]
                ? (_duckAttack[i] > 0f ? 1f / _duckAttack[i] : 1e6f)
                : (_duckRelease[i] > 0f ? 1f / _duckRelease[i] : 1e6f);
            float previous = _duckLevel[i];
            _duckLevel[i] = previous < target
                ? Math.Min(previous + speed * deltaSeconds, target)
                : Math.Max(previous - speed * deltaSeconds, target);
            if (_duckLevel[i] != previous)
                changed = true;
        }

        if (changed)
            _owner.RefreshEffectiveFromAutomation();
    }

    private void EnsureCapacity(int count)
    {
        if (_transitionFrom.Length < count)
        {
            _transitionFrom = new float[count];
            _transitionTo = new float[count];
        }
    }
}
