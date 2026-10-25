// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime;

/// <summary>One discrete sprite id key. Hold interpolation uses the last key at or before the sample time.</summary>
public struct SpriteObjectKey
{
    public float Time;
    public string Id;
}

/// <summary>
/// Discrete object-reference track for swapping sprite ids (or <see cref="SpriteResolver.Label"/>)
/// without interpolating between keys.
/// </summary>
public sealed class SpriteObjectTrack
{
    public string Path = string.Empty;
    public List<SpriteObjectKey> Keys = [];

    public string? Evaluate(float time)
    {
        string? id = null;
        for (int i = 0; i < Keys.Count; i++)
        {
            SpriteObjectKey key = Keys[i];
            if (key.Time > time)
                break;
            id = key.Id;
        }

        return id;
    }

    public void Apply(SpriteResolver resolver, float time)
    {
        if (resolver == null)
            return;
        string? id = Evaluate(time);
        if (id != null)
            resolver.Label = id;
    }
}
