// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Reflection;

namespace XEngine.PostProcessing;

/// <summary>
/// POD settings for one effect. Public <see cref="ParameterOverride"/> fields are collected
/// and blended by <see cref="PostProcessManager"/>.
/// </summary>
public abstract class PostProcessEffectSettings
{
    public bool active = true;
    public BoolParameter enabled = new(false, overrideState: true);

    [NonSerialized] private List<ParameterOverride>? _parameters;

    public IReadOnlyList<ParameterOverride> Parameters
    {
        get
        {
            EnsureParameters();
            return _parameters!;
        }
    }

    public void EnsureParameters()
    {
        if (_parameters != null)
            return;

        _parameters = new List<ParameterOverride>();
        FieldInfo[] fields = GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
        Array.Sort(fields, static (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
        for (int i = 0; i < fields.Length; i++)
        {
            if (!fields[i].FieldType.IsSubclassOf(typeof(ParameterOverride)))
                continue;
            if (fields[i].GetValue(this) is ParameterOverride parameter)
                _parameters.Add(parameter);
        }
    }

    public void SetAllOverridesTo(bool state, bool excludeEnabled = true)
    {
        EnsureParameters();
        for (int i = 0; i < _parameters!.Count; i++)
        {
            ParameterOverride parameter = _parameters[i];
            if (excludeEnabled && parameter == enabled)
                continue;
            parameter.overrideState = state;
        }
    }

    public virtual bool IsEnabledAndSupported() => active && enabled.value;

    public void ResetToDefault()
    {
        EnsureParameters();
        enabled.value = false;
        enabled.overrideState = true;
        for (int i = 0; i < _parameters!.Count; i++)
        {
            if (_parameters[i] != enabled)
                _parameters[i].overrideState = false;
        }
    }

    public void Interp(PostProcessEffectSettings source, float t)
    {
        if (source == null || source.GetType() != GetType())
            return;
        EnsureParameters();
        source.EnsureParameters();
        IReadOnlyList<ParameterOverride> from = source.Parameters;
        if (from.Count != _parameters!.Count)
            return;
        for (int i = 0; i < _parameters.Count; i++)
            _parameters[i].OverrideFrom(from[i], t);
    }

    public int GetHash()
    {
        EnsureParameters();
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < _parameters!.Count; i++)
                hash = hash * 23 + _parameters[i].GetHash();
            return hash;
        }
    }
}
