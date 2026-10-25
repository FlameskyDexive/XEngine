// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

using XEngine.Editor.Theming;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Vector;

namespace XEngine.Editor.GUI.Panels;

public partial class AnimationClipWindow
{
    // Property list, ruler+events, and dopesheet/curves each hang a compared subtree region
    // (8 / 16 MiB, host-shared fonts). The toolbar, banners, key inspector and event inspector
    // stay on the host — play/record clocks and text fields change every live frame.
    private UiPaperSubtreeRegion? _listPaintRegion;
    private UiPaperSubtreeRegion? _timelinePaintRegion;
    private UiPaperSubtreeRegion? _sheetPaintRegion;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _paintGeometryBudget = new(8 * 1024 * 1024);
    private readonly UiMemoryBudget _paintAtlasBudget = new(16 * 1024 * 1024);
    private static readonly object s_rulerPaintSource = new();
    private static readonly object s_eventsPaintSource = new();
    private static readonly object s_dopesheetPaintSource = new();
    private static readonly object s_curvesPaintSource = new();
    private readonly Action<Canvas, Rect> _drawRulerAction;
    private readonly Action<Canvas, Rect> _drawEventsAction;
    private readonly Action<Canvas, Rect> _drawDopesheetAction;
    private readonly Action<Canvas, Rect> _drawCurvesAction;
    private float _drawRulerWidth;
    private float _drawEventsWidth;
    private float _drawSheetWidth;
    private float _drawSheetHeight;
    private float _drawCurveWidth;
    private float _drawCurveHeight;

    private static int s_continuousWantCount;
    private bool _countedContinuous;

    /// <summary>A visible Animation window that is previewing, recording, or being scrubbed
    /// must keep presenting; an idle inspection can stay cached. Counted per instance so a
    /// second idle Animation window does not clear the live one.</summary>
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

    internal static bool PaintIncremental => Program.AnimationPaintIncremental;
    internal long ListRegionCaptures => _listPaintRegion?.CaptureCount ?? -1;
    internal long TimelineRegionCaptures => _timelinePaintRegion?.CaptureCount ?? -1;
    internal long SheetRegionCaptures => _sheetPaintRegion?.CaptureCount ?? -1;

    public AnimationClipWindow()
    {
        _drawRulerAction = DrawRulerDeferred;
        _drawEventsAction = DrawEventsDeferred;
        _drawDopesheetAction = DrawDopesheetDeferred;
        _drawCurvesAction = DrawCurvesDeferred;
    }

    private void AttachPaintRegion(Paper paper, ref UiPaperSubtreeRegion? region)
    {
        if (!Program.AnimationPaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
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

    private void DisposePaintRegions()
    {
        _listPaintRegion?.Dispose(); _listPaintRegion = null;
        _timelinePaintRegion?.Dispose(); _timelinePaintRegion = null;
        _sheetPaintRegion?.Dispose(); _sheetPaintRegion = null;
        _paintRegionPaper = null;
    }

    private void DrawRulerDeferred(Canvas canvas, Rect r)
        => DrawRulerContent(canvas, r, _drawRulerWidth);

    private void DrawEventsDeferred(Canvas canvas, Rect r)
        => DrawEventMarkers(canvas, r, _drawEventsWidth);

    private void DrawDopesheetDeferred(Canvas canvas, Rect r)
        => DrawDopesheetContent(canvas, r, _drawSheetWidth, _drawSheetHeight);

    private void DrawCurvesDeferred(Canvas canvas, Rect r)
        => DrawCurvesContent(canvas, r, _drawCurveWidth, _drawCurveHeight);

    private void PaintRuler(Paper paper, ref ElementHandle handle)
    {
        if (!Program.AnimationPaintIncremental)
        {
            paper.Draw(ref handle, _drawRulerAction);
            return;
        }
        ulong version = PackViewAndPlay();
        ulong detail = (uint)BitConverter.SingleToInt32Bits(_drawRulerWidth);
        paper.DrawVersioned(ref handle, "animation.ruler", version, detail,
            s_rulerPaintSource, _font ?? EditorTheme.DefaultFont, _drawRulerAction);
    }

    private void PaintEvents(Paper paper, ref ElementHandle handle)
    {
        if (!Program.AnimationPaintIncremental)
        {
            paper.Draw(ref handle, _drawEventsAction);
            return;
        }
        uint events = 0;
        var clip = _model.Clip;
        if (clip != null)
        {
            for (int i = 0; i < clip.Events.Count; i++)
                events ^= (uint)BitConverter.SingleToInt32Bits(clip.Events[i].Time);
            events ^= (uint)clip.Events.Count;
        }
        ulong version = PackViewAndPlay() ^ events;
        ulong detail = ((ulong)(uint)_model.ContentStamp << 32) | (uint)(_selectedEventIndex + 1);
        paper.DrawVersioned(ref handle, "animation.events", version, detail,
            s_eventsPaintSource, EditorTheme.DefaultFont, _drawEventsAction);
    }

    private void PaintDopesheet(Paper paper, ref ElementHandle handle)
    {
        if (!Program.AnimationPaintIncremental)
        {
            paper.Draw(ref handle, _drawDopesheetAction);
            return;
        }
        ulong version = PackSheetVersion();
        ulong detail = PackSheetDetail()
            ^ ((ulong)(uint)BitConverter.SingleToInt32Bits(_drawSheetHeight) << 32);
        paper.DrawVersioned(ref handle, "animation.dopesheet", version, detail,
            s_dopesheetPaintSource, EditorTheme.DefaultFont, _drawDopesheetAction);
    }

    private void PaintCurves(Paper paper, ref ElementHandle handle)
    {
        if (!Program.AnimationPaintIncremental)
        {
            paper.Draw(ref handle, _drawCurvesAction);
            return;
        }
        ulong version = PackSheetVersion();
        ulong detail = PackSheetDetail()
            ^ ((ulong)(uint)(_curveOverlayAll ? 1 : 0) << 48)
            ^ ((ulong)(uint)BitConverter.SingleToInt32Bits(_drawCurveHeight) << 16);
        paper.DrawVersioned(ref handle, "animation.curves", version, detail,
            s_curvesPaintSource, EditorTheme.DefaultFont, _drawCurvesAction);
    }

    private ulong PackViewAndPlay()
    {
        uint view = (uint)BitConverter.SingleToInt32Bits(_model.ViewStartTime)
            ^ (uint)BitConverter.SingleToInt32Bits(_model.ViewEndTime);
        uint play = (uint)BitConverter.SingleToInt32Bits((float)_playbackTime);
        return ((ulong)view << 32) | play;
    }

    private ulong PackSheetVersion()
    {
        uint style = unchecked((uint)HashCode.Combine(
            RuntimeHelpers.GetHashCode(_font ?? EditorTheme.DefaultFont),
            _model.ContentStamp, _model.VisualStamp, _rows.Count,
            _showCurves, _marquee));
        return ((ulong)style << 32) | (uint)BitConverter.SingleToInt32Bits(_propsScrollY);
    }

    private ulong PackSheetDetail()
    {
        uint size = (uint)BitConverter.SingleToInt32Bits(_drawSheetWidth);
        uint extra = 0;
        if (_marquee)
        {
            extra = (uint)BitConverter.SingleToInt32Bits(_marqueeX0)
                ^ (uint)BitConverter.SingleToInt32Bits(_marqueeY1);
        }
        return ((ulong)size << 32) | extra | (uint)PackViewAndPlay();
    }
}
