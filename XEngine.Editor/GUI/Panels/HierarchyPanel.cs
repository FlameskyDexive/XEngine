using System;
using System.Collections.Generic;
using System.Linq;

using XEngine.OrigamiUI;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Popups;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Rosetta;
using XEngine.Vector;

using Color = System.Drawing.Color;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.Editor.Utils;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;

namespace XEngine.Editor.GUI.Panels;

public class HierarchyPanel : DockPanel, IScriptReloadCleanup
{
    [MenuItem("Window/General/Hierarchy", priority: 2)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(HierarchyPanel));

    public override string Title => Loc.Get("panel.hierarchy");
    public override string Icon => EditorIcons.Sitemap;

    // The drag-hover targets are scene GameObjects; drop them before a hot-reload unload so they
    // don't pin the script AssemblyLoadContext. (Normally null outside an active drag.)
    public void OnScriptReloadCleanup()
    {
        _pingedGameObjects.Clear();
        _lastHierarchyPingGuid = Guid.Empty;
        _dragHoverTarget = null;
        _dragHoverTargetNext = null;
        ClearTreeCache();
    }

    private void ClearTreeCache()
    {
        _treePaintRegion?.Dispose(); _treePaintRegion = null; _treePaintPaper = null;
        _treeBuilder?.Dispose();
        _treeBuilder = null; _treeBuilderPaper = null; _treeBuilderTheme = null;
        _treeCacheKey = default;
        _rowIndex = null;
        _hasTreeCache = false;
        _horizontalLayout.Clear();
        _treeNodes.Clear();
        _treeNodePool.Clear();
        _flatObjects.Clear();
        _displayRoots.Clear();
        _goStyles.Clear();
        _expandState.Clear();
        _lastSelectionVersion = long.MinValue;
    }

    public override void OnClosed()
    {
        OnScriptReloadCleanup();
        _paper = null;
        _expandStore.Clear();
        _forceExpandedIds.Clear();
    }

    private string _searchText = "";
    private string? _parsedSearchText; // reference identity of the last parsed _searchText
    private HierarchySearchQuery _searchQuery;
    private readonly List<int> _visibleIndices = new();
    private bool _sceneExpanded = true;
    // Alphabetical display sort (Unity's optional hierarchy sort); off = transform sibling order.
    private bool _alphaSort;
    private Paper? _paper;
    private readonly HierarchyHorizontalLayout _horizontalLayout = new();
    private TreeBuilder? _treeBuilder;
    private Paper? _treeBuilderPaper;
    private OrigamiTheme? _treeBuilderTheme;
    private UiPaperSubtreeRegion? _treePaintRegion;
    private Paper? _treePaintPaper;
    private readonly UiMemoryBudget _treeGeometryBudget = new(8 * 1024 * 1024);
    private readonly UiMemoryBudget _treeAtlasBudget = new(16 * 1024 * 1024);

    private void AttachTreePaintReference(Paper paper)
    {
        if (!Program.HierarchyPaintReference || !paper.Renderer.SupportsRenderLayers) return;
        if (_treePaintRegion == null || !ReferenceEquals(_treePaintPaper, paper))
        {
            _treePaintRegion?.Dispose();
            if (Program.HierarchyPaintSharedAtlas)
            {
                paper.StabilizeFontAtlasBeforeRender = true;
                paper.Canvas.EnableFontAtlasRetirement();
            }
            int atlasSize = Program.HierarchyPaintMatchAtlas ? paper.Renderer.GetTextureSize(paper.Canvas.FontAtlasTexture).X : 512;
            _treePaintRegion = new UiPaperSubtreeRegion(paper, _treeGeometryBudget, _treeAtlasBudget, atlasSize,
                shareHostFonts: Program.HierarchyPaintSharedAtlas, compareVisualState: Program.HierarchyPaintIncremental);
            _treePaintPaper = paper;
        }
        // Production integration reference: never reuse unversioned visual state.
        // Incremental reuse is gated on the subsequent visual-dependency implementation.
        if (!Program.HierarchyPaintIncremental) _treePaintRegion.Invalidate();
        _treePaintRegion.Attach(paper.CurrentParent);
    }

    private TreeBuilder GetTreeBuilder(Paper paper, float width, float height)
    {
        if (!Program.IncrementalHierarchy)
            return Origami.Tree(paper, "hier_tree", width, height).RowHeight(HierarchyTableLayout.RowHeight);
        if (_treeBuilder == null || !ReferenceEquals(_treeBuilderPaper, paper) || !ReferenceEquals(_treeBuilderTheme, Origami.Current))
        {
            _treeBuilder?.Dispose();
            _treeBuilder = Origami.Tree(paper, "hier_tree", width, height);
            _treeBuilderPaper = paper; _treeBuilderTheme = Origami.Current;
        }
        return _treeBuilder.Size(width, height).RowHeight(HierarchyTableLayout.RowHeight);
    }
    // Rename state is managed by RenameOverlay

    private const float ToolbarHeight = EditorToolbar.HierarchyHeight;

    // Drag-drop state
    private bool _assetDropTarget;
    private enum DropPosition { Into, Above, Below }
    // Double-buffered drag hover state: deferred callbacks write to *Next,
    // promoted to current at frame start so layout reads the resolved value.
    private GameObject? _dragHoverTarget;
    private string? _dragHoverTargetId;
    private float _dragHoverNormalizedY;
    private GameObject? _dragHoverTargetNext;
    private string? _dragHoverTargetIdNext;
    private float _dragHoverNormalizedYNext;

    // Ping state which GOs in the hierarchy match the pinged GUID
    private static Guid _lastHierarchyPingGuid;
    private static readonly HashSet<GameObject> _pingedGameObjects = new();

    // IDs of nodes that need force-expanding (parents of pinged GOs)
    private readonly HashSet<string> _forceExpandedIds = new();
    // Filled by the tree each frame with each node's resolved expanded state, so drag-drop can turn a
    // "below" drop on an expanded node into a "first child" drop (where its first child visually sits).
    private readonly Dictionary<string, bool> _expandState = new();
    // Panel-owned source of truth for raw per-node expand flags (keyed by GameObject identifier).
    // Paper element storage is wiped for any element not drawn that frame (inactive dock tab,
    // collapsed scene section, hot reload), so the tree's expand state must live here instead.
    private readonly Dictionary<string, bool> _expandStore = new();
    // Identity hash of the scene the expand store belongs to; a scene switch clears stale entries.
    private int _expandStoreSceneId;
    // Set by RestoreState: the next scene to load adopts the restored expand entries instead of
    // clearing them (the runtime scene hash is never stable across sessions).
    private bool _adoptRestoredExpandStore;
    // Last Selection.ChangeVersion processed by reveal-on-selection.
    private long _lastSelectionVersion = long.MinValue;
    private readonly List<TreeNode> _treeNodes = new();
    private readonly List<TreeNode> _collapsedTreeNodes = new();
    private readonly List<object> _flatObjects = new();
    private readonly List<TreeNode> _treeNodePool = new();
    private readonly List<GameObject> _displayRoots = new();
    private readonly Dictionary<string, (IOrigamiIcon icon, Color color)> _goStyles = new();
    private HierarchyTreeKey _treeCacheKey;
    private bool _hasTreeCache;
    private bool _treeCacheIndexed;
    private HierarchyRowIndex? _rowIndex;
    private bool _rowIndexOverflow;
    internal long TreeModelBuilds { get; private set; }

    public override void OnGUI(Paper paper, float width, float height)
    {
        // Named like the SceneView/GameView scopes so live captures can attribute the Hierarchy
        // model rebuild + row build separately from the rest of UI.Base.
        using var profilerScope = EditorProfiler.BeginCpuScope("Hierarchy");
        _paper = paper;
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        if (!string.IsNullOrEmpty(Program.HierarchySearch) && _searchText.Length == 0)
            _searchText = Program.HierarchySearch;
        if (!ReferenceEquals(_parsedSearchText, _searchText))
        {
            _searchQuery = HierarchySearch.Parse(_searchText);
            _parsedSearchText = _searchText;
        }

        // Promote deferred hover state then clear the next slot. OnHover callbacks
        // fire every frame the mouse is over a node, so clearing is safe - the callback
        // will re-set it if still hovering. On the drop frame keep the current value.
        if (DragDrop.IsDropFrame)
        {
            // Drop frame: _dragHoverTarget already has the right value
        }
        else
        {
            _dragHoverTarget = _dragHoverTargetNext;
            _dragHoverTargetId = _dragHoverTargetIdNext;
            _dragHoverNormalizedY = _dragHoverNormalizedYNext;
            _dragHoverTargetNext = null;
            _dragHoverTargetIdNext = null;
            _dragHoverNormalizedYNext = 0f;
        }

        var scene = Scene.Current;

        // A scene switch invalidates every stored expand flag (identifiers belong to the old scene).
        int sceneIdentity = scene != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(scene) : 0;
        if (sceneIdentity != _expandStoreSceneId)
        {
            ClearTreeCache();
            _expandStoreSceneId = sceneIdentity;
            // Entries restored from layout state belong to the scene now loading for the first
            // time this session; identifiers are persistent Guids, so stale ones just never match.
            if (_adoptRestoredExpandStore && sceneIdentity != 0)
                _adoptRestoredExpandStore = false;
            else
                _expandStore.Clear();
        }

        using (paper.Column("hier_root")
            .Size(width, height)
            .OnClick(0, (_, _) => Selection.Clear())
            //.OnRightClick(0, (_, _) => Selection.Clear())
            .Enter())
        {
            // Prefab editing breadcrumb
            if (PrefabEditingMode.IsEditing)
            {
                using (paper.Row("hier_prefab_breadcrumb")
                    .Height(24)
                    .BackgroundColor(Color.FromArgb(40, EditorTheme.Purple400))
                    .Rounded(3).Margin(4, 4, 4, 0)
                    .ChildLeft(6).RowBetween(4)
                    .Enter())
                {
                    paper.Box("hier_prefab_back")
                        .Width(UnitValue.Auto).Height(24)
                        .Text($"{EditorIcons.ArrowLeft}  {Loc.Get("hierarchy.back")}", font)
                        .TextColor(EditorTheme.Purple400)
                        .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft)
                        .Hovered.TextColor(EditorTheme.Ink500).End()
                        .OnClick(0, (_, _) => PrefabEditingMode.Exit());

                    paper.Box("hier_prefab_sep_arrow")
                        .Width(UnitValue.Auto).Height(24)
                        .Text(EditorIcons.ChevronRight, font)
                        .TextColor(EditorTheme.Ink400)
                        .FontSize(8f).Alignment(TextAlignment.MiddleCenter);

                    string sceneName = PrefabEditingMode.OriginalSceneName ?? Loc.Get("panel.scene");
                    paper.Box("hier_prefab_scene")
                        .Width(UnitValue.Auto).Height(24)
                        .Text(sceneName, font)
                        .TextColor(EditorTheme.Ink400)
                        .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

                    paper.Box("hier_prefab_sep_arrow2")
                        .Width(UnitValue.Auto).Height(24)
                        .Text(EditorIcons.ChevronRight, font)
                        .TextColor(EditorTheme.Ink400)
                        .FontSize(8f).Alignment(TextAlignment.MiddleCenter);

                    string prefabName = PrefabEditingMode.EditingPrefabPath != null
                        ? System.IO.Path.GetFileNameWithoutExtension(PrefabEditingMode.EditingPrefabPath)
                        : Loc.Get("hierarchy.prefab_fallback");
                    paper.Box("hier_prefab_name")
                        .Width(UnitValue.Auto).Height(24)
                        .Text(prefabName, font)
                        .TextColor(EditorTheme.Purple400)
                        .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

                    paper.Box("hier_prefab_spacer");

                    Origami.Button(paper, "hier_prefab_save_exit", $"{EditorIcons.FloppyDisk}  {Loc.Get("hierarchy.save_and_exit")}", () => PrefabEditingMode.SaveAndExit()).Width(100).Show();
                }
            }

            // Toolbar
            DrawToolbar(paper, font, width);
            DrawColumnHeader(paper, font);

            if (scene == null)
            {
                EditorGUI.EmptyState(paper, "hier_empty", Loc.Get("hierarchy.no_scene_loaded"), font);
                Origami.Button(paper, "hier_create_scene", $"{EditorIcons.Plus}  {Loc.Get("hierarchy.new_scene")}", () => SceneViewPanel.CreateAndLoadDefaultScene()).Width(120).Show();
                return;
            }

            // Scene section header (collapsible, open by default)
            using (paper.Row("hier_scene_hdr")
                .Height(HierarchyTableLayout.RowHeight)
                .ChildLeft(HierarchyTableLayout.LeadingWidth + HierarchyTableLayout.SceneIndent)
                .BackgroundColor(Origami.Current.RowAlt)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .StopEventPropagation()
                .OnClick(0, (_, _) => _sceneExpanded = !_sceneExpanded)
                .Enter())
            {
                paper.Box("hier_scene_caret")
                    .Width(HierarchyTableLayout.CaretWidth).Height(HierarchyTableLayout.RowHeight).IsNotInteractable()
                    .Text(_sceneExpanded ? EditorIcons.ChevronDown : EditorIcons.ChevronRight, font)
                    .TextColor(EditorTheme.Ink300)
                    .FontSize(9f).Alignment(TextAlignment.MiddleCenter);

                paper.Box("hier_scene_icon")
                    .Width(HierarchyTableLayout.IconWidth).Height(HierarchyTableLayout.RowHeight).IsNotInteractable()
                    .Icon(paper, EditorIcons.Shapes_I, EditorTheme.Amber400, size: 14f);

                paper.Box("hier_scene_name_text")
                    .Width(UnitValue.StretchOne).Height(HierarchyTableLayout.RowHeight).ChildLeft(HierarchyTableLayout.NameInset)
                    .Text(scene.Name, font)
                    .TextColor(EditorTheme.Ink500)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft);
            }

            if (!_sceneExpanded)
            {
                // A collapsed scene still occupies a table viewport. Keep the empty-row
                // paint and release row bindings without rebuilding the scene model.
                GetTreeBuilder(paper, width, TreeViewportHeight(height))
                    .Nodes(_collapsedTreeNodes).RowIndex(null).ContentWidth(null)
                    .Padding(0).TopPadding(0).EmptyMessage(string.Empty)
                    .ZebraStripes(fillViewport: true).Show();
                return;
            }

            using (paper.Box("hier_bg").Enter())
            {
                // Background right-click create menu only
                BuildBackgroundContextMenu(paper);

                // Track if the background (hier_bg) is hovered for drop-on-empty-space
                bool bgHovered = paper.IsParentHovered;
                if (DragDrop.IsDragging && bgHovered)
                {
                    var mode = DragVisualMode.Rejected;
                    if (DragDrop.Payload is GameObjectDragPayload draggedObjects)
                    {
                        bool into = _dragHoverNormalizedY >= .25f && _dragHoverNormalizedY <= .75f
                            || _dragHoverTarget != null && _dragHoverTargetId != null && _dragHoverNormalizedY > .75f
                            && IsTargetExpanded(_dragHoverTarget, _dragHoverTargetId);
                        if (EditorDragFeedback.CanHierarchyDrop(draggedObjects, _dragHoverTarget, into)) mode = DragVisualMode.Link;
                    }
                    else if (DragDrop.Payload is AssetDragPayload asset)
                    {
                        mode = EditorDragFeedback.HierarchyAssetMode(asset.AssetType);
                    }
                    DragDrop.SetVisualMode(paper, mode);
                }

                // Hierarchy keyboard shortcuts
                if (bgHovered && !ShortcutManager.IsRebinding)
                {
                    if (ShortcutManager.IsPressed("Hierarchy/Delete"))
                    {
                        foreach (var go in ExcludeNestedSelections(Selection.GetSelected<GameObject>().ToList()))
                            DeleteGameObject(go);
                    }
                    else if (ShortcutManager.IsPressed("Hierarchy/Duplicate"))
                    {
                        var dupes = GameObjectClipboard.Duplicate(Selection.GetSelected<GameObject>().ToList());
                        foreach (var d in dupes) Undo.RegisterCreatedObject(d, "Duplicate");
                    }
                    else if (ShortcutManager.IsPressed("Hierarchy/Copy"))
                    {
                        GameObjectClipboard.Copy(Selection.GetSelected<GameObject>().ToList());
                    }
                    else if (ShortcutManager.IsPressed("Hierarchy/Cut"))
                    {
                        GameObjectClipboard.Cut(Selection.GetSelected<GameObject>().ToList());
                    }
                    else if (ShortcutManager.IsPressed("Hierarchy/PasteAsChild"))
                    {
                        PasteFromClipboard(Selection.GetSelected<GameObject>().FirstOrDefault());
                    }
                    else if (ShortcutManager.IsPressed("Hierarchy/Paste"))
                    {
                        // Unity: paste as sibling of the selection (root when nothing is selected).
                        PasteFromClipboard(ValidParentOf(Selection.GetSelected<GameObject>().FirstOrDefault()));
                    }
                    else if (ShortcutManager.IsPressed("Hierarchy/SelectAll"))
                    {
                        Selection.Clear();
                        foreach (var obj in _flatObjects)
                            Selection.AddToSelection(obj);
                    }
                    else if (ShortcutManager.IsPressed("Hierarchy/Rename"))
                    {
                        var first = Selection.GetSelected<GameObject>().FirstOrDefault();
                        if (first != null)
                            StartRenameGO(first, Selection.GetSelected<GameObject>());
                    }
                }

                // Handle ping search for the pinged GUID among GameObjects and their component AssetRefs.
                bool pingIsNew = false;
                if (Selection.PingedGuid != Guid.Empty && Selection.PingedGuid != _lastHierarchyPingGuid)
                {
                    _lastHierarchyPingGuid = Selection.PingedGuid;
                    _pingedGameObjects.Clear();
                    FindGameObjectsWithGuid(scene, Selection.PingedGuid, _pingedGameObjects);

                    // Collect parent IDs to force-expand so pinged GOs are visible
                    _forceExpandedIds.Clear();
                    foreach (var pinged in _pingedGameObjects)
                    {
                        var parent = pinged.Parent;
                        while (parent.IsValid())
                        {
                            _forceExpandedIds.Add(parent.Identifier.ToString());
                            parent = parent.Parent;
                        }
                    }
                    pingIsNew = _pingedGameObjects.Count > 0;
                }
                if (Selection.PingedGuid == Guid.Empty)
                {
                    _lastHierarchyPingGuid = Guid.Empty;
                    _pingedGameObjects.Clear();
                    _forceExpandedIds.Clear();
                }

                // Reveal-on-selection (Unity parity): when the selection changes, expand the ancestor
                // chain of every selected GameObject so its row is visible - newly created objects
                // (e.g. UI templates that end with Selection.Select) would otherwise sit hidden under
                // collapsed parents. Scroll only when a collapsed ancestor actually had to open, so a
                // plain click on an already-visible row never recenters the list.
                bool selectionRevealPending = false;
                if (Selection.ChangeVersion != _lastSelectionVersion)
                {
                    _lastSelectionVersion = Selection.ChangeVersion;
                    foreach (var selectedGo in Selection.GetSelected<GameObject>())
                    {
                        if (!selectedGo.IsValid() || selectedGo.Scene != scene) continue;
                        var ancestor = selectedGo.Parent;
                        while (ancestor.IsValid())
                        {
                            string ancestorId = ancestor.Identifier.ToString();
                            if (!_expandStore.TryGetValue(ancestorId, out bool wasExpanded) || !wasExpanded)
                            {
                                _expandStore[ancestorId] = true;
                                _rowIndex?.SetExpanded(ancestorId, true);
                                selectionRevealPending = true;
                            }
                            ancestor = ancestor.Parent;
                        }
                    }
                }

                float scrollHeight = TreeViewportHeight(height);
                var treeNodes = _treeNodes;
                var flatObjects = _flatObjects;
                // The whole-list tree reads selection while building rows, so its key must change
                // with the selection; the indexed tree (and the overflow fallback below, one frame
                // late) keys on topology only.
                var treeKey = ComputeTreeKey(_searchText, _sceneExpanded, _alphaSort, selectionDependent: !Program.IncrementalHierarchy || _rowIndexOverflow);
                if (!_hasTreeCache || _treeCacheIndexed != Program.IncrementalHierarchy || !treeKey.Equals(_treeCacheKey))
                {
                    var roots = GetDisplayRoots(scene);
                    RecycleTreeNodes();
                    for (int r = 0; r < roots.Count; r++)
                        BuildNodeList(roots[r], 0, treeNodes, flatObjects);
                    if (Program.IncrementalHierarchy)
                    {
                        // Beyond the index bound, degrade to the whole-list tree rather than throwing
                        // from OnGUI every frame (which stalls the whole editor render loop).
                        bool fits = treeNodes.Count <= HierarchyRowIndex.MaxNodes;
                        if (!fits && !_rowIndexOverflow)
                            Runtime.Debug.LogWarning($"Hierarchy: {treeNodes.Count:N0} rows exceed the indexed tree limit ({HierarchyRowIndex.MaxNodes:N0}); using the whole-list tree.");
                        _rowIndexOverflow = !fits;
                        _rowIndex = fits ? new HierarchyRowIndex(treeNodes, _expandStore, _expandState) : null;
                    }
                    else
                    {
                        _rowIndex = null;
                        _rowIndexOverflow = false;
                    }
                    _treeCacheKey = treeKey;
                    _treeCacheIndexed = Program.IncrementalHierarchy;
                    _hasTreeCache = true;
                    TreeModelBuilds++;
                }
                else
                    RefreshDropIndicators();

                if (_rowIndex != null && font != null)
                    _horizontalLayout.Update(paper.Canvas, font, EditorTheme.FontSizeSmall, treeNodes, TreeModelBuilds);

                // Scroll-to-ping: when a newly-pinged GO lives in the scene, center its row in the
                // scroll view so the yellow highlight is actually visible.
                if (pingIsNew)
                {
                    int pingIndex = -1;
                    if (_rowIndex != null)
                    {
                        foreach (var pinged in _pingedGameObjects)
                        {
                            int rank = _rowIndex.GetVisibleRank(pinged.Identifier.ToString());
                            if (rank >= 0 && (pingIndex < 0 || rank < pingIndex)) pingIndex = rank;
                        }
                    }
                    else for (int i = 0; i < treeNodes.Count; i++)
                    {
                        if (_pingedGameObjects.Contains((GameObject)treeNodes[i].UserData!)) { pingIndex = i; break; }
                    }
                    if (pingIndex >= 0)
                    {
                        float rowTotal = HierarchyTableLayout.RowHeight;
                        float targetY = pingIndex * rowTotal - (scrollHeight * 0.5f) + rowTotal * 0.5f;
                        float targetX = _rowIndex != null ? _horizontalLayout.RevealX(_rowIndex.GetNodeIndex(pingIndex), width) : 0;
                        Origami.ScrollTo("hier_tree_scroll", new Float2(targetX, targetY));
                    }
                }

                // Scroll the active selected row into view when reveal-on-selection just opened its
                // ancestor chain (same centering math as the ping scroll above).
                if (selectionRevealPending && Selection.GetActiveAs<GameObject>() is { } revealGo)
                {
                    int revealIndex = _rowIndex?.GetVisibleRank(revealGo.Identifier.ToString()) ?? -1;
                    if (_rowIndex == null) for (int i = 0; i < treeNodes.Count; i++)
                    {
                        if (ReferenceEquals(treeNodes[i].UserData, revealGo)) { revealIndex = i; break; }
                    }
                    if (revealIndex >= 0)
                    {
                        float rowTotal = HierarchyTableLayout.RowHeight;
                        float targetY = revealIndex * rowTotal - (scrollHeight * 0.5f) + rowTotal * 0.5f;
                        float targetX = _rowIndex != null ? _horizontalLayout.RevealX(_rowIndex.GetNodeIndex(revealIndex), width) : 0;
                        Origami.ScrollTo("hier_tree_scroll", new Float2(targetX, targetY));
                    }
                }

                // Tree view
                using (paper.Column("hier_paint_body").Size(width, scrollHeight).Enter())
                {
                bool selectionFocused = EditorSelectionPresentation.IsFocused(paper);
                AttachTreePaintReference(paper);
                GetTreeBuilder(paper, width, scrollHeight)
                    .RowHeight(HierarchyTableLayout.RowHeight)
                    .BaseIndent(HierarchyTableLayout.ObjectIndent)
                    .IndentSize(HierarchyTableLayout.IndentSize)
                    .Padding(0).TopPadding(0)
                    .LeadingRowContent(HierarchyTableLayout.LeadingWidth, DrawLeadingColumns)
                    .ContentWidth(_rowIndex != null && font != null ? _horizontalLayout.ContentWidth : null)
                    .Nodes(treeNodes)
                    .RowIndex(_rowIndex)
                    .MultiSelect()
                    .SelectionFocused(selectionFocused)
                    .Reorderable()
                    .ZebraStripes(fillViewport: true)
                    .IsSelected(n => Selection.IsSelected((GameObject)n.UserData!))
                    .OnSelectModified((e, ctrl, shift) =>
                    {
                        if (DragDrop.IsDragging || DragDrop.IsDropFrame) return;
                        var go = (GameObject)e.Node.UserData!;
                        Selection.HandleListClick(go, (IReadOnlyList<object>)flatObjects, e.Index, ctrl, shift);
                    })
                    .OnDoubleClick(e =>
                    {
                        // Focus the scene view camera on the double-clicked object
                        var go = (GameObject)e.Node.UserData!;
                        Selection.Select(go);
                        SceneViewPanel.FrameActiveSelection();
                    })
                    .OnRightClick(e =>
                    {
                        var go = (GameObject)e.Node.UserData!;
                        if (!Selection.IsSelected(go)) Selection.AddToSelection(go);
                    })
                    .OnDragStart(n =>
                    {
                        if (DragDrop.IsDragging) return;
                        var go = (GameObject)n.UserData!;
                        var selected = Selection.GetSelected<GameObject>().ToArray();
                        if (selected.Length > 0 && Selection.IsSelected(go))
                            DragDrop.StartDrag(new GameObjectDragPayload(selected));
                        else
                            DragDrop.StartDrag(new GameObjectDragPayload(go));
                    })
                    .OnHover((n, normY) =>
                    {
                        if (!DragDrop.IsDragging || DragDrop.Payload is not GameObjectDragPayload) return;
                        var go = (GameObject)n.UserData!;
                        _dragHoverTargetNext = go;
                        _dragHoverTargetIdNext = go.Identifier.ToString();
                        _dragHoverNormalizedYNext = normY;
                    })
                    .CustomRowContent((paper, node, isSel, isExp) =>
                    {
                        if (font == null) return;
                        var go = (GameObject)node.UserData!;
                        string goId = node.Id;

                        // Icon (vector, chosen from the GameObject's first component + coloured),
                        // or the user-assigned label/custom icon when one is set.
                        if (!_goStyles.TryGetValue(goId, out var style))
                            style = GetGoStyle(go);
                        var (goIcon, goColor) = style;
                        if (!go.EnabledInHierarchy) goColor = Color.FromArgb(120, goColor);
                        if (GUI.GameObjectIcons.HasIcon(go))
                            GUI.GameObjectIcons.Draw(paper, UiId.Cat("hier_lico_", goId), go, size: 12f,
                                slot: HierarchyTableLayout.IconWidth, height: HierarchyTableLayout.RowHeight);
                        else
                            paper.Box(UiId.Cat("hier_ico_", goId))
                                .Width(HierarchyTableLayout.IconWidth).Height(HierarchyTableLayout.RowHeight).IsNotInteractable()
                                .Icon(paper, goIcon, goColor, size: 14f);

                        // Name or rename field
                        if (RenameOverlay.IsRenaming(goId))
                        {
                            RenameOverlay.Draw(paper, UiId.Cat("hier_rename_", goId));
                        }
                        else
                        {
                            paper.Box(UiId.Cat("hier_name_", goId))
                                .Height(HierarchyTableLayout.RowHeight).ChildLeft(HierarchyTableLayout.NameInset)
                                .Text(go.Name, font)
                                .TextColor(isSel && go.EnabledInHierarchy
                                    ? Origami.Current.SelectionText(selectionFocused) : node.LabelColor ?? EditorTheme.Ink500)
                                .FontSize(EditorTheme.FontSizeSmall)
                                .Alignment(TextAlignment.MiddleLeft);
                        }

                        // Prefab instance roots get Unity's right-edge ">" arrow into prefab mode.
                        if (go.IsPrefabInstance && Prefabs.PrefabUtility.IsInstanceRoot(go)
                            && EditorAssetBackend.Instance?.GetEntry(go.PrefabAssetId) != null)
                        {
                            paper.Box(UiId.Cat("hier_pfopen_", goId))
                                .Width(HierarchyTableLayout.NavigationWidth).Height(HierarchyTableLayout.RowHeight)
                                .Text(EditorIcons.ChevronRight, font)
                                .TextColor(EditorTheme.Blue400)
                                .FontSize(9f).Alignment(TextAlignment.MiddleCenter)
                                .StopEventPropagation()
                                .OnClick(go, (g, _) => PrefabEditingMode.Enter(g.PrefabAssetId));
                        }

                        // Per-GameObject right-click menu
                        BuildGameObjectContextMenu(paper, UiId.Cat("hier_go_ctx_", goId));
                    })
                    .IsPinged(n => _pingedGameObjects.Contains((GameObject)n.UserData!) && Selection.PingedGuid != Guid.Empty)
                    .PingAlpha(() => Selection.GetPingAlpha())
                    .ExpandStateStore(_expandStore)
                    .ExpandStateSink(_expandState)
                    .EmptyMessage(Loc.Get("hierarchy.scene_empty"))
                    .Show();
                }

                HandleArrowNavigation(paper, treeNodes, flatObjects, bgHovered);

                // --- All drop handling uses hier_bg (the stable outer background) ---

                // Asset drops show visual indicator and spawn at root
                if (DragDrop.IsDraggingType<AssetDragPayload>() && bgHovered
                    && EditorDragFeedback.HierarchyAssetMode(((AssetDragPayload)DragDrop.Payload!).AssetType) != DragVisualMode.Rejected)
                {
                    _assetDropTarget = true;
                    EditorGUI.DropBanner(paper, "hier_drop_zone", Loc.Get("hierarchy.drop_to_spawn"));
                }
                else if (DragDrop.IsDraggingType<AssetDragPayload>())
                {
                    _assetDropTarget = false;
                }

                if (DragDrop.IsDropFrameFor(paper) && _assetDropTarget && DragDrop.Payload is AssetDragPayload assetDrop)
                {
                    if (assetDrop.AssetType == typeof(Runtime.Resources.Scene))
                    {
                        var entry = EditorAssetBackend.Instance?.GetEntry(assetDrop.AssetGuid);
                        if (entry != null)
                            EditorSceneManager.OpenScene(entry.Path);
                    }
                    else
                    {
                        SpawnAssetInScene(assetDrop, null, Float3.Zero);
                    }
                    DragDrop.EndDrag();
                    _assetDropTarget = false;
                }

                // GO drop process using hover target tracked by OnHover callback
                // Only process drops that land inside the hierarchy panel
                if (DragDrop.IsDropFrameFor(paper) && bgHovered && DragDrop.Payload is GameObjectDragPayload goDrop)
                {
                    if (_dragHoverTarget != null && _dragHoverTargetId != null)
                    {
                        // Dropped on a GO row use normalized Y to determine Above/Into/Below
                        DropPosition dropPos;
                        if (_dragHoverNormalizedY < 0.25f)
                            dropPos = DropPosition.Above;
                        else if (_dragHoverNormalizedY > 0.75f)
                            dropPos = DropPosition.Below;
                        else
                            dropPos = DropPosition.Into;

                        // The gap under an expanded node is visually its first-child slot, so drop
                        // there as child index 0 instead of as a sibling after the node.
                        if (dropPos == DropPosition.Below && IsTargetExpanded(_dragHoverTarget, _dragHoverTargetId))
                            ProcessGODrop(goDrop, _dragHoverTarget, _dragHoverTargetId, DropPosition.Into, 0);
                        else
                            ProcessGODrop(goDrop, _dragHoverTarget, _dragHoverTargetId, dropPos);
                    }
                    else if (bgHovered)
                    {
                        // Dropped on empty background unparent to root
                        foreach (var dragged in goDrop.GameObjects)
                        {
                            if (dragged.Parent != null && dragged.Parent.IsValid())
                            {
                                var oldParentId = dragged.Parent.Identifier;
                                var oldSibIdx = dragged.GetSiblingIndex() ?? -1;
                                var dId = dragged.Identifier;
                                Undo.RegisterAction("Unparent",
                                    undo: () => { var s = Scene.Current; if (s == null) return; var d = FindGOById(s, dId); var p = FindGOById(s, oldParentId); if (d != null && p != null) { d.SetParent(p); if (oldSibIdx >= 0) d.SetSiblingIndex(oldSibIdx); } },
                                    redo: () => { var s = Scene.Current; if (s == null) return; var d = FindGOById(s, dId); if (d != null) d.SetParent(default); });
                                dragged.SetParent(default);
                            }
                        }
                        EditorSceneManager.MarkDirty();
                        DragDrop.EndDrag();
                    }

                    _dragHoverTarget = null;
                    _dragHoverTargetId = null;
                }
            }
        }
    }

    private void HandleArrowNavigation(Paper paper, List<TreeNode> nodes, List<object> flat, bool hovered)
    {
        if (!hovered || ShortcutManager.IsRebinding || nodes.Count == 0) return;
        var activeGo = Selection.GetActiveAs<GameObject>();
        if (activeGo != null && RenameOverlay.IsRenaming(activeGo.Identifier.ToString()))
            return;

        bool up = paper.IsKeyPressedOrRepeating(PaperKey.Up);
        bool down = paper.IsKeyPressedOrRepeating(PaperKey.Down);
        bool left = paper.IsKeyPressed(PaperKey.Left);
        bool right = paper.IsKeyPressed(PaperKey.Right);
        if (!up && !down && !left && !right) return;

        bool shift = paper.IsKeyDown(PaperKey.LeftShift) || paper.IsKeyDown(PaperKey.RightShift);
        if (_rowIndex != null)
        {
            HandleIndexedArrowNavigation(nodes, flat, activeGo, up, down, left, shift);
            return;
        }
        HierarchyKeyboard.CollectVisibleIndices(nodes, _expandState, _visibleIndices);
        if (_visibleIndices.Count == 0) return;

        int vis = HierarchyKeyboard.IndexOfVisible(_visibleIndices, flat, Selection.ActiveObject);
        if (up || down)
        {
            int next = HierarchyKeyboard.StepVisible(vis, down ? 1 : -1, _visibleIndices.Count);
            if (next < 0) return;
            int flatIndex = _visibleIndices[next];
            Selection.HandleListClick(flat[flatIndex], flat, flatIndex, ctrl: false, shift);
            return;
        }

        if (vis < 0) return;
        int currentFlat = _visibleIndices[vis];
        TreeNode node = nodes[currentFlat];
        bool expanded = node.HasChildren && (!_expandState.TryGetValue(node.Id, out bool exp) || exp);
        int parentVis = HierarchyKeyboard.FindParentVisibleIndex(_visibleIndices, nodes, vis);
        var action = HierarchyKeyboard.ResolveFold(left, node.HasChildren, expanded, parentVis >= 0);
        switch (action)
        {
            case HierarchyFoldAction.Collapse:
                _expandStore[node.Id] = false;
                _rowIndex?.SetExpanded(node.Id, false);
                break;
            case HierarchyFoldAction.Expand:
                _expandStore[node.Id] = true;
                _rowIndex?.SetExpanded(node.Id, true);
                break;
            case HierarchyFoldAction.SelectParent:
                Selection.HandleListClick(flat[_visibleIndices[parentVis]], flat, _visibleIndices[parentVis], false, false);
                break;
            case HierarchyFoldAction.SelectFirstChild:
                int childVis = vis + 1;
                if (childVis < _visibleIndices.Count && nodes[_visibleIndices[childVis]].Depth > node.Depth)
                    Selection.HandleListClick(flat[_visibleIndices[childVis]], flat, _visibleIndices[childVis], false, false);
                break;
        }
    }

    private void HandleIndexedArrowNavigation(List<TreeNode> nodes, List<object> flat,
        GameObject? active, bool up, bool down, bool left, bool shift)
    {
        var index = _rowIndex!;
        int rank = active == null ? -1 : index.GetVisibleRank(active.Identifier.ToString());
        if (up || down)
        {
            int next = HierarchyKeyboard.StepVisible(rank, down ? 1 : -1, index.Count);
            if (next < 0) return;
            int slot = index.GetNodeIndex(next);
            Selection.HandleListClick(flat[slot], flat, slot, ctrl: false, shift);
            return;
        }
        if (rank < 0) return;
        int current = index.GetNodeIndex(rank), parent = index.GetParentIndex(current);
        var node = nodes[current];
        var action = HierarchyKeyboard.ResolveFold(left, node.HasChildren, index.IsExpanded(current), parent >= 0);
        switch (action)
        {
            case HierarchyFoldAction.Collapse:
            case HierarchyFoldAction.Expand:
                bool expanded = action == HierarchyFoldAction.Expand;
                _expandStore[node.Id] = expanded; index.SetExpanded(node.Id, expanded);
                break;
            case HierarchyFoldAction.SelectParent:
                Selection.HandleListClick(flat[parent], flat, parent, false, false);
                break;
            case HierarchyFoldAction.SelectFirstChild:
                if (rank + 1 >= index.Count) break;
                int child = index.GetNodeIndex(rank + 1);
                if (nodes[child].Depth > node.Depth)
                    Selection.HandleListClick(flat[child], flat, child, false, false);
                break;
        }
    }

    private static float TreeViewportHeight(float height)
    {
        float usedHeight = ToolbarHeight + HierarchyTableLayout.HeaderHeight + HierarchyTableLayout.RowHeight;
        if (PrefabEditingMode.IsEditing) usedHeight += 28;
        return MathF.Max(0, height - usedHeight);
    }

    private static void DrawColumnHeader(Paper paper, Scribe.FontFile font)
    {
        using (paper.Row("hier_columns").Height(HierarchyTableLayout.HeaderHeight)
            .BackgroundColor(Origami.Current.RowAlt).StopEventPropagation().Clip().Enter())
        {
            paper.Box("hier_column_visibility").Size(HierarchyTableLayout.ColumnWidth, HierarchyTableLayout.HeaderHeight)
                .Text(EditorIcons.Eye, font).TextColor(EditorTheme.Ink300).FontSize(10)
                .Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
            paper.Box("hier_column_picking").Size(HierarchyTableLayout.ColumnWidth, HierarchyTableLayout.HeaderHeight)
                .Text(EditorIcons.Hand, font).TextColor(EditorTheme.Ink300).FontSize(10)
                .Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
            paper.Box("hier_column_name").Height(HierarchyTableLayout.HeaderHeight).ChildLeft(4)
                .Text(Loc.Get("inspector.name"), font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
        }
    }

    private static void DrawLeadingColumns(Paper paper, TreeNode node, bool selected, bool expanded, bool hovered)
    {
        var go = (GameObject)node.UserData!;
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        bool hidden = SceneVisibilityManager.IsHidden(go);
        bool visible = SceneVisibilityManager.IsVisibleInHierarchy(go);
        bool pickable = SceneVisibilityManager.IsPickingEnabled(go);
        bool show = selected || hovered;
        paper.Box(UiId.Cat("hier_vis_", node.Id))
            .Size(HierarchyTableLayout.ColumnWidth, HierarchyTableLayout.RowHeight)
            .Text(hidden ? EditorIcons.EyeSlash : EditorIcons.Eye, font)
            .TextColor(hidden || !visible ? EditorTheme.Ink300 : show ? EditorTheme.Ink400 : Color.Transparent)
            .Hovered.TextColor(EditorTheme.Ink500).End()
            .FontSize(9f).Alignment(TextAlignment.MiddleCenter)
            .Tooltip(Loc.Get(hidden ? "hierarchy.show_in_scene" : "hierarchy.hide_in_scene"))
            .StopEventPropagation().OnClick(go, static (g, _) => ToggleSceneVisibility(g));
        paper.Box(UiId.Cat("hier_pick_", node.Id))
            .Size(HierarchyTableLayout.ColumnWidth, HierarchyTableLayout.RowHeight)
            .Text(pickable ? EditorIcons.Hand : EditorIcons.Ban, font)
            .TextColor(!pickable ? EditorTheme.Ink300 : show ? EditorTheme.Ink400 : Color.Transparent)
            .Hovered.TextColor(EditorTheme.Ink500).End()
            .FontSize(9f).Alignment(TextAlignment.MiddleCenter)
            .Tooltip(Loc.Get(pickable ? "hierarchy.disable_picking" : "hierarchy.enable_picking"))
            .StopEventPropagation().OnClick(go, static (g, _) => ToggleScenePicking(g));
    }

    private static void ToggleSceneVisibility(GameObject go)
    {
        if (!go.IsValid()) return;
        bool next = !SceneVisibilityManager.IsHidden(go);
        Guid id = go.Identifier;
        Undo.RegisterAction("Toggle Scene Visibility",
            undo: () => { var g = Undo.FindGO(id); if (g != null) SceneVisibilityManager.SetHidden(g, !next); },
            redo: () => { var g = Undo.FindGO(id); if (g != null) SceneVisibilityManager.SetHidden(g, next); });
        SceneVisibilityManager.SetHidden(go, next);
    }

    private static void ToggleScenePicking(GameObject go)
    {
        if (!go.IsValid()) return;
        bool next = !SceneVisibilityManager.IsPickingEnabled(go);
        Guid id = go.Identifier;
        Undo.RegisterAction("Toggle Scene Picking",
            undo: () => { var g = Undo.FindGO(id); if (g != null) SceneVisibilityManager.SetPickingEnabled(g, !next); },
            redo: () => { var g = Undo.FindGO(id); if (g != null) SceneVisibilityManager.SetPickingEnabled(g, next); });
        SceneVisibilityManager.SetPickingEnabled(go, next);
    }

    private void DrawToolbar(Paper paper, Scribe.FontFile font, float width)
    {
        using (paper.Row("hier_toolbar")
            .Height(ToolbarHeight)
            .StopEventPropagation()
            .ChildLeft(1).ChildRight(1)
            .Enter())
        {
            // Unity's "+" create dropdown, then search, then an A→Z sort toggle.
            EditorToolbar.IconButton(paper, "hier_create_btn", EditorIcons.Plus_I, () =>
                    Origami.ContextMenuAtPointer(b => BuildCreateMenu(b, null)),
                    height: EditorToolbar.HierarchyButtonHeight).Show();

            using (paper.Row("hier_search_wrap")
                .Width(UnitValue.StretchOne).Height(EditorToolbar.HierarchySearchHeight)
                .Margin(0, 2, UnitValue.StretchOne, UnitValue.StretchOne)
                .Enter())
                EditorToolbar.Search(paper, "hier_search", _searchText, v => _searchText = v,
                    UnitValue.StretchOne, height: EditorToolbar.HierarchySearchHeight, fontSize: 12).Show();

            EditorToolbar.IconButton(paper, "hier_sort_btn", EditorIcons.ArrowDownAZ_I,
                () => _alphaSort = !_alphaSort, active: _alphaSort,
                height: EditorToolbar.HierarchyButtonHeight).Show();
        }
    }

    private void BuildNodeList(GameObject go, int depth, List<TreeNode> nodes, List<object> flatObjects)
    {
        if (go.HideFlags.HasFlag(HideFlags.Hide) || go.HideFlags.HasFlag(HideFlags.HideAndDontSave))
            return;

        if (!_searchQuery.IsEmpty && !HierarchySearch.MatchesSelfOrDescendant(go, _searchQuery))
            return;

        string goId = go.Identifier.ToString();
        bool hasVisibleChildren = HasVisibleHierarchyChildren(go);

        // Determine drop indicator for this node
        TreeDropPosition? dropInd = null;
        if (_dragHoverTargetId == goId && CanShowHierarchyDrop())
        {
            if (_dragHoverNormalizedY < 0.25f) dropInd = TreeDropPosition.Above;
            else if (_dragHoverNormalizedY > 0.75f) dropInd = IsTargetExpanded(go, goId) ? TreeDropPosition.IntoFirst : TreeDropPosition.Below;
            else dropInd = TreeDropPosition.Into;
        }

        var node = RentTreeNode();
        node.Id = goId;
        node.Label = go.Name;
        node.Icon = GetGameObjectIcon(go);
        node.IconColor = go.EnabledInHierarchy ? null : EditorTheme.Ink300;
        node.LabelColor = GetPrefabTextColor(go);
        node.HasChildren = hasVisibleChildren;
        node.Depth = depth;
        node.UserData = go;
        node.TrailingIcon = SceneVisibilityManager.IsHidden(go) ? EditorIcons.EyeSlash : EditorIcons.Eye;
        node.TrailingIconColor = SceneVisibilityManager.IsHidden(go) ? EditorTheme.Ink300 : EditorTheme.Ink400;
        node.DropIndicator = dropInd;
        node.OverrideExpanded = null;
        _goStyles[goId] = GetGoStyle(go);

        // Force expand parents of pinged nodes and search ancestors. (Keyboard folds write the
        // panel-owned expand store directly - no per-node override needed.)
        if (_forceExpandedIds.Contains(goId))
            node.OverrideExpanded = true;
        else if (!_searchQuery.IsEmpty && !HierarchySearch.Matches(go, _searchQuery)
                 && HierarchySearch.MatchesSelfOrDescendant(go, _searchQuery))
            node.OverrideExpanded = true;

        // A pending cut dims the row until its moving paste lands (Unity grays cut objects).
        if (GameObjectClipboard.IsCut(go.Identifier))
            node.LabelColor = EditorTheme.Ink300;

        nodes.Add(node);
        flatObjects.Add(go);

        // Recurse children (the tree widget handles skipping collapsed children internally)
        if (_alphaSort)
        {
            var sortedKids = new List<GameObject>(go.Children);
            sortedKids.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var child in sortedKids)
                BuildNodeList(child, depth + 1, nodes, flatObjects);
        }
        else
            foreach (var child in go.Children)
                BuildNodeList(child, depth + 1, nodes, flatObjects);
    }

    // ================================================================
    //  Drag & Drop for reparenting/reordering
    // ================================================================

    // True when the target node is expanded and actually has children (its first-child slot is visible).
    private static GameObject? ValidParentOf(GameObject? go)
        => go?.Parent is { } p && p.IsValid() ? p : null;

    /// <summary>
    /// Paste into <paramref name="parent"/> (null = scene root). A pending Cut MOVES the original
    /// objects (with drag-drop's prefab guards and undo shape); otherwise the serialized clipboard
    /// payload is instantiated.
    /// </summary>
    public static void PasteFromClipboard(GameObject? parent)
    {
        if (GameObjectClipboard.HasCutObjects)
        {
            var scene = Scene.Current;
            if (scene == null) return;
            var moved = new List<GameObject>();
            foreach (var id in GameObjectClipboard.TakeCutIds())
            {
                var go = FindGOById(scene, id);
                if (go == null || go == parent) continue;
                if (parent != null && IsDescendantOf(parent, go)) continue;
                if (MoveGameObjectTo(go, parent)) moved.Add(go);
            }
            if (moved.Count > 0)
            {
                Selection.Clear();
                foreach (var m in moved) Selection.AddToSelection(m);
                EditorSceneManager.MarkDirty();
            }
            return;
        }

        var pasted = GameObjectClipboard.Paste(parent);
        foreach (var p in pasted) Undo.RegisterCreatedObject(p, "Paste");
    }

    /// <summary>Cut-paste reparent: same prefab-structure guards and undo record as drag-drop.</summary>
    private static bool MoveGameObjectTo(GameObject go, GameObject? newParent)
    {
        if (go.Parent != null && go.Parent.IsPrefabInstance && go.Parent.PrefabChildCount >= 0)
        {
            int idx = go.Parent.Children.IndexOf(go);
            if (idx >= 0 && idx < go.Parent.PrefabChildCount)
            {
                Toasts.Show(Loc.Get("toast.prefab_structure"), Loc.Get("toast.prefab_cant_move"), ToastType.Warning, 3f);
                return false;
            }
        }
        if (newParent != null && newParent.IsPrefabInstance && newParent.PrefabChildCount >= 0)
        {
            Toasts.Show(Loc.Get("toast.prefab_structure"), Loc.Get("toast.prefab_cant_add"), ToastType.Warning, 3f);
            return false;
        }

        var scene = Scene.Current;
        if (scene == null) return false;
        var goId = go.Identifier;
        var oldParentId = go.Parent?.Identifier ?? Guid.Empty;
        var oldSiblingIdx = go.GetSiblingIndex() ?? -1;
        var oldRootIdx = oldParentId == Guid.Empty ? scene.GetRootIndex(go) : -1;

        if (newParent == null) go.SetParent(default);
        else go.SetParent(newParent);

        var newParentId = newParent?.Identifier ?? Guid.Empty;
        Undo.RegisterAction("Move",
            undo: () =>
            {
                var s = Scene.Current;
                if (s == null) return;
                var d = FindGOById(s, goId);
                if (d == null) return;
                if (oldParentId == Guid.Empty)
                {
                    d.SetParent(default);
                    if (oldRootIdx >= 0) s.SetRootIndex(d, oldRootIdx);
                }
                else
                {
                    var p = FindGOById(s, oldParentId);
                    if (p != null) { d.SetParent(p); if (oldSiblingIdx >= 0) d.SetSiblingIndex(oldSiblingIdx); }
                }
            },
            redo: () =>
            {
                var s = Scene.Current;
                if (s == null) return;
                var d = FindGOById(s, goId);
                if (d == null) return;
                if (newParentId == Guid.Empty) d.SetParent(default);
                else
                {
                    var p = FindGOById(s, newParentId);
                    if (p != null) d.SetParent(p);
                }
            });
        return true;
    }

    private bool IsTargetExpanded(GameObject target, string targetId)
        => target.Children.Count > 0 && _expandState.TryGetValue(targetId, out var e) && e;

    private void ProcessGODrop(GameObjectDragPayload goDrop, GameObject target, string targetId, DropPosition dropPos, int insertIndex = -1)
    {
        var targetParent = target.Parent;
        bool targetIsRoot = targetParent == null || !targetParent.IsValid();

        // A descendant dragged alongside its own ancestor moves implicitly with it; reparenting it
        // again here would yank it out from under the ancestor and flatten it as a sibling instead.
        foreach (var dragged in ExcludeNestedSelections(goDrop.GameObjects))
        {
            if (dragged == target || IsDescendantOf(target, dragged))
                continue;

            // Block moving a prefab child out of its parent
            if (dragged.Parent != null && dragged.Parent.IsPrefabInstance && dragged.Parent.PrefabChildCount >= 0)
            {
                int dragChildIdx = dragged.Parent.Children.IndexOf(dragged);
                if (dragChildIdx >= 0 && dragChildIdx < dragged.Parent.PrefabChildCount)
                {
                    Toasts.Show(Loc.Get("toast.prefab_structure"), Loc.Get("toast.prefab_cant_move"), ToastType.Warning, 3f);
                    continue;
                }
            }

            // Capture state for undo (BEFORE the move)
            var oldParentId = dragged.Parent?.Identifier ?? Guid.Empty;
            var oldSiblingIdx = dragged.GetSiblingIndex() ?? -1;
            var oldRootIdx = oldParentId == Guid.Empty ? (Scene.Current?.GetRootIndex(dragged) ?? -1) : -1;
            var draggedId = dragged.Identifier;

            switch (dropPos)
            {
                case DropPosition.Into:
                    if (target.IsPrefabInstance && target.PrefabChildCount >= 0)
                    {
                        Toasts.Show(Loc.Get("toast.prefab_structure"), Loc.Get("toast.prefab_cant_add"), ToastType.Warning, 3f);
                        continue;
                    }
                    dragged.SetParent(target);
                    if (insertIndex >= 0)
                        dragged.SetSiblingIndex(insertIndex);
                    break;

                case DropPosition.Above:
                case DropPosition.Below:
                    if (targetIsRoot)
                    {
                        var scene = Scene.Current;
                        if (scene == null) break;

                        // Unparent if needed
                        if (dragged.Parent != null && dragged.Parent.IsValid())
                            dragged.SetParent(default);

                        // Root reorder via Scene list
                        int targetRootIdx = scene.GetRootIndex(target);
                        if (dropPos == DropPosition.Below) targetRootIdx++;
                        int dragRootIdx = scene.GetRootIndex(dragged);
                        if (dragRootIdx >= 0 && dragRootIdx < targetRootIdx) targetRootIdx--;
                        scene.SetRootIndex(dragged, Math.Max(0, targetRootIdx));
                    }
                    else
                    {
                        // Child reorder reparent to target's parent, then set sibling index
                        if (dragged.Parent != targetParent)
                            dragged.SetParent(targetParent!);

                        int targetIdx = target.GetSiblingIndex() ?? 0;
                        if (dropPos == DropPosition.Below) targetIdx++;
                        int dragIdx = dragged.GetSiblingIndex() ?? 0;
                        if (dragIdx < targetIdx) targetIdx--;
                        dragged.SetSiblingIndex(Math.Max(0, targetIdx));
                    }
                    break;
            }

            // Register undo for reparent/reorder
            var newParentId = dragged.Parent?.Identifier ?? Guid.Empty;
            var newSiblingIdx = dragged.GetSiblingIndex() ?? -1;
            var newRootIdx = newParentId == Guid.Empty ? (Scene.Current?.GetRootIndex(dragged) ?? -1) : -1;

            bool changed = oldParentId != newParentId || oldSiblingIdx != newSiblingIdx
                || (oldParentId == Guid.Empty && newParentId == Guid.Empty && oldRootIdx != newRootIdx);

            if (changed)
            {
                Undo.RegisterAction("Reparent",
                    undo: () =>
                    {
                        var scene = Scene.Current;
                        if (scene == null) return;
                        var d = FindGOById(scene, draggedId);
                        if (d == null) return;
                        if (oldParentId == Guid.Empty)
                        {
                            d.SetParent(default);
                            if (oldRootIdx >= 0) scene.SetRootIndex(d, oldRootIdx);
                        }
                        else
                        {
                            var p = FindGOById(scene, oldParentId);
                            if (p != null) { d.SetParent(p); if (oldSiblingIdx >= 0) d.SetSiblingIndex(oldSiblingIdx); }
                        }
                    },
                    redo: () =>
                    {
                        var scene = Scene.Current;
                        if (scene == null) return;
                        var d = FindGOById(scene, draggedId);
                        if (d == null) return;
                        if (newParentId == Guid.Empty)
                        {
                            d.SetParent(default);
                            if (newRootIdx >= 0) scene.SetRootIndex(d, newRootIdx);
                        }
                        else
                        {
                            var p = FindGOById(scene, newParentId);
                            if (p != null) { d.SetParent(p); if (newSiblingIdx >= 0) d.SetSiblingIndex(newSiblingIdx); }
                        }
                    });
            }
        }

        EditorSceneManager.MarkDirty();
        DragDrop.EndDrag();
    }

    private static bool IsDescendantOf(GameObject potentialChild, GameObject potentialParent)
    {
        var current = potentialChild.Parent;
        while (current != null && current.IsValid())
        {
            if (current == potentialParent) return true;
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Filters out any GameObject whose ancestor is also present in the same collection. A
    /// per-item operation (delete, reparent) applied to an ancestor already cascades to its
    /// descendants structurally, so re-applying it to a separately-selected descendant corrupts
    /// undo (double-serializes it) or flattens it out of its parent during a reparent.
    /// </summary>
    internal static List<GameObject> ExcludeNestedSelections(IReadOnlyCollection<GameObject> selection)
    {
        var result = new List<GameObject>(selection.Count);
        foreach (var go in selection)
        {
            bool hasSelectedAncestor = false;
            foreach (var other in selection)
            {
                if (!ReferenceEquals(other, go) && IsDescendantOf(go, other))
                {
                    hasSelectedAncestor = true;
                    break;
                }
            }
            if (!hasSelectedAncestor)
                result.Add(go);
        }
        return result;
    }

    // ================================================================
    //  Context Menus
    // ================================================================

    private void BuildBackgroundContextMenu(Paper paper)
    {
        Origami.RightClickMenu(paper, "hier_bg_ctx", builder =>
        {
            builder.Header("Create");
            BuildCreateMenu(builder, null);
        });
    }

    private void BuildGameObjectContextMenu(Paper paper, string id)
    {
        Origami.RightClickMenu(paper, id, builder =>
        {
            var selectedGOs = Selection.GetSelected<GameObject>().ToList();
            var firstSelected = selectedGOs.FirstOrDefault();
            if (selectedGOs.Count == 0) return;

            bool multiSelect = selectedGOs.Count > 1;

            builder.Title(multiSelect ? Loc.Get("project.item_count", new { count = Selection.Count }) : firstSelected.Name, iconDraw: GetGoStyle(firstSelected).icon);

            // Create parent to first selected
            builder.Submenu("Create", (b) => { BuildCreateMenu(b, firstSelected); }, EditorIcons.Plus);

            builder.Separator();

            // Clipboard group (Unity order: Cut / Copy / Paste / Paste As Child).
            string countSuffix = multiSelect ? $" ({selectedGOs.Count})" : "";
            builder.Item($"Cut{countSuffix}", () => GameObjectClipboard.Cut(selectedGOs), icon: EditorIcons.Scissors);
            builder.Item($"Copy{countSuffix}", () => GameObjectClipboard.Copy(selectedGOs), icon: EditorIcons.Copy);
            builder.Item("Paste", () => PasteFromClipboard(ValidParentOf(firstSelected)),
                enabled: GameObjectClipboard.CanPaste, icon: EditorIcons.Paste);
            builder.Item("Paste As Child", () => PasteFromClipboard(firstSelected),
                enabled: GameObjectClipboard.CanPaste, icon: EditorIcons.Paste);

            bool anyChildren = selectedGOs.Any(g => g.Children.Count > 0);
            builder.Item("Select Children", () =>
            {
                var kids = new List<GameObject>();
                foreach (var s in selectedGOs)
                    foreach (var c in s.Children)
                        kids.Add(c);
                if (kids.Count == 0) return;
                Selection.Clear();
                foreach (var k in kids) Selection.AddToSelection(k);
            }, enabled: anyChildren, icon: EditorIcons.ObjectGroup);

            builder.Separator();

            if (multiSelect)
            {
                builder.Item($"{Loc.Get("hierarchy.duplicate")} ({selectedGOs.Count})", () =>
                {
                    var dupes = GameObjectClipboard.Duplicate(selectedGOs);
                    foreach (var d in dupes) Undo.RegisterCreatedObject(d, "Duplicate");
                }, icon: EditorIcons.Copy);

                builder.Item($"{Loc.Get("hierarchy.rename")} ({selectedGOs.Count})", () =>
                {
                    StartRenameGO(firstSelected!, selectedGOs);
                }, icon: EditorIcons.PenToSquare);

                builder.Item($"{Loc.Get("hierarchy.delete")} ({selectedGOs.Count})", () =>
                {
                    foreach (var go in ExcludeNestedSelections(selectedGOs)) DeleteGameObject(go);
                }, icon: EditorIcons.Trash);

                builder.Separator();

                bool anyEnabled = selectedGOs.Any(g => g.Enabled);
                builder.Item(anyEnabled ? Loc.Get("hierarchy.disable_all") : Loc.Get("hierarchy.enable_all"), () =>
                {
                    bool newState = !anyEnabled;
                    var oldStates = selectedGOs.Select(g => (g.Identifier, g.Enabled)).ToList();
                    Undo.RegisterAction(newState ? "Enable All" : "Disable All",
                        undo: () => { foreach (var (id, old) in oldStates) { var r = Undo.FindGO(id); if (r != null) r.Enabled = old; } },
                        redo: () => { foreach (var (id, _) in oldStates) { var r = Undo.FindGO(id); if (r != null) r.Enabled = newState; } });
                    foreach (var go in selectedGOs) go.Enabled = newState;
                }, icon: anyEnabled ? EditorIcons.EyeSlash : EditorIcons.Eye);
            }
            else
            {
                var go = firstSelected!;
                builder.Item(Loc.Get("hierarchy.duplicate"), () =>
                {
                    var dupes = GameObjectClipboard.Duplicate([go]);
                    foreach (var d in dupes) Undo.RegisterCreatedObject(d, "Duplicate");
                }, icon: EditorIcons.Copy);
                builder.Item(Loc.Get("hierarchy.rename"), () =>
                {
                    StartRenameGO(go, [go]);
                }, icon: EditorIcons.PenToSquare);
                builder.Item(Loc.Get("hierarchy.delete"), () => DeleteGameObject(go), icon: EditorIcons.Trash);
                builder.Separator();
                builder.Item(go.Enabled ? Loc.Get("hierarchy.disable") : Loc.Get("hierarchy.enable"), () =>
                {
                    Undo.RecordGameObjectChange(go, "Toggle Enabled", go.Enabled, !go.Enabled, (g, e) => g.Enabled = e);
                    go.Enabled = !go.Enabled;
                }, icon: go.Enabled ? EditorIcons.EyeSlash : EditorIcons.Eye);

                bool hidden = SceneVisibilityManager.IsHidden(go);
                builder.Item(hidden ? Loc.Get("hierarchy.show_in_scene") : Loc.Get("hierarchy.hide_in_scene"), () =>
                {
                    SceneVisibilityManager.SetHidden(go, !hidden);
                }, icon: hidden ? EditorIcons.Eye : EditorIcons.EyeSlash);

                bool pickable = SceneVisibilityManager.IsPickingEnabled(go);
                builder.Item(pickable ? Loc.Get("hierarchy.disable_picking") : Loc.Get("hierarchy.enable_picking"), () =>
                {
                    SceneVisibilityManager.SetPickingEnabled(go, !pickable);
                }, icon: pickable ? EditorIcons.Ban : EditorIcons.Hand);
            }

            builder.Separator();

            // Focused Properties window (Unity 6 Properties / Alt+P).
            builder.Item(Loc.Get("context.properties"), () =>
            {
                Panels.PropertiesPanel.Open(firstSelected);
            }, icon: EditorIcons.Sliders);

            // Move to View / Move View To
            var cam = SceneViewPanel.ActiveCamera;
            if (cam != null)
            {
                builder.Item(Loc.Get("hierarchy.move_to_view"), () =>
                {
                    foreach (var go in selectedGOs)
                    {
                        go.Transform.Position = cam.Position;
                        go.Transform.LocalEulerAngles = new Float3(cam.Pitch, cam.Yaw, 0);
                    }
                    EditorSceneManager.MarkDirty();
                }, icon: EditorIcons.ArrowRight);

                builder.Item(Loc.Get("hierarchy.move_view_to"), () =>
                {
                    cam.SetPosition(firstSelected!.Transform.Position);
                    cam.SetOrientation((float)firstSelected!.Transform.LocalEulerAngles.Y, (float)firstSelected!.Transform.LocalEulerAngles.X);
                }, icon: EditorIcons.Eye);

                builder.Separator();
            }

            // Prefab operations
            if (!multiSelect && firstSelected!.IsPrefabInstance)
            {
                builder.Item(Loc.Get("hierarchy.select_prefab_asset"), () =>
                {
                    Selection.Ping(firstSelected.PrefabAssetId);
                }, icon: EditorIcons.Cubes);

                bool hasOverrides = Prefabs.PrefabUtility.HasAnyOverrides(firstSelected);

                builder.Item(Loc.Get("hierarchy.apply_prefab_overrides"), () =>
                {
                    var root = Prefabs.PrefabUtility.GetPrefabInstanceRoot(firstSelected);
                    if (root != null) Prefabs.PrefabUtility.ApplyOverrides(root);
                }, enabled: hasOverrides, icon: EditorIcons.Check);

                builder.Item(Loc.Get("hierarchy.revert_to_prefab"), () =>
                {
                    var root = Prefabs.PrefabUtility.GetPrefabInstanceRoot(firstSelected);
                    if (root != null) Prefabs.PrefabUtility.RevertOverrides(root);
                }, enabled: hasOverrides, icon: EditorIcons.ArrowsRotate);

                // Unity's two-level unpack: "Unpack Prefab" keeps nested prefab roots as instances,
                // "Unpack Completely" clears prefab tracking recursively.
                builder.Item("Unpack Prefab", () =>
                {
                    Prefabs.PrefabUtility.UnpackPrefabInstance(firstSelected);
                }, icon: EditorIcons.LinkSlash);
                builder.Item("Unpack Completely", () =>
                {
                    Prefabs.PrefabUtility.BreakPrefabInstance(firstSelected);
                }, icon: EditorIcons.LinkSlash);

                builder.Separator();
            }

            if (multiSelect)
            {
                builder.Item($"{Loc.Get("hierarchy.delete")} ({selectedGOs.Count})", () =>
                {
                    foreach (var go in ExcludeNestedSelections(selectedGOs)) DeleteGameObject(go);
                }, icon: EditorIcons.Trash, danger: true);
            }
            else
            {
                var go = firstSelected!;
                builder.Item(Loc.Get("hierarchy.delete"), () => DeleteGameObject(go), icon: EditorIcons.Trash, danger: true);
            }

        });
    }

    // ================================================================
    //  Create Menu
    // ================================================================

    private void BuildCreateMenu(ContextBuilder builder, GameObject? parent)
    {
        MenuContext.Set(parent);
        MenuItemAttribute.BuildContextMenu(builder, "GameObject");
    }

    /// <summary>
    /// Creates a GameObject in the current scene and registers it for undo.
    /// </summary>
    /// <param name="select">When true, the new object becomes the current selection.</param>
    /// <param name="beginRename">When true, the inline rename overlay opens on the new object.
    /// Pass false for objects created as a side effect (e.g. an auto-generated parent Canvas)
    /// so they don't steal selection / rename focus from the object the user actually asked for.</param>
    public static GameObject CreateGameObject(string name, GameObject? parent, bool select = true, bool beginRename = true, bool registerUndo = true)
    {
        var scene = Scene.Current;
        if (scene == null) return new GameObject(name);

        var go = new GameObject(name);
        scene.Add(go);
        if (parent != null)
            go.SetParent(parent);
        if (select)
            Selection.Select(go);

        // registerUndo: false lets compound creations (e.g. XGUI templates) register ONE grouped
        // undo step for the whole batch instead of one step per node.
        if (registerUndo)
            Undo.RegisterCreatedObject(go, "Create GameObject");

        // Creating an object dirties the scene (Unity semantics). Undo.RegisterCreatedObject only
        // marks dirty inside its undo/redo lambdas, so without this the hierarchy's stamp cache
        // never saw the new object until something else dirtied the scene.
        EditorSceneManager.MarkDirty();

        if (beginRename)
        {
            // Enter rename via global overlay
            string goIdStr = go.Identifier.ToString();
            var goGuid = go.Identifier;
            RenameOverlay.Begin(goIdStr, go.Name, newName =>
            {
                var oldName = go.Name;
                Undo.RegisterAction("Rename",
                    () => { var r = Undo.FindGO(goGuid); if (r != null) r.Name = oldName; },
                    () => { var r = Undo.FindGO(goGuid); if (r != null) r.Name = newName; });
                go.Name = newName;
                EditorSceneManager.MarkDirty();
            });
        }

        return go;
    }

    private void StartRenameGO(GameObject primary, IEnumerable<GameObject> allTargets)
    {
        var targets = allTargets.ToList();
        var oldNames = targets.Select(g => (g.Identifier, g.Name)).ToList();
        string goId = primary.Identifier.ToString();
        RenameOverlay.Begin(goId, primary.Name, newName =>
        {
            Undo.RegisterAction("Rename",
                undo: () => { foreach (var (id, old) in oldNames) { var r = Undo.FindGO(id); if (r != null) r.Name = old; } },
                redo: () => { foreach (var (id, _) in oldNames) { var r = Undo.FindGO(id); if (r != null) r.Name = newName; } });
            foreach (var go in targets)
                go.Name = newName;
            EditorSceneManager.MarkDirty();
        });
    }

    /// <summary>Delete a GameObject, blocking deletion of prefab-structural children (with a toast)
    /// and registering proper undo. Shared with SceneViewPanel's in-viewport Delete shortcut so both
    /// entry points enforce the same rules instead of the viewport bypassing them.</summary>
    internal static void DeleteGameObject(GameObject go)
    {
        // Block deleting prefab children that are part of the prefab structure
        if (go.Parent != null && go.Parent.IsPrefabInstance && go.Parent.PrefabChildCount >= 0)
        {
            int childIdx = go.Parent.Children.IndexOf(go);
            if (childIdx >= 0 && childIdx < go.Parent.PrefabChildCount)
            {
                Toasts.Show(Loc.Get("toast.prefab_structure"), Loc.Get("toast.prefab_cant_delete"), ToastType.Warning, 3f);
                return;
            }
        }

        var scene = Scene.Current;
        if (scene == null) return;

        Undo.RegisterDestroyObject(go, "Delete GameObject");

        if (Selection.IsSelected(go))
            Selection.RemoveFromSelection(go);

        foreach (var child in go.GetChildrenDeep().ToList())
            scene.Remove(child);

        scene.Remove(go);
        go.Dispose();

        // Deleting an object dirties the scene (Unity semantics); see CreateGameObject.
        EditorSceneManager.MarkDirty();
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private static GameObject? FindGOById(Scene scene, Guid id)
    {
        foreach (var root in scene.RootObjects)
        {
            var found = root.FindChildByIdentifier(id);
            if (found != null) return found;
        }
        return null;
    }

    private List<GameObject> GetDisplayRoots(Scene scene)
    {
        _displayRoots.Clear();
        foreach (var go in scene.RootObjects)
        {
            if (go.HideFlags.HasFlag(HideFlags.Hide) || go.HideFlags.HasFlag(HideFlags.HideAndDontSave))
                continue;
            _displayRoots.Add(go);
        }
        if (_alphaSort)
            _displayRoots.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return _displayRoots;
    }

    internal static bool HasVisibleHierarchyChildren(GameObject go)
    {
        var children = go.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            if (!c.HideFlags.HasFlag(HideFlags.Hide) && !c.HideFlags.HasFlag(HideFlags.HideAndDontSave))
                return true;
        }
        return false;
    }

    internal static HierarchyTreeKey ComputeTreeKey(string searchText, bool sceneExpanded, bool alphaSort = false, bool selectionDependent = true)
    {
        var scene = Scene.Current;
        return new HierarchyTreeKey(scene, scene?.StructureVersion ?? 0, EditorSceneManager.ChangeVersion,
            selectionDependent ? Selection.ChangeVersion : 0, searchText, PrefabEditingMode.IsEditing, SceneVisibilityManager.Version,
            Selection.PingedGuid, sceneExpanded, alphaSort, GameObjectClipboard.CutVersion);
    }

    private TreeNode RentTreeNode()
    {
        int last = _treeNodePool.Count - 1;
        if (last < 0)
            return new TreeNode();
        var node = _treeNodePool[last];
        _treeNodePool.RemoveAt(last);
        return node;
    }

    /// <summary>Pooled nodes keep only their allocation, never the scene: a pooled node still holding
    /// its GameObject in UserData pinned every object of a removed 100k subtree (~120 MiB managed
    /// live after a forced GC). The pool is also capped so one huge scene does not size it forever.</summary>
    private const int TreeNodePoolCapacity = 4096;

    private void RecycleTreeNodes()
    {
        for (int i = 0; i < _treeNodes.Count && _treeNodePool.Count < TreeNodePoolCapacity; i++)
        {
            var node = _treeNodes[i];
            node.UserData = null; node.Id = ""; node.Label = ""; node.Badge = null; node.IconDraw = null;
            _treeNodePool.Add(node);
        }
        _treeNodes.Clear();
        _flatObjects.Clear();
        _goStyles.Clear();
    }

    private void RefreshDropIndicators()
    {
        if (_rowIndex != null)
        {
            _rowIndex.UpdateDropIndicator(CanShowHierarchyDrop() ? _dragHoverTargetId : null, _dragHoverNormalizedY);
            return;
        }
        for (int i = 0; i < _treeNodes.Count; i++)
        {
            var node = _treeNodes[i];
            if (node.UserData is not GameObject go)
                continue;
            string goId = node.Id;
            TreeDropPosition? dropInd = null;
            if (_dragHoverTargetId == goId && CanShowHierarchyDrop())
            {
                if (_dragHoverNormalizedY < 0.25f) dropInd = TreeDropPosition.Above;
                else if (_dragHoverNormalizedY > 0.75f) dropInd = IsTargetExpanded(go, goId) ? TreeDropPosition.IntoFirst : TreeDropPosition.Below;
                else dropInd = TreeDropPosition.Into;
            }
            node.DropIndicator = dropInd;
        }
    }

    private bool CanShowHierarchyDrop()
    {
        if (!DragDrop.IsDragging || DragDrop.Payload is not GameObjectDragPayload payload || _dragHoverTarget == null) return false;
        bool into = _dragHoverNormalizedY >= .25f && _dragHoverNormalizedY <= .75f
            || _dragHoverNormalizedY > .75f && _dragHoverTargetId != null && IsTargetExpanded(_dragHoverTarget, _dragHoverTargetId);
        return EditorDragFeedback.CanHierarchyDrop(payload, _dragHoverTarget, into);
    }

    private static Color GetPrefabTextColor(GameObject go)
    {
        if (!go.IsPrefabInstance)
            return go.EnabledInHierarchy ? EditorTheme.Ink500 : EditorTheme.Ink300;

        // Check if the prefab asset still exists
        var entry = EditorAssetBackend.Instance?.GetEntry(go.PrefabAssetId);
        if (entry == null)
        {
            // Broken prefab link red text
            return go.EnabledInHierarchy ? EditorTheme.Red400 : EditorTheme.Red300;
        }

        // Valid prefab: Unity's blue text
        return go.EnabledInHierarchy ? EditorTheme.Blue400 : EditorTheme.Blue300;
    }

    private static string GetGameObjectIcon(GameObject go)
    {
        if (go.GetComponent<Camera>() != null) return EditorIcons.Camera;
        if (go.GetComponent<Light>() != null) return EditorIcons.Sun;
        if (go.GetComponent<MeshRenderer>() != null) return EditorIcons.Cube;
        if (go.GetComponent<SkinnedMeshRenderer>() != null) return EditorIcons.Cubes;
        return EditorIcons.Circle;
    }

    // Vector icon + accent colour chosen from the GameObject's defining (first) component.
    private static (IOrigamiIcon icon, Color color) GetGoStyle(GameObject go)
    {
        var first = go.GetComponents<MonoBehaviour>().FirstOrDefault();
        if (first is Camera)               return (EditorIcons.Camera_I, EditorTheme.Blue400);    // blue
        if (first is Light)                return (EditorIcons.Lightbulb_I, EditorTheme.Amber400);    // amber
        if (first is SkinnedMeshRenderer)  return (EditorIcons.Cubes_I, EditorTheme.Purple400);    // purple
        if (first is MeshRenderer)         return (EditorIcons.Cube_I, EditorTheme.Purple400);    // purple
        if (first != null)                 return (EditorIcons.FileCode_I, EditorTheme.Green400);   // any other component = green script
        // Empty GameObject: a group icon when it parents others, else a dim generic mark.
        return go.Children.Count > 0
            ? (EditorIcons.ObjectGroup_I, EditorTheme.Ink300)
            : (EditorIcons.Cube_I, EditorTheme.InkDim);
    }

    private GameObject? FindGOByIdentifier(string id)
    {
        var scene = Scene.Current;
        if (scene == null) return null;
        return scene.AllObjects.FirstOrDefault(g => g.Identifier.ToString() == id);
    }

    /// <summary>
    /// Search all GameObjects in the scene for any that reference the given GUID.
    /// Checks each component's AssetID and all AssetRef fields.
    /// </summary>
    private static void FindGameObjectsWithGuid(Scene scene, Guid guid, HashSet<GameObject> results)
    {
        foreach (var go in scene.AllObjects)
        {
            // Direct GO match (e.g. scene-view click -> Ping(go.Identifier))
            if (go.Identifier == guid)
            {
                results.Add(go);
                continue;
            }

            foreach (var comp in go.GetComponents<MonoBehaviour>())
            {
                if (comp.AssetID == guid)
                {
                    results.Add(go);
                    break;
                }

                // Search fields for AssetRef<T> that reference this GUID
                bool found = false;
                var type = comp.GetType();
                foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    var fieldType = field.FieldType;
                    if (!fieldType.IsGenericType) continue;
                    if (fieldType.GetGenericTypeDefinition() != typeof(AssetRef<>)) continue;

                    var assetRef = field.GetValue(comp);
                    if (assetRef == null) continue;

                    var assetIdProp = fieldType.GetProperty("AssetID");
                    if (assetIdProp?.GetValue(assetRef) is Guid refGuid && refGuid == guid)
                    {
                        results.Add(go);
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
        }
    }

    // ================================================================
    //  Asset Drop -> Spawn in Scene
    // ================================================================

    public static void SpawnAssetInScene(AssetDragPayload payload, GameObject? parent, Float3 position)
    {
        var scene = Scene.Current;
        if (scene == null) return;

        var asset = Runtime.AssetDatabase.Get(payload.AssetGuid);
        if (asset == null) return;

        string name = System.IO.Path.GetFileNameWithoutExtension(payload.AssetName);

        // Model files import as PrefabAsset (prowl-sync M4.1) — the PrefabAsset branch below
        // covers them, routing through PrefabUtility.InstantiatePrefab.

        if (asset is Mesh mesh)
        {
            var go = new GameObject(name);
            go.Transform.Position = position;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.Mesh = mesh;
            renderer.Material = new Material(Shader.LoadDefault(DefaultShader.Standard));
            scene.Add(go);
            if (parent != null) go.SetParent(parent);
            Selection.Select(go);
            Undo.RegisterCreatedObject(go, "Spawn Mesh");
        }
        else if (asset is Sprite sprite)
        {
            var go = new GameObject(string.IsNullOrEmpty(sprite.Name) ? name : sprite.Name);
            go.Transform.Position = position;
            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.Sprite = sprite;
            scene.Add(go);
            if (parent != null) go.SetParent(parent);
            Selection.Select(go);
            Undo.RegisterCreatedObject(go, "Spawn Sprite");
        }
        else if (asset is PrefabAsset)
        {
            var instance = Prefabs.PrefabUtility.InstantiatePrefab(payload.AssetGuid);
            if (instance != null)
            {
                instance.Transform.Position = position;
                scene.Add(instance);
                if (parent != null) instance.SetParent(parent);
                Selection.Select(instance);
                Undo.RegisterCreatedObject(instance, "Spawn Prefab");
            }
        }
        else
        {
            Runtime.Debug.LogWarning($"Cannot spawn asset of type {asset.GetType().Name} in scene.");
        }
    }

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        state["search"] = _searchText;
        state["sceneExpanded"] = _sceneExpanded;
        state["alphaSort"] = _alphaSort;
        var expand = new System.Text.Json.Nodes.JsonObject();
        foreach (var kv in _expandStore)
            expand[kv.Key] = kv.Value;
        state["expand"] = expand;
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        _hasTreeCache = false;
        _rowIndex = null;
        _searchText = state["search"]?.GetValue<string>() ?? _searchText;
        _sceneExpanded = state["sceneExpanded"]?.GetValue<bool>() ?? _sceneExpanded;
        _alphaSort = state["alphaSort"]?.GetValue<bool>() ?? _alphaSort;
        if (state["expand"] is System.Text.Json.Nodes.JsonObject expand)
        {
            _expandStore.Clear();
            foreach (var kv in expand)
                if (kv.Value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out bool b))
                    _expandStore[kv.Key] = b;
            _adoptRestoredExpandStore = _expandStore.Count > 0;
        }
    }
}
