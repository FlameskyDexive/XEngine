// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Active <see cref="DecalProjector"/> list. No LINQ, no per-frame alloc; the pass indexes this list.
/// </summary>
public sealed class DecalSystem
{
    public static DecalSystem Instance { get; } = new();

    private readonly List<DecalProjector> _projectors = new();

    public int Count => _projectors.Count;

    public DecalProjector this[int index] => _projectors[index];

    public void Register(DecalProjector projector)
    {
        if (projector == null || projector.IsDisposed)
            return;
        for (int i = 0; i < _projectors.Count; i++)
        {
            if (_projectors[i] == projector)
                return;
        }

        _projectors.Add(projector);
    }

    public void Unregister(DecalProjector projector)
    {
        for (int i = _projectors.Count - 1; i >= 0; i--)
        {
            if (_projectors[i] == projector)
            {
                _projectors.RemoveAt(i);
                return;
            }
        }
    }

    public void ResetForTests() => _projectors.Clear();
}
