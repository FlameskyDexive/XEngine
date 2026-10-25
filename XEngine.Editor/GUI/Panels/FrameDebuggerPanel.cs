// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;
using XEngine.Vector.Spatial;

using PaperTextAlignment = XEngine.PaperUI.TextAlignment;
using Color32 = XEngine.Vector.Color32;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// Unity Frame Debugger 1:1. Toolbar (enable / attach target / event scrubber / step),
/// searchable event tree with draw-count badges, right pane with the event title,
/// Output | Mesh Preview tabs, RT / channel / levels preview controls, and the Unity
/// details block + property foldouts. Capture and truncated replay run in the runtime
/// FrameDebugger; the panel is a pure view over <see cref="FrameDebugger.LastCapture"/>.
/// </summary>
public sealed partial class FrameDebuggerPanel : DockPanel
{
    private const float ToolbarHeight = 32f;
    private const float StatusHeight = 20f;
    private const float SplitterSize = 4f;
    private const float MinSidebar = 200f;
    private const float MaxSidebar = 560f;
    private const float PreviewMinHeight = 140f;

    // Layout
    private float _sidebarWidth = 320f;
    private float _previewHeight = 260f;

    // Selection / preview controls
    private int _selectedLeaf = -1;
    private int _channelIndex; // 0=All 1=R 2=G 3=B 4=A
    private float _levelsMin, _levelsMax = 1f;
    private int _rtIndex; // 0 = the event's bound target, N = capture RT N-1
    private int _outputTab; // 0=Output 1=Mesh Preview
    private readonly List<string> _rtOptions = new();

    // Options (gear menu)
    private bool _showStateEvents;
    private bool _showRasterSection;

    // Tree
    private string _searchText = "";
    private readonly List<TreeNode> _treeNodes = new();
    private readonly Dictionary<string, bool> _expandStates = new();
    private readonly Dictionary<string, bool> _pendingExpandStates = new();
    private readonly Dictionary<int, string> _leafParentGroupIds = new();

    /// <summary>Highlighted group row (Unity: selecting a group truncates after its last
    /// event while the group row keeps the highlight). Null when a leaf is selected.</summary>
    private string? _selectedGroupId;
    private FrameCapture? _treeCapture;
    private string _treeFilterText = "";
    private bool _treeShowState;

    // Caches
    private GpuStateSnapshot? _cachedSnapshot;
    private int _cachedSnapshotLeaf = int.MinValue;
    private FrameCapture? _cachedCapture;

    // Details
    private int _rowOrdinal;
    private readonly Dictionary<string, bool> _foldStates = new();

    [MenuItem("Window/Analysis/Frame Debugger", priority: 22)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(FrameDebuggerPanel));

    public override string Title => "Frame Debugger";
    public override string Icon => EditorIcons.Bug;
    public override float HeaderWidth => 34f;

    public override void OnHeaderContent(Paper paper, float width, float height)
    {
        bool enabled = FrameDebugger.IsEnabled;
        Origami.Button(paper, "fd_header_toggle",
                enabled ? EditorIcons.Pause : EditorIcons.Play,
                ToggleEnabled)
            .Width(MathF.Min(28f, width)).Height(MathF.Min(26f, height))
            .Tooltip(enabled ? "Disable Frame Debugger" : "Enable Frame Debugger")
            .Show();
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        FontFile? font = EditorTheme.DefaultFont;
        if (font == null) return;

        SyncSelectionFromDebugger();
        DrawToolbar(paper, font, width);

        string? status = GetStatusMessage(out bool warning);
        if (status != null)
            DrawStatusStrip(paper, font, width, status, warning);

        float bodyH = height - ToolbarHeight - (status != null ? StatusHeight : 0f);
        if (bodyH < 80f) bodyH = 80f;
        float sidebar = Math.Clamp(_sidebarWidth, MinSidebar, Math.Max(MinSidebar, width - 220f));
        sidebar = Math.Min(sidebar, MaxSidebar);

        using (paper.Row("fd_body").Width(width).Height(bodyH).Clip().Enter())
        {
            bool hovered = paper.IsParentHovered;
            DrawLeftColumn(paper, font, sidebar, bodyH);
            DrawVSplitter(paper, bodyH);
            float rightW = width - sidebar - SplitterSize;
            DrawRightColumn(paper, font, rightW, bodyH);
            HandleKeyboard(paper, hovered);
        }
    }

    // ── Shared actions ────────────────────────────────────────────────────────────

    private void ToggleEnabled()
    {
        if (FrameDebugger.IsEnabled) FrameDebugger.Disable();
        else FrameDebugger.Enable();
        InvalidateSnapshot();
        RepaintViews();
    }

    private void SelectLeaf(int leaf)
    {
        _selectedGroupId = null;
        FrameDebugger.SelectedEventIndex = leaf;
        _selectedLeaf = leaf;
        InvalidateSnapshot();
        FollowSelectedRt();
        RepaintViews();
    }

    private void Step(int delta)
    {
        FrameCapture? capture = FrameDebugger.LastCapture;
        if (capture == null || capture.LeafEvents.Count == 0) return;
        int next = Math.Clamp(FrameDebugger.SelectedEventIndex + delta, 0, capture.LeafEvents.Count - 1);
        SelectLeaf(next);
    }

    private void SyncSelectionFromDebugger()
    {
        int sel = FrameDebugger.SelectedEventIndex;
        if (sel != _selectedLeaf)
        {
            // Selection moved externally (scrubber, MCP) — highlight the leaf, not a group.
            _selectedGroupId = null;
            _selectedLeaf = sel;
            InvalidateSnapshot();
            FollowSelectedRt();
        }
    }

    private void InvalidateSnapshot()
    {
        _cachedSnapshot = null;
        _cachedSnapshotLeaf = int.MinValue;
    }

    private static void RepaintViews()
    {
        EditorApplication.Instance?.InvalidateSurface(
            EditorSurfaceDirtyReason.GameView | EditorSurfaceDirtyReason.ExplicitRepaint);
    }

    private GpuStateSnapshot? GetSelectedSnapshot()
    {
        FrameCapture? capture = FrameDebugger.LastCapture;
        if (capture == null || capture.LeafEvents.Count == 0)
            return null;
        int leaf = Math.Clamp(FrameDebugger.SelectedEventIndex, 0, capture.LeafEvents.Count - 1);
        if (_cachedSnapshot != null && _cachedSnapshotLeaf == leaf && ReferenceEquals(_cachedCapture, capture))
            return _cachedSnapshot;
        _cachedCapture = capture;
        _cachedSnapshot = GpuStateRebuilder.Rebuild(capture, leaf);
        _cachedSnapshotLeaf = leaf;
        return _cachedSnapshot;
    }

    private static FrameEvent? GetSelectedEvent(FrameCapture? capture)
    {
        if (capture == null || capture.LeafEvents.Count == 0) return null;
        int leaf = Math.Clamp(FrameDebugger.SelectedEventIndex, 0, capture.LeafEvents.Count - 1);
        return capture.LeafEvents[leaf];
    }

    private static CapturedOp? GetSelectedOp(FrameCapture? capture)
    {
        FrameEvent? e = GetSelectedEvent(capture);
        if (e == null || capture == null) return null;
        return e.OpIndex >= 0 && e.OpIndex < capture.Ops.Count ? capture.Ops[e.OpIndex] : null;
    }

    private string? GetStatusMessage(out bool warning)
    {
        warning = false;
        if (FrameDebugger.IsCapturing)
            return "Capturing frame…";
        if (FrameDebugger.IsEnabled && FrameDebugger.LastCapture == null)
        {
            if (GameViewPanel.ActiveInstance == null
                && FrameDebugger.CaptureScope == FrameDebuggerCaptureScope.GameRendering)
            {
                warning = true;
                return "Game view is not visible — open a Game panel to capture its rendering";
            }
            return "Waiting for next frame capture…";
        }
        if (FrameDebugger.IsCaptureStale)
        {
            warning = true;
            return "Capture is stale — the frame content has changed. Refresh to re-capture.";
        }
        FrameCapture? capture = FrameDebugger.LastCapture;
        if (capture is { IndexMappingReliable: false })
        {
            warning = true;
            return "Capture indices may not match GPU ops on this backend — stepping can be off.";
        }
        return null;
    }

    private void HandleKeyboard(Paper paper, bool hovered)
    {
        if (!hovered) return;
        FrameCapture? capture = FrameDebugger.LastCapture;
        if (capture == null || capture.LeafEvents.Count == 0) return;

        bool up = paper.IsKeyPressedOrRepeating(PaperKey.Up);
        bool down = paper.IsKeyPressedOrRepeating(PaperKey.Down);
        bool left = paper.IsKeyPressed(PaperKey.Left);
        bool right = paper.IsKeyPressed(PaperKey.Right);
        if (!up && !down && !left && !right) return;

        if (up) Step(-1);
        else if (down) Step(1);
        else if (left || right)
        {
            // Left collapses the selected event's group, Right expands it.
            if (_leafParentGroupIds.TryGetValue(FrameDebugger.SelectedEventIndex, out string? groupId) && groupId != null)
                _pendingExpandStates[groupId] = right;
        }
    }

    private void DrawVSplitter(Paper paper, float h)
    {
        paper.Box("fd_vsplit").Width(SplitterSize).Height(h)
            .BackgroundColor(EditorTheme.BorderSoft)
            .Cursor(PaperCursor.ResizeHorizontal)
            .OnDragging(e => _sidebarWidth = Math.Clamp(_sidebarWidth + (float)e.Delta.X, MinSidebar, MaxSidebar));
    }

    private void DrawHSplitter(Paper paper, float w)
    {
        paper.Box("fd_hsplit").Width(w).Height(SplitterSize)
            .BackgroundColor(EditorTheme.BorderSoft)
            .Cursor(PaperCursor.ResizeVertical)
            .OnDragging(e => _previewHeight = Math.Clamp(_previewHeight + (float)e.Delta.Y, PreviewMinHeight, 700f));
    }

    // ── Persistence ───────────────────────────────────────────────────────────────

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        state["sidebar"] = _sidebarWidth;
        state["previewH"] = _previewHeight;
        state["channel"] = _channelIndex;
        state["levelsMin"] = _levelsMin;
        state["levelsMax"] = _levelsMax;
        state["tab"] = _outputTab;
        state["showState"] = _showStateEvents;
        state["showRaster"] = _showRasterSection;
        state["fullFrame"] = FrameDebugger.CaptureScope == FrameDebuggerCaptureScope.FullFrame;
        var folds = new System.Text.Json.Nodes.JsonObject();
        foreach (KeyValuePair<string, bool> kv in _foldStates)
            folds[kv.Key] = kv.Value;
        state["folds"] = folds;
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        _sidebarWidth = Math.Clamp(state["sidebar"]?.GetValue<float>() ?? _sidebarWidth, MinSidebar, MaxSidebar);
        _previewHeight = Math.Clamp(state["previewH"]?.GetValue<float>() ?? _previewHeight, PreviewMinHeight, 700f);
        if (state["channel"]?.GetValue<int>() is int ch && ch is >= 0 and <= 4)
            _channelIndex = ch;
        _levelsMin = Math.Clamp(state["levelsMin"]?.GetValue<float>() ?? _levelsMin, 0f, 1f);
        _levelsMax = Math.Clamp(state["levelsMax"]?.GetValue<float>() ?? _levelsMax, _levelsMin, 1f);
        if (state["tab"]?.GetValue<int>() is int tab && tab is 0 or 1)
            _outputTab = tab;
        _showStateEvents = state["showState"]?.GetValue<bool>() ?? _showStateEvents;
        _showRasterSection = state["showRaster"]?.GetValue<bool>() ?? _showRasterSection;
        if (state["fullFrame"]?.GetValue<bool>() is bool full)
            FrameDebugger.CaptureScope = full
                ? FrameDebuggerCaptureScope.FullFrame
                : FrameDebuggerCaptureScope.GameRendering;
        if (state["folds"] is System.Text.Json.Nodes.JsonObject folds)
        {
            foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> kv in folds)
            {
                if (kv.Value?.GetValue<bool>() is bool v)
                    _foldStates[kv.Key] = v;
            }
        }
    }
}
