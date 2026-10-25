// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.UI;

/// <summary>Read-only host snapshot for XGUI diagnostics: canvases, clip chains, and last UI RenderStats.</summary>
public readonly struct XGuiHostDiagnosticsSnapshot
{
    public int CanvasCount { get; init; }
    public int OverlayNodes { get; init; }
    public int WorldNodes { get; init; }
    public int ActiveNodes { get; init; }
    public int RectClips { get; init; }
    public int StencilClips { get; init; }
    public int UIBatches { get; init; }
    public int UIDrawCalls { get; init; }
    public int MeshUploads { get; init; }
    public int LayoutRebuilds { get; init; }
    public int GraphicRebuilds { get; init; }
    public int DirtyLayoutRoots { get; init; }
}

/// <summary>Collects CanvasRegistry and RenderStats counters for the diagnostics panel and host tests.</summary>
public static class XGuiHostDiagnostics
{
    public static XGuiHostDiagnosticsSnapshot Capture(Scene? scene)
    {
        if (scene == null)
            return default;

        IReadOnlyList<GameCanvas> canvases = CanvasRegistry.GetCanvases(scene);
        var nodes = new List<CanvasRenderNode>();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.Overlay, nodes);
        int overlay = nodes.Count;
        int active = CountActive(nodes);
        int rectClips = CountRectClips(nodes);
        int stencilClips = CountStencilClips(nodes);

        nodes.Clear();
        CanvasRegistry.CollectRenderNodes(scene, UISurface.World, nodes);
        int world = nodes.Count;
        active += CountActive(nodes);
        rectClips += CountRectClips(nodes);
        stencilClips += CountStencilClips(nodes);

        RenderStats.Frame frame = RenderStats.Last;
        return new XGuiHostDiagnosticsSnapshot
        {
            CanvasCount = canvases.Count,
            OverlayNodes = overlay,
            WorldNodes = world,
            ActiveNodes = active,
            RectClips = rectClips,
            StencilClips = stencilClips,
            UIBatches = frame.UIBatches,
            UIDrawCalls = frame.UIDrawCalls,
            MeshUploads = frame.MeshUploads,
            LayoutRebuilds = frame.LayoutRebuilds,
            GraphicRebuilds = frame.GraphicRebuilds,
            DirtyLayoutRoots = frame.DirtyLayoutRoots,
        };
    }

    private static int CountActive(List<CanvasRenderNode> nodes)
    {
        int count = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].IsAttached)
                count++;
        }
        return count;
    }

    private static int CountRectClips(List<CanvasRenderNode> nodes)
    {
        int count = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Clip.HasRect)
                count++;
        }
        return count;
    }

    private static int CountStencilClips(List<CanvasRenderNode> nodes)
    {
        int count = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Clip.UsesStencil)
                count++;
        }
        return count;
    }
}
