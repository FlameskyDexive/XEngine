// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Collects scene <see cref="Volume"/> components and blends their profiles at a camera position
/// into a reused <see cref="VolumeStack"/>.
/// </summary>
public sealed class VolumeManager
{
    public static VolumeManager Instance { get; } = new();

    private readonly List<Volume> _volumes = new();
    private readonly List<Volume> _scratch = new();
    private readonly List<FogVolume> _fogVolumes = new();
    private readonly List<FogVolume> _fogUpload = new();
    private readonly List<float> _fogDist = new();
    private readonly VolumeStack _stack = new();

    public const int MaxFogVolumes = 16;

    public VolumeStack Stack => _stack;

    public int RegisteredCount => _volumes.Count;

    internal Volume GetRegistered(int index) => _volumes[index];

    public int FogVolumeUploadCount => _fogUpload.Count;

    public void Register(Volume volume)
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

    public void RegisterFogVolume(FogVolume volume)
    {
        if (volume == null || volume.IsDisposed)
            return;
        for (int i = 0; i < _fogVolumes.Count; i++)
        {
            if (_fogVolumes[i] == volume)
                return;
        }

        _fogVolumes.Add(volume);
    }

    public void UnregisterFogVolume(FogVolume volume)
    {
        for (int i = _fogVolumes.Count - 1; i >= 0; i--)
        {
            if (_fogVolumes[i] == volume)
            {
                _fogVolumes.RemoveAt(i);
                return;
            }
        }
    }

    public FogVolume GetFogVolumeUpload(int index) => _fogUpload[index];

    /// <summary>
    /// Sort enabled spatial fog regions by distance (globals first). Reuses lists; no per-frame alloc.
    /// </summary>
    public void PrepareFogVolumes(Float3 cameraPosition)
    {
        _fogUpload.Clear();
        _fogDist.Clear();
        for (int i = 0; i < _fogVolumes.Count; i++)
        {
            FogVolume volume = _fogVolumes[i];
            if (volume == null || volume.IsDisposed || !volume.Enabled
                || volume.GameObject == null || !volume.GameObject.EnabledInHierarchy)
            {
                continue;
            }

            float distSq = volume.Shape == FogVolumeShape.Global
                ? -1f
                : (float)Float3.DistanceSquared(cameraPosition, volume.Transform.Position);
            InsertFogSorted(volume, distSq);
        }

        while (_fogUpload.Count > MaxFogVolumes)
        {
            int last = _fogUpload.Count - 1;
            _fogUpload.RemoveAt(last);
            _fogDist.RemoveAt(last);
        }
    }

    private void InsertFogSorted(FogVolume volume, float distSq)
    {
        int index = _fogDist.Count;
        for (int i = 0; i < _fogDist.Count; i++)
        {
            if (distSq < _fogDist[i])
            {
                index = i;
                break;
            }
        }

        _fogUpload.Insert(index, volume);
        _fogDist.Insert(index, distSq);
    }

    public void Unregister(Volume volume)
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

    public void Update(Float3 cameraPosition)
    {
        _stack.ResetToDefaults();
        _scratch.Clear();
        for (int i = 0; i < _volumes.Count; i++)
        {
            Volume volume = _volumes[i];
            if (volume == null || volume.IsDisposed)
                continue;
            _scratch.Add(volume);
        }

        SortByPriority(_scratch);

        for (int i = 0; i < _scratch.Count; i++)
        {
            Volume volume = _scratch[i];
            float influence = volume.EvaluateInfluence(cameraPosition);
            if (influence <= 0f)
                continue;

            VolumeProfile? profile = volume.ResolvedProfile;
            if (profile == null)
                continue;

            List<VolumeComponent>? components = profile.Components;
            if (components == null)
                continue;

            for (int c = 0; c < components.Count; c++)
            {
                VolumeComponent? source = components[c];
                if (source == null || !source.Active)
                    continue;
                VolumeComponent dest = _stack.GetOrAdd(source.GetType());
                dest.Interp(source, influence);
            }
        }
    }

    public T Get<T>() where T : VolumeComponent, new() => _stack.Get<T>();

    /// <summary>Drop registered volumes and stacked instances. Tests call this between cases.</summary>
    public void ResetForTests()
    {
        _volumes.Clear();
        _scratch.Clear();
        _fogVolumes.Clear();
        _fogUpload.Clear();
        _fogDist.Clear();
        _stack.Clear();
    }

    private static void SortByPriority(List<Volume> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            Volume key = list[i];
            int j = i - 1;
            while (j >= 0 && list[j].Priority > key.Priority)
            {
                list[j + 1] = list[j];
                j--;
            }

            list[j + 1] = key;
        }
    }
}
