// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.OrigamiUI;
using XEngine.OrigamiUI.Charts;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.Profiling;
using XEngine.Runtime.Rendering;
using XEngine.Scribe;
using XEngine.Vector;
using Color = System.Drawing.Color;
using PaperTextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// Unity-shaped profiler: a recorded ring of frame samples plotted as CPU / Rendering / Memory
/// chart strips with click-to-select frame inspection, plus the live sampler readouts below.
/// Recording samples the editor-side counters (RenderStats + EditorProfiler) once per rendered
/// frame into preallocated arrays — a steady profiling session allocates nothing per frame.
/// </summary>
public sealed class ProfilerPanel : DockPanel, IScriptReloadCleanup
{
    [MenuItem("Window/Analysis/Profiler", priority: 20)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(ProfilerPanel));

    public override string Title => "Profiler";
    public override string Icon => EditorIcons.ChartLine;

    // ── Incremental paint (Program.ProfilerPaintIncremental) ─────────────────────────────
    // Two compared subtree regions sharing one geometry/atlas budget pair: the three chart
    // strips (already one custom-painted box; the painter now declares an exact key) and the
    // details scroll. The toolbar stays on the host — record/clear chips and the frame label
    // change while a session is live. Regions recreate when the panel changes host Paper and
    // release on close / before a script reload.
    private UiPaperSubtreeRegion? _chartPaintRegion;
    private UiPaperSubtreeRegion? _detailPaintRegion;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _paintGeometryBudget = new(8 * 1024 * 1024);
    private readonly UiMemoryBudget _paintAtlasBudget = new(16 * 1024 * 1024);
    private static readonly object s_chartPaintSource = new();
    private readonly Action<Canvas, Rect> _drawChartsAction;
    private float _drawChartWidth;

    /// <summary>A visible Profiler that is recording, showing the live frame, or listening must
    /// keep presenting; a historical inspection with recording off can stay idle.</summary>
    public static bool WantsContinuousPresent { get; set; }

    internal static bool PaintIncremental => Program.ProfilerPaintIncremental;
    internal long ChartRegionCaptures => _chartPaintRegion?.CaptureCount ?? -1;
    internal long DetailRegionCaptures => _detailPaintRegion?.CaptureCount ?? -1;
    internal bool Recording { get => _recording; set => _recording = value; }
    internal int SelectedOffset
    {
        get => _selectedOffset;
        set => _selectedOffset = value < 0 ? -1 : (_historyCount == 0 ? -1 : Math.Min(value, _historyCount - 1));
    }
    internal int ScopeView { get => _scopeView; set => _scopeView = value; }
    internal int HistoryCount => _historyCount;

    public ProfilerPanel()
    {
        _drawChartsAction = DrawChartsDeferred;
    }

    public void OnScriptReloadCleanup()
    {
        DisposePaintRegions();
        WantsContinuousPresent = false;
    }

    /// <summary>Attach (creating on demand) one of the panel's paint regions to the current parent.
    /// No-op unless the incremental flag is set and the renderer can paint to a separate layer.</summary>
    private void AttachPaintRegion(Paper paper, ref UiPaperSubtreeRegion? region)
    {
        if (!Program.ProfilerPaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
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
        _chartPaintRegion?.Dispose(); _chartPaintRegion = null;
        _detailPaintRegion?.Dispose(); _detailPaintRegion = null;
        _paintRegionPaper = null;
    }

    internal void RecordTestSample(float frameMs, int draws, int batches, long tris, float heapMb)
    {
        _history[_historyHead] = new FrameSample
        {
            FrameMs = frameMs,
            Draws = draws,
            Batches = batches,
            Tris = tris,
            HeapMb = heapMb,
        };
        _scopeCounts[_historyHead] = 0;
        _historyHead = (_historyHead + 1) % HistoryLength;
        if (_historyCount < HistoryLength) _historyCount++;
    }

    // ── Recorded history ────────────────────────────────────────────────────
    private const int HistoryLength = 300;

    private struct FrameSample
    {
        public float FrameMs;
        public int Draws;
        public int Batches;
        public long Tris;
        public float HeapMb;
        public float GcDeltaKb;
    }

    private readonly FrameSample[] _history = new FrameSample[HistoryLength];
    // Marker scopes per recorded frame (jagged, fully preallocated: ~300 × 512 × 24 B ≈ 3.7 MB).
    private readonly ProfilerFrames.Entry[][] _scopeHistory = new ProfilerFrames.Entry[HistoryLength][];
    private readonly int[] _scopeCounts = new int[HistoryLength];
    private int _historyHead;   // next write slot
    private int _historyCount;  // filled entries (<= HistoryLength)
    private bool _recording = true;
    private bool _clearOnPlay = true;
    private bool _wasPlaying;
    private int _lastFrameTimeIndex = -1;
    private long _lastHeapBytes;

    /// <summary>Frames back from the newest recorded sample; -1 = live (no selection).</summary>
    private int _selectedOffset = -1;

    // ── Selected-frame scope views (built once per selection, not per UI frame) ──
    private int _scopeView;          // 0 = Hierarchy, 1 = Timeline, 2 = Raw
    private int _builtSlot = -1;     // history slot the trees below were built from
    private int _builtCount = -1;
    private readonly List<ScopeTreeNode> _treeRoots = new();
    private readonly List<FlameScope> _flameRoots = new();
    private readonly List<(ScopeTreeNode node, int depth)> _treeRows = new();

    private sealed class ScopeTreeNode
    {
        public int Id;
        public long TotalTicks;
        public long ChildTicks;
        public int Calls;
        public readonly List<ScopeTreeNode> Children = new();
    }

    /// <summary>Flame-view node for the v3.3.0 Origami Charts subsystem (Name/Value/Children selectors).</summary>
    private sealed class FlameScope
    {
        public string Name = "";
        public double Ms;
        public readonly List<FlameScope> Kids = new();
    }

    private ProfilerSample _sample;
    private ProfilerTelemetryFrame _telemetry;
    private readonly EditorProfilerCapture _remoteCapture = new();
    private string _listenPort = "49494";
    private string _captureStatus = "Local Editor";

    private const float ToolbarH = 26f;
    private const float ChartH = 84f;
    private const float ChartLabelW = 88f;

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        using var profilerScope = EditorProfiler.BeginCpuScope("Profiler");

        _sample = EditorProfiler.SampleNow();
        _telemetry = EditorProfiler.SampleTelemetryNow();
        if (_remoteCapture.Poll())
            _captureStatus = $"Remote {_remoteCapture.LastFrame.Target}";

        RecordFrameIfNew();
        WantsContinuousPresent = _recording || _selectedOffset < 0 || _remoteCapture.IsListening;

        using (paper.Column("prof_root").Size(width, height).Padding(8, 8, 8, 8).Clip().Enter())
        {
            DrawToolbar(paper, font, width - 16);
            paper.Box("prof_tb_gap").Height(4).IsNotInteractable();

            // Toolbar stays on the host (live frame label / chips). Charts and the details
            // scroll are the compared paint regions.
            using (paper.Box("prof_charts_paint").Width(width - 16).Height(ChartRowsHeight()).Enter())
            {
                AttachPaintRegion(paper, ref _chartPaintRegion);
                DrawCharts(paper, font, width - 16);
            }
            paper.Box("prof_chart_gap").Height(6).IsNotInteractable();

            FrameSample selectedSample = default;
            int selectedFrame = 0;
            bool selected = _selectedOffset >= 0 && TryGetSample(_selectedOffset, out selectedSample, out selectedFrame);
            if (selected)
            {
                int slot = (_historyHead - 1 - _selectedOffset + HistoryLength * 2) % HistoryLength;
                EnsureScopeViews(slot);
                using (paper.Row("prof_scope_tabs").Height(24).RowBetween(4).Enter())
                {
                    DrawViewTab(paper, font, "prof_view_h", "Hierarchy", 0);
                    DrawViewTab(paper, font, "prof_view_t", "Timeline", 1);
                    DrawViewTab(paper, font, "prof_view_r", "Raw", 2);
                }
                paper.Box("prof_tabs_gap").Height(4).IsNotInteractable();
            }

            float used = ToolbarH + 4 + ChartRowsHeight() + 6 + (selected ? 28f : 0f);
            float detailsH = MathF.Max(60f, height - 16 - used);
            using (paper.Column("prof_details_paint").Size(width - 16, detailsH).Enter())
            {
                AttachPaintRegion(paper, ref _detailPaintRegion);
                Origami.ScrollView(paper, "prof_details", width - 16, detailsH).Body(() =>
                {
                    if (selected)
                        DrawSelectedFrame(paper, font, selectedSample, selectedFrame);
                    else
                        DrawLiveSections(paper, font);
                });
            }
        }
    }

    // ── Recording ───────────────────────────────────────────────────────────

    private void RecordFrameIfNew()
    {
        // Clear on entering play mode (edge-triggered so it works with any entry path).
        bool playing = Runtime.Application.IsPlaying;
        if (playing && !_wasPlaying && _clearOnPlay)
            ClearHistory();
        _wasPlaying = playing;

        if (!_recording) return;

        int frameIndex = RenderStats.FrameTimeIndex;
        if (frameIndex == _lastFrameTimeIndex) return; // no new rendered frame since last GUI tick
        _lastFrameTimeIndex = frameIndex;

        // Frames are flowing (play mode / active edits): keep the editor UI ticking so the next
        // sample lands too. Goes quiet automatically once no new frame shows up.
        EditorApplication.Instance?.RequestContinuousRedraw(this);

        var last = RenderStats.Last;
        float[] ring = RenderStats.FrameTimeHistory;
        int newest = (frameIndex - 1 + ring.Length) % ring.Length;
        long heap = GC.GetTotalMemory(false);
        float gcDeltaKb = _lastHeapBytes == 0 ? 0f : (heap - _lastHeapBytes) / 1024f;
        _lastHeapBytes = heap;

        _history[_historyHead] = new FrameSample
        {
            FrameMs = ring[newest],
            Draws = last.DrawCalls,
            Batches = last.Batches,
            Tris = last.Triangles,
            HeapMb = heap / (1024f * 1024f),
            GcDeltaKb = MathF.Max(0f, gcDeltaKb),
        };

        _scopeHistory[_historyHead] ??= new ProfilerFrames.Entry[ProfilerFrames.MaxScopesPerFrame];
        _scopeCounts[_historyHead] = ProfilerFrames.CopyCompletedFrame(_scopeHistory[_historyHead], out _);

        _historyHead = (_historyHead + 1) % HistoryLength;
        if (_historyCount < HistoryLength) _historyCount++;
    }

    private void ClearHistory()
    {
        _historyHead = 0;
        _historyCount = 0;
        _selectedOffset = -1;
    }

    /// <summary>Sample <paramref name="offset"/> frames back from the newest recorded one.</summary>
    private bool TryGetSample(int offset, out FrameSample sample, out int frameNumber)
    {
        sample = default;
        frameNumber = 0;
        if (offset < 0 || offset >= _historyCount) return false;
        int index = (_historyHead - 1 - offset + HistoryLength * 2) % HistoryLength;
        sample = _history[index];
        frameNumber = _historyCount - offset;
        return true;
    }

    // ── Toolbar ─────────────────────────────────────────────────────────────

    private void DrawToolbar(Paper paper, FontFile font, float width)
    {
        using (paper.Row("prof_tb").Width(width).Height(ToolbarH).RowBetween(4).Enter())
        {
            var rec = Origami.IconButton(paper, "prof_rec", EditorIcons.Circle_I,
                () => _recording = !_recording).Style(ButtonStyle.Ghost);
            if (_recording) rec.Danger();
            rec.Show();

            Origami.Button(paper, "prof_clear", "Clear", ClearHistory).Height(22).Show();

            var cop = Origami.Button(paper, "prof_clear_play", "Clear on Play",
                () => _clearOnPlay = !_clearOnPlay).Height(22);
            if (_clearOnPlay) cop.Primary();
            cop.Show();

            paper.Box("prof_tb_div").Width(1).Height(18)
                .Margin(2, 2, UnitValue.StretchOne, UnitValue.StretchOne)
                .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

            Origami.IconButton(paper, "prof_prev", EditorIcons.CaretLeft_I, () =>
            {
                if (_historyCount == 0) return;
                _selectedOffset = _selectedOffset < 0 ? 0 : Math.Min(_selectedOffset + 1, _historyCount - 1);
            }).Style(ButtonStyle.Ghost).Show();

            string frameLabel = _selectedOffset >= 0 && TryGetSample(_selectedOffset, out _, out int fn)
                ? $"Frame {fn}/{_historyCount}"
                : $"Current ({_historyCount})";
            paper.Box("prof_frame_lbl").Width(110).Height(ToolbarH).IsNotInteractable()
                .Text(frameLabel, font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleCenter);

            Origami.IconButton(paper, "prof_next", EditorIcons.CaretRight_I, () =>
            {
                if (_selectedOffset > 0) _selectedOffset--;
                else _selectedOffset = -1;
            }).Style(ButtonStyle.Ghost).Show();

            Origami.Button(paper, "prof_current", "Current", () => _selectedOffset = -1).Height(22).Show();

            paper.Box("prof_tb_sp");

            // Player UDP capture (unchanged behaviour, moved into the toolbar).
            paper.Box("prof_capture_label").Width(40).Height(ToolbarH).IsNotInteractable()
                .Text("Listen", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft);
            Origami.TextField(paper, "prof_capture_port", _listenPort, value => _listenPort = value).Width(64).Show();
            string icon = _remoteCapture.IsListening ? EditorIcons.Stop : EditorIcons.Play;
            Origami.Button(paper, "prof_capture_toggle", icon, ToggleCapture).Width(26).Show();
            paper.Box("prof_capture_status").Width(150).Height(ToolbarH).IsNotInteractable()
                .Text(_captureStatus, font).TextColor(EditorTheme.InkDim)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleRight).TextTruncate();
        }
    }

    // ── Charts ──────────────────────────────────────────────────────────────

    private static float ChartRowsHeight() => ChartH * 3 + 8;

    private void DrawCharts(Paper paper, FontFile font, float width)
    {
        _drawChartWidth = width;
        paper.Box("prof_charts").Width(width).Height(ChartRowsHeight())
            .BackgroundColor(EditorTheme.Neutral100)
            .Rounded(6)
            .OnClick(0, (_, e) => SelectFrameAtX((float)e.RelativePosition.X, width))
            .OnDragging(e => SelectFrameAtX((float)e.RelativePosition.X, width))
            .OnPostLayout((handle, layoutRect) =>
            {
                _ = layoutRect;
                if (!Program.ProfilerPaintIncremental)
                {
                    paper.Draw(ref handle, _drawChartsAction);
                    return;
                }
                uint style = unchecked((uint)HashCode.Combine(
                    EditorTheme.Ink500.ToArgb(), EditorTheme.InkDim.ToArgb(),
                    EditorTheme.BorderSoft.ToArgb(), EditorTheme.FontSize, EditorTheme.FontSizeSmall,
                    RuntimeHelpers.GetHashCode(EditorTheme.DefaultFont)));
                uint content = 0;
                if (_historyCount > 0 && TryGetSample(0, out FrameSample newest, out int frameNumber))
                {
                    _ = frameNumber;
                    content = (uint)BitConverter.SingleToInt32Bits(newest.FrameMs)
                        ^ (uint)newest.Batches
                        ^ (uint)BitConverter.SingleToInt32Bits(newest.HeapMb);
                }
                ulong version = ((ulong)style << 32) | ((uint)_historyCount ^ content
                    ^ (uint)BitConverter.SingleToInt32Bits(_drawChartWidth));
                ulong detail = ((ulong)(uint)_historyHead << 32) | (uint)(_selectedOffset + 1);
                paper.DrawVersioned(ref handle, "profiler.charts", version, detail,
                    s_chartPaintSource, EditorTheme.DefaultFont, _drawChartsAction);
            });
    }

    private void DrawChartsDeferred(Canvas canvas, Rect r)
        => DrawChartsContent(canvas, r, _drawChartWidth);

    private void SelectFrameAtX(float relX, float width)
    {
        if (_historyCount == 0) return;
        float plotX = ChartLabelW;
        float plotW = width - ChartLabelW - 6;
        if (relX < plotX) return;
        float t = Math.Clamp((relX - plotX) / MathF.Max(1f, plotW), 0f, 1f);
        // Newest sample sits at the right edge.
        int offsetFromNewest = (int)MathF.Round((1f - t) * (_historyCount - 1));
        _selectedOffset = Math.Clamp(offsetFromNewest, 0, _historyCount - 1);
    }

    private void DrawChartsContent(Canvas canvas, Rect r, float width)
    {
        float left = (float)r.Min.X, top = (float)r.Min.Y;
        float plotX = left + ChartLabelW;
        float plotW = width - ChartLabelW - 6;

        DrawChartStrip(canvas, plotX, top + 2, plotW, ChartH - 4, ChartChannel.Cpu);
        DrawChartStrip(canvas, plotX, top + ChartH + 2, plotW, ChartH - 4, ChartChannel.Rendering);
        DrawChartStrip(canvas, plotX, top + ChartH * 2 + 2, plotW, ChartH - 4, ChartChannel.Memory);

        DrawChartLabel(canvas, left + 8, top + 6, "CPU", "frame ms");
        DrawChartLabel(canvas, left + 8, top + ChartH + 6, "Rendering", "batches");
        DrawChartLabel(canvas, left + 8, top + ChartH * 2 + 6, "Memory", "heap MB");

        // Selected-frame vertical marker across all strips.
        if (_selectedOffset >= 0 && _historyCount > 1)
        {
            float t = 1f - _selectedOffset / (float)(_historyCount - 1);
            float x = plotX + t * plotW;
            canvas.RectFilled(x, top + 2, 1.5f, ChartRowsHeight() - 4, Color.FromArgb(230, 255, 255, 255));
        }

        // Strip separators.
        canvas.RectFilled(left, top + ChartH, width, 1, EditorTheme.BorderSoft);
        canvas.RectFilled(left, top + ChartH * 2, width, 1, EditorTheme.BorderSoft);
    }

    private enum ChartChannel { Cpu, Rendering, Memory }

    private float ChannelValue(in FrameSample s, ChartChannel channel) => channel switch
    {
        ChartChannel.Cpu => s.FrameMs,
        ChartChannel.Rendering => s.Batches,
        _ => s.HeapMb,
    };

    private static Color32 ChannelColor(ChartChannel channel) => channel switch
    {
        ChartChannel.Cpu => new Color32(96, 165, 250, 255),   // blue
        ChartChannel.Rendering => new Color32(251, 146, 60, 255), // orange
        _ => new Color32(74, 222, 128, 255),                  // green
    };

    private void DrawChartStrip(Canvas canvas, float x, float y, float w, float h, ChartChannel channel)
    {
        if (_historyCount == 0) return;

        float max = 1e-5f;
        for (int i = 0; i < _historyCount; i++)
        {
            if (!TryGetSample(i, out FrameSample s, out _)) continue;
            max = MathF.Max(max, ChannelValue(in s, channel));
        }
        // CPU strip: fixed 33.3ms ceiling when frames are fast, so 60/30fps lines stay meaningful.
        if (channel == ChartChannel.Cpu) max = MathF.Max(max, 33.4f);

        Color32 color = ChannelColor(channel);
        float barW = w / _historyCount;
        for (int i = 0; i < _historyCount; i++)
        {
            if (!TryGetSample(i, out FrameSample s, out _)) continue;
            float v = ChannelValue(in s, channel) / max;
            float bh = MathF.Max(1f, v * (h - 12));
            // offset i counts back from the newest → newest bar at the right edge.
            float bx = x + w - (i + 1) * barW;
            canvas.RectFilled(bx, y + h - bh, MathF.Max(1f, barW - 0.5f), bh,
                new Color32(color.R, color.G, color.B, 170));
        }

        if (channel == ChartChannel.Cpu)
        {
            float y60 = y + h - 12 - (16.667f / max) * (h - 12);
            float y30 = y + h - 12 - (33.333f / max) * (h - 12);
            canvas.RectFilled(x, y60 + 12, w, 1, new Color32(74, 222, 128, 90));
            canvas.RectFilled(x, y30 + 12, w, 1, new Color32(251, 113, 133, 90));
        }
    }

    private void DrawChartLabel(Canvas canvas, float x, float y, string title, string unit)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        var titleLayout = canvas.CreateLayout(title, new TextLayoutSettings
        { Font = font, PixelSize = EditorTheme.FontSize, LineHeight = 1f, Quality = FontQuality.Normal });
        canvas.DrawLayout(titleLayout, x, y, EditorTheme.Ink500);
        var unitLayout = canvas.CreateLayout(unit, new TextLayoutSettings
        { Font = font, PixelSize = EditorTheme.FontSizeSmall, LineHeight = 1f, Quality = FontQuality.Normal });
        canvas.DrawLayout(unitLayout, x, y + 18, EditorTheme.InkDim);
    }

    // ── Details ─────────────────────────────────────────────────────────────

    private void DrawSelectedFrame(Paper paper, FontFile font, FrameSample s, int frameNumber)
    {
        DrawSection(paper, font, $"Frame {frameNumber}", new[]
        {
            $"CPU frame   {s.FrameMs,7:F2} ms",
            $"Draw calls  {s.Draws}   Batches {s.Batches}   Tris {s.Tris:N0}",
            $"Managed heap {s.HeapMb,7:F1} MB   GC delta {s.GcDeltaKb,7:F1} KB",
        });

        switch (_scopeView)
        {
            case 0: DrawScopeTree(paper, font); break;
            case 1: DrawScopeTimeline(paper); break;
            default: DrawSelectedFrameScopes(paper, font); break;
        }
    }

    private void DrawViewTab(Paper paper, FontFile font, string id, string label, int index)
    {
        var btn = Origami.Button(paper, id, label, () => _scopeView = index).Height(22);
        if (_scopeView == index) btn.Primary();
        btn.Show();
    }

    /// <summary>Rebuild the aggregated call tree and flame nodes when the selection points at a
    /// different recorded frame. Allocation happens here only, never while merely rendering.</summary>
    private void EnsureScopeViews(int slot)
    {
        int count = _scopeHistory[slot] != null ? _scopeCounts[slot] : 0;
        if (slot == _builtSlot && count == _builtCount) return;
        _builtSlot = slot;
        _builtCount = count;
        _treeRoots.Clear();
        _flameRoots.Clear();
        _treeRows.Clear();
        if (count == 0) return;

        var scopes = _scopeHistory[slot];

        // Reconstruct nesting from the depth-ordered entry list with explicit parent stacks;
        // the tree aggregates equal-path scopes while the flame view keeps every occurrence.
        var nodeStack = new ScopeTreeNode?[64];
        var flameStack = new FlameScope?[64];

        for (int i = 0; i < count; i++)
        {
            ref ProfilerFrames.Entry e = ref scopes[i];
            int depth = Math.Min(e.Depth, (byte)63);
            long span = Math.Max(0, e.EndTicks - e.StartTicks);
            string name = ProfilerFrames.NameOf(e.Id);

            // Tree: find-or-add the child with this id under the parent at depth-1.
            List<ScopeTreeNode> siblings = depth == 0 ? _treeRoots
                : (nodeStack[depth - 1]?.Children ?? _treeRoots);
            ScopeTreeNode? node = null;
            for (int c = 0; c < siblings.Count; c++)
                if (siblings[c].Id == e.Id) { node = siblings[c]; break; }
            if (node == null)
            {
                node = new ScopeTreeNode { Id = e.Id };
                siblings.Add(node);
            }
            node.Calls++;
            node.TotalTicks += span;
            if (depth > 0 && nodeStack[depth - 1] is { } parent)
                parent.ChildTicks += span;
            nodeStack[depth] = node;

            // Flame: every occurrence is its own bar, one depth row below its parent. The v3.3.0
            // Charts subsystem places rows by depth, so only the span value matters (no start).
            var flame = new FlameScope { Name = name, Ms = span * ProfilerFrames.MsPerTick };
            if (depth == 0) _flameRoots.Add(flame);
            else (flameStack[depth - 1]?.Kids ?? _flameRoots).Add(flame);
            flameStack[depth] = flame;
        }

        SortAndFlatten(_treeRoots, 0);
    }

    private void SortAndFlatten(List<ScopeTreeNode> nodes, int depth)
    {
        nodes.Sort(static (a, b) => b.TotalTicks.CompareTo(a.TotalTicks));
        for (int i = 0; i < nodes.Count; i++)
        {
            _treeRows.Add((nodes[i], depth));
            SortAndFlatten(nodes[i].Children, depth + 1);
        }
    }

    private void DrawScopeTree(Paper paper, FontFile font)
    {
        if (_treeRows.Count == 0)
        {
            paper.Box("prof_tree_empty").Height(18).IsNotInteractable()
                .Text("No marker scopes recorded for this frame.", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft);
            return;
        }

        double frameTicks = 0;
        for (int i = 0; i < _treeRoots.Count; i++) frameTicks += _treeRoots[i].TotalTicks;
        if (frameTicks <= 0) frameTicks = 1;

        using (paper.Row("prof_tree_hdr").Height(20).Enter())
        {
            TreeCell(paper, font, "prof_th_n", "Name", UnitValue.Stretch(), EditorTheme.Ink500);
            TreeCell(paper, font, "prof_th_t", "Total ms", 70, EditorTheme.Ink500);
            TreeCell(paper, font, "prof_th_s", "Self ms", 70, EditorTheme.Ink500);
            TreeCell(paper, font, "prof_th_c", "Calls", 50, EditorTheme.Ink500);
            TreeCell(paper, font, "prof_th_p", "Total %", 55, EditorTheme.Ink500);
        }

        int shown = Math.Min(_treeRows.Count, 64);
        for (int i = 0; i < shown; i++)
        {
            var (node, depth) = _treeRows[i];
            double totalMs = node.TotalTicks * ProfilerFrames.MsPerTick;
            double selfMs = Math.Max(0, node.TotalTicks - node.ChildTicks) * ProfilerFrames.MsPerTick;
            double percent = node.TotalTicks / frameTicks * 100.0;
            string indent = depth switch { 0 => "", 1 => "   ", 2 => "      ", _ => "         " };

            using (paper.Row("prof_tr" + i).Height(18).Enter())
            {
                TreeCell(paper, font, "prof_tc_n" + i, $"{indent}{ProfilerFrames.NameOf(node.Id)}",
                    UnitValue.Stretch(), EditorTheme.Ink300);
                TreeCell(paper, font, "prof_tc_t" + i, $"{totalMs:F3}", 70, EditorTheme.Ink400);
                TreeCell(paper, font, "prof_tc_s" + i, $"{selfMs:F3}", 70, EditorTheme.Ink400);
                TreeCell(paper, font, "prof_tc_c" + i, node.Calls.ToString(), 50, EditorTheme.Ink400);
                TreeCell(paper, font, "prof_tc_p" + i, $"{percent:F1}%", 55, EditorTheme.Ink400);
            }
        }
        if (_treeRows.Count > shown)
            paper.Box("prof_tree_more").Height(17).IsNotInteractable()
                .Text($"… {_treeRows.Count - shown} more rows", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft);
    }

    private static void TreeCell(Paper paper, FontFile font, string id, string text, UnitValue width, Color color)
    {
        paper.Box(id).Width(width).Height(18).IsNotInteractable()
            .Text(text, font).TextColor(color)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft);
    }

    private static void TreeCell(Paper paper, FontFile font, string id, string text, float width, Color color)
        => TreeCell(paper, font, id, text, UnitValue.Pixels(width), color);

    private void DrawScopeTimeline(Paper paper)
    {
        if (_flameRoots.Count == 0)
        {
            var font = EditorTheme.DefaultFont;
            if (font != null)
                paper.Box("prof_flame_empty").Height(18).IsNotInteractable()
                    .Text("No marker scopes recorded for this frame.", font)
                    .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(PaperTextAlignment.MiddleLeft);
            return;
        }

        Chart.FlameGraph(paper, "prof_flame", _flameRoots)
            .Name(static n => n.Name)
            .Value(static n => n.Ms)
            .Children(static n => n.Kids)
            .Width(UnitValue.Stretch())
            .Height(200)
            .ValueFormatter(static ms => $"{ms:F3} ms")
            .Show();
    }

    /// <summary>Marker scopes of the selected frame, depth-indented (the M8-③ table/flame views
    /// replace this flat listing later).</summary>
    private void DrawSelectedFrameScopes(Paper paper, FontFile font)
    {
        int slot = (_historyHead - 1 - _selectedOffset + HistoryLength * 2) % HistoryLength;
        var scopes = _scopeHistory[slot];
        int count = _scopeCounts[slot];
        if (scopes == null || count == 0)
        {
            paper.Box("prof_noscopes").Height(18).IsNotInteractable()
                .Text("No marker scopes recorded for this frame.", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft);
            return;
        }

        paper.Box("prof_scopes_hdr").Height(22).IsNotInteractable()
            .Text("CPU Scopes", font).TextColor(EditorTheme.AccentText)
            .FontSize(EditorTheme.FontSize).Alignment(PaperTextAlignment.MiddleLeft);

        const int maxRows = 48;
        int shown = Math.Min(count, maxRows);
        for (int i = 0; i < shown; i++)
        {
            ref ProfilerFrames.Entry e = ref scopes[i];
            double ms = (e.EndTicks - e.StartTicks) * ProfilerFrames.MsPerTick;
            string indent = e.Depth switch { 0 => "", 1 => "  ", 2 => "    ", _ => "      " };
            paper.Box("prof_scope" + i).Height(17).IsNotInteractable()
                .Text($"{indent}{ProfilerFrames.NameOf(e.Id)}  {ms:F3} ms", font)
                .TextColor(EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft);
        }
        if (count > maxRows)
            paper.Box("prof_scope_more").Height(17).IsNotInteractable()
                .Text($"… {count - maxRows} more scopes", font)
                .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(PaperTextAlignment.MiddleLeft);
    }

    private void DrawLiveSections(Paper paper, FontFile font)
    {
        bool isRemote = _remoteCapture.HasFrame;
        ProfilerTelemetryFrame displayTelemetry = isRemote ? _remoteCapture.LastFrame : _telemetry;

        DrawSection(paper, font, "Process", new[]
        {
            $"CPU   {FormatCpuMetric(displayTelemetry.ProcessCpuPercent, displayTelemetry.ProcessCpuSource)}",
            $"RAM   {FormatMetric(displayTelemetry.WorkingSetBytes, displayTelemetry.WorkingSetSource)} working set",
            $"Heap  {FormatMetric(displayTelemetry.ManagedHeapBytes, displayTelemetry.ManagedHeapSource)} managed"
        });
        DrawSection(paper, font, "Rendering", isRemote ? new[]
        {
            $"Player frame {displayTelemetry.FrameIndex}",
            "Pass counters unavailable in profiler transport"
        } : new[]
        {
            $"Frame {_sample.FrameTimeMs,6:F2} ms (CPU frame delta)",
            $"Color {_sample.ColorPassMs,6:F2} ms   Shadow {_sample.ShadowPassMs,6:F2} ms   FX {_sample.PostFxMs,6:F2} ms",
            $"Draws {_sample.Render.DrawCalls}   Tris {_sample.Render.Triangles:N0}   Batches {_sample.Render.Batches}",
        });
        DrawSection(paper, font, "GPU", isRemote ? new[]
        {
            FormatGpuMetric(displayTelemetry),
            $"Hardware timestamps: {HasCapability(displayTelemetry.Capabilities, ProfilerTelemetryCapabilities.GpuTimestampQueries)}",
        } : new[]
        {
            _sample.GpuStatus,
            $"Render-thread wait {_sample.RenderThreadWaitMs:F2} ms (proxy, not GPU execution time)",
        });
    }

    public override void OnClosed()
    {
        OnScriptReloadCleanup();
        _remoteCapture.Dispose();
    }

    private void ToggleCapture()
    {
        if (_remoteCapture.IsListening)
        {
            _remoteCapture.Stop();
            _captureStatus = "Local Editor";
            return;
        }

        if (!int.TryParse(_listenPort, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            _captureStatus = "Invalid UDP port";
            return;
        }

        _captureStatus = _remoteCapture.TryStart(port)
            ? $"Listening on UDP {port}"
            : $"Unable to listen on UDP {port}";
    }

    private static void DrawSection(Paper paper, FontFile font, string title, string[] lines)
    {
        using (paper.Column("prof_" + title.ToLowerInvariant()).Height(30 + lines.Length * 18).Padding(0, 8, 0, 0).Enter())
        {
            paper.Box("header").Height(22).Text(title, font).TextColor(EditorTheme.AccentText)
                .FontSize(EditorTheme.FontSize).Alignment(PaperTextAlignment.MiddleLeft);
            for (int i = 0; i < lines.Length; i++)
                paper.Box("line" + i).Height(18).Text(lines[i], font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024f * 1024f):F1} MB";
        return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
    }

    private static string FormatMetric(long bytes, ProfilerMetricSource source) => source == ProfilerMetricSource.Unavailable
        ? "unavailable"
        : $"{FormatBytes(bytes)} ({source})";

    private static string FormatCpuMetric(float percent, ProfilerMetricSource source) => source == ProfilerMetricSource.Unavailable
        ? "unavailable"
        : $"{percent,6:F1}% ({source})";

    private static string FormatGpuMetric(ProfilerTelemetryFrame frame) => frame.HasGpuFrameTime
        ? $"GPU frame {frame.GpuFrameTimeMs:F2} ms (asynchronous timestamp query)"
        : "GPU execution time unavailable";

    private static bool HasCapability(ProfilerTelemetryCapabilities capabilities,
        ProfilerTelemetryCapabilities capability) => (capabilities & capability) != 0;

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        state["listenPort"] = _listenPort;
        state["clearOnPlay"] = _clearOnPlay;
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        _listenPort = state["listenPort"]?.GetValue<string>() ?? _listenPort;
        _clearOnPlay = state["clearOnPlay"]?.GetValue<bool>() ?? _clearOnPlay;
    }
}
