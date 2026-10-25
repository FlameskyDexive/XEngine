// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Preallocated blended result of every <see cref="VolumeComponent"/> type the manager has seen.
/// <see cref="Get{T}"/> returns the same instance across frames (no per-frame alloc).
/// </summary>
public sealed class VolumeStack
{
    private readonly List<VolumeComponent> _components = new();

    public int ComponentCount => _components.Count;

    public T Get<T>() where T : VolumeComponent, new()
    {
        for (int i = 0; i < _components.Count; i++)
        {
            if (_components[i] is T typed)
                return typed;
        }

        var created = new T();
        created.ResetToDefault();
        _components.Add(created);
        return created;
    }

    internal VolumeComponent GetOrAdd(Type type)
    {
        for (int i = 0; i < _components.Count; i++)
        {
            VolumeComponent existing = _components[i];
            if (existing.GetType() == type)
                return existing;
        }

        var created = (VolumeComponent)Activator.CreateInstance(type)!;
        created.ResetToDefault();
        _components.Add(created);
        return created;
    }

    internal void ResetToDefaults()
    {
        for (int i = 0; i < _components.Count; i++)
            _components[i].ResetToDefault();
    }

    internal void Clear() => _components.Clear();

    internal VolumeComponent GetComponent(int index) => _components[index];
}
