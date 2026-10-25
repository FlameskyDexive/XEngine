// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.PaperUI;
using XEngine.PaperUI.Events;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Vector;
using XEngine.Vector.Spatial;

using Color = System.Drawing.Color;

namespace XEngine.OrigamiUI;

// ================================================================
//  Data types
// ================================================================

/// <summary>
/// Describes one node in an Origami tree. Built by the caller for each visible node;
/// the tree widget handles layout, selection, expand/collapse, checkboxes, drag-drop, etc.
/// </summary>
public sealed class TreeNode
{
    /// <summary>Stable unique ID for this node (used for expand state, selection, etc.).</summary>
    public string Id = "";

    /// <summary>Display label.</summary>
    public string Label = "";

    /// <summary>Icon glyph (FontAwesome etc.) shown before the label. Empty = no icon.</summary>
    public string Icon = "";

    /// <summary>
    /// Optional node-type icon (~13px), drawn tinted to <see cref="IconColor"/>. Takes precedence
    /// over the glyph <see cref="Icon"/>.
    /// </summary>
    public IOrigamiIcon? IconDraw;

    /// <summary>Icon color override. Null = use default ink color.</summary>
    public Color? IconColor;

    /// <summary>Label color override. Null = use default ink color.</summary>
    public Color? LabelColor;

    /// <summary>Optional right-aligned badge text (counts, status, etc.).</summary>
    public string? Badge;

    /// <summary>Badge color override.</summary>
    public Color? BadgeColor;

    /// <summary>Whether this node has children (shows expand arrow even if children aren't provided yet - for lazy loading).</summary>
    public bool HasChildren;

    /// <summary>Whether this node starts expanded on first appearance. Ignored after first frame (state persists).</summary>
    public bool DefaultExpanded;

    /// <summary>Whether this node is a leaf that cannot be expanded (no arrow shown regardless of HasChildren).</summary>
    public bool IsLeaf;

    /// <summary>Depth in the tree (0 = root). Set by the caller; controls indentation.</summary>
    public int Depth;

    /// <summary>Arbitrary user data attached to this node.</summary>
    public object? UserData;

    /// <summary>If true, the node's checkbox is checked (only used when the tree has checkboxes enabled).</summary>
    public bool Checked;

    /// <summary>If true, the node is in an indeterminate/partial check state (visual only - drawn as a dash).</summary>
    public bool Indeterminate;

    /// <summary>If true, this node cannot be interacted with (dimmed, no click/drag).</summary>
    public bool Disabled;

    /// <summary>If true, this node is currently being renamed (draws a text field instead of a label).</summary>
    public bool IsRenaming;

    /// <summary>Optional secondary icon on the right side of the row (e.g., visibility eye).</summary>
    public string? TrailingIcon;

    /// <summary>Color for the trailing icon.</summary>
    public Color? TrailingIconColor;

    /// <summary>
    /// If set, overrides the internal expand state this frame AND persists it.
    /// Use to force-expand parents of pinged/searched nodes.
    /// </summary>
    public bool? OverrideExpanded;

    /// <summary>
    /// If set, the tree draws a drop indicator (above/below bar or into-tint) on this node.
    /// Set by the caller each frame based on drag state.
    /// </summary>
    public TreeDropPosition? DropIndicator;
}

/// <summary>Where a drag payload was dropped relative to a tree node.</summary>
public enum TreeDropPosition
{
    Above,
    Into,
    Below,
    /// <summary>Drop as the node's first child (index 0). Shown as an indented line at the child slot.</summary>
    IntoFirst
}

/// <summary>Event args for tree node interactions.</summary>
public readonly struct TreeNodeEvent
{
    public readonly TreeNode Node;
    public readonly int Index; // Flat index in the visible list
    public TreeNodeEvent(TreeNode node, int index) { Node = node; Index = index; }
}

/// <summary>Event args for tree drag-drop.</summary>
public readonly struct TreeDropEvent
{
    public readonly TreeNode TargetNode;
    public readonly TreeDropPosition Position;
    public readonly float NormalizedY; // 0..1 within the row
    public TreeDropEvent(TreeNode target, TreeDropPosition pos, float normY) { TargetNode = target; Position = pos; NormalizedY = normY; }
}

// ================================================================
//  Builder
// ================================================================

/// <summary>
/// Fluent builder for an Origami tree view. Construct via <see cref="Origami.Tree"/>;
/// configure callbacks and options; call <see cref="Show"/> to render.
///
/// The tree is virtualised for large lists: only visible rows are laid out. Nodes are
/// provided as a flat list (pre-ordered depth-first) with a Depth field for indentation;
/// expand/collapse state is managed internally via Paper element storage.
/// </summary>
public sealed class TreeBuilder : IDisposable
{
    private readonly Paper _paper;
    private readonly string _id;
    private readonly string _scrollId;
    private readonly OrigamiTheme _theme;

    // Node data
    private List<TreeNode>? _nodes;

    // Layout - default to 0 meaning "use theme HeaderHeight"
    private float _rowHeight;
    private float _indentSize = 14f;
    private float _baseIndent = 6f;
    private float _arrowWidth = 12f;
    private float _width;
    private float _height;
    private float _padding = 4f;
    private float? _topPadding;
    private float? _contentWidth;
    private float _scrollX;
    private float _leadingWidth;

    // Features
    private bool _checkboxes;
    private bool _multiSelect;
    private bool _reorderable; // show above/below drop indicators
    private bool _zebraStripes;
    private bool _fillViewportStripes;
    private ScrollViewport _stripeViewport;
    private float _stripeRowHeight;
    private Color _stripeEven, _stripeAlt;
    private ulong _stripeRevision;
    private Action<Canvas, XEngine.Vector.Rect>? _drawViewportStripes;

    // Selection (external state)
    private Func<TreeNode, bool>? _isSelected;
    private bool _selectionFocused = true;
    private Action<TreeNodeEvent>? _onSelect;
    private Action<TreeNodeEvent, bool, bool>? _onSelectModified; // (node, ctrl, shift)

    // Expand/collapse
    private Action<TreeNode, bool>? _onExpandChanged;

    // Checkbox
    private Action<TreeNode, bool>? _onCheckedChanged;

    // Clicks
    private Action<TreeNodeEvent>? _onDoubleClick;
    private Action<TreeNodeEvent>? _onRightClick;

    // Trailing icon click
    private Action<TreeNode>? _onTrailingIconClick;

    // Context menu
    private Action<TreeNode>? _onContextMenu;

    // Drag-drop
    private Func<TreeNode, bool>? _canDrag;
    private Action<TreeNode>? _onDragStart;
    private Func<TreeNode, bool>? _canDrop;
    private Action<TreeDropEvent>? _onDrop;

    // Hover
    private Action<TreeNode, float>? _onHover; // (node, normalizedY)

    // Rename
    private Action<TreeNode, string>? _onRenamed;

    // Custom row rendering
    private Action<Paper, TreeNode, bool, bool>? _customRowContent; // (paper, node, isSelected, isExpanded)
    private Action<Paper, TreeNode, bool, bool, bool>? _leadingRowContent;

    // Ping highlight
    private Func<TreeNode, bool>? _isPinged;
    private Func<float>? _pingAlpha;

    // Optional sink: each Show() fills this with the resolved expanded state per node id, so callers
    // (e.g. a hierarchy panel doing drag-drop) can know whether a node is currently expanded.
    private Dictionary<string, bool>? _expandStateSink;

    // Optional caller-owned expand-state store (raw per-node expand flags, keyed by node id). When
    // set it replaces Paper element storage as the source of truth, so expand state survives frames
    // where the tree isn't drawn (inactive dock tab, collapsed section, hot reload) - Paper deletes
    // element storage for any element not created that frame.
    private Dictionary<string, bool>? _expandStateStore;
    private ITreeRowIndex? _rowIndex;
    /// <summary>Opt-in fixed-height virtualization: only rows intersecting the viewport (plus a
    /// two-row overscan) are built. The most recent fold animates its child block exactly like the
    /// unvirtualized wrapper path (0.15s linear height lerp, clipped); a newer fold or a rebuilt
    /// index snaps the previous transition. Child blocks taller than four viewports snap.</summary>
    public TreeBuilder RowIndex(ITreeRowIndex? index) { _rowIndex = index; return this; }

    // ── Indexed fold transition (single active fold, bounded work) ─────
    private const float FoldDuration = 0.15f;
    private const float FoldAnimationViewportLimit = 4f;
    private ITreeRowIndex? _foldIndex;
    private long _foldRevisionSeen;
    private int _foldNode = -1;
    private bool _foldExpanding;
    private float _foldProgress;
    private int _foldRows, _foldRank;
    private bool _foldTransient;
    /// <summary>Node index whose fold is currently animating, or -1 when settled.</summary>
    public int AnimatingFoldNode => _foldNode;
    /// <summary>Progress of the active fold transition (0 collapsed .. 1 expanded).</summary>
    public float FoldProgress => _foldNode < 0 ? (_foldExpanding ? 1f : 0f) : _foldProgress;

    /// <summary>Advances the single indexed fold transition and, for a collapse, transiently shows
    /// the vanishing rows so they can shrink out of view. Must be balanced by
    /// <see cref="EndFoldTransient"/> in the same Show.</summary>
    private void UpdateFoldTransition(List<TreeNode> nodes)
    {
        var index = _rowIndex!;
        _foldRows = 0;
        if (!ReferenceEquals(index, _foldIndex))
        {
            // New topology/index instance: adopt its fold counter without animating history.
            _foldIndex = index; _foldRevisionSeen = index.FoldRevision; _foldNode = -1;
        }
        if (index.FoldRevision != _foldRevisionSeen)
        {
            _foldRevisionSeen = index.FoldRevision;
            int node = index.LastFoldNodeIndex;
            bool expanding = index.LastFoldExpanded;
            // Re-toggling the animating node reverses from its current progress, like AnimateBool.
            if (node != _foldNode) _foldProgress = expanding ? 0f : 1f;
            _foldNode = node; _foldExpanding = expanding;
        }
        if (_foldNode < 0) return;
        if ((uint)_foldNode >= (uint)nodes.Count) { _foldNode = -1; return; }
        // A programmatic ScrollTo on this tree (reveal-on-selection, ping) addresses the settled
        // layout; the scroll view would clamp it against the shrunken content mid-animation, so the
        // fold snaps instead. Interactive toggles never enqueue a ScrollTo and keep animating.
        if (ScrollViewBuilder.s_pendingScrollTo.ContainsKey(_scrollId)) { _foldNode = -1; return; }
        float step = _paper.DeltaTime > 0f ? _paper.DeltaTime / FoldDuration : 0f;
        _foldProgress = _foldExpanding ? MathF.Min(1f, _foldProgress + step) : MathF.Max(0f, _foldProgress - step);
        if (_foldExpanding ? _foldProgress >= 1f - float.Epsilon : _foldProgress <= float.Epsilon) { _foldNode = -1; return; }
        int rank = index.GetVisibleRank(_foldNode);
        if (rank < 0) { _foldNode = -1; return; } // hidden by an ancestor fold: nothing to animate
        if (!_foldExpanding) { index.SetTransientVisible(_foldNode, true); _foldTransient = true; }
        int rows = index.GetVisibleDescendantCount(_foldNode);
        if (rows == 0 || rows * _rowHeight > FoldAnimationViewportLimit * MathF.Max(_height, _rowHeight))
        {
            EndFoldTransient(); _foldNode = -1; return;
        }
        _foldRank = rank; _foldRows = rows;
    }

    private void EndFoldTransient()
    {
        if (!_foldTransient) return;
        _foldTransient = false;
        _rowIndex?.SetTransientVisible(_foldNode, false);
    }

    // Empty state
    private string? _emptyMessage;

    // ── Per-frame allocation reuse ────────────────────────────────────
    // Rows used to allocate a fresh closure per subscription per visible node per presented frame
    // (73k dead DisplayClass67_* on one overnight dump). Rows now rent a RowBindings slot whose
    // per-event delegates are created once and reused; composite row ids come from a per-node cache.
    private const int RetainedRowLimit = 512;
    private readonly Stack<RowBindings> _bindingPool = new();
    private static readonly Action<bool> s_noopChecked = static _ => { };
    private readonly List<RowBindings> _frameBindings = new();
    private readonly Stack<(int depth, IDisposable scope)> _wrapperStack = new();
    private Dictionary<string, CachedRowIds>? _rowIdCache;
    private readonly Queue<string> _rowIdOrder = new();
    private bool _disposed;
    public int BindingCreations { get; private set; }
    public int CachedRowIdCount => _rowIdCache?.Count ?? 0;
    public int RetainedBindingCount => _bindingPool.Count + _frameBindings.Count;

    /// <summary>Release row callbacks and model references when the owning panel closes,
    /// reloads or changes surface. Saved callbacks become inert; disposed builders cannot draw.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var binding in _frameBindings) binding.Clear();
        foreach (var binding in _bindingPool) binding.Clear();
        _frameBindings.Clear(); _bindingPool.Clear();
        _rowIdCache?.Clear(); _rowIdOrder.Clear();
        _nodes = null; _rowIndex = null; _expandStateStore = null; _expandStateSink = null;
        _isSelected = null; _onSelect = null; _onSelectModified = null;
        _selectionFocused = true;
        _onExpandChanged = null; _onCheckedChanged = null; _onDoubleClick = null;
        _onRightClick = null; _onTrailingIconClick = null; _onContextMenu = null;
        _canDrag = null; _onDragStart = null; _canDrop = null; _onDrop = null;
        _onHover = null; _onRenamed = null; _customRowContent = null; _leadingRowContent = null;
        _isPinged = null; _pingAlpha = null;
    }

    internal TreeBuilder(Paper paper, string id, OrigamiTheme theme)
    {
        _paper = paper ?? throw new ArgumentNullException(nameof(paper));
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _scrollId = _id + "_scroll";
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
    }

    // ── Data ──────────────────────────────────────────────────────────

    /// <summary>
    /// Provide the flat, depth-first-ordered node list. Each node's Depth field
    /// controls indentation. Children of a collapsed parent should be omitted by
    /// the caller (or the tree will skip them based on expand state).
    /// </summary>
    public TreeBuilder Nodes(List<TreeNode> nodes) { _nodes = nodes; return this; }

    // ── Layout ────────────────────────────────────────────────────────

    public TreeBuilder RowHeight(float h) { _rowHeight = MathF.Max(16, h); return this; }
    public TreeBuilder IndentSize(float s) { _indentSize = MathF.Max(0, s); return this; }

    /// <summary>Left inset (px) applied to every row before the per-depth indent. Default 6.</summary>
    public TreeBuilder BaseIndent(float s) { _baseIndent = MathF.Max(0, s); return this; }

    /// <summary>Width (px) reserved for the caret/arrow slot, drawn or not. Default 12.</summary>
    public TreeBuilder ArrowWidth(float w) { _arrowWidth = MathF.Max(0, w); return this; }

    public TreeBuilder Size(float w, float h) { _width = w; _height = h; return this; }
    public TreeBuilder Padding(float p) { _padding = p; return this; }

    /// <summary>Override the default top gap without changing other trees' padding.</summary>
    public TreeBuilder TopPadding(float p) { _topPadding = MathF.Max(0, p); return this; }

    /// <summary>Opt into horizontal scrolling with a caller-owned content extent. The caller
    /// can cache measurements independently of which virtual rows are currently built.</summary>
    public TreeBuilder ContentWidth(float? width)
    {
        if (width.HasValue && (!float.IsFinite(width.Value) || width.Value < 0))
            throw new ArgumentOutOfRangeException(nameof(width));
        _contentWidth = width;
        return this;
    }

    // ── Features ──────────────────────────────────────────────────────

    /// <summary>Show checkboxes on each node.</summary>
    public TreeBuilder Checkboxes(bool show = true) { _checkboxes = show; return this; }

    /// <summary>Enable multi-select (Ctrl+Click, Shift+Click). Requires OnSelectModified callback.</summary>
    public TreeBuilder MultiSelect(bool enable = true) { _multiSelect = enable; return this; }

    /// <summary>Show above/below drop indicators during drag (for reorder trees like Hierarchy).</summary>
    public TreeBuilder Reorderable(bool enable = true) { _reorderable = enable; return this; }

    /// <summary>Paint odd visible rows with <see cref="OrigamiTheme.RowAlt"/> so adjacent objects read apart.</summary>
    public TreeBuilder ZebraStripes(bool enable = true, bool fillViewport = false)
    { _zebraStripes = enable; _fillViewportStripes = fillViewport; return this; }

    /// <summary>Resolve the idle row fill. Drop-into tint wins; zebra uses opaque even/odd fills.</summary>
    public static Color ResolveRowBackground(bool zebraStripes, int paintedRow, bool isDropInto, Color dropTint, Color rowAlt, Color rowEven)
    {
        if (isDropInto) return dropTint;
        if (!zebraStripes) return Color.Transparent;
        return (paintedRow & 1) == 1 ? rowAlt : rowEven;
    }

    // ── Selection ─────────────────────────────────────────────────────

    /// <summary>Predicate to determine if a node is currently selected (for highlighting).</summary>
    public TreeBuilder IsSelected(Func<TreeNode, bool> predicate) { _isSelected = predicate; return this; }

    /// <summary>Collection keyboard focus, supplied by the host. Standalone colors are unchanged.</summary>
    public TreeBuilder SelectionFocused(bool focused) { _selectionFocused = focused; return this; }

    /// <summary>Called on plain click (no modifiers). Single-select behavior.</summary>
    public TreeBuilder OnSelect(Action<TreeNodeEvent> handler) { _onSelect = handler; return this; }

    /// <summary>Called on click with modifier key info. For multi-select trees.</summary>
    public TreeBuilder OnSelectModified(Action<TreeNodeEvent, bool, bool> handler) { _onSelectModified = handler; return this; }

    // ── Expand / Collapse ─────────────────────────────────────────────

    /// <summary>Called when a node's expand state changes. Use to trigger lazy child loading.</summary>
    public TreeBuilder OnExpandChanged(Action<TreeNode, bool> handler) { _onExpandChanged = handler; return this; }

    // ── Checkbox ──────────────────────────────────────────────────────

    /// <summary>Called when a node's checkbox is toggled.</summary>
    public TreeBuilder OnCheckedChanged(Action<TreeNode, bool> handler) { _onCheckedChanged = handler; return this; }

    // ── Clicks ────────────────────────────────────────────────────────

    public TreeBuilder OnDoubleClick(Action<TreeNodeEvent> handler) { _onDoubleClick = handler; return this; }
    public TreeBuilder OnRightClick(Action<TreeNodeEvent> handler) { _onRightClick = handler; return this; }
    public TreeBuilder OnTrailingIconClick(Action<TreeNode> handler) { _onTrailingIconClick = handler; return this; }

    // ── Context Menu ──────────────────────────────────────────────────

    /// <summary>Called to build a context menu for a node. Use with ContextMenuHelper inside the callback.</summary>
    public TreeBuilder OnContextMenu(Action<TreeNode> handler) { _onContextMenu = handler; return this; }

    // ── Drag & Drop ───────────────────────────────────────────────────

    public TreeBuilder CanDrag(Func<TreeNode, bool> predicate) { _canDrag = predicate; return this; }
    public TreeBuilder OnDragStart(Action<TreeNode> handler) { _onDragStart = handler; return this; }
    public TreeBuilder CanDrop(Func<TreeNode, bool> predicate) { _canDrop = predicate; return this; }
    public TreeBuilder OnDrop(Action<TreeDropEvent> handler) { _onDrop = handler; return this; }

    // ── Hover ─────────────────────────────────────────────────────────

    /// <summary>Called when hovering a node during drag. Receives normalizedY (0..1) within the row.</summary>
    public TreeBuilder OnHover(Action<TreeNode, float> handler) { _onHover = handler; return this; }

    // ── Rename ────────────────────────────────────────────────────────

    /// <summary>Called when a rename is committed. Node.IsRenaming must be set by the caller.</summary>
    public TreeBuilder OnRenamed(Action<TreeNode, string> handler) { _onRenamed = handler; return this; }

    // ── Custom Content ────────────────────────────────────────────────

    /// <summary>
    /// Override the default row content rendering. The callback receives the paper,
    /// the node, whether it's selected, and whether it's expanded. The callback draws
    /// inside the row's layout scope. Arrow and checkbox are still drawn by the tree.
    /// </summary>
    public TreeBuilder CustomRowContent(Action<Paper, TreeNode, bool, bool> renderer) { _customRowContent = renderer; return this; }

    /// <summary>Opt into fixed columns before the caret. The renderer draws inside a clipped,
    /// fixed-width row scope and receives selected, expanded and whole-row-hovered state.
    /// Columns stay at the viewport edge while names scroll horizontally. Null removes them.</summary>
    public TreeBuilder LeadingRowContent(float width, Action<Paper, TreeNode, bool, bool, bool>? renderer)
    {
        if (!float.IsFinite(width) || width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        _leadingWidth = renderer == null ? 0 : width;
        _leadingRowContent = renderer;
        return this;
    }

    // ── Ping ──────────────────────────────────────────────────────────

    /// <summary>Predicate to determine if a node should show a ping highlight.</summary>
    public TreeBuilder IsPinged(Func<TreeNode, bool> predicate) { _isPinged = predicate; return this; }

    /// <summary>Returns the current ping alpha (0..1) for fade animation.</summary>
    public TreeBuilder PingAlpha(Func<float> getter) { _pingAlpha = getter; return this; }

    /// <summary>Provide a dictionary that Show() fills with each node's resolved expanded state (by node id).</summary>
    public TreeBuilder ExpandStateSink(Dictionary<string, bool> sink) { _expandStateSink = sink; return this; }

    /// <summary>
    /// Provide a caller-owned dictionary holding each node's raw expand flag (by node id). The tree
    /// reads and writes this instead of Paper element storage, so expand state persists across frames
    /// where the tree isn't drawn. Missing entries fall back to <see cref="TreeNode.DefaultExpanded"/>.
    /// The caller may also write to it directly (e.g. reveal-on-selection expanding ancestors).
    /// </summary>
    public TreeBuilder ExpandStateStore(Dictionary<string, bool> store) { _expandStateStore = store; return this; }

    // ── Empty state ───────────────────────────────────────────────────

    /// <summary>Message shown when the node list is empty.</summary>
    public TreeBuilder EmptyMessage(string msg) { _emptyMessage = msg; return this; }

    // ── Terminator ────────────────────────────────────────────────────

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var nodes = _nodes;
        if (nodes == null) return;

        // Recycle last frame's row bindings before renting fresh ones: the elements they were
        // attached to are dead (Paper rebuilds the tree in place), so their delegates are stale.
        while (_frameBindings.Count > 0)
        {
            var binding = _frameBindings[^1];
            _frameBindings.RemoveAt(_frameBindings.Count - 1);
            binding.Clear();
            if (_bindingPool.Count < RetainedRowLimit) _bindingPool.Push(binding);
        }

        var font = _theme.Font;
        var ink = _theme.Ink;
        var metrics = _theme.Metrics;
        float rounding = metrics.Rounding;

        // Resolve row height from the theme so it follows RowHeight changes (caller can still override).
        if (_rowHeight <= 0)
            _rowHeight = metrics.RowHeight;

        // Expand state storage key prefix
        string expandPrefix = $"{_id}_exp_";

        // Capture a stable handle for expand state storage. We use a hidden box
        // that lives outside the scroll so its handle doesn't shift with scroll content.
        var stateBox = _paper.Box($"{_id}_state").Height(0).Width(0);
        var stateHandle = stateBox._handle;

        if (_rowIndex != null) UpdateFoldTransition(nodes);
        try { ShowScrollBody(nodes, font, ink, metrics, rounding, stateHandle, expandPrefix); }
        finally { EndFoldTransient(); }
    }

    private void ShowScrollBody(List<TreeNode> nodes, XEngine.Scribe.FontFile? font, OrigamiRamp ink,
        OrigamiMetrics metrics, float rounding, ElementHandle stateHandle, string expandPrefix)
    {
        // Rows of the animating child block currently occupy rowHeight*progress each.
        float foldShrink = _foldRows * _rowHeight * (1f - _foldProgress);

        // Full-width rows (no side inset) with a small top gap so the first row's selection doesn't
        // butt up against the tab bar / container edge above it.
        Origami.ScrollView(_paper, _scrollId, _width, _height)
            .Horizontal(_contentWidth.HasValue)
            .ContentWidth(_contentWidth)
            .Padding(0f, 0f, _topPadding ?? MathF.Max(6f, _padding), _padding)
            .ContentHeight(_rowIndex == null ? null : MathF.Max(0f, _rowIndex.Count * _rowHeight - foldShrink))
            .Body(viewport =>
            {
                _scrollX = viewport.ScrollX;
                if (_zebraStripes && _fillViewportStripes)
                {
                    if (viewport.ScrollX != _stripeViewport.ScrollX || viewport.ScrollY != _stripeViewport.ScrollY
                        || viewport.Width != _stripeViewport.Width || viewport.Height != _stripeViewport.Height
                        || _rowHeight != _stripeRowHeight || _theme.RowEven != _stripeEven || _theme.RowAlt != _stripeAlt)
                    {
                        _stripeViewport = viewport; _stripeRowHeight = _rowHeight;
                        _stripeEven = _theme.RowEven; _stripeAlt = _theme.RowAlt; _stripeRevision++;
                    }
                    _paper.DrawVersioned("tree.viewport-stripes", _stripeRevision,
                        _drawViewportStripes ??= DrawViewportStripes);
                }
                if (nodes.Count == 0 && !string.IsNullOrEmpty(_emptyMessage) && font != null)
                {
                    _paper.Box($"{_id}_empty").Height(40)
                        .Text(_emptyMessage, font)
                        .TextColor(ink.C300)
                        .FontSize(metrics.FontSize - 1)
                        .Alignment(TextAlignment.MiddleCenter);
                    return;
                }

                if (_rowIndex != null)
                {
                    int total = _rowIndex.Count;
                    float rh = _rowHeight;
                    int first = Math.Clamp((int)MathF.Floor(viewport.ScrollY / rh) - 2, 0, total);
                    int end = Math.Clamp((int)MathF.Ceiling((viewport.ScrollY + viewport.Height) / rh) + 2, first, total);

                    // Animating child block [rangeStart, rangeEnd): built whole (bounded by the
                    // viewport limit) so its clipped wrapper shows the same top slice as the legacy
                    // wrapper, and neighbouring rows shift by the block's current shrink.
                    int rangeStart = _foldRows > 0 ? _foldRank + 1 : int.MaxValue, rangeEnd = rangeStart + _foldRows;
                    float beforeShrink = 0f, afterShrink = 0f;
                    if (_foldRows > 0)
                    {
                        int shrinkRows = (int)MathF.Ceiling(_foldRows * (1f - _foldProgress));
                        // Rows after the block sit higher than their nominal rank while it shrinks,
                        // so the viewport bottom reaches further down the rank order.
                        if (rangeStart < end) end = Math.Clamp(end + shrinkRows, first, total);
                        if (rangeStart < end && rangeEnd > first)
                        {
                            first = Math.Min(first, rangeStart);
                            end = Math.Clamp(Math.Max(end, rangeEnd), first, total);
                        }
                        if (rangeEnd <= first) beforeShrink = foldShrink;
                        else if (rangeStart >= end) afterShrink = foldShrink;
                    }

                    if (first > 0) _paper.Box($"{_id}_virtual_before").Height(MathF.Max(0f, first * rh - beforeShrink)).IsNotInteractable();
                    IDisposable? foldWrapper = null;
                    for (int row = first; row < end; row++)
                    {
                        if (row == rangeStart)
                        {
                            foldWrapper = _paper.Column($"{_id}_cw_{nodes[_foldNode].Id}")
                                .Width(UnitValue.Stretch())
                                .Height(_foldRows * rh * _foldProgress)
                                .Clip()
                                .Enter();
                        }
                        else if (row == rangeEnd && foldWrapper != null) { foldWrapper.Dispose(); foldWrapper = null; }
                        int index = _rowIndex.GetNodeIndex(row);
                        var node = nodes[index];
                        DrawNode(font, ink, metrics, rounding, stateHandle, expandPrefix, nodes, node,
                            index, row, _isSelected?.Invoke(node) ?? false, _rowIndex.IsExpanded(index),
                            node.HasChildren && !node.IsLeaf, _isPinged?.Invoke(node) ?? false);
                    }
                    foldWrapper?.Dispose();
                    if (end < total) _paper.Box($"{_id}_virtual_after").Height(MathF.Max(0f, (total - end) * rh - afterShrink)).IsNotInteractable();
                    return;
                }

                // We walk the flat list in depth-first order. Expandable parents get an
                // animated wrapper around their children that lerps height 0..Auto.
                // A stack tracks open wrappers so we close them when depth decreases.

                // Stack of (parentDepth, IDisposable wrapper scope) — reused across frames.
                var wrapperStack = _wrapperStack;
                wrapperStack.Clear();
                int paintedRow = 0;

                for (int i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];

                    // Close any wrappers for parents we've moved past
                    while (wrapperStack.Count > 0 && node.Depth <= wrapperStack.Peek().depth)
                    {
                        var (_, scope) = wrapperStack.Pop();
                        scope.Dispose();
                    }

                    bool isExpandable = node.HasChildren && !node.IsLeaf;
                    string expKey = expandPrefix + node.Id;
                    bool isExpanded;
                    if (node.OverrideExpanded.HasValue)
                    {
                        isExpanded = isExpandable && node.OverrideExpanded.Value;
                        WriteExpandState(stateHandle, expKey, node.Id, node.OverrideExpanded.Value);
                    }
                    else
                    {
                        isExpanded = isExpandable && ReadExpandState(
                            stateHandle, expKey, node.Id, node.DefaultExpanded);
                    }

                    if (_expandStateSink != null)
                        _expandStateSink[node.Id] = isExpanded;

                    bool isSelected = _isSelected?.Invoke(node) ?? false;
                    bool isPinged = _isPinged?.Invoke(node) ?? false;

                    DrawNode(font, ink, metrics, rounding, stateHandle, expandPrefix, nodes,
                        node, i, paintedRow, isSelected, isExpanded, isExpandable, isPinged);
                    paintedRow++;

                    // If this node is expandable, open an animated wrapper for its children
                    if (isExpandable)
                    {
                        string animId = $"{_id}_anim_{node.Id}";
                        float anim = _paper.AnimateBool(isExpanded, 0.15f, id: animId);

                        if (!isExpanded && anim <= float.Epsilon)
                        {
                            // Fully collapsed and animation done - skip all children
                            int parentDepth = node.Depth;
                            while (i + 1 < nodes.Count && nodes[i + 1].Depth > parentDepth)
                                i++;
                        }
                        else
                        {
                            // Animating or expanded - wrap children in a height-lerped container
                            var wrapper = _paper.Column($"{_id}_cw_{node.Id}")
                                .Width(UnitValue.Stretch())
                                .Height(UnitValue.Lerp(0, UnitValue.Auto, anim));

                            // Only clip during animation (not at rest) since Clip isn't cheap
                            bool animating = anim > float.Epsilon && anim < 1f - float.Epsilon;
                            if (animating)
                                wrapper.Clip();

                            wrapperStack.Push((node.Depth, wrapper.Enter()));
                        }
                    }
                }

                // Close any remaining open wrappers
                while (wrapperStack.Count > 0)
                {
                    var (_, scope) = wrapperStack.Pop();
                    scope.Dispose();
                }
            });
    }

    private void DrawViewportStripes(Canvas canvas, XEngine.Vector.Rect rect)
    {
        if (_disposed || _stripeRowHeight <= 0) return;
        var viewport = _stripeViewport;
        int first = Math.Max(0, (int)MathF.Floor(viewport.ScrollY / _stripeRowHeight));
        int end = (int)MathF.Ceiling((viewport.ScrollY + viewport.Height) / _stripeRowHeight);
        float x = (float)rect.Min.X + viewport.ScrollX;
        for (int row = first; row < end; row++)
        {
            Color color = (row & 1) == 0 ? _stripeEven : _stripeAlt;
            canvas.RoundedRectFilled(x, (float)rect.Min.Y + row * _stripeRowHeight,
                viewport.Width, _stripeRowHeight, 0, color);
        }
    }

    private void DrawNode(XEngine.Scribe.FontFile? font, OrigamiRamp ink, OrigamiMetrics metrics,
        float rounding, ElementHandle stateHandle, string expandPrefix, List<TreeNode> allNodes,
        TreeNode node, int index, int paintedRow, bool isSelected, bool isExpanded,
        bool isExpandable, bool isPinged)
    {
        float indent = _leadingWidth + _baseIndent + node.Depth * _indentSize;
        var ids = GetRowIds(node);
        bool disabled = node.Disabled;

        // Per-row subscription state rides a pooled binding: its event delegates are created once
        // per slot and reused every frame, so rebuilding the tree no longer allocates closures.
        RowBindings b = RentBinding(node, index);
        b.StateHandle = stateHandle;
        b.ExpandPrefix = expandPrefix;
        b.AllNodes = allNodes;
        b.Expanded = isExpanded;

        // Accent (#A855F7) for drop tints / indicators.
        var accentColor = _theme.Primary.C500;
        // Full-width, square rows (matches the Nebula tree: solid selection edge-to-edge, no inset).
        const float rowRounding = 0f;

        // Selection is a solid accent fill; hover is a soft purple wash; drop-into a translucent tint.
        Color selFill = _theme.SelectionBackground(_selectionFocused);
        Color hoverBg = _theme.Hover;

        bool isDropInto = node.DropIndicator == TreeDropPosition.Into;
        Color dropTint = Color.FromArgb(60, accentColor.R, accentColor.G, accentColor.B);
        Color rowBg = ResolveRowBackground(_zebraStripes, paintedRow, isDropInto, dropTint, _theme.RowAlt, _theme.RowEven);

        // Build the row element
        var row = _paper.Row(ids.Row)
            .Height(_rowHeight)
            .BackgroundColor(rowBg)
            .Hovered.BackgroundColor(hoverBg).End()
            .Rounded(rowRounding)
            .Clip()   // keep long labels from spilling under the overlay scrollbar
            .ChildLeft(indent)
            .ChildRight(6);

        // Click handling
        if (!disabled)
        {
            if (_onSelectModified != null && _multiSelect)
                row.OnClick(b.SelectModifiedClick);
            else if (_onSelect != null)
                row.OnClick(b.SelectClick);

            if (_onSelect != null || _onSelectModified != null)
                row.Cursor(PaperCursor.Pointer);

            if (_onDoubleClick != null)
                row.OnDoubleClick(b.DoubleClick);

            if (_onRightClick != null)
                row.OnRightClick(b.RightClick);

            // Drag
            if (_onDragStart != null && (_canDrag == null || _canDrag(node)))
                row.OnDragStart(b.DragStart).CursorDragging(PaperCursor.Grabbing);

            // Hover (for drag-drop position tracking)
            if (_onHover != null)
                row.OnHover(b.HoverDrag);
        }

        // Ping highlight
        if (isPinged)
        {
            row.OnPostLayout(b.PingLayout);
        }

        // Drop indicator (Above / Below / IntoFirst). Drawn as a canvas overlay in post-layout so it
        // never adds layout height the way an inline box would. An inline indicator pushes the row,
        // which moves it out from under the cursor and causes hover/indicator flicker.
        if (node.DropIndicator is TreeDropPosition.Above or TreeDropPosition.Below or TreeDropPosition.IntoFirst)
        {
            b.IndentForDrop = indent;
            b.DropPos = node.DropIndicator.Value;
            row.OnPostLayout(b.DropIndicatorLayout);
        }

        using (row.Enter())
        {
            // ---- Row fill (zebra / drop-into), then selection on top ----
            if (!isSelected && rowBg.A > 0)
            {
                _paper.DrawVersioned("tree.row-fill", unchecked((uint)rowBg.ToArgb()), (canvas, r) =>
                {
                    float x = (float)r.Min.X, y = (float)r.Min.Y;
                    float w = (float)r.Size.X, h = (float)r.Size.Y;
                    canvas.RoundedRectFilled(x, y, w, h, rowRounding, new Color32(rowBg.R, rowBg.G, rowBg.B, rowBg.A));
                });
            }
            else if (isSelected)
            {
                _paper.DrawVersioned("tree.row-fill", unchecked((uint)selFill.ToArgb()), (canvas, r) =>
                {
                    float x = (float)r.Min.X, y = (float)r.Min.Y;
                    float w = (float)r.Size.X, h = (float)r.Size.Y;
                    canvas.RoundedRectFilled(x, y, w, h, rowRounding, selFill);
                });
            }

            // ---- Caret ----
            // 12px slot. Expandable nodes draw a vector chevron that rotates from
            // pointing-right (collapsed) to pointing-down (expanded). Leaves reserve the gap.
            if (isExpandable)
            {
                float caretT = _paper.AnimateBool(isExpanded, 0.15f, id: $"{ids.Row}_caret");
                Color caretCol = isSelected ? _theme.SelectionText(_selectionFocused) : ink.C200;
                b.CaretT = caretT;
                b.CaretColor = caretCol;

                var caret = _paper.Box($"{ids.Row}_arr")
                    .Width(12).Height(_rowHeight)
                    .OnPress(b.CaretPress)
                    .OnClick(b.SwallowClick);

                caret.Cursor(PaperCursor.Pointer);

                using (caret.Enter())
                    _paper.DrawVersioned("tree.caret", ((ulong)unchecked((uint)BitConverter.SingleToInt32Bits(caretT)) << 32)
                        | unchecked((uint)caretCol.ToArgb()), b.CaretDraw);
            }
            else
            {
                _paper.Box($"{ids.Row}_arr").Width(12).Height(_rowHeight);
            }

            // ---- Checkbox ----
            if (_checkboxes && !disabled)
            {
                Origami.Checkbox(_paper, $"{ids.Row}_chk", node.Checked, b.CheckedChanged).Show();
            }
            else if (_checkboxes && disabled)
            {
                Origami.Checkbox(_paper, $"{ids.Row}_chk", node.Checked, s_noopChecked).Disabled().Show();
            }

            // ---- Custom or default content ----
            if (_customRowContent != null)
            {
                _customRowContent(_paper, node, isSelected, isExpanded);
            }
            else
            {
                DrawDefaultContent(font, ink, metrics, node, ids, isSelected, disabled, b);
            }

            // Paint fixed columns last so horizontally scrolled names cannot cover them.
            // The row itself moves by -ScrollX; offset this child by +ScrollX to stay fixed.
            if (_leadingRowContent != null && _leadingWidth > 0)
            {
                bool hovered = _paper.IsParentHovered;
                Color leadingBg = isSelected ? selFill : rowBg.A == 0 ? _theme.RowEven : rowBg;
                using (_paper.Row($"{ids.Row}_leading")
                    .PositionType(PositionType.SelfDirected).Position(_scrollX, 0)
                    .Size(_leadingWidth, _rowHeight).BackgroundColor(leadingBg).Clip().Enter())
                    _leadingRowContent(_paper, node, isSelected, isExpanded, hovered);
            }
        }

    }

    /// <summary>Reads a node's raw expand flag from the caller-owned store when one is set,
    /// otherwise from Paper element storage (which does not survive undrawn frames).</summary>
    private bool ReadExpandState(ElementHandle stateHandle, string expKey, string nodeId, bool defaultExpanded)
    {
        if (_expandStateStore != null)
            return _expandStateStore.TryGetValue(nodeId, out bool stored) ? stored : defaultExpanded;
        return _paper.GetElementStorage(stateHandle, expKey, defaultExpanded);
    }

    /// <summary>Writes a node's raw expand flag to the caller-owned store when one is set,
    /// otherwise to Paper element storage.</summary>
    private void WriteExpandState(ElementHandle stateHandle, string expKey, string nodeId, bool expanded)
    {
        _rowIndex?.SetExpanded(nodeId, expanded);
        if (_expandStateStore != null)
            _expandStateStore[nodeId] = expanded;
        else
            _paper.SetElementStorage(stateHandle, expKey, expanded);
    }

    /// <summary>
    /// Vector disclosure chevron. Points right when collapsed and rotates to point down as
    /// <paramref name="t"/> animates 0..1 (expand progress).
    /// </summary>
    private static void DrawCaret(Canvas canvas, Rect rect, float t, Color color)
    {
        float cx = (float)(rect.Min.X + rect.Size.X * 0.5);
        float cy = (float)(rect.Min.Y + rect.Size.Y * 0.5);

        canvas.SaveState();
        canvas.TransformBy(Transform2D.CreateTranslation(cx, cy));
        canvas.TransformBy(Transform2D.CreateRotation(90f * t));
        canvas.SetStrokeColor(color);
        canvas.SetStrokeWidth(1.5f);
        canvas.SetStrokeCap(EndCapStyle.Round);
        canvas.SetStrokeJoint(JointStyle.Round);
        canvas.BeginPath();
        canvas.MoveTo(-2f, -3.5f);
        canvas.LineTo(1.5f, 0f);
        canvas.LineTo(-2f, 3.5f);
        canvas.Stroke();
        canvas.RestoreState();
    }

    // ── Reuse infrastructure (RowBindings / CachedRowIds) ─────────────

    /// <summary>Pre-built composite element ids for one node, created lazily and reused forever
    /// (node sets are stable across frames in the retained-ID panels that host this widget).</summary>
    private sealed class CachedRowIds
    {
        public string Row = "";
        public string Exp = "";
        public string Anim = "";
        public string Cw = "";
        public string RootRename = "";
        public string Ico = "";
        public string Lbl = "";
        public string Badge = "";
        public string Trail = "";
    }

    private CachedRowIds GetRowIds(TreeNode node)
    {
        _rowIdCache ??= new Dictionary<string, CachedRowIds>();
        if (!_rowIdCache.TryGetValue(node.Id, out var ids))
        {
            if (_rowIdCache.Count == RetainedRowLimit) _rowIdCache.Remove(_rowIdOrder.Dequeue());
            ids = new CachedRowIds
            {
                Row = $"{_id}_r_{node.Id}",
                Exp = $"{_id}_exp_{node.Id}",
                Anim = $"{_id}_anim_{node.Id}",
                Cw = $"{_id}_cw_{node.Id}",
                RootRename = $"{_id}_rename_{node.Id}",
            };
            ids.Ico = $"{ids.Row}_ico";
            ids.Lbl = $"{ids.Row}_lbl";
            ids.Badge = $"{ids.Row}_badge";
            ids.Trail = $"{ids.Row}_trail";
            _rowIdCache[node.Id] = ids;
            _rowIdOrder.Enqueue(node.Id);
        }
        return ids;
    }

    private RowBindings RentBinding(TreeNode node, int index)
    {
        if (!_bindingPool.TryPop(out var binding))
        {
            binding = new RowBindings();
            BindingCreations++;
        }
        binding.Owner = this;
        binding.Node = node;
        binding.Index = index;
        _frameBindings.Add(binding);
        return binding;
    }

    /// <summary>
    /// One pooled slot of per-row subscription state. Delegates are cached lazily per event kind;
    /// frame-varying inputs (expand state, drop position, caret animation) ride plain fields set
    /// during <see cref="DrawNode"/> before any handler can fire.
    /// </summary>
    private sealed class RowBindings
    {
        public TreeBuilder Owner = null!;
        public TreeNode Node = null!;
        public int Index;

        // Frame-varying inputs read by the handlers below.
        public ElementHandle StateHandle;
        public string ExpandPrefix = "";
        public List<TreeNode> AllNodes = null!;
        public bool Expanded;
        public float CaretT;
        public Color CaretColor;
        public float IndentForDrop;
        public TreeDropPosition DropPos;
        public float PingAlpha;

        public void Clear()
        {
            Owner = null!; Node = null!; AllNodes = null!;
            StateHandle = default; ExpandPrefix = "";
        }

        private Action<ClickEvent>? _selectModifiedClick;
        private Action<ClickEvent>? _selectClick;
        private Action<ClickEvent>? _doubleClick;
        private Action<ClickEvent>? _rightClick;
        private Action<DragEvent>? _dragStart;
        private Action<ElementEvent>? _hoverDrag;
        private Action<ElementHandle, Rect>? _pingLayout;
        private Action<Canvas, Rect>? _pingDraw;
        private Action<ElementHandle, Rect>? _dropIndicatorLayout;
        private Action<Canvas, Rect>? _dropIndicatorDraw;
        private Action<ClickEvent>? _caretPress;
        private Action<Canvas, Rect>? _caretDraw;
        private Action<bool>? _checkedChanged;
        private Action<ClickEvent>? _trailClick;

        public Action<ClickEvent> SelectModifiedClick => _selectModifiedClick ??= e =>
        {
            if (Owner == null) return;
            e.StopPropagation();
            var o = Owner;
            bool ctrl = o._paper.IsKeyDown(PaperKey.LeftControl) || o._paper.IsKeyDown(PaperKey.RightControl);
            bool shift = o._paper.IsKeyDown(PaperKey.LeftShift) || o._paper.IsKeyDown(PaperKey.RightShift);
            o._onSelectModified!(new TreeNodeEvent(Node, Index), ctrl, shift);
        };

        public Action<ClickEvent> SelectClick => _selectClick ??= e =>
        {
            if (Owner == null) return;
            e.StopPropagation();
            Owner._onSelect!(new TreeNodeEvent(Node, Index));
        };

        public Action<ClickEvent> DoubleClick => _doubleClick ??= e =>
        {
            if (Owner == null) return;
            e.StopPropagation();
            Owner._onDoubleClick!(new TreeNodeEvent(Node, Index));
        };

        public Action<ClickEvent> RightClick => _rightClick ??= e =>
        {
            if (Owner == null) return;
            e.StopPropagation();
            Owner._onRightClick!(new TreeNodeEvent(Node, Index));
        };

        public Action<DragEvent> DragStart => _dragStart ??= _ =>
        {
            if (Owner == null) return;
            Owner._onDragStart!(Node);
        };

        public Action<ElementEvent> HoverDrag => _hoverDrag ??= e =>
        {
            if (Owner == null) return;
            Owner._onHover!(Node, (float)e.NormalizedPosition.Y);
        };

        public Action<ElementHandle, Rect> PingLayout => _pingLayout ??= (handle, _) =>
        {
            if (Owner == null) return;
            PingAlpha = Owner._pingAlpha?.Invoke() ?? 1f;
            if (PingAlpha <= 0f) return;
            var h = handle;
            Owner._paper.Draw(ref h, GetPingDraw());
        };

        private Action<Canvas, Rect> GetPingDraw() => _pingDraw ??= (canvas, r) =>
        {
            if (Owner == null) return;
            float alpha = PingAlpha;
            var fillColor = Color.FromArgb((int)(alpha * 60), 255, 220, 50);
            var borderColor = Color.FromArgb((int)(alpha * 200), 255, 200, 0);
            float x = (float)r.Min.X, y = (float)r.Min.Y;
            float w = (float)r.Size.X, hgt = (float)r.Size.Y;
            canvas.RoundedRectFilled(x, y, w, hgt, 4, 4, 4, 4, fillColor);
            canvas.SetStrokeColor(borderColor);
            canvas.SetStrokeWidth(2f);
            canvas.BeginPath();
            canvas.RoundedRect(x + 1, y + 1, w - 2, hgt - 2, 3, 3, 3, 3);
            canvas.Stroke();
        };

        public Action<ElementHandle, Rect> DropIndicatorLayout => _dropIndicatorLayout ??= (handle, _) =>
        {
            if (Owner == null) return;
            var h = handle;
            Owner._paper.Draw(ref h, GetDropIndicatorDraw());
        };

        private Action<Canvas, Rect> GetDropIndicatorDraw() => _dropIndicatorDraw ??= (canvas, r) =>
        {
            if (Owner == null) return;
            var o = Owner;
            // IntoFirst points at the node's first-child slot, so indent one more level.
            float left = IndentForDrop + o._theme.Metrics.SpacingLarge
                + (DropPos == TreeDropPosition.IntoFirst ? o._indentSize : 0f);
            float hgt = 3f;
            float x = (float)r.Min.X + left;
            float w = (float)r.Size.X - left - o._theme.Metrics.Spacing;
            float y = DropPos == TreeDropPosition.Above
                ? (float)r.Min.Y
                : (float)r.Min.Y + (float)r.Size.Y - hgt;
            float rad = o._theme.Metrics.SmallRounding;
            canvas.RoundedRectFilled(x, y, w, hgt, rad, rad, rad, rad, o._theme.Primary.C500);
        };

        public Action<ClickEvent> CaretPress => _caretPress ??= e =>
        {
            if (Owner == null) return;
            // Expand on press (Unity/OS tree convention). On-demand editor frames keep
            // presenting while the button is held, so the new expand state lays out on
            // the next frame without waiting for mouse-up + an extra mouse move.
            // Stop per-event (not blanket .StopEventPropagation()) so wheel scroll still
            // bubbles to a parent ScrollView while the pointer is parked over the caret.
            e.StopPropagation();
            var o = Owner;
            bool newState = !Expanded;
            bool alt = o._paper.IsKeyDown(PaperKey.LeftAlt) || o._paper.IsKeyDown(PaperKey.RightAlt);

            o.WriteExpandState(StateHandle, ExpandPrefix + Node.Id, Node.Id, newState);
            o._onExpandChanged?.Invoke(Node, newState);

            // Alt+Click: recursively expand/collapse all descendants
            if (alt)
            {
                int parentDepth = Node.Depth;
                var nodes = AllNodes;
                for (int di = Index + 1; di < nodes.Count; di++)
                {
                    var desc = nodes[di];
                    if (desc.Depth <= parentDepth) break;
                    if (desc.HasChildren && !desc.IsLeaf)
                    {
                        string descKey = ExpandPrefix + desc.Id;
                        o.WriteExpandState(StateHandle, descKey, desc.Id, newState);
                        o._onExpandChanged?.Invoke(desc, newState);
                    }
                }
            }
        };

        /// <summary>Shared by every caret of every tree: press already toggled expand.</summary>
        public static readonly Action<ClickEvent> SwallowClickStatic = static e => e.StopPropagation();

        public Action<ClickEvent> SwallowClick => SwallowClickStatic;

        public Action<Canvas, Rect> CaretDraw => _caretDraw ??= (canvas, r) =>
        {
            if (Owner == null) return;
            TreeBuilder.DrawCaret(canvas, r, CaretT, CaretColor);
        };

        public Action<bool> CheckedChanged => _checkedChanged ??= v =>
        {
            if (Owner == null) return;
            Owner._onCheckedChanged?.Invoke(Node, v);
        };

        public Action<ClickEvent> TrailClick => _trailClick ??= e =>
        {
            if (Owner == null) return;
            e.StopPropagation();
            Owner._onTrailingIconClick!(Node);
        };
    }

    private void DrawDefaultContent(XEngine.Scribe.FontFile? font, OrigamiRamp ink,
        OrigamiMetrics metrics, TreeNode node, CachedRowIds ids, bool isSelected, bool disabled,
        RowBindings b)
    {
        // ---- Node type icon (~13px). Vector hook preferred; glyph is a legacy fallback. ----
        if (node.IconDraw != null)
        {
            var icon = node.IconDraw;
            Color iconCol = node.IconColor ?? (isSelected ? _theme.SelectionText(_selectionFocused) : (disabled ? ink.C200 : ink.C400));
            using (_paper.Box($"{ids.Ico}")
                .Width(13).Height(_rowHeight)
                .Margin(6, 0, 0, 0)
                .IsNotInteractable()
                .Enter())
            {
                _paper.Draw((canvas, r) =>
                {
                    const float size = 13f;
                    float cx = (float)(r.Min.X + r.Size.X * 0.5);
                    float cy = (float)(r.Min.Y + r.Size.Y * 0.5);
                    icon.Draw(canvas, new Rect(
                        new Float2(cx - size * 0.5f, cy - size * 0.5f),
                        new Float2(cx + size * 0.5f, cy + size * 0.5f)), iconCol);
                });
            }
        }
        else if (font != null && !string.IsNullOrEmpty(node.Icon))
        {
            Color iconColor = node.IconColor ?? (isSelected ? _theme.SelectionText(_selectionFocused) : (disabled ? ink.C200 : ink.C400));
            _paper.Box($"{ids.Ico}")
                .Width(13).Height(_rowHeight)
                .Margin(6, 0, 0, 0)
                .IsNotInteractable()
                .Text(node.Icon, font)
                .TextColor(iconColor)
                .FontSize(metrics.FontSize).Alignment(TextAlignment.MiddleCenter);
        }

        if (font == null) return;

        // Label or rename field
        if (node.IsRenaming && _onRenamed != null)
        {
            string renameKey = $"{_id}_rename_{node.Id}";
            string currentName = _paper.GetElementStorage(_paper.CurrentParent, renameKey, node.Label);

            Origami.TextField(_paper, $"{ids.Row}_rename", currentName, v =>
            {
                _paper.SetElementStorage(_paper.CurrentParent, renameKey, v);
            }).Show();

            // Commit on Enter
            if (_paper.IsKeyPressed(PaperKey.Enter) || _paper.IsKeyPressed(PaperKey.KeypadEnter))
            {
                string finalName = _paper.GetElementStorage(_paper.CurrentParent, renameKey, node.Label);
                _onRenamed(node, finalName);
            }
        }
        else
        {
            // Row text is `t` (#c0bbd2); a selected row flips to white.
            Color labelColor = node.LabelColor ?? (isSelected ? _theme.SelectionText(_selectionFocused) : (disabled ? ink.C200 : ink.C400));
            _paper.Box($"{ids.Lbl}")
                .Width(UnitValue.Stretch()).Height(_rowHeight)
                .Margin(6, 0, 0, 0)
                .IsNotInteractable()
                .Text(node.Label, font)
                .TextColor(labelColor)
                .FontSize(metrics.FontSize)
                .Alignment(TextAlignment.MiddleLeft);
        }

        // Badge
        if (!string.IsNullOrEmpty(node.Badge))
        {
            _paper.Box($"{ids.Badge}")
                .Width(UnitValue.Auto).Height(_rowHeight)
                .Margin(6, 0, 0, 0)
                .IsNotInteractable()
                .Text(node.Badge, font)
                .TextColor(isSelected ? _theme.SelectionText(_selectionFocused) : (node.BadgeColor ?? ink.C300))
                .FontSize(metrics.FontSize - 2)
                .Alignment(TextAlignment.MiddleRight);
        }

        // Trailing icon (e.g., visibility eye)
        if (!string.IsNullOrEmpty(node.TrailingIcon))
        {
            Color trailColor = node.TrailingIconColor ?? (isSelected ? _theme.SelectionText(_selectionFocused) : ink.C400);
            var trailBox = _paper.Box($"{ids.Trail}")
                .Width(18).Height(_rowHeight)
                .Margin(6, 0, 0, 0)
                .Text(node.TrailingIcon, font)
                .TextColor(trailColor)
                .FontSize(9f).Alignment(TextAlignment.MiddleCenter);

            // Per-event stop (not blanket .StopEventPropagation()) so the click doesn't select the
            // row but the wheel still bubbles to a parent ScrollView while hovering the icon.
            if (_onTrailingIconClick != null && !disabled)
                trailBox.OnClick(b.TrailClick).Cursor(PaperCursor.Pointer);
        }
    }
}
