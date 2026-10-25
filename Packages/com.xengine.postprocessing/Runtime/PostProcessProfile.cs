// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.PostProcessing;

/// <summary>Shared post-process settings asset. Echo-serialized as <c>.postprofile</c>.</summary>
[CreateAssetMenu("Rendering/Post-process Profile", Extension = ".postprofile", Order = 44)]
public sealed class PostProcessProfile : ScriptableObject
{
    public List<PostProcessEffectSettings> settings = new();

    [NonSerialized] public bool isDirty = true;

    public T AddSettings<T>() where T : PostProcessEffectSettings, new()
        => (T)AddSettings(typeof(T));

    public PostProcessEffectSettings AddSettings(Type type)
    {
        if (HasSettings(type))
            throw new InvalidOperationException("Effect already exists in the stack");
        if (Activator.CreateInstance(type) is not PostProcessEffectSettings effect)
            throw new InvalidOperationException("Type is not PostProcessEffectSettings");
        effect.enabled.value = true;
        settings.Add(effect);
        isDirty = true;
        return effect;
    }

    public PostProcessEffectSettings AddSettings(PostProcessEffectSettings effect)
    {
        if (effect == null)
            throw new ArgumentNullException(nameof(effect));
        if (HasSettings(effect.GetType()))
            throw new InvalidOperationException("Effect already exists in the stack");
        settings.Add(effect);
        isDirty = true;
        return effect;
    }

    public void RemoveSettings<T>() where T : PostProcessEffectSettings
        => RemoveSettings(typeof(T));

    public void RemoveSettings(Type type)
    {
        int toRemove = -1;
        for (int i = 0; i < settings.Count; i++)
        {
            if (settings[i] != null && settings[i].GetType() == type)
            {
                toRemove = i;
                break;
            }
        }

        if (toRemove < 0)
            throw new InvalidOperationException("Effect doesn't exist in the profile");
        settings.RemoveAt(toRemove);
        isDirty = true;
    }

    public bool HasSettings<T>() where T : PostProcessEffectSettings => HasSettings(typeof(T));

    public bool HasSettings(Type type)
    {
        for (int i = 0; i < settings.Count; i++)
        {
            if (settings[i] != null && settings[i].GetType() == type)
                return true;
        }

        return false;
    }

    public T? GetSetting<T>() where T : PostProcessEffectSettings
    {
        TryGetSettings(out T? setting);
        return setting;
    }

    public bool TryGetSettings<T>(out T? outSetting) where T : PostProcessEffectSettings
    {
        for (int i = 0; i < settings.Count; i++)
        {
            if (settings[i] is T typed)
            {
                outSetting = typed;
                return true;
            }
        }

        outSetting = null;
        return false;
    }
}
