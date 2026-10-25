// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Animation;

/// <summary>
/// M48 — lightweight parameter store for the Animator's code-driven mode (Animancer
/// <c>ParameterDictionary</c> equivalent): hash-keyed float values with optional exponentially
/// damped targets (<c>SmoothedFloatParameter</c> semantics). FSM controllers keep their own compiled
/// parameter table; this store serves <see cref="Transitions.MixerTransition"/>s and gameplay code
/// that drives blends without a controller asset. Unused stores cost nothing (lazy dictionaries,
/// empty tick early-out).
/// </summary>
public sealed class AnimatorParameterStore
{
    private struct Smoothed
    {
        public float Target;
        public float Rate; // 1 / dampSeconds.
    }

    private Dictionary<int, float>? _values;
    private Dictionary<int, Smoothed>? _smoothed;

    /// <summary>Current value for <paramref name="hash"/>, or 0 when never set.</summary>
    public float GetFloat(int hash)
        => _values != null && _values.TryGetValue(hash, out float v) ? v : 0f;

    /// <summary>Current value for the hashed <paramref name="name"/>, or 0 when never set.</summary>
    public float GetFloat(string name) => GetFloat(AnimationNameHash.Hash(name));

    /// <summary>Sets the value outright, cancelling any smoothing on it.</summary>
    public void SetFloat(int hash, float value)
    {
        _values ??= new Dictionary<int, float>();
        _values[hash] = value;
        _smoothed?.Remove(hash);
    }

    /// <summary>Sets the value outright by name, cancelling any smoothing on it.</summary>
    public void SetFloat(string name, float value) => SetFloat(AnimationNameHash.Hash(name), value);

    /// <summary>
    /// Sets a damped target: the stored value eases toward <paramref name="target"/> with time
    /// constant <paramref name="dampSeconds"/> (rate 1/damp; non-positive snaps). A value never set
    /// before starts at the target so the first damp does not ramp in from 0.
    /// </summary>
    public void SetFloatTarget(int hash, float target, float dampSeconds)
    {
        if (dampSeconds <= 0f)
        {
            SetFloat(hash, target);
            return;
        }

        _values ??= new Dictionary<int, float>();
        if (!_values.ContainsKey(hash))
            _values[hash] = target;
        _smoothed ??= new Dictionary<int, Smoothed>();
        _smoothed[hash] = new Smoothed { Target = target, Rate = 1f / dampSeconds };
    }

    /// <summary>By-name <see cref="SetFloatTarget(int, float, float)"/>.</summary>
    public void SetFloatTarget(string name, float target, float dampSeconds)
        => SetFloatTarget(AnimationNameHash.Hash(name), target, dampSeconds);

    /// <summary>Advances damped values by <paramref name="dt"/> seconds. Zero-cost when none active.</summary>
    public void Tick(float dt)
    {
        if (_smoothed == null || _smoothed.Count == 0)
            return;

        List<int>? settled = null;
        foreach (KeyValuePair<int, Smoothed> kv in _smoothed)
        {
            float current = _values[kv.Key];
            float delta = kv.Value.Target - current;
            float step = delta * (1f - System.MathF.Exp(-dt * kv.Value.Rate));
            float next = current + step;
            if (System.MathF.Abs(kv.Value.Target - next) <= 1e-4f)
            {
                _values[kv.Key] = kv.Value.Target;
                settled ??= new List<int>();
                settled.Add(kv.Key);
            }
            else
            {
                _values[kv.Key] = next;
            }
        }

        if (settled != null)
            for (int i = 0; i < settled.Count; i++)
                _smoothed.Remove(settled[i]);
    }

    /// <summary>True when a damped target is still converging on <paramref name="hash"/>.</summary>
    public bool IsSmoothing(int hash) => _smoothed != null && _smoothed.ContainsKey(hash);
}
