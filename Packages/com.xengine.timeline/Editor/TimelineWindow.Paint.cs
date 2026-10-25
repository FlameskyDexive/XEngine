// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

using XEngine.Editor;
using XEngine.Editor.Theming;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Timeline.Editor;

public partial class TimelineWindow
{
    // Track headers and the ruler/sheet each hang a compared subtree region (8 / 16 MiB,
    // host-shared fonts). The toolbar and clip inspector stay on the host — the playhead
    // clock and numeric fields change every live frame.
    private UiPaperSubtreeRegion? _headerPaintRegion;
    private UiPaperSubtreeRegion? _sheetPaintRegion;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _paintGeometryBudget = new(8 * 1024 * 1024);
    private readonly UiMemoryBudget _paintAtlasBudget = new(16 * 1024 * 1024);
    private static readonly object s_rulerPaintSource = new();
    private static readonly object s_sheetPaintSource = new();
    private readonly Action<Canvas, Rect> _drawRulerAction;
    private readonly Action<Canvas, Rect> _drawSheetAction;
    private float _drawPaneWidth;
    private float _drawSheetHeight;
    private TimelineAsset? _drawAsset;

    private int _contentStamp;
    private int _visualStamp;
    private static int s_continuousWantCount;
    private bool _countedContinuous;
    private bool _previewPlaying;
    private DateTime _lastFrameTime = DateTime.UtcNow;

    /// <summary>A visible Timeline that is playing or being scrubbed must keep presenting.
    /// Counted per instance so a second idle Timeline does not clear a live director.</summary>
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
    internal long HeaderRegionCaptures => _headerPaintRegion?.CaptureCount ?? -1;
    internal long SheetRegionCaptures => _sheetPaintRegion?.CaptureCount ?? -1;
    public static float HeaderStripWidth => TrackListWidth;

    public bool IsPreviewPlaying
    {
        get => _previewPlaying || (_director?.IsPlaying ?? false);
        set
        {
            _previewPlaying = value;
            if (_director != null)
            {
                if (value) _director.Play();
                else _director.Pause();
            }
            PublishContinuousPresent(value || _dragKind != DragKind.None, ref _countedContinuous);
        }
    }

    void BumpContent() => _contentStamp++;
    void BumpVisual() => _visualStamp++;

    void AttachPaintRegion(Paper paper, ref UiPaperSubtreeRegion? region)
    {
        if (!Program.TimelinePaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
        if (!ReferenceEquals(_paintRegionPaper, paper))
        {
            DisposePaintRegions();
            _paintRegionPaper = paper;
        }
        if (region == null)
        {
            paper.StabilizeFontAtlasBeforeRender = true;
            paper.Canvas.EnableFontAtlasRetirement();
            region = new UiPaperSubtreeRegion(paper, _paintGeometryBudget, _paintAtlasBudget, 512,
                shareHostFonts: true, compareVisualState: true);
        }
        region.Attach(paper.CurrentParent);
    }

    void DisposePaintRegions()
    {
        _headerPaintRegion?.Dispose(); _headerPaintRegion = null;
        _sheetPaintRegion?.Dispose(); _sheetPaintRegion = null;
        _paintRegionPaper = null;
    }

    void DrawRulerDeferred(Canvas canvas, Rect r)
    {
        if (_drawAsset != null)
            DrawTimeRuler(canvas, r, _drawPaneWidth, _drawAsset);
    }

    void DrawSheetDeferred(Canvas canvas, Rect r)
    {
        if (_drawAsset != null)
            DrawSheet(canvas, r, _drawPaneWidth, _drawSheetHeight, _drawAsset);
    }

    void PaintRuler(Paper paper, ref ElementHandle handle)
    {
        if (!Program.TimelinePaintIncremental)
        {
            paper.Draw(ref handle, _drawRulerAction);
            return;
        }
        paper.DrawVersioned(ref handle, "timeline.ruler", PackTimelineVersion(),
            (uint)BitConverter.SingleToInt32Bits(_drawPaneWidth),
            s_rulerPaintSource, _font ?? EditorTheme.DefaultFont, _drawRulerAction);
    }

    void PaintSheet(Paper paper, ref ElementHandle handle)
    {
        if (!Program.TimelinePaintIncremental)
        {
            paper.Draw(ref handle, _drawSheetAction);
            return;
        }
        ulong detail = ((ulong)(uint)BitConverter.SingleToInt32Bits(_drawSheetHeight) << 32)
            | (uint)BitConverter.SingleToInt32Bits(_drawPaneWidth);
        paper.DrawVersioned(ref handle, "timeline.sheet", PackTimelineVersion(), detail,
            s_sheetPaintSource, _font ?? EditorTheme.DefaultFont, _drawSheetAction);
    }

    ulong PackTimelineVersion()
    {
        uint style = unchecked((uint)HashCode.Combine(
            RuntimeHelpers.GetHashCode(_font ?? EditorTheme.DefaultFont),
            _contentStamp, _visualStamp, _selectedTrackIndex, _selectedClipIndex,
            _selectedMarkerIndex, ActiveAsset?.Tracks.Count ?? 0));
        uint play = (uint)BitConverter.SingleToInt32Bits((float)PlayheadTime);
        return ((ulong)style << 32) | play;
    }

    static TimelineWindow()
    {
        XEngine.Editor.Core.EditorShellIdleHosts.RegisterLiveTool(typeof(TimelineWindow),
            static () => WantsContinuousPresent);
    }

    public TimelineWindow()
    {
        _drawRulerAction = DrawRulerDeferred;
        _drawSheetAction = DrawSheetDeferred;
    }

    void TickPreviewTime()
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;
        if (!_previewPlaying || _director != null) return;
        var ta = ActiveAsset;
        if (ta is null) return;
        _previewTime += dt;
        double dur = DurationOf(ta);
        if (dur > 0 && _previewTime >= dur)
            _previewTime %= dur;
    }
}
