using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Utils;
using static XEngine.Editor.GUI.EditorGUI;
using XEngine.Editor.Theming;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Scribe;
using XEngine.Vector;

using Color = System.Drawing.Color;
using TextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

public class ConsolePanel : DockPanel, IScriptReloadCleanup
{
    [MenuItem("Window/General/Console", priority: 0)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(ConsolePanel));

    public override string Title => Loc.Get("panel.console");
    public override string Icon => EditorIcons.Terminal;

    private const int MaxMessages = 500;
    private static float RowHeight => EditorTheme.RowHeight + 2f;

    // ── Incremental paint (Program.ConsolePaintIncremental) ─────────────────────────────
    // Two compared subtree regions sharing one geometry/atlas budget pair: the message list
    // (already one custom-painted box; its painter now declares an exact dependency key so the
    // region can tell a settled list from a changed one) and the detail pane. The toolbar stays on
    // the host: its counts change with every log and its chips animate on hover. Regions are
    // recreated when the panel changes host Paper and released on close / before a script reload.
    private UiPaperSubtreeRegion? _listPaintRegion;
    private UiPaperSubtreeRegion? _detailPaintRegion;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _paintGeometryBudget = new(8 * 1024 * 1024);
    private readonly UiMemoryBudget _paintAtlasBudget = new(16 * 1024 * 1024);
    private static readonly object s_rowPaintSource = new();
    /// <summary>Advances whenever the filtered list is rebuilt (new log, filter, search, collapse,
    /// clear); together with the visible slice, selection and hover row it forms the row painter's key.</summary>
    private uint _listVersion;
    // Per-frame inputs of the cached row painter, stored in fields so the deferred draw needs no closure.
    private readonly Action<Canvas, Rect> _drawRowsAction;
    private Paper? _drawPaper;
    private FontFile? _drawFont;
    private float _drawRowH;
    private int _drawFirst, _drawLast, _drawHoverRow;

    internal static bool PaintIncremental => Program.ConsolePaintIncremental;
    internal long ListRegionCaptures => _listPaintRegion?.CaptureCount ?? -1;
    internal long DetailRegionCaptures => _detailPaintRegion?.CaptureCount ?? -1;
    internal uint ListVersion => _listVersion;
    internal int RowsPainted => _drawLast >= _drawFirst ? _drawLast - _drawFirst + 1 : 0;

    public void OnScriptReloadCleanup()
    {
        _listPaintRegion?.Dispose(); _listPaintRegion = null;
        _detailPaintRegion?.Dispose(); _detailPaintRegion = null;
        _paintRegionPaper = null;
        _drawPaper = null; _drawFont = null;
    }

    public override void OnClosed() => OnScriptReloadCleanup();

    /// <summary>Attach (creating on demand) one of the panel's paint regions to the current parent.
    /// No-op unless the incremental flag is set and the renderer can paint to a separate layer.</summary>
    private void AttachPaintRegion(Paper paper, ref UiPaperSubtreeRegion? region)
    {
        if (!Program.ConsolePaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
        if (!ReferenceEquals(_paintRegionPaper, paper))
        {
            _listPaintRegion?.Dispose(); _listPaintRegion = null;
            _detailPaintRegion?.Dispose(); _detailPaintRegion = null;
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

    private static readonly List<LogEntry> _messages = new();
    /// <summary>Guards _messages / count fields: Debug.OnLog fires from any thread (asset loader,
    /// render thread, MCP server) while the UI thread iterates in RebuildFilteredList/DrawMessages.
    /// Without the lock, List.Add during iteration throws ArgumentOutOfRangeException (the
    /// "dragging the scene view crashes the editor" bug).</summary>
    private static readonly object _messagesLock = new();
    private static bool _subscribed;
    /// <summary>Advances on every mutation of the shared store (append, adjacent merge, eviction,
    /// clear). Read under the lock or as a plain trigger value; it only has to differ, not be exact.</summary>
    private static int s_storeVersion;
    private static int s_infoCount;
    private static int s_warnCount;
    private static int s_errCount;

    // Settings
    private bool _showTime = true;
    private bool _collapse; // default off, matching Unity's Console
    private bool _multiLine = false;

    /// <summary>Height of the detail pane at the bottom of the panel, dragged by its splitter.</summary>
    private float _detailHeight = 140f;
    private const float MinDetailHeight = 44f;
    private const float MinListHeight = 60f;
    private const float SplitterSize = 4f;

    /// <summary>Text last handed to the detail pane's text area, to detect a change of selected log.</summary>
    private string _detailShownText = "";

    /// <summary>
    /// Pause play mode as soon as an error is logged, so the frame that produced it can be inspected.
    /// Static (and applied from the shared log handler) so it works regardless of which Console tab —
    /// or none at all — is currently visible.
    /// </summary>
    private static bool _errorPause;

    /// <summary>Wipe the log when play mode starts, like Unity's "Clear on Play".</summary>
    private static bool _clearOnPlay = true;

    // Filters
    private bool _showInfo = true;
    private bool _showWarnings = true;
    private bool _showErrors = true;
    private string _searchText = "";

    // Cached filtered list (rebuilt when messages or filters change). Per-instance: each Console
    // tab has its own filter settings/selection, so the cache derived from them must be per-instance
    // too - a shared static cache lets two open tabs stomp each other's filtered view and selection.
    private int _lastMessageCount;
    private int _lastFilterHash;
    private bool _lastCollapseState;
    private readonly List<int> _filteredIndices = new();
    private int _selectedFilteredIndex = -1;

    // Collapse-mode grouping: per visible row, the total occurrence count across the whole store
    // (not just adjacent repeats) and its lazily built badge layout. Rebuilt with the filter list.
    private readonly List<int> _collapsedCounts = new();
    private readonly List<TextLayout?> _collapsedCountLayouts = new();
    private readonly Dictionary<(string, LogSeverity), int> _collapseRowByKey = new();

    /// <summary>How much of the stack trace the detail pane shows (Unity's Stack Trace setting).</summary>
    internal enum StackTraceLevel : byte { None, ScriptOnly, Full }
    private StackTraceLevel _stackTraceLevel = StackTraceLevel.Full;

    internal struct LogEntry
    {
        public string Message;
        public string FullMessage;
        public LogSeverity Severity;
        public string TimeString;
        public int Count;
        public DebugStackTrace? StackTrace;

        // Cached text layouts, built lazily on first draw and reused every frame.
        public TextLayout? NameLayout;
        public TextLayout? MessageLayout;
        public TextLayout? TimeLayout;
        public TextLayout? CountLayout;
        public TextLayout? SourceLayout;
        public TextLayout? StackLayout;
    }

    public ConsolePanel()
    {
        EnsureSubscribed();
        _drawRowsAction = DrawRowsDeferred;
    }

    /// <summary>Subscribe the shared log store to Debug.OnLog if not already. Safe to call before any
    /// ConsolePanel is opened - the status-bar footer relies on this to show logs regardless.</summary>
    public static void EnsureSubscribed()
    {
        if (_subscribed) return;
        _subscribed = true;
        // Idempotent on purpose: an assembly load without a matching unload (REPL eval, partial
        // reload) runs the [OnAssemblyLoad] hook while the old subscription is still attached, and
        // a second handler makes every log count twice.
        Debug.OnLog -= OnLogMessage;
        Debug.OnLog += OnLogMessage;
    }

    /// <summary>
    /// Hot-reload: <c>Debug.ClearLogSubscribers</c> nulls <c>Debug.OnLog</c> (so script-bound
    /// subscribers cannot pin the collectible ALC), which also drops this panel's subscription.
    /// Without resubscribing here the console and the status-bar footer go permanently silent
    /// after the first successful hot-reload.
    /// </summary>
    [OnAssemblyLoad]
    static void ResubscribeAfterReload()
    {
        _subscribed = false;
        EnsureSubscribed();
    }

    // ---- Status-bar footer accessors (read-only view of the shared log store) ----

    /// <summary>Icon + color for a log severity, matching the console rows.</summary>
    public static (IOrigamiIcon icon, Color color) SeverityStyle(LogSeverity severity)
    {
        var (icon, color, _) = LevelOf(severity);
        return (icon, color);
    }

    /// <summary>Total Info / Warning / Error counts (including collapsed repeats).</summary>
    public static (int info, int warn, int err) LogCounts() => (s_infoCount, s_warnCount, s_errCount);

    private static void AddToCounts(LogSeverity severity, int delta)
    {
        if (severity == LogSeverity.Warning) s_warnCount += delta;
        else if (severity is LogSeverity.Error or LogSeverity.Exception) s_errCount += delta;
        else s_infoCount += delta;
    }

    private static void ResetCounts()
    {
        s_infoCount = 0;
        s_warnCount = 0;
        s_errCount = 0;
    }

    /// <summary>The most recent log entry (message, source class, collapse count), or null if none.</summary>
    public static (LogSeverity severity, string message, string? source, int count)? LastLog()
    {
        lock (_messagesLock)
        {
            if (_messages.Count == 0) return null;
            var m = _messages[^1];
            return (m.Severity, m.Message, SourceOf(m), m.Count);
        }
    }

    private static void OnLogMessage(string message, DebugStackTrace? stackTrace, LogSeverity severity)
    {
        string firstLine = message.Contains('\n') ? message.Split('\n')[0] : message;

        lock (_messagesLock)
        {
            if (_messages.Count > 0)
            {
                var last = _messages[^1];
                if (last.FullMessage == message && last.Severity == severity)
                {
                    last.Count += 1;
                    last.TimeString = DateTime.Now.ToString("HH:mm:ss");
                    last.StackTrace = stackTrace ?? last.StackTrace;
                    last.TimeLayout = null;
                    last.CountLayout = null;
                    _messages[^1] = last;
                    AddToCounts(severity, 1);
                    s_storeVersion++;
                    goto AfterStore;
                }
            }

            s_storeVersion++;
            _messages.Add(new LogEntry
            {
                Message = firstLine,
                FullMessage = message,
                Severity = severity,
                TimeString = DateTime.Now.ToString("HH:mm:ss"),
                Count = 1,
                StackTrace = stackTrace,
            });
            AddToCounts(severity, 1);

            while (_messages.Count > MaxMessages)
            {
                var removed = _messages[0];
                AddToCounts(removed.Severity, -removed.Count);
                _messages.RemoveAt(0);
            }
        }

        AfterStore:
        if (_errorPause && severity is LogSeverity.Error or LogSeverity.Exception
            && Application.IsPlaying && !Application.IsPaused)
            Application.IsPaused = true;
    }

    /// <summary>Called when play mode starts so "Clear on Play" can wipe the previous run's logs.</summary>
    public static void OnEnterPlayMode()
    {
        if (!_clearOnPlay) return;
        lock (_messagesLock)
        {
            _messages.Clear();
            ResetCounts();
            s_storeVersion++;
        }
    }

    // ================================================================
    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        using var profilerScope = EditorProfiler.BeginCpuScope("Console");

        using (paper.Column("con_root").Size(width, height).Enter())
        {
            const float toolbarHeight = EditorToolbar.Height;

            // The detail pane is always present (empty until a log is picked) so the list doesn't
            // jump around as the selection changes, and it never squeezes the list below a usable size.
            float available = MathF.Max(0f, height - toolbarHeight - SplitterSize);
            float detailHeight = Math.Clamp(_detailHeight, MinDetailHeight,
                MathF.Max(MinDetailHeight, available - MinListHeight));
            float listHeight = MathF.Max(0f, available - detailHeight);

            DrawToolbar(paper, font, width);
            DrawMessages(paper, font, width, listHeight);
            DrawDetailSplitter(paper, width, available);
            DrawDetail(paper, font, width, detailHeight);
        }
    }

    /// <summary>Drag handle between the log list and the detail pane. Dragging down shrinks the pane.</summary>
    private void DrawDetailSplitter(Paper paper, float width, float available)
        => paper.Box("con_detail_split").Width(width).Height(SplitterSize)
            .BackgroundColor(EditorTheme.BorderSoft)
            .Hovered.BackgroundColor(EditorTheme.Accent).End()
            .Cursor(PaperCursor.ResizeVertical)
            .OnDragging(e => _detailHeight = Math.Clamp(_detailHeight - (float)e.Delta.Y,
                MinDetailHeight, MathF.Max(MinDetailHeight, available - MinListHeight)));

    // ================================================================
    //  Toolbar
    // ================================================================
    private void DrawToolbar(Paper paper, FontFile font, float width)
    {
        var (infoCount, warnCount, errCount) = LogCounts();

        using (paper.Column("con_tb_col").Height(EditorToolbar.Height).Enter())
        {
            using (paper.Row("con_tb").Height(EditorToolbar.ButtonHeight).Padding(1, 1, 0, 0).RowBetween(2).Enter())
            {
                LevelChip(paper, font, "con_collapse", EditorIcons.LayerGroup_I,
                    _collapse ? EditorTheme.AccentText : EditorTheme.InkDim, Loc.Get("console.collapse"), null, _collapse, false, () => _collapse = !_collapse);

                LevelChip(paper, font, "con_errpause", EditorIcons.CirclePause_I,
                    _errorPause ? EditorTheme.AccentText : EditorTheme.InkDim, Loc.Get("console.error_pause"),
                    null, _errorPause, false, () => _errorPause = !_errorPause);

                paper.Box("con_div1").Width(1).Margin(4, 4, 0, 0).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

                LevelChip(paper, font, "con_info", EditorIcons.CircleInfo_I, EditorTheme.Blue400, null, infoCount.ToString(), _showInfo, !_showInfo, () => _showInfo = !_showInfo);
                LevelChip(paper, font, "con_warn", EditorIcons.TriangleExclamation_I, EditorTheme.Amber400, null, warnCount.ToString(), _showWarnings, !_showWarnings, () => _showWarnings = !_showWarnings);
                LevelChip(paper, font, "con_err", EditorIcons.CircleExclamation_I, EditorTheme.Red400, null, errCount.ToString(), _showErrors, !_showErrors, () => _showErrors = !_showErrors);

                paper.Box("con_sp");

                using (paper.Row("con_search_wrap").Width(130).Height(EditorToolbar.SearchHeight).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                    EditorToolbar.Search(paper, "con_search", _searchText, v => _searchText = v,
                        130, Loc.Get("console.filter")).Show();

                EditorToolbar.IconButton(paper, "con_clear", EditorIcons.Trash_I, ClearMessages).Show();
                EditorToolbar.IconButton(paper, "con_clear_opts", EditorIcons.CaretDown_I,
                    () => Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, menu =>
                    {
                        menu.Item(Loc.Get("console.clear"), ClearMessages, icon: EditorIcons.Trash);
                        menu.Separator();
                        menu.Toggle(Loc.Get("console.clear_on_play"),
                            () => _clearOnPlay = !_clearOnPlay, () => _clearOnPlay);
                        menu.Toggle(Loc.Get("console.error_pause"),
                            () => _errorPause = !_errorPause, () => _errorPause);
                    })).Show();
                EditorToolbar.IconButton(paper, "con_opts", EditorIcons.EllipsisVertical_I,
                    () => Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, BuildOptionsMenu)).Show();
            }
            paper.Box("con_tb_div").Height(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
        }
    }

    // cs-lvl chip: icon + optional label + optional count; "on" gets a glass inset, "off" dims.
    private void LevelChip(Paper p, FontFile font, string id, IOrigamiIcon icon, Color iconColor, string? label, string? count, bool on, bool dim, Action onClick)
    {
        var mono = EditorTheme.FontMono ?? font;
        Color ic = dim ? Color.FromArgb(115, iconColor.R, iconColor.G, iconColor.B) : iconColor;

        using (p.Row(id).Width(UnitValue.Auto).Height(EditorToolbar.ButtonHeight).Rounded(EditorTheme.Roundness).Padding(5, 5, 0, 0).RowBetween(3).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(on ? EditorTheme.Glass : Color.Transparent)
            .BorderColor(on ? EditorTheme.BorderSoft : Color.Transparent).BorderWidth(1)
            .Transition(GuiProp.BackgroundColor, 0.15f).Transition(GuiProp.BorderColor, 0.15f)
            .Hovered.BackgroundColor(on ? EditorTheme.Glass : EditorTheme.Hover).End()
            .OnClick(_ => onClick())
            .Enter())
        {
            p.Box(id + "_i").Width(14).Height(EditorToolbar.ButtonHeight).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Icon(p, icon, ic, size: 12f);
            if (!string.IsNullOrEmpty(label))
                p.Box(id + "_l").Width(UnitValue.Auto).Height(EditorToolbar.ButtonHeight).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                    .Text(label, font).TextColor(on ? EditorTheme.Ink500 : (dim ? EditorTheme.InkFaint : EditorTheme.InkDim)).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
            if (!string.IsNullOrEmpty(count))
                p.Box(id + "_c").Width(UnitValue.Auto).Height(EditorToolbar.ButtonHeight).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                    .Text(count, mono).TextColor(dim ? EditorTheme.InkFaint : EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
        }
    }

    private void ClearMessages()
    {
        lock (_messagesLock)
        {
            _messages.Clear();
            ResetCounts();
            s_storeVersion++;
        }
        _filteredIndices.Clear();
        _selectedFilteredIndex = -1;
        _listVersion++;
    }

    private void BuildOptionsMenu(ContextBuilder menu)
    {
        menu.Submenu(Loc.Get("console.log_tests"), sub =>
        {
            sub.Item(Loc.Get("console.log"), () => Debug.Log("This is a Normal Log."))
                .Item(Loc.Get("console.log_warning"), () => Debug.LogWarning("This is a Warning Log."))
                .Item(Loc.Get("console.log_error"), () => Debug.LogError("This is an Error Log."))
                .Item(Loc.Get("console.log_success"), () => Debug.LogSuccess("This is a Success Log."));
        }, EditorIcons.Flask);
        menu.Separator();
        menu.Header(Loc.Get("console.display"));
        menu.Toggle(Loc.Get("console.show_time"), () => _showTime = !_showTime, () => _showTime);
        menu.Toggle(Loc.Get("console.multi_line"), () => _multiLine = !_multiLine, () => _multiLine);
        menu.Separator();
        menu.Header(Loc.Get("console.stack_trace"));
        menu.Toggle(Loc.Get("console.stack_none"), () => _stackTraceLevel = StackTraceLevel.None,
            () => _stackTraceLevel == StackTraceLevel.None);
        menu.Toggle(Loc.Get("console.stack_script"), () => _stackTraceLevel = StackTraceLevel.ScriptOnly,
            () => _stackTraceLevel == StackTraceLevel.ScriptOnly);
        menu.Toggle(Loc.Get("console.stack_full"), () => _stackTraceLevel = StackTraceLevel.Full,
            () => _stackTraceLevel == StackTraceLevel.Full);
    }

    // ================================================================
    //  Message list
    // ================================================================
    // Rows aren't individual elements: the whole list is one fixed-height box that only paints the
    // visible slice, drawing cached TextLayouts straight to the canvas so 500 messages stay cheap.
    private void DrawMessages(Paper paper, FontFile font, float width, float height)
    {
        // The store version folds every mutation that leaves _messages.Count unchanged into the
        // rebuild trigger: adjacent-merge increments, and a full store (MaxMessages) where each new
        // log evicts the oldest so the count and often the per-severity totals stay the same while
        // every index shifts. Without it the filtered list went stale exactly when the log was busiest.
        int filterHash = HashCode.Combine(_showInfo, _showWarnings, _showErrors, _searchText,
            s_infoCount + s_warnCount + s_errCount, s_storeVersion);
        if (_lastMessageCount != _messages.Count || _lastFilterHash != filterHash || _lastCollapseState != _collapse)
        {
            _lastMessageCount = _messages.Count;
            _lastFilterHash = filterHash;
            _lastCollapseState = _collapse;
            RebuildFilteredList();
        }

        int count = _filteredIndices.Count;
        float rowH = _multiLine ? RowHeight * 1.85f : RowHeight;
        _drawPaper = paper; _drawFont = font; _drawRowH = rowH;
        _drawFirst = 0; _drawLast = -1; _drawHoverRow = -1;

        // The list is the panel's first paint region; the empty-state label and the scrollbar are
        // part of it, so a settled list (no new log, no hover, no scroll) re-declares nothing.
        using var listBody = paper.Column("con_list_paint_body").Size(width, height).Enter();
        AttachPaintRegion(paper, ref _listPaintRegion);

        Origami.ScrollView(paper, "con_msgs", width, height).ForceScrollbar().Body(() =>
        {
            if (count == 0)
            {
                EditorGUI.EmptyState(paper, "con_empty", Loc.Get("console.no_logs"), font);
                return;
            }

            paper.Box("con_content").Width(width - 12).Height(count * rowH).Clip()
                .OnClick(0, (_, e) =>
                {
                    int row = (int)((float)e.RelativePosition.Y / rowH);
                    if (row < 0 || row >= _filteredIndices.Count) return;
                    _selectedFilteredIndex = row;
                })
                .OnDoubleClick(e =>
                {
                    // Unity opens the source of the top stack frame on double-click.
                    int row = (int)((float)e.RelativePosition.Y / rowH);
                    if (row < 0 || row >= _filteredIndices.Count) return;
                    var entry = _messages[_filteredIndices[row]];
                    if (entry.StackTrace is { StackFrames.Length: > 0 } trace)
                        OpenStackFrame(trace.StackFrames[0]);
                })
                .OnRightClick(0, (_, e) =>
                {
                    int row = (int)((float)e.RelativePosition.Y / rowH);
                    if (row < 0 || row >= _filteredIndices.Count) return;
                    _selectedFilteredIndex = row;
                    var entry = _messages[_filteredIndices[row]];
                    DebugStackFrame? src = null;
                    var frames = entry.StackTrace?.StackFrames;
                    if (frames != null)
                        for (int i = 0; i < frames.Length; i++)
                            if (!string.IsNullOrEmpty(frames[i].FileName)) { src = frames[i]; break; }
                    Origami.ContextMenuAtPointer(menu =>
                    {
                        menu.Item(Loc.Get("console.copy_details"), () => paper.SetClipboard(DetailText(entry)));
                        if (src is { } target)
                        {
                            menu.Item(Loc.Get("console.open_source"), () => OpenStackFrame(target));
                            menu.Item(Loc.Get("project.show_in_explorer"),
                                () => EditorUtils.OpenFileSystemPath(target.FileName!));
                        }
                    });
                })
                .OnPostLayout(this, static (self, handle, contentRect) => self.OnRowsLaidOut(handle, contentRect));
        });
    }

    /// <summary>
    /// Post-layout of the list's content box: computes the visible slice and the hovered row from
    /// the laid-out rectangle and registers the row painter. In incremental mode the draw declares
    /// its exact inputs (store/filter version, slice, selection, hover, display options, palette
    /// and row font) so the compared region keeps the cached list while none of them change; the
    /// legacy path registers the same painter as an opaque draw.
    /// </summary>
    private void OnRowsLaidOut(ElementHandle handle, Rect contentRect)
    {
        var paper = _drawPaper!;
        float rowH = _drawRowH;
        int count = _filteredIndices.Count;
        var container = paper.GetElementData(handle.Data.ParentIndex);
        var clip = paper.GetElementData(container.ParentIndex);
        float contentTop = (float)contentRect.Min.Y;
        _drawFirst = Math.Max(0, (int)(((float)clip.LayoutRect.Min.Y - contentTop) / rowH) - 1);
        _drawLast = Math.Min(count - 1, (int)(((float)clip.LayoutRect.Max.Y - contentTop) / rowH) + 1);

        // Hover is resolved here, against the host-space rectangle, rather than inside the painter:
        // the painter may run into a region canvas, and the key below has to name the hovered row.
        var ptr = paper.PointerPos;
        _drawHoverRow = (ptr.X >= contentRect.Min.X && ptr.X <= contentRect.Max.X && ptr.Y >= contentTop && ptr.Y <= contentRect.Max.Y)
            ? (int)(((float)ptr.Y - contentTop) / rowH) : -1;
        // Only a row that is actually painted can show the hover tint; a pointer over the empty
        // area below the last row must not re-key the draw as it moves.
        if (_drawHoverRow < _drawFirst || _drawHoverRow > _drawLast) _drawHoverRow = -1;

        if (!Program.ConsolePaintIncremental)
        {
            paper.Draw(ref handle, _drawRowsAction);
            return;
        }

        var font = _drawFont!;
        var mono = EditorTheme.FontMono ?? font;
        uint styleHash = unchecked((uint)HashCode.Combine(
            HashCode.Combine(EditorTheme.Selected.ToArgb(), EditorTheme.Accent.ToArgb(), EditorTheme.BorderSoft.ToArgb(), EditorTheme.Neutral400.ToArgb()),
            HashCode.Combine(EditorTheme.Ink300.ToArgb(), EditorTheme.Ink400.ToArgb(), EditorTheme.InkDim.ToArgb(), EditorTheme.InkFaint.ToArgb()),
            HashCode.Combine(EditorTheme.Blue400.ToArgb(), EditorTheme.Amber400.ToArgb(), EditorTheme.Red400.ToArgb()),
            // The mono font is the draw's resource dependency; the other row fonts and the row
            // text size are folded in by identity / value.
            HashCode.Combine(RuntimeHelpers.GetHashCode(EditorTheme.FontSemiBold ?? font), RuntimeHelpers.GetHashCode(EditorTheme.FontBold ?? font),
                EditorTheme.FontSizeSmall),
            _showTime, _multiLine, _collapse, rowH));
        ulong version = (ulong)styleHash << 32 | _listVersion;
        ulong detail = (ulong)(ushort)_drawFirst | (ulong)(ushort)(_drawLast + 1) << 16
            | (ulong)(ushort)(_selectedFilteredIndex + 1) << 32 | (ulong)(ushort)(_drawHoverRow + 1) << 48;
        paper.DrawVersioned(ref handle, "console.rows", version, detail, s_rowPaintSource, mono, _drawRowsAction);
    }

    private void DrawRowsDeferred(Canvas canvas, Rect r)
        => DrawRows(canvas, r, _drawFont!, _drawRowH, _drawFirst, _drawLast, _drawHoverRow);

    private void DrawRows(Canvas canvas, Rect r, FontFile font, float rowH, int first, int last, int hoverRow)
    {
        var mono = EditorTheme.FontMono ?? font;
        var semi = EditorTheme.FontSemiBold ?? font;
        var bold = EditorTheme.FontBold ?? font;

        float left = (float)r.Min.X, right = (float)r.Max.X, top = (float)r.Min.Y, w = (float)r.Size.X;
        const float padL = 12f, padR = 12f, gap = 8f, iconSize = 14f;

        float LW(TextLayout l) => canvas.PixelToLogical((float)l.Size.X);
        float LH(TextLayout l) => canvas.PixelToLogical((float)l.Size.Y);
        void DrawMid(TextLayout l, float x, float cy, Color c) => canvas.DrawLayout(l, x, cy - LH(l) * 0.5f, c);
        TextLayout Make(string text, FontFile f, float size) =>
            canvas.CreateLayout(text, new TextLayoutSettings { Font = f, PixelSize = size, LineHeight = 1f, Quality = FontQuality.Normal });

        for (int vi = first; vi <= last; vi++)
        {
            // first/last were captured at layout time; a mid-frame Clear can shrink the list before
            // this deferred draw runs, so re-check bounds against the live collections.
            if (vi < 0 || vi >= _filteredIndices.Count) break;
            int msgIdx = _filteredIndices[vi];
            if (msgIdx < 0 || msgIdx >= _messages.Count) continue;
            var msg = _messages[msgIdx];
            (IOrigamiIcon icon, Color color, string name) = LevelOf(msg.Severity);
            bool selected = vi == _selectedFilteredIndex;

            float rowY = top + vi * rowH;
            float line1 = rowY + (_multiLine ? rowH * 0.33f : rowH * 0.5f);
            float line2 = rowY + rowH * 0.70f;

            if (selected)
            {
                canvas.RectFilled(left, rowY, w, rowH, EditorTheme.Selected);
                canvas.RectFilled(left, rowY, 2f, rowH, EditorTheme.Accent);
            }
            else if (vi == hoverRow)
                canvas.RectFilled(left, rowY, w, rowH, Color.FromArgb(13, 168, 85, 247));

            float ix = left + padL;
            icon.Draw(canvas, new Rect(ix, line1 - iconSize * 0.5f, ix + iconSize, line1 + iconSize * 0.5f), color, 1.6f);
            float cursorX = ix + iconSize + gap;

            msg.NameLayout ??= Make($"{name}:", semi, EditorTheme.FontSizeSmall);
            DrawMid(msg.NameLayout, cursorX, line1, color);
            cursorX += LW(msg.NameLayout) + 5f;

            // Right cluster, laid out from the right edge inward; message clips before it.
            float rightCursor = right - padR;

            if (_showTime)
            {
                msg.TimeLayout ??= Make(msg.TimeString, mono, EditorTheme.FontSizeSmall);
                float tw = LW(msg.TimeLayout);
                DrawMid(msg.TimeLayout, rightCursor - tw, line1, EditorTheme.InkFaint);
                rightCursor -= tw + gap;
            }

            string? src = SourceOf(msg);
            if (!string.IsNullOrEmpty(src))
            {
                msg.SourceLayout ??= Make($"[{src}]", mono, EditorTheme.FontSizeSmall);
                float sw = LW(msg.SourceLayout);
                DrawMid(msg.SourceLayout, rightCursor - sw, line1, EditorTheme.InkDim);
                rightCursor -= sw + gap;
            }

            int displayCount = _collapse && vi < _collapsedCounts.Count ? _collapsedCounts[vi] : msg.Count;
            if (_collapse && displayCount > 1)
            {
                TextLayout? countLayout = vi < _collapsedCountLayouts.Count ? _collapsedCountLayouts[vi] : null;
                if (countLayout == null)
                {
                    countLayout = Make(displayCount.ToString(), bold, EditorTheme.FontSizeSmall);
                    if (vi < _collapsedCountLayouts.Count) _collapsedCountLayouts[vi] = countLayout;
                }
                float cw = LW(countLayout), badgeW = cw + 12f, badgeH = 16f;
                float badgeX = rightCursor - badgeW, badgeY = line1 - badgeH * 0.5f;
                canvas.RoundedRectFilled(badgeX, badgeY, badgeW, badgeH, badgeH * 0.5f, EditorTheme.Neutral400);
                canvas.DrawLayout(countLayout, badgeX + (badgeW - cw) * 0.5f, line1 - LH(countLayout) * 0.5f, EditorTheme.Ink300);
                rightCursor -= badgeW + gap;
            }

            float msgLimit = Math.Max(cursorX, rightCursor);
            canvas.SaveState();
            canvas.IntersectScissor(cursorX, rowY, msgLimit - cursorX, rowH);
            msg.MessageLayout ??= Make(msg.Message, mono, EditorTheme.FontSizeSmall);
            DrawMid(msg.MessageLayout, cursorX, line1, EditorTheme.Ink400);
            if (_multiLine && msg.StackTrace is { StackFrames.Length: > 0 })
            {
                msg.StackLayout ??= Make(msg.StackTrace.StackFrames[0].ToString(), mono, EditorTheme.FontSizeSmall);
                DrawMid(msg.StackLayout, cursorX, line2, EditorTheme.InkDim);
            }
            canvas.RestoreState();

            canvas.RectFilled(left, rowY + rowH - 1f, w, 1f, EditorTheme.BorderSoft);

            _messages[msgIdx] = msg;
        }
    }

    // ================================================================
    //  Detail pane
    // ================================================================

    /// <summary>
    /// The pane below the list showing the selected log in full: message, then one row per stack frame.
    /// This is where log details live now — selecting a log no longer takes over the Inspector.
    /// </summary>
    private void DrawDetail(Paper paper, FontFile font, float width, float height)
    {
        var mono = EditorTheme.FontMono ?? font;
        LogEntry? selected = SelectedEntry();

        using (paper.Column("con_detail").Width(width).Height(height)
            .BackgroundColor(EditorTheme.Neutral100).Enter())
        {
            // Second paint region: header, read-only text area and stack-frame links. The text
            // area's painter is versioned by Paper (content, caret, selection, scroll, blink phase),
            // so an unfocused detail pane settles and a focused one recaptures on caret flips only.
            AttachPaintRegion(paper, ref _detailPaintRegion);
            if (selected is not { } entry)
            {
                paper.Box("con_detail_empty").Width(UnitValue.Stretch()).Height(UnitValue.Stretch())
                    .IsNotInteractable()
                    .Text(Loc.Get("console.select_log"), font).TextColor(EditorTheme.InkFaint)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);
                return;
            }

            (IOrigamiIcon icon, Color color, string name) = LevelOf(entry.Severity);
            string fullText = DetailText(entry);

            using (paper.Row("con_detail_hdr").Height(22).Padding(10, 8, 0, 0).RowBetween(6).Enter())
            {
                paper.Box("con_detail_icon").Width(12).Height(22)
                    .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Icon(paper, icon, color, size: 11f);
                paper.Box("con_detail_name").Width(UnitValue.Auto).Height(22)
                    .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Text(name, font).TextColor(color)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
                paper.Box("con_detail_time").Width(UnitValue.Auto).Height(22)
                    .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Text(entry.TimeString, mono).TextColor(EditorTheme.InkFaint)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

                paper.Box("con_detail_sp");

                ToolbarIconBtn(paper, "con_detail_copy", EditorIcons.Copy, false,
                    () => paper.SetClipboard(fullText));
            }

            Origami.ScrollView(paper, "con_detail_body", width, MathF.Max(0f, height - 22f)).Body(() =>
            {
                DrawDetailText(paper, mono, entry.FullMessage, fullText, width - 14);
                DrawStackFrameLinks(paper, mono, entry, width - 14);
            });
        }
    }

    /// <summary>
    /// One row per stack frame under the message; frames that carry a source file render their
    /// file:line part as a clickable link into the external code editor (Unity's "(at Foo.cs:42)").
    /// </summary>
    private void DrawStackFrameLinks(Paper paper, FontFile mono, LogEntry entry, float width)
    {
        if (_stackTraceLevel == StackTraceLevel.None) return;
        var frames = entry.StackTrace?.StackFrames;
        if (frames == null || frames.Length == 0) return;
        for (int i = 0; i < frames.Length; i++)
        {
            var frame = frames[i];
            if (_stackTraceLevel == StackTraceLevel.ScriptOnly && !IsScriptFrame(frame)) continue;
            using (paper.Row($"con_frame_{i}").Width(width).Height(18).Padding(10, 0, 0, 0).Enter())
            {
                paper.Box($"con_frame_m_{i}").Width(UnitValue.Auto).Height(18).IsNotInteractable()
                    .Text(frame.Method ?? "?", mono).TextColor(EditorTheme.InkDim)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
                if (string.IsNullOrEmpty(frame.FileName)) continue;

                var captured = frame;
                string link = frame.Line is int ln ? $"  (at {frame.FileName}:{ln})" : $"  (at {frame.FileName})";
                paper.Box($"con_frame_f_{i}").Width(UnitValue.Auto).Height(18)
                    .Text(link, mono).TextColor(EditorTheme.Blue400)
                    .Hovered.TextColor(EditorTheme.Accent).End()
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft)
                    .Cursor(PaperCursor.Pointer)
                    .OnClick(0, (_, _) => OpenStackFrame(captured));
            }
        }
    }

    /// <summary>
    /// The message and stack trace as one read-only text area, so any part of it can be dragged over
    /// and copied the way it can in Unity's console — a plain text element offers no selection.
    /// </summary>
    private void DrawDetailText(Paper paper, FontFile mono, string text, string copyText, float width)
    {
        var settings = ElementBuilder.TextInputSettings.Default;
        settings.Font = mono;
        settings.TextColor = EditorTheme.Ink500;
        settings.ReadOnly = true;
        settings.DoWrap = true;

        // Picking another log entry while the pane still holds focus has to replace the text: the
        // field only adopts an external value on its own when unfocused.
        if (!string.Equals(text, _detailShownText, StringComparison.Ordinal))
        {
            settings.ForceValue = text;
            _detailShownText = text;
        }

        // TabIndex + HookToParent mirrors Origami's text field: the wrapper takes focus and clicks
        // (so Ctrl+C and Ctrl+A reach the field), while the hooked text element keeps its own
        // cursor-placement and drag-select handlers.
        using (paper.Box("con_detail_sel").Width(width).Height(UnitValue.Auto)
            .Padding(10, 10, 0, 8).TabIndex(0).Enter())
        {
            // Double-click now selects a word, so the jump to source moved to this menu.
            ContextMenu.RightClickMenu(paper, "con_detail_ctx", menu =>
            {
                menu.Item(Loc.Get("console.copy_details"), () => paper.SetClipboard(copyText));
                DebugStackFrame? frame = FirstFrameWithFile();
                if (frame is { } target)
                    menu.Item(Loc.Get("console.open_source"), () => OpenStackFrame(target));
            });

            paper.Box("con_detail_txt").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                .HookToParent().IsNotInteractable()
                .FontSize(EditorTheme.FontSizeSmall)
                .TextArea(text, settings);
        }
    }

    /// <summary>Topmost stack frame of the selected log that carries a source file, if any.</summary>
    private DebugStackFrame? FirstFrameWithFile()
    {
        var frames = SelectedEntry()?.StackTrace?.StackFrames;
        if (frames == null) return null;
        for (int i = 0; i < frames.Length; i++)
        {
            if (!string.IsNullOrEmpty(frames[i].FileName)) return frames[i];
        }
        return null;
    }

    private LogEntry? SelectedEntry()
    {
        if (_selectedFilteredIndex < 0 || _selectedFilteredIndex >= _filteredIndices.Count)
            return null;
        int index = _filteredIndices[_selectedFilteredIndex];
        return index >= 0 && index < _messages.Count ? _messages[index] : null;
    }

    /// <summary>Message plus stack trace, in the shape people expect to paste into a bug report.</summary>
    private static string DetailText(LogEntry entry)
        => entry.StackTrace == null
            ? entry.FullMessage
            : entry.FullMessage + "\n" + entry.StackTrace;

    /// <summary>
    /// The shared log store as plain text ("HH:mm:ss [severity] message + stack"), oldest first —
    /// what the Bug Reporter attaches and what CrashHandler writes into its crash records.
    /// Collapsed repeats are expanded to their full count so no occurrence is lost.
    /// </summary>
    public static string DumpRecentLogs(int maxEntries = MaxMessages)
    {
        lock (_messagesLock)
        {
            int count = _messages.Count;
            int start = Math.Max(0, count - maxEntries);
            var sb = new System.Text.StringBuilder();
            for (int i = start; i < count; i++)
            {
                var m = _messages[i];
                sb.Append(m.TimeString).Append(" [").Append(m.Severity.ToString().ToLowerInvariant()).Append(']');
                if (m.Count > 1) sb.Append(" x").Append(m.Count);
                sb.Append(' ').AppendLine(DetailText(m));
            }
            return sb.ToString();
        }
    }

    private static void OpenStackFrame(DebugStackFrame frame)
    {
        if (string.IsNullOrEmpty(frame.FileName) || !System.IO.File.Exists(frame.FileName)) return;
        ExternalCodeEditor.Open(frame.FileName, frame.Line ?? 0, frame.Column ?? 0);
    }

    /// <summary>
    /// "Script Only" keeps frames that come from the current project's own sources; engine and BCL
    /// frames (no file, or a file outside the project) are hidden. Without an open project, any
    /// frame with a source file counts as script.
    /// </summary>
    private static bool IsScriptFrame(DebugStackFrame frame)
    {
        if (string.IsNullOrEmpty(frame.FileName)) return false;
        string? assets = Projects.Project.Current?.AssetsPath;
        if (string.IsNullOrEmpty(assets)) return true;
        return frame.FileName.StartsWith(assets, StringComparison.OrdinalIgnoreCase);
    }

    // Class the log originated from (first captured frame's declaring type), for the source label.
    private static string? SourceOf(LogEntry msg)
    {
        var frames = msg.StackTrace?.StackFrames;
        if (frames == null || frames.Length == 0) return null;
        string? method = frames[0].Method;
        if (string.IsNullOrEmpty(method)) return null;
        int dot = method.LastIndexOf('.');
        return dot > 0 ? method[..dot] : method;
    }

    private static (IOrigamiIcon, Color, string) LevelOf(LogSeverity s) => s switch
    {
        LogSeverity.Warning => (EditorIcons.TriangleExclamation_I, EditorTheme.Amber400, "warning"),
        LogSeverity.Error or LogSeverity.Exception => (EditorIcons.CircleExclamation_I, EditorTheme.Red400, "error"),
        _ => (EditorIcons.CircleInfo_I, EditorTheme.Blue400, "info"),
    };

    private void RebuildFilteredList()
    {
        _listVersion++;
        _filteredIndices.Clear();
        _collapsedCounts.Clear();
        _collapsedCountLayouts.Clear();
        lock (_messagesLock)
        {
        if (_collapse)
        {
            // Unity's Collapse groups identical logs globally (not just adjacent repeats):
            // one row per (message, severity), newest occurrence on top, total count badge.
            _collapseRowByKey.Clear();
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                var msg = _messages[i];
                if (!ShouldShow(msg.Severity)) continue;
                if (!EditorUtils.MatchesSearch(msg.Message, _searchText))
                    continue;

                var key = (msg.FullMessage, msg.Severity);
                if (_collapseRowByKey.TryGetValue(key, out int row)
                    && (uint)row < (uint)_collapsedCounts.Count)
                {
                    _collapsedCounts[row] += msg.Count;
                    _collapsedCountLayouts[row] = null;
                }
                else
                {
                    _collapseRowByKey[key] = _filteredIndices.Count;
                    _filteredIndices.Add(i);
                    _collapsedCounts.Add(msg.Count);
                    _collapsedCountLayouts.Add(null);
                }
            }
        }
        else
        {
            for (int i = _messages.Count - 1; i >= 0; i--)
            {
                var msg = _messages[i];
                if (!ShouldShow(msg.Severity)) continue;
                if (!EditorUtils.MatchesSearch(msg.Message, _searchText))
                    continue;

                if (msg.Count == 1)
                    _filteredIndices.Add(i);
                else
                    for (int t = 0; t < msg.Count; t++)
                        _filteredIndices.Add(i);
            }
        }
        }
        if (_selectedFilteredIndex >= _filteredIndices.Count)
            _selectedFilteredIndex = -1;
    }

    private bool ShouldShow(LogSeverity severity) => severity switch
    {
        LogSeverity.Normal or LogSeverity.Success => _showInfo,
        LogSeverity.Warning => _showWarnings,
        LogSeverity.Error or LogSeverity.Exception => _showErrors,
        _ => true,
    };

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        state["detailHeight"] = _detailHeight;
        state["showTime"] = _showTime;
        state["collapse"] = _collapse;
        state["multiLine"] = _multiLine;
        state["errorPause"] = _errorPause;
        state["clearOnPlay"] = _clearOnPlay;
        state["stackTrace"] = (int)_stackTraceLevel;
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        _detailHeight = Math.Clamp(state["detailHeight"]?.GetValue<float>() ?? _detailHeight,
            MinDetailHeight, 600f);
        _showTime = state["showTime"]?.GetValue<bool>() ?? _showTime;
        _collapse = state["collapse"]?.GetValue<bool>() ?? _collapse;
        _multiLine = state["multiLine"]?.GetValue<bool>() ?? _multiLine;
        _errorPause = state["errorPause"]?.GetValue<bool>() ?? _errorPause;
        _clearOnPlay = state["clearOnPlay"]?.GetValue<bool>() ?? _clearOnPlay;
        int stackLevel = state["stackTrace"]?.GetValue<int>() ?? (int)_stackTraceLevel;
        if (stackLevel is >= 0 and <= (int)StackTraceLevel.Full)
            _stackTraceLevel = (StackTraceLevel)stackLevel;
    }
}
