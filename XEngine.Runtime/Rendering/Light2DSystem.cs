// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Active <see cref="Light2D"/> list. No LINQ, no per-frame alloc; the 2D renderer indexes this list.
/// </summary>
public sealed class Light2DSystem
{
    public static Light2DSystem Instance { get; } = new();

    private readonly List<Light2D> _lights = new();

    public int Count => _lights.Count;

    public Light2D this[int index] => _lights[index];

    public void Register(Light2D light)
    {
        if (light == null || light.IsDisposed)
            return;
        for (int i = 0; i < _lights.Count; i++)
        {
            if (_lights[i] == light)
                return;
        }

        _lights.Add(light);
    }

    public void Unregister(Light2D light)
    {
        for (int i = _lights.Count - 1; i >= 0; i--)
        {
            if (_lights[i] == light)
            {
                _lights.RemoveAt(i);
                return;
            }
        }
    }

    public void ResetForTests() => _lights.Clear();
}
