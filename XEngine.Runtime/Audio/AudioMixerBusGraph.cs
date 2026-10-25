// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using XEngine.Runtime.Audio.Native;

namespace XEngine.Runtime.Audio;

/// <summary>
/// Native side of an <see cref="AudioMixerInstance"/> (N2): one real audio node per mixer group
/// in the device's node graph, chained child → parent with a pass-through metering node between
/// each group and its parent. Group gains are applied exactly once on the group node; meters see
/// the post-fader sum of everything routed into the group.
/// <para>
/// All construction and teardown happens on the main thread while the engine is alive; the audio
/// callback only runs the metering procs, which copy audio through and publish Peak/RMS via
/// interlocked exchanges. When the backend has no native graph (Web Audio, headless) the graph
/// stays unbuilt and sources fall back to the managed per-source gain path.
/// </para>
/// </summary>
public sealed class AudioMixerBusGraph
{
    // Compiled-group parallel arrays; rebuilt with the instance.
    private IntPtr[] _groupNodes = Array.Empty<IntPtr>();          // ma_sound_group*
    private ma_effect_node_ptr[] _meterNodes = Array.Empty<ma_effect_node_ptr>();
    private ma_effect_node_process_proc[] _meterProcs = Array.Empty<ma_effect_node_process_proc>();
    // Pins keep the metering delegates alive for as long as the native nodes reference
    // them — rebuilt graphs replace the array, which would otherwise let the GC collect
    // delegates the device thread is about to invoke (FailFast on a collected delegate).
    private GCHandle[] _meterProcPins = Array.Empty<GCHandle>();
    private int[] _meterPeakBits;   // audio thread → main thread (Interlocked)
    private int[] _meterRmsBits;
    private int[] _clipLatches;     // 1 once a callback peak ≥ ClipThreshold until cleared

    private readonly AudioMixerInstance _owner;
    private bool _built;
    private uint _channels;
    private uint _sampleRate;

    /// <summary>A callback peak at or above this value latches the group's clip indicator.</summary>
    public const float ClipThreshold = 0.999f;

    public AudioMixerBusGraph(AudioMixerInstance owner) => _owner = owner;

    public bool IsBuilt => _built;

    // ----------------------------------------------------------------------------------------
    // Build / teardown
    // ----------------------------------------------------------------------------------------

    /// <summary>Builds (or rebuilds) the native graph for the instance's compiled topology.
    /// Returns false when the current backend cannot host a bus graph.</summary>
    public bool EnsureBuilt(int groupCount)
    {
        if (_built && _groupNodes.Length == groupCount)
            return true;
        if (!AudioContext.IsInitialized || AudioContext.NativeContext == IntPtr.Zero)
            return false;
        if (!AudioContext.Backend.Capabilities.HasFlag(AudioBackendCapabilities.BusRouting))
            return false;

        Teardown();
        _channels = (uint)AudioContext.Channels;
        _sampleRate = (uint)AudioContext.SampleRate;

        // Teardown uninited the nodes referencing the old delegates; release stale pins
        // before allocating the fresh generation.
        for (int i = 0; i < _meterProcPins.Length; i++)
            if (_meterProcPins[i].IsAllocated)
                _meterProcPins[i].Free();

        _groupNodes = new IntPtr[groupCount];
        _meterNodes = new ma_effect_node_ptr[groupCount];
        _meterProcs = new ma_effect_node_process_proc[groupCount];
        _meterProcPins = new GCHandle[groupCount];
        _meterPeakBits = new int[groupCount];
        _meterRmsBits = new int[groupCount];
        _clipLatches = new int[groupCount];

        var engine = new ma_engine_ptr(MiniAudioExNative.ma_ex_context_get_engine(AudioContext.NativeContext));
        int master = _owner.GetIndex(AudioMixer.MasterGroupName);

        // Create group nodes parents-first (fixed-point passes; the compiled hierarchy is
        // cycle-free by construction). Master hangs under the endpoint (null parent).
        var created = new bool[groupCount];
        int remaining = groupCount;
        while (remaining > 0)
        {
            bool progress = false;
            for (int i = 0; i < groupCount; i++)
            {
                if (created[i]) continue;

                int parentIdx = _owner.GetParentIndex(i);
                bool parentReady = parentIdx < 0 || created[parentIdx];
                if (!parentReady) continue;

                var group = new ma_sound_group_ptr(true);
                if (group.pointer == IntPtr.Zero)
                    continue;
                var parent = parentIdx >= 0
                    ? new ma_sound_group_ptr(_groupNodes[parentIdx])
                    : default;
                if (MiniAudioNative.ma_sound_group_init(engine, 0, parent, group) != ma_result.success)
                {
                    group.Free();
                    continue;
                }

                _groupNodes[i] = group.pointer;
                created[i] = true;
                remaining--;
                progress = true;
            }
            if (!progress)
                break; // should not happen on a compiled (acyclic) topology
        }

        if (remaining > 0)
        {
            Teardown();
            return false;
        }

        // Insert a metering node between every group and its parent: group.out → meter.in,
        // meter.out → parent.in (init already attached group.out → parent.in).
        for (int i = 0; i < groupCount; i++)
        {
            int idx = i; // closure capture
            var proc = new ma_effect_node_process_proc((pNode, ppFramesIn, pFrameCountIn, ppFramesOut, pFrameCountOut) =>
                MeterProcess(idx, pNode, ppFramesIn, pFrameCountIn, ppFramesOut, pFrameCountOut));
            _meterProcs[i] = proc;
            _meterProcPins[i] = GCHandle.Alloc(proc);

            var meter = new ma_effect_node_ptr(true);
            if (meter.pointer == IntPtr.Zero)
                continue;

            var config = MiniAudioNative.ma_effect_node_config_init(_channels, _sampleRate, proc, IntPtr.Zero);
            if (MiniAudioNative.ma_effect_node_init(MiniAudioNative.ma_engine_get_node_graph(engine), ref config, meter) != ma_result.success)
            {
                meter.Free();
                continue;
            }

            var groupNode = new ma_node_ptr(_groupNodes[i]);
            var parentNode = ResolveParentNode(engine, i);
            MiniAudioNative.ma_node_attach_output_bus(groupNode, 0, new ma_node_ptr(meter.pointer), 0);
            MiniAudioNative.ma_node_attach_output_bus(new ma_node_ptr(meter.pointer), 0, parentNode, 0);
            _meterNodes[i] = meter;
        }

        // Push the current effective gains into the fresh nodes.
        for (int i = 0; i < groupCount; i++)
            ApplyGroupGain(i);

        _built = true;
        return true;
    }

    private ma_node_ptr ResolveParentNode(ma_engine_ptr engine, int index)
    {
        int parentIdx = _owner.GetParentIndex(index);
        if (parentIdx >= 0 && _groupNodes[parentIdx] != IntPtr.Zero)
            return new ma_node_ptr(_groupNodes[parentIdx]);
        return MiniAudioNative.ma_engine_get_endpoint(engine); // root groups feed the endpoint
    }

    /// <summary>Tears the graph down. Call while the engine is still alive (main thread).</summary>
    public void Teardown()
    {
        if (!_built && Array.TrueForAll(_meterNodes, n => n.pointer == IntPtr.Zero) && Array.TrueForAll(_groupNodes, n => n == IntPtr.Zero))
            return;

        for (int i = 0; i < _meterNodes.Length; i++)
        {
            if (_meterNodes[i].pointer != IntPtr.Zero)
            {
                MiniAudioNative.ma_effect_node_uninit(_meterNodes[i]);
                _meterNodes[i].Free();
                _meterNodes[i] = default;
            }
        }
        for (int i = 0; i < _meterProcPins.Length; i++)
            if (_meterProcPins[i].IsAllocated)
                _meterProcPins[i].Free();
        _meterProcPins = Array.Empty<GCHandle>();
        _meterProcs = Array.Empty<ma_effect_node_process_proc>();

        for (int i = 0; i < _groupNodes.Length; i++)
        {
            if (_groupNodes[i] != IntPtr.Zero)
            {
                MiniAudioNative.ma_sound_group_uninit(new ma_sound_group_ptr(_groupNodes[i]));
                _groupNodes[i] = IntPtr.Zero;
            }
        }

        _built = false;
    }

    /// <summary>Backend restarted: the old native nodes died with the previous engine, but
    /// the delegates must stay pinned until a fresh graph replaces them — an in-flight
    /// device callback could still invoke the old function pointers.</summary>
    public void Invalidate()
    {
        _groupNodes = Array.Empty<IntPtr>();
        _meterNodes = Array.Empty<ma_effect_node_ptr>();
        _meterProcs = Array.Empty<ma_effect_node_process_proc>();
        _built = false;
        // NOTE: _meterProcPins intentionally survive until Teardown/next build replaces
        // them — freeing them here resurrects the collected-delegate crash the pins exist
        // to prevent.
        _meterPeakBits = Array.Empty<int>();
        _meterRmsBits = Array.Empty<int>();
        _clipLatches = Array.Empty<int>();
        _built = false;
    }

    // ----------------------------------------------------------------------------------------
    // Gain & attachment
    // ----------------------------------------------------------------------------------------

    /// <summary>Applies the group's effective linear gain (mute/solo silencing included) to its
    /// node — the single place the bus gain touches audio.</summary>
    public void ApplyGroupGain(int index)
    {
        if (!_built || (uint)index >= (uint)_groupNodes.Length || _groupNodes[index] == IntPtr.Zero)
            return;
        float gain = _owner.GetChainGain(index);
        MiniAudioNative.ma_sound_group_set_volume(new ma_sound_group_ptr(_groupNodes[index]), gain);
    }

    /// <summary>Attaches a source's chain-end node output to this mixer group's bus. Returns false
    /// when the graph is not built (caller falls back to endpoint attachment + managed gain).</summary>
    public bool AttachSource(IntPtr sourceEndNode, int groupIndex)
    {
        if (!_built || (uint)groupIndex >= (uint)_groupNodes.Length || _groupNodes[groupIndex] == IntPtr.Zero)
            return false;
        return MiniAudioNative.ma_node_attach_output_bus(
            new ma_node_ptr(sourceEndNode), 0,
            new ma_node_ptr(_groupNodes[groupIndex]), 0) == ma_result.success;
    }

    // ----------------------------------------------------------------------------------------
    // Metering
    // ----------------------------------------------------------------------------------------

    private unsafe void MeterProcess(int index, ma_node_ptr pNode, IntPtr ppFramesIn, IntPtr pFrameCountIn, IntPtr ppFramesOut, IntPtr pFrameCountOut)
    {
        // The graph can be rebuilt on the main thread while the audio thread still runs this
        // proc for a dying node: bail instead of indexing the fresh (possibly smaller) arrays.
        if ((uint)index >= (uint)_meterPeakBits.Length)
        {
            if (pNode.pointer != IntPtr.Zero)
                CopyThrough(pNode, ppFramesIn, pFrameCountIn, ppFramesOut, pFrameCountOut);
            return;
        }

        var node = (ma_effect_node*)pNode.pointer;
        uint channels = node->config.channels;

        var framesIn = (float**)ppFramesIn;
        var framesOut = (float**)ppFramesOut;
        uint countIn = *(uint*)pFrameCountIn;
        uint countOut = *(uint*)pFrameCountOut;

        NativeArray<float> bufferIn = new(framesIn[0], (int)(countIn * channels));
        NativeArray<float> bufferOut = new(framesOut[0], (int)(countOut * channels));
        bufferIn.CopyTo(bufferOut);
        *(uint*)pFrameCountOut = countIn;

        long sumSquares = 0;
        float peak = 0;
        int samples = (int)(countIn * channels);
        for (int i = 0; i < samples; i++)
        {
            float s = bufferIn[i];
            float abs = s < 0 ? -s : s;
            if (abs > peak) peak = abs;
            sumSquares += (long)(s * s * 1e6f);
        }

        Interlocked.Exchange(ref _meterPeakBits[index], BitConverter.SingleToInt32Bits(peak));
        float rms = samples > 0 ? MathF.Sqrt(sumSquares / (samples * 1e6f)) : 0f;
        Interlocked.Exchange(ref _meterRmsBits[index], BitConverter.SingleToInt32Bits(rms));

        if (peak >= ClipThreshold)
            Interlocked.Exchange(ref _clipLatches[index], 1);
    }

    private static unsafe void CopyThrough(ma_node_ptr pNode, IntPtr ppFramesIn, IntPtr pFrameCountIn, IntPtr ppFramesOut, IntPtr pFrameCountOut)
    {
        var node = (ma_effect_node*)pNode.pointer;
        uint channels = node->config.channels;
        uint countIn = *(uint*)pFrameCountIn;
        NativeArray<float> bufferIn = new(*(float**)ppFramesIn, (int)(countIn * channels));
        NativeArray<float> bufferOut = new(*(float**)ppFramesOut, (int)(*(uint*)pFrameCountOut * channels));
        bufferIn.CopyTo(bufferOut);
        *(uint*)pFrameCountOut = countIn;
    }

    /// <summary>Latest post-fader Peak/RMS of the group's summed signal (main thread).</summary>
    public void GetMetering(int index, out float peak, out float rms)
    {
        if (!_built || (uint)index >= (uint)_meterPeakBits.Length)
        {
            peak = 0;
            rms = 0;
            return;
        }
        peak = BitConverter.Int32BitsToSingle(Interlocked.CompareExchange(ref _meterPeakBits[index], 0, 0));
        rms = BitConverter.Int32BitsToSingle(Interlocked.CompareExchange(ref _meterRmsBits[index], 0, 0));
    }

    /// <summary>Returns and clears the clip latch for the group.</summary>
    public bool TakeClip(int index)
    {
        if (!_built || (uint)index >= (uint)_clipLatches.Length)
            return false;
        return Interlocked.Exchange(ref _clipLatches[index], 0) == 1;
    }
}
