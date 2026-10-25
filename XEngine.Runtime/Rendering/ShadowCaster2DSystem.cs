// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Active <see cref="ShadowCaster2D"/> list. No LINQ, no per-frame alloc; the 2D shadow pass indexes this list.
/// </summary>
public sealed class ShadowCaster2DSystem
{
    public static ShadowCaster2DSystem Instance { get; } = new();

    private readonly List<ShadowCaster2D> _casters = new();

    public int Count => _casters.Count;

    public ShadowCaster2D this[int index] => _casters[index];

    public void Register(ShadowCaster2D caster)
    {
        if (caster == null || caster.IsDisposed)
            return;
        for (int i = 0; i < _casters.Count; i++)
        {
            if (_casters[i] == caster)
                return;
        }

        _casters.Add(caster);
    }

    public void Unregister(ShadowCaster2D caster)
    {
        for (int i = _casters.Count - 1; i >= 0; i--)
        {
            if (_casters[i] == caster)
            {
                _casters.RemoveAt(i);
                return;
            }
        }
    }

    public void ResetForTests() => _casters.Clear();
}
