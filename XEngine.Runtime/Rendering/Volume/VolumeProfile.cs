// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Shared volume settings asset. Echo-serialized as <c>.volumeprofile</c> so AI CLI can
/// inspect/patch via <c>volume_inspect</c> / <c>volume_patch</c>.
/// </summary>
[CreateAssetMenu("Rendering/Volume Profile", Extension = ".volumeprofile", Order = 42)]
public sealed class VolumeProfile : ScriptableObject
{
    public List<VolumeComponent> Components = new();

    public bool TryGet<T>(out T component) where T : VolumeComponent
    {
        List<VolumeComponent> list = Components;
        if (list != null)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is T typed)
                {
                    component = typed;
                    return true;
                }
            }
        }

        component = null!;
        return false;
    }

    public T GetOrAdd<T>() where T : VolumeComponent, new()
    {
        if (TryGet(out T existing))
            return existing;

        Components ??= new List<VolumeComponent>();
        var created = new T();
        Components.Add(created);
        return created;
    }
}
