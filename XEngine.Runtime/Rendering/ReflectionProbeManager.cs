// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Picks up to <see cref="ShaderConfig.MaxReflectionProbes"/> cubemap probes for a camera
/// position and uploads IBL globals. No probe → count 0 so Standard keeps the ambient specular approx.
/// </summary>
public sealed class ReflectionProbeManager
{
    public static ReflectionProbeManager Instance { get; } = new();

    private readonly List<ReflectionProbe> _probes = new();
    private Cubemap? _emptyCube;
    private IGraphicsDevice? _emptyCubeDevice;

    private ReflectionProbe? _probe0;
    private ReflectionProbe? _probe1;
    private float _weight0;
    private float _weight1;

    public int PreparedCount { get; private set; }

    public ReflectionProbe? GetPrepared(int index)
        => index == 0 ? _probe0 : index == 1 ? _probe1 : null;

    public float GetPreparedWeight(int index)
        => index == 0 ? _weight0 : index == 1 ? _weight1 : 0f;

    public void Register(ReflectionProbe probe)
    {
        if (probe == null || probe.IsDisposed)
            return;
        for (int i = 0; i < _probes.Count; i++)
        {
            if (_probes[i] == probe)
                return;
        }

        _probes.Add(probe);
    }

    public void Unregister(ReflectionProbe probe)
    {
        for (int i = _probes.Count - 1; i >= 0; i--)
        {
            if (_probes[i] == probe)
            {
                _probes.RemoveAt(i);
                return;
            }
        }
    }

    public void Prepare(Float3 worldPosition)
    {
        _probe0 = null;
        _probe1 = null;
        _weight0 = 0f;
        _weight1 = 0f;
        PreparedCount = 0;

        for (int i = 0; i < _probes.Count; i++)
        {
            ReflectionProbe probe = _probes[i];
            if (probe == null || probe.IsDisposed)
                continue;
            float weight = probe.EvaluateInfluence(worldPosition);
            if (weight <= 0f)
                continue;
            Consider(probe, weight);
        }
    }

    public void UploadGlobals()
    {
        EnsureEmptyCube();
        Cubemap dummy = _emptyCube!;
        Cubemap cube0 = _probe0?.ResolvedTexture ?? dummy;
        Cubemap cube1 = _probe1?.ResolvedTexture ?? dummy;

        using var cmd = Graphics.GetCommandBuffer("ReflectionProbes");
        cmd.SetGlobalInt("_ReflectionProbeCount", PreparedCount);
        cmd.SetGlobalVector("_ProbeWeights", new Float2(_weight0, _weight1));
        cmd.SetGlobalTextureCube("_SpecCube0", cube0);
        cmd.SetGlobalTextureCube("_SpecCube1", cube1);
        UploadSlot(cmd, 0, _probe0);
        UploadSlot(cmd, 1, _probe1);
        Graphics.Submit(cmd);
    }

    public void PrepareAndUpload(Float3 worldPosition)
    {
        Prepare(worldPosition);
        UploadGlobals();
    }

    public void ResetForTests()
    {
        _probes.Clear();
        _probe0 = null;
        _probe1 = null;
        _weight0 = 0f;
        _weight1 = 0f;
        PreparedCount = 0;
        _emptyCube?.Dispose();
        _emptyCube = null;
        _emptyCubeDevice = null;
    }

    private void Consider(ReflectionProbe probe, float weight)
    {
        if (PreparedCount == 0)
        {
            _probe0 = probe;
            _weight0 = weight;
            PreparedCount = 1;
            return;
        }

        if (Better(probe, weight, _probe0!, _weight0))
        {
            _probe1 = _probe0;
            _weight1 = _weight0;
            _probe0 = probe;
            _weight0 = weight;
            PreparedCount = 2;
            return;
        }

        if (PreparedCount == 1 || Better(probe, weight, _probe1!, _weight1))
        {
            _probe1 = probe;
            _weight1 = weight;
            PreparedCount = 2;
        }
    }

    private static bool Better(ReflectionProbe candidate, float candidateWeight, ReflectionProbe current, float currentWeight)
    {
        if (candidate.Importance != current.Importance)
            return candidate.Importance > current.Importance;
        return candidateWeight > currentWeight;
    }

    private static void UploadSlot(CommandBuffer cmd, int slot, ReflectionProbe? probe)
    {
        string prefix = slot == 0 ? "_Probe0" : "_Probe1";
        if (probe == null)
        {
            cmd.SetGlobalVector(prefix + "Position", Float3.Zero);
            cmd.SetGlobalVector(prefix + "BoxMin", Float3.Zero);
            cmd.SetGlobalVector(prefix + "BoxMax", Float3.Zero);
            cmd.SetGlobalVector(prefix + "Params", Float4.Zero);
            return;
        }

        probe.GetWorldBounds(out Float3 boxMin, out Float3 boxMax);
        Cubemap? cube = probe.ResolvedTexture;
        cmd.SetGlobalVector(prefix + "Position", probe.Transform.Position);
        cmd.SetGlobalVector(prefix + "BoxMin", boxMin);
        cmd.SetGlobalVector(prefix + "BoxMax", boxMax);
        cmd.SetGlobalVector(prefix + "Params", new Float4(
            probe.Intensity,
            probe.BoxProjection ? 1f : 0f,
            cube != null ? cube.MipLevels : 1f,
            0f));
    }

    private void EnsureEmptyCube()
    {
        if (_emptyCube != null && !_emptyCube.IsDisposed
            && ReferenceEquals(_emptyCubeDevice, Graphics.Device))
            return;

        _emptyCube?.Dispose();
        _emptyCube = new Cubemap(4, mipChain: false);
        _emptyCubeDevice = Graphics.Device;
        var zeros = new ushort[4 * 4 * 4];
        var memory = zeros.AsMemory();
        for (int face = 0; face < 6; face++)
            _emptyCube.SetFaceData(face, memory);
    }
}
