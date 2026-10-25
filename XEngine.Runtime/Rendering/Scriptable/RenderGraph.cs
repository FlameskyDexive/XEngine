// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Runtime.Rendering;

/// <summary>Imported camera textures CopyColor / CopyDepth write and later passes sample.</summary>
public enum RenderGraphImportedTexture : byte
{
    CameraOpaqueColor = 0,
    CameraDepth = 1,
}

/// <summary>Index of a texture node in a <see cref="RenderGraph"/>.</summary>
public readonly struct RenderGraphResource
{
    public readonly int Index;

    internal RenderGraphResource(int index) => Index = index;
}

/// <summary>
/// URP 17 pass kinds. Raster is the default CommandBuffer draw path; Compute marks a
/// dispatch; Unsafe is an unmerged native pass (recorded, not a Graphite compiler).
/// </summary>
public enum RenderGraphPassKind : byte
{
    Raster = 0,
    Compute = 1,
    Unsafe = 2,
}

/// <summary>
/// CommandBuffer DAG over <see cref="RenderPass"/> + imported RTHandles.
/// Writers with no live readers are culled (M18). Reused per camera; no per-frame alloc
/// in the steady-state 7-pass + copy path.
/// </summary>
public sealed class RenderGraph
{
    private const int InitialPassCapacity = 32;
    private const int EdgesPerPass = 4;
    private const int ResourceCapacity = 8;

    private RenderPass?[] _passes = new RenderPass?[InitialPassCapacity];
    private bool[] _allowCulling = new bool[InitialPassCapacity];
    private bool[] _live = new bool[InitialPassCapacity];
    private byte[] _kinds = new byte[InitialPassCapacity];
    private byte[] _readCount = new byte[InitialPassCapacity];
    private byte[] _writeCount = new byte[InitialPassCapacity];
    private int[] _reads = new int[InitialPassCapacity * EdgesPerPass];
    private int[] _writes = new int[InitialPassCapacity * EdgesPerPass];
    private readonly int[] _liveReaders = new int[ResourceCapacity];
    private readonly int[] _forcedReaders = new int[ResourceCapacity];

    private int _passCount;
    private int _resourceCount;
    private int _currentPass = -1;
    private int _culledCount;
    private bool _compiled;

    public int PassCount => _passCount;
    public int CulledCount => _culledCount;
    public int ExecutedCount => _passCount - _culledCount;

    public RenderGraphResource Import(RenderGraphImportedTexture texture)
    {
        int index = (int)texture;
        if (index >= _resourceCount)
            _resourceCount = index + 1;
        return new RenderGraphResource(index);
    }

    public void Reset()
    {
        _passCount = 0;
        _resourceCount = 0;
        _currentPass = -1;
        _culledCount = 0;
        _compiled = false;
        for (int r = 0; r < ResourceCapacity; r++)
            _forcedReaders[r] = 0;
    }

    public void ForceRead(RenderGraphResource resource)
    {
        int index = resource.Index;
        if ((uint)index < (uint)ResourceCapacity)
            _forcedReaders[index] = 1;
    }

    public void BeginPass(RenderPass pass, bool allowCulling)
        => BeginPass(pass, allowCulling, RenderGraphPassKind.Raster);

    public void BeginPass(RenderPass pass, bool allowCulling, RenderGraphPassKind kind)
    {
        if (_passCount >= _passes.Length)
            Grow();

        int i = _passCount++;
        _passes[i] = pass;
        _allowCulling[i] = allowCulling;
        _live[i] = false;
        _kinds[i] = (byte)kind;
        _readCount[i] = 0;
        _writeCount[i] = 0;
        _currentPass = i;
        _compiled = false;
    }

    public RenderGraphPassKind GetPassKind(int passIndex)
        => (uint)passIndex < (uint)_passCount ? (RenderGraphPassKind)_kinds[passIndex] : RenderGraphPassKind.Raster;

    public void Read(RenderGraphResource resource)
    {
        AddEdge(_reads, _readCount, resource.Index);
    }

    public void Write(RenderGraphResource resource)
    {
        AddEdge(_writes, _writeCount, resource.Index);
    }

    public void EndPass() => _currentPass = -1;

    public int Compile()
    {
        for (int i = 0; i < _passCount; i++)
            _live[i] = !_allowCulling[i];

        bool changed = true;
        int guard = _passCount + 1;
        while (changed && guard-- > 0)
        {
            changed = false;
            for (int r = 0; r < _resourceCount; r++)
                _liveReaders[r] = _forcedReaders[r];

            for (int i = 0; i < _passCount; i++)
            {
                if (!_live[i])
                    continue;
                int readCount = _readCount[i];
                int baseIndex = i * EdgesPerPass;
                for (int k = 0; k < readCount; k++)
                {
                    int resource = _reads[baseIndex + k];
                    if ((uint)resource < (uint)_resourceCount)
                        _liveReaders[resource]++;
                }
            }

            for (int i = 0; i < _passCount; i++)
            {
                if (_live[i])
                    continue;
                int writeCount = _writeCount[i];
                int baseIndex = i * EdgesPerPass;
                for (int k = 0; k < writeCount; k++)
                {
                    int resource = _writes[baseIndex + k];
                    if ((uint)resource < (uint)_resourceCount && _liveReaders[resource] > 0)
                    {
                        _live[i] = true;
                        changed = true;
                        break;
                    }
                }
            }
        }

        _culledCount = 0;
        for (int i = 0; i < _passCount; i++)
        {
            if (!_live[i])
                _culledCount++;
        }

        _compiled = true;
        return _culledCount;
    }

    [HotPath]
    public void Execute(ScriptableRenderContext context)
    {
        if (!_compiled)
            Compile();

        for (int i = 0; i < _passCount; i++)
        {
            RenderPass? pass = _passes[i];
            if (pass == null)
                continue;
            if (_live[i])
            {
                if (FrameDebugger.IsCapturing)
                    FrameDebugger.BeginPassGroup(pass);
                pass.Execute(context);
                if (FrameDebugger.IsCapturing)
                    FrameDebugger.EndGroup();
                RenderStats.RecordPassExecuted();
            }
            else
            {
                RenderStats.RecordCulledGraphPass();
            }
        }
    }

    public bool IsLive(int passIndex)
        => (uint)passIndex < (uint)_passCount && _live[passIndex];

    private void AddEdge(int[] edges, byte[] counts, int resourceIndex)
    {
        int pass = _currentPass;
        if ((uint)pass >= (uint)_passCount)
            return;
        int count = counts[pass];
        if (count >= EdgesPerPass)
            return;
        edges[pass * EdgesPerPass + count] = resourceIndex;
        counts[pass] = (byte)(count + 1);
        if (resourceIndex >= _resourceCount)
            _resourceCount = resourceIndex + 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        int next = _passes.Length * 2;
        Array.Resize(ref _passes, next);
        Array.Resize(ref _allowCulling, next);
        Array.Resize(ref _live, next);
        Array.Resize(ref _kinds, next);
        Array.Resize(ref _readCount, next);
        Array.Resize(ref _writeCount, next);
        Array.Resize(ref _reads, next * EdgesPerPass);
        Array.Resize(ref _writes, next * EdgesPerPass);
    }
}
