// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

using XEngine.Animation;
using XEngine.Editor.Theming;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.GUI.Panels;

public partial class AnimatorWindow
{
    // Layers/parameters list and the graph canvas each hang a subtree region (8 / 16 MiB,
    // host-shared fonts). The toolbar, Layers|Parameters tabs, splitters and inspector stay
    // on the host. The canvas cannot use visual-state compare: Origami.NodeGraph still
    // issues unversioned Draw for grid/wires/overlay, so recapture is keyed.
    private UiPaperSubtreeRegion? _sidebarPaintRegion;
    private UiPaperSubtreeRegion? _canvasPaintRegion;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _paintGeometryBudget = new(8 * 1024 * 1024);
    private readonly UiMemoryBudget _paintAtlasBudget = new(16 * 1024 * 1024);
    private ulong _canvasPaintKey;
    private bool _canvasPointerOver;
    private int _contentStamp;
    private int _visualStamp;
    private static int s_continuousWantCount;
    private bool _countedContinuous;
    private bool _previewPlaying;
    private static readonly object s_transitionPaintSource = new();

    /// <summary>A visible Animator that is play-linked or probe-playing must keep presenting.
    /// Counted per instance so a second idle Animator does not clear a live one.</summary>
    public static bool WantsContinuousPresent
    {
        get => s_continuousWantCount > 0;
        set
        {
            if (value && s_continuousWantCount == 0) s_continuousWantCount = 1;
            if (!value) s_continuousWantCount = 0;
        }
    }

    internal static void PublishContinuousPresent(bool wants, ref bool counted)
    {
        if (wants == counted) return;
        counted = wants;
        s_continuousWantCount += wants ? 1 : -1;
        if (s_continuousWantCount < 0) s_continuousWantCount = 0;
    }

    internal int ContentStamp => _contentStamp;
    internal int VisualStamp => _visualStamp;
    internal long SidebarRegionCaptures => _sidebarPaintRegion?.CaptureCount ?? -1;
    internal long CanvasRegionCaptures => _canvasPaintRegion?.CaptureCount ?? -1;
    public float HeaderStripWidth => _sidebarWidth + SplitterSize;

    public bool IsPreviewPlaying
    {
        get => _previewPlaying || (Application.IsPlaying && ResolveLiveAnimator() != null);
        set
        {
            _previewPlaying = value;
            PublishContinuousPresent(value, ref _countedContinuous);
        }
    }

    void BumpContent() => _contentStamp++;
    void BumpVisual() => _visualStamp++;

    void AttachSidebarPaintRegion(Paper paper)
    {
        if (!Program.AnimatorPaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
        EnsurePaintPaper(paper);
        if (_sidebarPaintRegion == null)
        {
            paper.StabilizeFontAtlasBeforeRender = true;
            paper.Canvas.EnableFontAtlasRetirement();
            _sidebarPaintRegion = new UiPaperSubtreeRegion(paper, _paintGeometryBudget, _paintAtlasBudget, 512,
                shareHostFonts: true, compareVisualState: true);
        }
        _sidebarPaintRegion.Attach(paper.CurrentParent);
    }

    void AttachCanvasPaintRegion(Paper paper)
    {
        if (!Program.AnimatorPaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
        EnsurePaintPaper(paper);
        if (_canvasPaintRegion == null)
        {
            paper.StabilizeFontAtlasBeforeRender = true;
            paper.Canvas.EnableFontAtlasRetirement();
            _canvasPaintRegion = new UiPaperSubtreeRegion(paper, _paintGeometryBudget, _paintAtlasBudget, 512,
                shareHostFonts: true, compareVisualState: false);
        }
        var rect = paper.CurrentParent.Data.LayoutRect;
        bool pointerOver = paper.PointerPos.X >= rect.Min.X && paper.PointerPos.X <= rect.Max.X
            && paper.PointerPos.Y >= rect.Min.Y && paper.PointerPos.Y <= rect.Max.Y;
        ulong key = PackCanvasKey();
        if (_canvasPaintRegion.CaptureCount == 0 || key != _canvasPaintKey || pointerOver || _canvasPointerOver
            || WantsContinuousPresent)
            _canvasPaintRegion.Invalidate();
        _canvasPaintKey = key;
        _canvasPointerOver = pointerOver;
        _canvasPaintRegion.Attach(paper.CurrentParent);
    }

    void EnsurePaintPaper(Paper paper)
    {
        if (ReferenceEquals(_paintRegionPaper, paper)) return;
        DisposePaintRegions();
        _paintRegionPaper = paper;
    }

    void DisposePaintRegions()
    {
        _sidebarPaintRegion?.Dispose(); _sidebarPaintRegion = null;
        _canvasPaintRegion?.Dispose(); _canvasPaintRegion = null;
        _paintRegionPaper = null;
        _canvasPaintKey = 0;
        _canvasPointerOver = false;
    }

    ulong PackCanvasKey()
    {
        uint style = unchecked((uint)HashCode.Combine(
            _contentStamp, _visualStamp, _nodes.Count, _wires.Count,
            _selectedLayerIndex, _smPath.Count, _sidebarTab,
            HashCode.Combine(_selectedStateName, _selectedTransitionName, _editingBlendTreeState,
                RuntimeHelpers.GetHashCode(_controller))));
        uint view = (uint)BitConverter.SingleToInt32Bits(_ctrl.Pan.X)
            ^ (uint)BitConverter.SingleToInt32Bits(_ctrl.Pan.Y)
            ^ (uint)BitConverter.SingleToInt32Bits(_ctrl.Zoom);
        return ((ulong)style << 32) | view;
    }

    void PaintTransitionTimeline(Paper paper, ref ElementHandle handle, AnimatorController.Transition t)
    {
        if (!Program.AnimatorPaintIncremental)
        {
            paper.Draw(ref handle, (canvas, r) => DrawTransitionTimelineContent(canvas, r, t));
            return;
        }
        uint version = unchecked((uint)HashCode.Combine(
            t.Name, t.DestinationStateName,
            BitConverter.SingleToInt32Bits(t.Duration),
            BitConverter.SingleToInt32Bits(t.Offset),
            BitConverter.SingleToInt32Bits(t.ExitTime),
            t.HasExitTime, t.HasFixedDuration, _tlHit));
        paper.DrawVersioned(ref handle, "animator.transition", version, 0,
            s_transitionPaintSource, EditorTheme.DefaultFont,
            (canvas, r) => DrawTransitionTimelineContent(canvas, r, t));
    }
}
