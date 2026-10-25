// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Ordered pass executor. M1 uses a reused list and insertion sort (stable, no LINQ).
/// M18 compiles the queue plus cullable CopyColor/CopyDepth into a CommandBuffer DAG.
/// </summary>
public class ScriptableRenderer
{
    private readonly List<RenderPass> _activePasses = new();
    private readonly RenderGraph _graph = new();
    private readonly RenderPassEvent[] _lastEvents = new RenderPassEvent[32];
    private int _lastEventCount;

    public int EnqueuedCount => _activePasses.Count;
    public ScriptableRenderPassInput RequestedInputs { get; private set; }
    public int GraphCulledCount => _graph.CulledCount;
    public int GraphExecutedCount => _graph.ExecutedCount;

    public void Clear()
    {
        _activePasses.Clear();
        RequestedInputs = ScriptableRenderPassInput.None;
        _graph.Reset();
    }

    public void AddRequestedInputs(ScriptableRenderPassInput input) => RequestedInputs |= input;

    public void EnqueuePass(RenderPass? pass)
    {
        if (pass == null || !pass.Enabled)
            return;
        _activePasses.Add(pass);
        RequestedInputs |= pass.Input;
        RenderStats.RecordPassEnqueued();
    }

    /// <summary>Copy enqueued events into <paramref name="destination"/>; returns the count written.</summary>
    public int CopyEnqueuedEvents(Span<RenderPassEvent> destination)
    {
        int count = Math.Min(_activePasses.Count, destination.Length);
        for (int i = 0; i < count; i++)
            destination[i] = _activePasses[i].Event;
        return count;
    }

    internal int CopyLastPassEvents(Span<RenderPassEvent> destination)
    {
        int count = Math.Min(_lastEventCount, destination.Length);
        for (int i = 0; i < count; i++)
            destination[i] = _lastEvents[i];
        return count;
    }

    [HotPath]
    public void Execute(ScriptableRenderContext context)
        => Execute(context, copyColor: null, copyDepth: null);

    [HotPath]
    public void Execute(ScriptableRenderContext context, RenderPass? copyColor, RenderPass? copyDepth)
    {
        SortByEvent(_activePasses);
        CompileGraph(copyColor, copyDepth);
        _graph.Execute(context);
    }

    internal void CompileGraph(RenderPass? copyColor, RenderPass? copyDepth)
    {
        SortByEvent(_activePasses);
        _graph.Reset();
        RenderGraphResource opaqueColor = _graph.Import(RenderGraphImportedTexture.CameraOpaqueColor);
        RenderGraphResource cameraDepth = _graph.Import(RenderGraphImportedTexture.CameraDepth);
        if ((RequestedInputs & ScriptableRenderPassInput.Color) != 0)
            _graph.ForceRead(opaqueColor);
        if ((RequestedInputs & ScriptableRenderPassInput.Depth) != 0)
            _graph.ForceRead(cameraDepth);

        bool copiesRecorded = false;
        for (int i = 0; i < _activePasses.Count; i++)
        {
            RenderPass pass = _activePasses[i];
            if (!copiesRecorded && pass.Event > RenderPassEvent.AfterRenderingOpaques)
            {
                RecordCopyPasses(copyColor, copyDepth, opaqueColor, cameraDepth);
                copiesRecorded = true;
            }

            RecordDeclaredPass(pass, opaqueColor, cameraDepth, allowCulling: false);
        }

        if (!copiesRecorded)
            RecordCopyPasses(copyColor, copyDepth, opaqueColor, cameraDepth);

        _graph.Compile();
        int eventCount = Math.Min(_activePasses.Count, _lastEvents.Length);
        for (int i = 0; i < eventCount; i++)
            _lastEvents[i] = _activePasses[i].Event;
        _lastEventCount = eventCount;
    }

    private void RecordDeclaredPass(
        RenderPass pass,
        RenderGraphResource opaqueColor,
        RenderGraphResource cameraDepth,
        bool allowCulling)
    {
        _graph.BeginPass(pass, allowCulling);
        ScriptableRenderPassInput input = pass.Input;
        if ((input & ScriptableRenderPassInput.Color) != 0)
            _graph.Read(opaqueColor);
        if ((input & ScriptableRenderPassInput.Depth) != 0)
            _graph.Read(cameraDepth);
        _graph.EndPass();
    }

    private void RecordCopyPasses(
        RenderPass? copyColor,
        RenderPass? copyDepth,
        RenderGraphResource opaqueColor,
        RenderGraphResource cameraDepth)
    {
        if (copyColor != null)
        {
            _graph.BeginPass(copyColor, allowCulling: true);
            _graph.Write(opaqueColor);
            _graph.EndPass();
        }

        if (copyDepth != null)
        {
            _graph.BeginPass(copyDepth, allowCulling: true);
            _graph.Write(cameraDepth);
            _graph.EndPass();
        }
    }

    [HotPath]
    private static void SortByEvent(List<RenderPass> passes)
    {
        for (int i = 1; i < passes.Count; i++)
        {
            RenderPass key = passes[i];
            int j = i - 1;
            while (j >= 0 && passes[j].Event > key.Event)
            {
                passes[j + 1] = passes[j];
                j--;
            }
            passes[j + 1] = key;
        }
    }
}
