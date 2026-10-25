// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.PostProcessing;

/// <summary>Collects <see cref="PostProcessVolume"/> instances and blends them into a reused stack.</summary>
public sealed class PostProcessManager
{
    public static PostProcessManager instance { get; } = new();

    private readonly List<PostProcessVolume> _volumes = new();
    private readonly List<PostProcessVolume> _scratch = new();
    private readonly Dictionary<Type, PostProcessEffectSettings> _stack = new();

    public int RegisteredCount => _volumes.Count;

    public void Register(PostProcessVolume volume)
    {
        if (volume == null || volume.IsDisposed)
            return;
        for (int i = 0; i < _volumes.Count; i++)
        {
            if (_volumes[i] == volume)
                return;
        }

        _volumes.Add(volume);
    }

    public void Unregister(PostProcessVolume volume)
    {
        for (int i = _volumes.Count - 1; i >= 0; i--)
        {
            if (_volumes[i] == volume)
            {
                _volumes.RemoveAt(i);
                return;
            }
        }
    }

    public void Update(Float3 triggerPosition, LayerMask volumeLayer)
    {
        foreach (PostProcessEffectSettings settings in _stack.Values)
            settings.ResetToDefault();

        _scratch.Clear();
        for (int i = 0; i < _volumes.Count; i++)
        {
            PostProcessVolume volume = _volumes[i];
            if (volume == null || volume.IsDisposed)
                continue;
            _scratch.Add(volume);
        }

        SortByPriority(_scratch);

        for (int i = 0; i < _scratch.Count; i++)
        {
            PostProcessVolume volume = _scratch[i];
            if (volume.GameObject != null && !volumeLayer.HasLayer(volume.GameObject.LayerIndex))
                continue;

            float influence = volume.EvaluateInfluence(triggerPosition);
            if (influence <= 0f)
                continue;

            PostProcessProfile? profile = volume.profileRef;
            if (profile?.settings == null)
                continue;

            for (int s = 0; s < profile.settings.Count; s++)
            {
                PostProcessEffectSettings? source = profile.settings[s];
                if (source == null || !source.active)
                    continue;
                PostProcessEffectSettings dest = GetOrAdd(source.GetType());
                dest.Interp(source, influence);
            }
        }
    }

    public T Get<T>() where T : PostProcessEffectSettings, new()
        => (T)GetOrAdd(typeof(T));

    public bool TryGet<T>(out T settings) where T : PostProcessEffectSettings
    {
        if (_stack.TryGetValue(typeof(T), out PostProcessEffectSettings? value) && value is T typed)
        {
            settings = typed;
            return true;
        }

        settings = null!;
        return false;
    }

    public void ResetForTests()
    {
        _volumes.Clear();
        _scratch.Clear();
        _stack.Clear();
    }

    private PostProcessEffectSettings GetOrAdd(Type type)
    {
        if (_stack.TryGetValue(type, out PostProcessEffectSettings? existing))
            return existing;
        if (Activator.CreateInstance(type) is not PostProcessEffectSettings created)
            throw new InvalidOperationException("Type is not PostProcessEffectSettings");
        created.ResetToDefault();
        _stack[type] = created;
        return created;
    }

    private static void SortByPriority(List<PostProcessVolume> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            PostProcessVolume key = list[i];
            int j = i - 1;
            while (j >= 0 && list[j].priority > key.priority)
            {
                list[j + 1] = list[j];
                j--;
            }

            list[j + 1] = key;
        }
    }
}
