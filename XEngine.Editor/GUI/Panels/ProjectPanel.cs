using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using XEngine.OrigamiUI;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Registries;
using static XEngine.Editor.GUI.EditorGUI;
using XEngine.Editor.GUI.Popups;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Vector;

using Color = System.Drawing.Color;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Utils;
using XEngine.Editor.Core;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Theming;
using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Packages;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;

namespace XEngine.Editor.GUI.Panels;

public class ProjectPanel : DockPanel, IScriptReloadCleanup
{
    [MenuItem("Window/General/Project", priority: 4)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(ProjectPanel));

    // ── Incremental paint (Program.ProjectPaintIncremental) ─────────────────────────────
    // Two compared subtree regions: the folder tree and the content area (list table or thumbnail
    // grid). Both share one geometry/atlas budget pair. Regions are recreated when the host Paper
    // changes (panel moved between hosts) and released on close / before a script reload so the
    // cached geometry never outlives the panel or pins the collectible assembly context.
    private UiPaperSubtreeRegion? _treePaintRegion;
    private UiPaperSubtreeRegion? _contentPaintRegion;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _paintGeometryBudget = new(8 * 1024 * 1024);
    private readonly UiMemoryBudget _paintAtlasBudget = new(16 * 1024 * 1024);
    // Grid rows are banded (only rows intersecting the viewport are built) once a folder holds at
    // least this many entries; below it the full flow layout is cheap and stays pixel-exact. The
    // band's row offsets come from one full layout of the folder (see ProjectGridRowTable).
    internal const int GridVirtualizeThreshold = 64;
    private readonly ProjectGridRowTable _gridRows = new();
    private readonly List<float> _gridRowScratch = new();
    private int _gridRowsBuilt, _gridCardsBuilt;
    private readonly List<object> _gridItemObjects = new();
    private long _gridItemObjectsStamp = long.MinValue;
    private readonly Dictionary<Guid, List<object>> _subObjectsCache = new();

    internal static bool PaintIncremental => Program.ProjectPaintIncremental;
    internal long TreeRegionCaptures => _treePaintRegion?.CaptureCount ?? -1;
    internal long ContentRegionCaptures => _contentPaintRegion?.CaptureCount ?? -1;
    internal int GridRowsBuilt => _gridRowsBuilt;
    internal int GridCardsBuilt => _gridCardsBuilt;
    internal int GridRowMeasurements => _gridRows.Measurements;
    internal int GridRowMismatches => _gridRows.Mismatches;
    internal string? GridRowLastMismatch => _gridRows.LastMismatch;

    public void OnScriptReloadCleanup()
    {
        _treePaintRegion?.Dispose(); _treePaintRegion = null;
        _contentPaintRegion?.Dispose(); _contentPaintRegion = null;
        _paintRegionPaper = null;
        _gridItemObjects.Clear(); _gridItemObjectsStamp = long.MinValue;
        _subObjectsCache.Clear();
        _gridRows.Reset(); _gridRowsBuilt = 0;
        _dragHoverFolder = null; _dragHoverFolderNext = null;
    }

    public override void OnClosed()
    {
        OnScriptReloadCleanup();
        _paper = null;
        _cachedEntries.Clear(); _entriesStamp = long.MinValue;
        _folderTreeNodes.Clear(); _folderTreeItems.Clear(); _folderTreeStamp = long.MinValue;
        _visibleRows.Clear(); _flatSelectionObjects.Clear();
    }

    /// <summary>Attach (creating on demand) one of the panel's paint regions to the current parent.
    /// No-op unless the incremental flag is set and the renderer can paint to a separate layer.</summary>
    private void AttachPaintRegion(Paper paper, ref UiPaperSubtreeRegion? region)
    {
        if (!Program.ProjectPaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
        if (!ReferenceEquals(_paintRegionPaper, paper))
        {
            _treePaintRegion?.Dispose(); _treePaintRegion = null;
            _contentPaintRegion?.Dispose(); _contentPaintRegion = null;
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

    public static ProjectPanel Instance;
    public override string Title => Loc.Get("panel.project");
    public override string Icon => EditorIcons.Folder;

    // Refresh control lives in the leaf's tab-bar header (right side), matching the Nebula design.
    public override float HeaderWidth => 28f;
    public override void OnHeaderContent(Paper paper, float width, float height)
    {
        EditorGUI.HeaderIconButton(paper, "proj_hdr_refresh", EditorIcons.ArrowRotateRight, () =>
        {
            if (EditorAssetBackend.Instance != null)
            {
                Runtime.Debug.Log("Refreshing asset database...");
                var db = new EditorAssetBackend(Project.Current!);
                db.Initialize();
            }
        });
    }

    // Handled Virtual (Placeholder) content to be displayed with normal objects
    public List<ContentItem> VirtualContentItems = new();

    public string CurrentFolder => _currentFolder;
    private string _currentFolder = ""; // Relative to Assets/, empty = Assets root
    private string _searchText = "";
    private float _thumbnailSize = 64f;
    private Paper? _paper; // Cached for modifier key checks in callbacks

    // Drag-hover tracking the mouse's current drop target while a drag is active. Tree
    // nodes and folder items in the grid/list set this via OnHover (same pattern the
    // HierarchyPanel uses). Reset each frame before the body draws.
    //   null   -> mouse not over any folder drop target
    //   ""     -> over the Assets root (represented by an empty relative path)
    //   "Foo"  -> over folder 'Foo' (assets-relative)
    private string? _dragHoverFolder;      // current frame's resolved hover target (from last frame's callbacks)
    private string? _dragHoverFolderNext;  // written by deferred callbacks, promoted next frame
    private string? _dragDwellFolder;     // folder being dwelled on for auto-open
    private float _dragDwellTimer;        // seconds spent hovering the dwell folder
    private const float DragDwellOpenDelay = 0.75f;
    // True while the mouse is over the content-area background (not just a folder item) -
    // lets "drop on empty space" fall back to the currently-open folder.
    private bool _contentBgHovered;
    private bool _contentSelectionFocused;
    // Rename state is managed by RenameOverlay
    private static readonly HashSet<Guid> _expandedAssets = new(); // files with sub-assets expanded
    private static Guid _pendingPingNavigate; // Navigate to pinged asset's folder on next frame
    private static Guid _lastPingedGuid; // Track when a new ping starts
    private const float MinThumbSize = 20f;  // Below this = list mode
    private const float MaxThumbSize = 128f;
    private const float ListThreshold = 32f; // Below this = list view

    private const float ToolbarHeight = EditorToolbar.Height;
    private const float FooterHeight = 26;
    private const float FolderTreeWidth = 172f; // default; the live width is draggable
    private const float TreeSplitterW = 4f;
    private float _treeWidth = FolderTreeWidth;

    // -- asset-browser view state --
    private enum SortMode { Name, Type, Size, Modified }
    private SortMode _sortBy = SortMode.Name;
    private bool _showExtensions;
    private bool _showHidden;
    private bool _groupByType;
    private readonly Stack<string> _navBack = new();
    private readonly Stack<string> _navForward = new();
    private readonly List<ContentItem> _cachedEntries = new();
    private long _entriesStamp = long.MinValue;
    private readonly List<OrigamiUI.TreeNode> _folderTreeNodes = new();
    private readonly List<object> _folderTreeItems = new();
    private long _folderTreeStamp = long.MinValue;
    private readonly Dictionary<string, string> _packageTooltips = new(StringComparer.OrdinalIgnoreCase);

    private bool IsListView => _thumbnailSize < ListThreshold;

    internal void NavigateTo(string folder)
    {
        if (folder == _currentFolder) return;
        _navBack.Push(_currentFolder);
        _navForward.Clear();
        _currentFolder = folder;
    }

    /// <summary>Closest ancestor of <paramref name="folder"/> the folder index still knows ("" = Assets root).</summary>
    internal static string NearestExistingAncestor(EditorAssetBackend db, string folder)
    {
        while (folder.Length > 0)
        {
            int slash = folder.LastIndexOf('/');
            folder = slash < 0 ? "" : folder[..slash];
            if (folder.Length == 0 || db.FolderExists(folder)) return folder;
        }
        return "";
    }

    private void NavBack()
    {
        if (_navBack.Count == 0) return;
        _navForward.Push(_currentFolder);
        _currentFolder = _navBack.Pop();
    }

    private void NavForward()
    {
        if (_navForward.Count == 0) return;
        _navBack.Push(_currentFolder);
        _currentFolder = _navForward.Pop();
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        using var profilerScope = EditorProfiler.BeginCpuScope("Project");
        _paper = paper;
        var font = EditorTheme.DefaultFont;
        if (font == null || Project.Current == null) return;

        // Sets itself as instance on start
        Instance ??= this;

        // Detect new ping and navigate to the pinged asset's folder
        if (Selection.PingedGuid != Guid.Empty && Selection.PingedGuid != _lastPingedGuid)
        {
            _lastPingedGuid = Selection.PingedGuid;
            _pendingPingNavigate = Selection.PingedGuid;
        }
        if (Selection.PingedGuid == Guid.Empty)
            _lastPingedGuid = Guid.Empty;

        if (_pendingPingNavigate != Guid.Empty)
        {
            var pingGuid = _pendingPingNavigate;
            _pendingPingNavigate = Guid.Empty;
            var db = EditorAssetBackend.Instance;
            if (db != null)
            {
                string? path = db.GuidToPath(pingGuid);
                if (path != null)
                {
                    // Navigate to the folder containing this asset
                    string folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                    _currentFolder = folder;

                    // Reset the content scroll so the pinged item lands in view stored scroll
                    // from a previous folder can otherwise leave the row off-screen.
                    Origami.ScrollTo("proj_content", new Float2(0, 0));
                }
            }
        }

        // Promote deferred hover target to current, then always clear the next slot.
        // Hover callbacks will re-set it this frame if the mouse is still over a folder.
        // On the drop frame, we keep the last promoted value (don't null it out).
        if (DragDrop.IsDropFrame)
        {
            // Drop frame: _dragHoverFolder already has the right value from last promotion
        }
        else
        {
            _dragHoverFolder = _dragHoverFolderNext;
            _dragHoverFolderNext = null;
        }
        _contentBgHovered = false;

        // Drag-dwell auto-open: if hovering the same folder for DragDwellOpenDelay, navigate into it
        if (DragDrop.IsDragging && _dragHoverFolder != null)
        {
            if (_dragHoverFolder == _dragDwellFolder)
            {
                _dragDwellTimer += (float)Runtime.Time.UnscaledDeltaTime;
                if (_dragDwellTimer >= DragDwellOpenDelay)
                {
                    _currentFolder = _dragDwellFolder;
                    _dragDwellFolder = null;
                    _dragDwellTimer = 0f;
                }
            }
            else
            {
                _dragDwellFolder = _dragHoverFolder;
                _dragDwellTimer = 0f;
            }
        }
        else
        {
            _dragDwellFolder = null;
            _dragDwellTimer = 0f;
        }

        var assetDb = EditorAssetBackend.Instance;
        var entries = assetDb != null ? GetContentEntries(assetDb) : new List<ContentItem>();

        using (paper.Row("proj_root").Size(width, height).Enter())
        {
            DrawFolderTree(paper, font, height);

            paper.Box("proj_tree_split").Width(TreeSplitterW).Height(height)
                .BackgroundColor(EditorTheme.BorderSoft)
                .Cursor(PaperCursor.ResizeHorizontal)
                .OnDragging(e => _treeWidth = Math.Clamp(_treeWidth + (float)e.Delta.X, 120f, 400f));

            float mainW = width - _treeWidth - TreeSplitterW;
            using (paper.Column("proj_main").Width(mainW).Height(height).Enter())
            {
                DrawToolbar(paper, font, mainW);
                DrawContent(paper, font, entries, mainW, height - ToolbarHeight - FooterHeight);
                DrawFooter(paper, font, entries, mainW);
            }
            if (DragDrop.IsDragging && paper.IsParentHovered)
            {
                string target = _dragHoverFolder ?? _currentFolder;
                DragDrop.SetVisualMode(paper, DragDrop.Payload switch
                {
                    AssetDragPayload => CanAcceptAssetDropInto(target) ? DragVisualMode.Move : DragVisualMode.Rejected,
                    GameObjectDragPayload => IsPackagesTreePath(target) ? DragVisualMode.Rejected : DragVisualMode.Copy,
                    _ => DragVisualMode.Rejected
                });
            }
        }

        // Process drops at the end of the frame after every OnHover callback from the
        // tree and content panels has fired. Uses the captured drop target (_dragHoverFolder)
        // or falls back to the currently-open folder when the drop lands on empty space in
        // the content area.
        if (DragDrop.IsDropFrameFor(paper) && DragDrop.Payload != null)
        {
            string? target = _dragHoverFolder;
            if (target == null && _contentBgHovered) target = _currentFolder;
            if (target != null) DispatchProjectDrop(target, DragDrop.Payload);
        }
    }

    private void DispatchProjectDrop(string targetFolder, DragPayload payload)
    {
        switch (payload)
        {
            case AssetDragPayload ap when CanAcceptAssetDropInto(targetFolder):
                PerformAssetMove(ap, targetFolder);
                DragDrop.EndDrag();
                break;
            case GameObjectDragPayload gp when !IsPackagesTreePath(targetFolder):
                foreach (var go in gp.GameObjects)
                    if (go != null) CreatePrefabInFolder(go, targetFolder);
                DragDrop.EndDrag();
                break;
        }
    }

    // ================================================================
    //  Toolbar
    // ================================================================

    private void DrawToolbar(Paper paper, Scribe.FontFile font, float width)
    {
        using (paper.Column("proj_toolbar_col").Height(ToolbarHeight).Enter())
        {
            using (paper.Row("proj_toolbar")
                .Height(ToolbarHeight - 1)
                .ChildLeft(1).ChildRight(1).RowBetween(2)
                .Enter())
            {
                NavBtn(paper, font, "proj_back", EditorIcons.ChevronLeft, _navBack.Count > 0, NavBack);
                NavBtn(paper, font, "proj_fwd", EditorIcons.ChevronRight, _navForward.Count > 0, NavForward);

                DrawCrumbs(paper, font);

                paper.Box("proj_tb_spacer");

                EditorToolbar.IconButton(paper, "proj_view", IsListView ? EditorIcons.TableCellsLarge_I : EditorIcons.List_I,
                    () => _thumbnailSize = IsListView ? 64f : 24f).Show();

                using (paper.Row("proj_search_wrap").Width(150).Height(EditorToolbar.SearchHeight).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                    EditorToolbar.Search(paper, "proj_search", _searchText, v => _searchText = v,
                        150, Loc.Get("project.search")).Show();

                EditorToolbar.IconButton(paper, "proj_opts", EditorIcons.EllipsisVertical_I,
                    () => Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, BuildOptionsMenu)).Show();
            }
            paper.Box("proj_tb_div").Height(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
        }
    }

    // View / sort / filter menu, built with Origami's ContextMenu so it uses the theme's default fonts.
    private void BuildOptionsMenu(ContextBuilder b)
    {
        b.Header(Loc.Get("project.menu_view"));
        b.Toggle(Loc.Get("project.show_extensions"), () => _showExtensions = !_showExtensions, () => _showExtensions);
        b.Item(IsListView ? Loc.Get("project.grid_view") : Loc.Get("project.list_view"), () => _thumbnailSize = IsListView ? 64f : 24f);

        b.Header(Loc.Get("project.sort_by"));
        foreach (SortMode s in Enum.GetValues<SortMode>())
        {
            var sc = s;
            b.Item(s.ToString(), () => _sortBy = sc, on: _sortBy == s);
        }

        b.Header(Loc.Get("project.filter"));
        b.Toggle(Loc.Get("project.show_hidden"), () => _showHidden = !_showHidden, () => _showHidden);
        b.Toggle(Loc.Get("project.group_by_type"), () => _groupByType = !_groupByType, () => _groupByType);
    }

    private void NavBtn(Paper p, Scribe.FontFile font, string id, string glyph, bool enabled, Action onClick)
    {
        var b = p.Box(id).Width(24).Height(EditorToolbar.ButtonHeight).Rounded(EditorTheme.Roundness).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .Text(glyph, font).TextColor(enabled ? EditorTheme.Ink300 : EditorTheme.InkFaint).FontSize(13f).Alignment(TextAlignment.MiddleCenter);
        if (enabled) { b.Hovered.BackgroundColor(EditorTheme.Hover).End(); b.OnClick(_ => onClick()); }
        else b.IsNotInteractable();
    }

    // Breadcrumb trail (Origami widget). Packages/... uses a Packages root crumb (not Assets > Packages).
    private void DrawCrumbs(Paper p, Scribe.FontFile font)
    {
        var items = new List<BreadcrumbItem>();
        bool inPackages = IsPackagesTreePath(_currentFolder);
        if (inPackages)
        {
            items.Add(new BreadcrumbItem("Packages", EditorIcons.BoxOpen_I, (object)"Packages"));
            if (!_currentFolder.Equals("Packages", StringComparison.OrdinalIgnoreCase))
            {
                string rest = _currentFolder["Packages/".Length..];
                string acc = "Packages";
                bool first = true;
                foreach (var seg in rest.Split('/'))
                {
                    acc = acc + "/" + seg;
                    string label = first ? PackageDisplayName(seg) : seg;
                    first = false;
                    items.Add(new BreadcrumbItem(label, "", acc));
                }
            }
        }
        else
        {
            items.Add(new BreadcrumbItem("Assets", EditorIcons.Folder_I, (object)""));
            if (!string.IsNullOrEmpty(_currentFolder))
            {
                string acc = "";
                foreach (var seg in _currentFolder.Split('/'))
                {
                    acc = acc.Length > 0 ? acc + "/" + seg : seg;
                    items.Add(new BreadcrumbItem(seg, "", acc));
                }
            }
        }

        using (p.Row("proj_crumbs").Width(UnitValue.Auto).Height(EditorToolbar.ButtonHeight).Clip().Margin(4, 0, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
            Origami.Breadcrumb(p, "proj_crumbs_bc", items, it => NavigateTo((string)it.UserData!))
                .Width(UnitValue.Auto).Height(EditorToolbar.ButtonHeight).Show();
    }

    // ================================================================
    //  Footer (count + thumbnail size slider)
    // ================================================================

    private void DrawFooter(Paper paper, Scribe.FontFile font, List<ContentItem> entries, float width)
    {
        var mono = EditorTheme.FontMono ?? font;
        int selCount = Selection.GetSelected<ContentItem>().Count();

        using (paper.Row("proj_footer").Height(FooterHeight)
            .Padding(11, 11, 0, 0)
            .BackgroundColor(Color.FromArgb(36, 0, 0, 0))
            .Enter())
        {
            paper.Box("proj_foot_div").PositionType(PositionType.SelfDirected).Position(0, 0).Size(width, 1)
                .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

            paper.Box("proj_foot_count").Width(UnitValue.Auto).Height(FooterHeight).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .Text(Loc.Get("project.item_count", new { count = entries.Count }), mono).TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
            if (selCount > 0)
                paper.Box("proj_foot_sel").Width(UnitValue.Auto).Height(FooterHeight).Margin(6, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                    .Text("- " + Loc.Get("project.selected_count", new { count = selCount }), mono).TextColor(EditorTheme.AccentText).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

            paper.Box("proj_foot_spacer");

            paper.Box("proj_foot_ico1").Width(14).Height(FooterHeight).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Text(EditorIcons.Image, font).TextColor(EditorTheme.InkFaint).FontSize(11f).Alignment(TextAlignment.MiddleCenter);
            using (paper.Row("proj_foot_sld_wrap").Width(90).Height(FooterHeight).ChildTop(UnitValue.StretchOne).ChildBottom(UnitValue.StretchOne).Margin(7, 0, UnitValue.StretchOne, UnitValue.StretchOne).Enter())
                Origami.Slider(paper, "proj_thumb_slider", _thumbnailSize, v => _thumbnailSize = v, MinThumbSize, MaxThumbSize)
                    .ShowValue(false).Width(90f).TrackThickness(4).ThumbSize(12).Height(18).Show();
            paper.Box("proj_foot_ico2").Width(16).Height(FooterHeight).Margin(7, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                .Text(EditorIcons.Image, font).TextColor(EditorTheme.InkDim).FontSize(13f).Alignment(TextAlignment.MiddleCenter);
        }
    }

    // ================================================================
    //  Drag & Drop helpers
    // ================================================================

    /// <summary>
    /// Build an <see cref="AssetDragPayload"/> for starting a drag from <paramref name="item"/>.
    /// If the item is part of the current selection, the payload includes every selected
    /// item so a multi-select drag moves the whole set. Returns null for undraggable items
    /// (sub-assets they live inside a parent asset and can't be moved independently).
    /// </summary>
    private static AssetDragPayload? BuildAssetDragPayload(ContentItem item)
    {
        if (item.IsSubAsset) return null;

        // Primary lookup the item the user actually grabbed.
        Type? primaryType = null;
        if (!item.IsFolder && item.Guid != Guid.Empty)
            primaryType = EditorAssetBackend.Instance?.GetEntry(item.RelativePath)?.MainAssetType;

        // Expand to the full selection ONLY if the grabbed item is inside it. That matches
        // how Explorer / Finder behave: clicking-and-dragging an unselected item drags just
        // that item, not the prior selection.
        var bundle = new List<ContentItem>();
        if (Selection.IsSelected(item))
        {
            foreach (var s in Selection.GetSelected<ContentItem>())
                if (!s.IsSubAsset) bundle.Add(s);
            // Ensure the grabbed item is first (payload.AssetGuid/Name reflect primary).
            bundle.Remove(item);
            bundle.Insert(0, item);
        }
        else
        {
            bundle.Add(item);
        }

        var guids = bundle.Select(b => b.Guid).ToArray();
        var paths = bundle.Select(b => b.RelativePath).ToArray();
        return new AssetDragPayload(item.Guid, item.Name, primaryType, guids, paths);
    }

    /// <summary>
    /// Move every asset/folder in <paramref name="payload"/> into <paramref name="destRelFolder"/>
    /// (assets-relative; empty = root). Skips no-ops and self-containment cycles, resolves
    /// name collisions with " (N)" suffixes, and refreshes thumbnails after the move.
    /// </summary>
    private void PerformAssetMove(AssetDragPayload payload, string destRelFolder)
    {
        var db = EditorAssetBackend.Instance;
        if (db == null || Project.Current == null) return;

        destRelFolder = (destRelFolder ?? "").Replace('\\', '/').TrimEnd('/');
        if (IsPackagesTreePath(destRelFolder)) return;
        string destAbs = string.IsNullOrEmpty(destRelFolder)
            ? Project.Current.AssetsPath
            : Path.Combine(Project.Current.AssetsPath, destRelFolder);
        if (!Directory.Exists(destAbs)) return;

        int moved = 0;
        foreach (var rawSrc in payload.AssetPaths)
        {
            if (string.IsNullOrEmpty(rawSrc)) continue;
            string src = rawSrc.Replace('\\', '/').TrimEnd('/');
            if (IsPackagePath(src)) continue;
            string srcName = Path.GetFileName(src);
            string srcDir = Path.GetDirectoryName(src)?.Replace('\\', '/') ?? "";

            // Already in target folder nothing to do.
            if (srcDir.Equals(destRelFolder, StringComparison.OrdinalIgnoreCase)) continue;

            string srcAbs = Path.Combine(Project.Current.AssetsPath, src);
            bool isFolder = Directory.Exists(srcAbs) && !File.Exists(srcAbs);

            if (isFolder)
            {
                // Can't drop a folder on itself or into one of its descendants that would
                // delete the folder mid-move.
                string srcWithSlash = src + "/";
                if (destRelFolder.Equals(src, StringComparison.OrdinalIgnoreCase)
                    || destRelFolder.StartsWith(srcWithSlash, StringComparison.OrdinalIgnoreCase))
                {
                    Runtime.Debug.LogWarning($"Skipped: can't move folder '{src}' into itself.");
                    continue;
                }

                string unique = AssetCreateMenu.FindUniqueName(destAbs, srcName, "");
                string newRel = string.IsNullOrEmpty(destRelFolder) ? unique : destRelFolder + "/" + unique;
                if (AssetUndo.MoveFolder(src, newRel)) moved++;
            }
            else if (File.Exists(srcAbs))
            {
                string ext = Path.GetExtension(srcName);
                string baseName = Path.GetFileNameWithoutExtension(srcName);
                string unique = AssetCreateMenu.FindUniqueName(destAbs, baseName, ext);
                string newRel = string.IsNullOrEmpty(destRelFolder) ? unique : destRelFolder + "/" + unique;
                if (AssetUndo.MoveAsset(src, newRel)) moved++;
            }
        }

        if (moved > 0)
        {
            // Thumbnails are keyed by GUID, not path, so a move/rename doesn't invalidate them.
            Runtime.Debug.Log($"Moved {moved} item(s) to '{(string.IsNullOrEmpty(destRelFolder) ? "Assets" : destRelFolder)}'.");
        }
    }

    /// <summary>
    /// Create a prefab for <paramref name="go"/> inside <paramref name="destRelFolder"/>
    /// (assets-relative; empty = root). Uniquifies the filename against what's already there.
    /// </summary>
    private static void CreatePrefabInFolder(GameObject go, string destRelFolder)
    {
        if (Project.Current == null) return;
        if (IsPackagesTreePath(destRelFolder)) return;
        string absFolder = string.IsNullOrEmpty(destRelFolder)
            ? Project.Current.AssetsPath
            : Path.Combine(Project.Current.AssetsPath, destRelFolder);
        if (!Directory.Exists(absFolder)) return;

        string uniqueName = AssetCreateMenu.FindUniqueName(absFolder, go.Name, ".prefab");
        string relPath = string.IsNullOrEmpty(destRelFolder) ? uniqueName : destRelFolder + "/" + uniqueName;
        Prefabs.PrefabUtility.CreatePrefab(go, relPath);
    }

    /// <summary>
    /// True when an <see cref="AssetDragPayload"/> could meaningfully land in
    /// <paramref name="destRelFolder"/> used to gate the hover highlight so folders that
    /// would no-op (already the parent) or cycle (self/descendant) don't light up.
    /// </summary>
    private static bool CanAcceptAssetDropInto(string destRelFolder)
    {
        // Use HasPayloadType (not IsDraggingType) so this works on the drop frame
        // when IsDragging is already false but the payload is still available.
        if (!DragDrop.HasPayloadType<AssetDragPayload>()) return false;
        if (IsPackagesTreePath(destRelFolder)) return false;
        var payload = (AssetDragPayload)DragDrop.Payload!;

        foreach (var rawSrc in payload.AssetPaths)
        {
            if (string.IsNullOrEmpty(rawSrc)) continue;
            string src = rawSrc.Replace('\\', '/').TrimEnd('/');
            if (IsPackagePath(src)) return false;
            string srcDir = Path.GetDirectoryName(src)?.Replace('\\', '/') ?? "";

            if (srcDir.Equals(destRelFolder, StringComparison.OrdinalIgnoreCase)) continue;

            // Self / descendant cycle check (folders only).
            string srcWithSlash = src + "/";
            if (destRelFolder.Equals(src, StringComparison.OrdinalIgnoreCase)
                || destRelFolder.StartsWith(srcWithSlash, StringComparison.OrdinalIgnoreCase))
                continue;

            return true;
        }
        return false;
    }

    // ================================================================
    //  Body: Folder Tree + Content
    // ================================================================

    // ================================================================
    //  Folder Tree (left)
    // ================================================================

    private void DrawFolderTree(Paper paper, Scribe.FontFile font, float height)
    {
        using (paper.Box("proj_tree_bg")
            .Size(_treeWidth, height)
            .BackgroundColor(Color.FromArgb(26, 0, 0, 0))
            .OnClick(0, (_, _) => Selection.Clear())
            .Enter())
        {
            // Right-edge divider (a2 tree border-right).
            paper.Box("proj_tree_div").PositionType(PositionType.SelfDirected).Position(_treeWidth - 1, 0)
                .Size(1, height).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();

            // Right-click background show create/explorer menu
            BuildBackgroundContextMenu(paper, "proj_tree_bg_ctx");

            // Build flat node list by walking directories recursively. The Assets/ tree is the primary
            // root; a virtual "Packages" root is added beneath it whenever the PackageManager resolved
            // one or more packages, exposing each package's assets as a read-only subfolder (Unity-style).
            var nodes = _folderTreeNodes;
            var folderItems = _folderTreeItems;
            long treeStamp = ComputeFolderTreeStamp();
            if (treeStamp != _folderTreeStamp)
            {
                nodes.Clear();
                folderItems.Clear();
                BuildFolderNodes(nodes, "", "Assets", 0);

                var project = Project.Current;
                _packageTooltips.Clear();
                if (project != null)
                {
                    // Peek only — Resolve hashes package contents and must not run during draw.
                    var packages = PackageManager.PeekCached(project);
                    if (packages is { Count: > 0 })
                    {
                        BuildFolderNodes(nodes, "Packages", "Packages", 0);
                        foreach (var pkg in packages)
                        {
                            _packageTooltips[pkg.LogicalRoot] =
                                $"{pkg.Name} / {pkg.Version} / {pkg.Source} / {pkg.ResolvedRoot}";
                        }
                    }
                }

                foreach (var n in nodes)
                {
                    string relPath = (string)n.UserData!;
                    folderItems.Add(new ContentItem
                    {
                        Name = n.Label,
                        RelativePath = relPath,
                        IsFolder = true,
                        Icon = FolderNodeIcon(relPath).Glyph,
                        TypeLabel = IsPackageRoot(relPath) ? "Package" : "Folder"
                    });
                }
                _folderTreeStamp = treeStamp;
            }

            using var treeBody = paper.Column("proj_tree_paint_body").Size(_treeWidth, height).Enter();
            bool treeFocused = EditorSelectionPresentation.IsFocused(paper);
            AttachPaintRegion(paper, ref _treePaintRegion);
            Origami.Tree(paper, "proj_tree", _treeWidth, height)
                .Nodes(nodes)
                .MultiSelect()
                .SelectionFocused(treeFocused)
                .IsSelected(n =>
                {
                    string relPath = (string)n.UserData!;
                    // Check if any selected ContentItem matches this folder
                    foreach (var sel in Selection.GetSelected<ContentItem>())
                        if (sel.IsFolder && sel.RelativePath == relPath) return true;
                    return _currentFolder == relPath;
                })
                .OnSelectModified((e, ctrl, shift) =>
                {
                    NavigateTo((string)e.Node.UserData!);
                    Selection.HandleListClick(folderItems[e.Index], (IReadOnlyList<object>)folderItems, e.Index, ctrl, shift);
                })
                .OnDoubleClick(e => { /* tree handles expand toggle internally */ })
                .OnRightClick(e =>
                {
                    var item = (ContentItem)folderItems[e.Index];
                    if (!Selection.IsSelected(item))
                        Selection.Select(item);
                })
                .OnHover((n, normY) =>
                {
                    if (DragDrop.IsDragging || DragDrop.IsDropFrame)
                        _dragHoverFolderNext = (string)n.UserData!;
                })
                .CustomRowContent((p, node, isSel, isExp) =>
                {
                    string relativePath = (string)node.UserData!;
                    var (glyph, iconColor) = FolderNodeIcon(relativePath);

                    // Folder / package icon
                    p.Box(UiId.Cat("proj_fi_", node.Id.GetHashCode()))
                        .Width(18).Height(22)
                        .Text(glyph, font)
                        .TextColor(iconColor)
                        .FontSize(12f).Alignment(TextAlignment.MiddleCenter);

                    // Name (inline rename or label)
                    if (RenameOverlay.IsRenaming(UiId.Cat("proj_folder_", relativePath)))
                    {
                        RenameOverlay.Draw(p, UiId.Cat("proj_ft_rename_", node.Id.GetHashCode()));
                    }
                    else
                    {
                        var label = p.Box(UiId.Cat("proj_fl_", node.Id.GetHashCode()))
                            .Height(22)
                            .Margin(4, 0, 0, 0)
                            .Text(node.Label, font)
                            .TextColor(isSel ? Origami.Current.SelectionText(treeFocused) : EditorTheme.Ink500)
                            .FontSize(EditorTheme.FontSize)
                            .Alignment(TextAlignment.MiddleLeft);
                        if (_packageTooltips.TryGetValue(relativePath, out string? tip))
                            label.Tooltip(tip);
                    }

                    // Right-click context menu on folder tree
                    BuildFolderTreeContextMenu(p, UiId.Cat("proj_ft_ctx_", node.Id.GetHashCode()), relativePath);

                    // Drop-target highlight
                    if (_dragHoverFolder == relativePath
                        && (DragDrop.IsDraggingType<GameObjectDragPayload>()
                            || (DragDrop.IsDraggingType<AssetDragPayload>() && CanAcceptAssetDropInto(relativePath))))
                    {
                        p.Box(UiId.Cat("proj_fn_drop_", node.Id.GetHashCode()))
                            .PositionType(PositionType.SelfDirected)
                            .Position(0, 0).Size(UnitValue.Stretch(), UnitValue.Stretch())
                            .Rounded(3).IsNotInteractable()
                            .BackgroundColor(Color.FromArgb(40, EditorTheme.Purple400))
                            .BorderColor(EditorTheme.Purple400).BorderWidth(1);
                    }
                })
                .Show();
        }
    }

    private static void BuildFolderNodes(List<OrigamiUI.TreeNode> nodes, string relativePath, string displayName, int depth)
    {
        // Read the folder structure from the asset database's cached index instead of walking the
        // filesystem every frame.
        var db = EditorAssetBackend.Instance;
        var subDirs = db != null
            ? db.GetSubFolders(relativePath)
                .Where(f => !f.Name.StartsWith('.'))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<EditorAssetBackend.FolderRecord>();

        var (glyph, iconColor) = FolderNodeIcon(relativePath);
        nodes.Add(new OrigamiUI.TreeNode
        {
            Id = relativePath,
            Label = displayName,
            Icon = glyph,
            IconColor = iconColor,
            HasChildren = subDirs.Count > 0,
            DefaultExpanded = depth < 2,
            Depth = depth,
            UserData = relativePath
        });

        foreach (var subDir in subDirs)
            BuildFolderNodes(nodes, subDir.RelativePath, subDir.Name, depth + 1);
    }

    // ================================================================
    //  Content Area (right)
    // ================================================================

    private void DrawContent(Paper paper, Scribe.FontFile font, List<ContentItem> entries, float width, float height)
    {
        bool isList = IsListView;
        _gridCardsBuilt = 0;
        if (isList) _gridRowsBuilt = 0;

        // Transparent background so the dock panel's glass (over the nebula) shows through.
        using (paper.Box("proj_content_bg")
            .Size(width, height)
            .OnClick(0, (_, _) => Selection.Clear())
            .Enter())
        {
            // Remember that the mouse is over the content background the central drop
            // dispatcher uses this as a fallback ("drop on empty content area" means drop
            // into the currently-open folder).
            if (DragDrop.IsDragging || DragDrop.IsDropFrame)
                _contentBgHovered = paper.IsParentHovered;

            // Right-click background show create/explorer menu
            BuildBackgroundContextMenu(paper, "proj_content_bg_ctx");

            // Project keyboard shortcuts
            if (paper.IsParentHovered && !ShortcutManager.IsRebinding)
            {
                if (ShortcutManager.IsPressed("Project/Delete"))
                    DeleteSelectedItems();
                else if (ShortcutManager.IsPressed("Project/Rename"))
                {
                    var first = Selection.GetSelected<ContentItem>().FirstOrDefault();
                    if (first != null) StartRename(first);
                }
                else if (ShortcutManager.IsPressed("Project/Duplicate"))
                    DuplicateSelectedItems();
                else if (ShortcutManager.IsPressed("Project/SelectAll"))
                {
                    Selection.Clear();
                    foreach (var it in entries) Selection.AddToSelection(it);
                }
                else if (!paper.WantsCaptureKeyboard)
                    HandleArrowNavigation(paper, entries, width, isList);
            }

            // While any drag is active, show a banner at the top of the content area
            // summarizing what the user will get by dropping on the background. This is a
            // visual hint only the actual Accept runs AFTER the items are drawn so per-item
            // drop targets (folders in the grid) can win before the background fallback.
            if (DragDrop.IsDraggingType<GameObjectDragPayload>() && paper.IsParentHovered)
            {
                DropBanner(paper, "proj_prefab_drop",
                    Loc.Get("project.drop_create_prefab", new { folder = string.IsNullOrEmpty(_currentFolder) ? "Assets" : _currentFolder }));
            }
            else if (DragDrop.IsDraggingType<AssetDragPayload>() && paper.IsParentHovered
                && CanAcceptAssetDropInto(_currentFolder))
            {
                DropBanner(paper, "proj_asset_drop_hint",
                    Loc.Get("project.drop_move", new { folder = string.IsNullOrEmpty(_currentFolder) ? "Assets" : _currentFolder }));
            }

            // The list table / thumbnail grid is the panel's second paint region. Everything above
            // (banner, shortcuts, background context menu) stays on the host so the region's visual
            // state only tracks the rows themselves.
            using (paper.Column("proj_content_paint_body").Size(width, height).Enter())
            {
                _contentSelectionFocused = EditorSelectionPresentation.IsFocused(paper);
                AttachPaintRegion(paper, ref _contentPaintRegion);
                if (isList)
                {
                    // List mode uses the tree widget which has its own scroll
                    DrawListView(paper, font, entries, width, height);
                }
                else
                {
                    // Grid mode uses a plain scroll view. Large folders band their rows against the
                    // live viewport (incremental mode only); the callback receives the offset.
                    bool virtualizeGrid = Program.ProjectPaintIncremental && entries.Count >= GridVirtualizeThreshold;
                    var scroll = Origami.ScrollView(paper, "proj_content", width, height);
                    if (virtualizeGrid) scroll.SmoothScroll(false);
                    scroll.Body(vp =>
                    {
                        using (paper.Column("proj_content_inner")
                            .Margin(9, 9, 9, 9)
                            .Height(UnitValue.Auto)
                            .Enter())
                        {
                            if (entries.Count == 0)
                            {
                                paper.Box("proj_empty")
                                    .Height(60)
                                    .Text(Loc.Get("project.folder_empty"), font)
                                    .TextColor(EditorTheme.Ink300)
                                    .FontSize(EditorTheme.FontSizeSmall)
                                    .Alignment(TextAlignment.MiddleCenter);
                            }
                            else if (virtualizeGrid)
                            {
                                DrawGridViewBanded(paper, font, entries, width, vp);
                            }
                            else
                            {
                                DrawGridView(paper, font, entries, width);
                            }
                        }
                    });
                }
            }
        }
    }

    // ================================================================
    //  List View
    // ================================================================

    private static float TableRowH => EditorTheme.RowHeight;

    private void DrawListView(Paper paper, Scribe.FontFile font, List<ContentItem> entries, float width, float height)
    {
        var mono = EditorTheme.FontMono ?? font;
        var semi = EditorTheme.FontSemiBold ?? font;
        // Flat visible list (top-level rows + the sub-asset rows of any expanded parent). The
        // buffers are reused: this runs per presented frame and the LINQ variant
        // (new List + Select(...).ToList()) was a standing churn source on every draw.
        _visibleRows.Clear();
        foreach (var e in entries)
        {
            _visibleRows.Add((e, false));
            if (e.Subs.Count > 0 && _expandedAssets.Contains(e.Guid))
                foreach (var s in e.Subs) _visibleRows.Add((s, true));
        }
        List<(ContentItem item, bool isSub)> visible = _visibleRows;
        _flatSelectionObjects.Clear();
        foreach (var row in visible) _flatSelectionObjects.Add(row.item);
        List<object> flatObjects = _flatSelectionObjects;

        int activeCol = _sortBy switch { SortMode.Name => 0, SortMode.Type => 1, SortMode.Size => 2, _ => -1 };

        Origami.Table(paper, "proj_table", -1, _ => { })
            .Bordered(false)
            .Scroll(width, height)
            // Incremental mode builds only the rows intersecting the viewport (fixed row stride);
            // the legacy path keeps every row live so its layout/scroll behaviour is unchanged.
            .Virtualize(Program.ProjectPaintIncremental)
            .RowHeight(TableRowH)
            .Column("Name", 2f, sortable: true)
            .Column("Type", 1f, sortable: true)
            .Column("Size", 0.7f, sortable: true, align: TextAlignment.MiddleRight)
            .Sort(activeCol, _sortBy != SortMode.Size, col =>
                _sortBy = col switch { 0 => SortMode.Name, 1 => SortMode.Type, 2 => SortMode.Size, _ => _sortBy })
            .MultiSelect()
            .IsSelected(i => Selection.IsSelected(visible[i].item))
            .SelectionFocused(_contentSelectionFocused)
            .OnSelectModified((i, ctrl, shift) => Selection.HandleListClick(visible[i].item, flatObjects, i, ctrl, shift))
            .OnRowActivate(i =>
            {
                var it = visible[i].item;
                if (it.IsFolder) NavigateTo(it.RelativePath);
                else if (it.Subs.Count > 0)
                {
                    if (_expandedAssets.Contains(it.Guid)) _expandedAssets.Remove(it.Guid);
                    else _expandedAssets.Add(it.Guid);
                }
                else if (!EditorSceneManager.HandleAssetDoubleClick(it.RelativePath, it.Guid))
                    OpenWithSystem(it); // no registered handler: open with the OS association, like Unity
            })
            .OnRowContext(i =>
            {
                var it = visible[i].item;
                if (!Selection.IsSelected(it)) Selection.AddToSelection(it);
                Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, ItemContextMenu(it));
            })
            .OnRowDragStart(i =>
            {
                var it = visible[i].item;
                if (it.IsSubAsset && it.Guid != Guid.Empty)
                {
                    var db = EditorAssetBackend.Instance;
                    Type? subType = db?.GetSubAssets(it.ParentGuid).FirstOrDefault(s => s.Guid == it.Guid)?.Type;
                    DragDrop.StartDrag(new AssetDragPayload(it.Guid, it.Name, subType));
                }
                else
                {
                    var payload = BuildAssetDragPayload(it);
                    if (payload != null) DragDrop.StartDrag(payload);
                }
            })
            .OnRowHover((i, _) =>
            {
                var it = visible[i].item;
                if (it.IsFolder && (DragDrop.IsDragging || DragDrop.IsDropFrame)) _dragHoverFolderNext = it.RelativePath;
            })
            .IsPinged(i => { var g = visible[i].item.Guid; return g != Guid.Empty && g == Selection.PingedGuid; })
            .PingAlpha(() => Selection.GetPingAlpha())
            .RowCount(visible.Count)
            .CellContent((rowIdx, col) => DrawTableCell(paper, font, mono, semi, visible[rowIdx].item, visible[rowIdx].isSub, col))
            .Show();
    }

    // Per-frame reused view buffers for DrawListView (see note there).
    private readonly List<(ContentItem item, bool isSub)> _visibleRows = new();
    private readonly List<object> _flatSelectionObjects = new();

    // Host-drawn content for one table cell (the table owns the column width, selection bg, scrolling).
    private void DrawTableCell(Paper paper, Scribe.FontFile font, Scribe.FontFile mono, Scribe.FontFile semi,
        ContentItem item, bool isSub, int col)
    {
        string id = item.Guid != Guid.Empty ? item.Guid.ToString() : item.RelativePath;
        bool isSelected = Selection.IsSelected(item);
        bool hasSubs = item.Subs.Count > 0;
        bool expanded = hasSubs && _expandedAssets.Contains(item.Guid);
        var style = item.IsFolder
            ? (IsPackageRoot(item.RelativePath) ? AssetTypeStyles.Package : AssetTypeStyles.Folder)
            : AssetTypeStyles.For(Path.GetExtension(item.Name), item.TypeLabel);

        if (col == 0)
        {
            if (isSub)
                paper.Box(UiId.Underscore("proj_tcind", id)).Width(20).Height(TableRowH).IsNotInteractable();
            else if (hasSubs)
                paper.Box(UiId.Underscore("proj_tccar", id)).Width(15).Height(TableRowH)
                    .StopEventPropagation()
                    .OnClick(item.Guid, (g, _) =>
                    {
                        if (_expandedAssets.Contains(g)) _expandedAssets.Remove(g);
                        else _expandedAssets.Add(g);
                    })
                    .Icon(paper, expanded ? EditorIcons.ChevronDown_I : EditorIcons.ChevronRight_I, EditorTheme.InkDim, size: 11f);
            else
                paper.Box(UiId.Underscore("proj_tcsp", id)).Width(15).Height(TableRowH).IsNotInteractable();

            var ic = paper.Box(UiId.Underscore("proj_tcico", id)).Width(18).Height(TableRowH).Margin(2, 0, 0, 0);
            if (style.Badge != null)
                ic.Text(style.Badge, mono).TextColor(style.Color).FontSize(10f).Alignment(TextAlignment.MiddleCenter);
            else
                ic.Icon(paper, style.Icon, style.IconColor, size: 15f);

            if (RenameOverlay.IsRenaming(UiId.Underscore("proj_asset", item.RelativePath)))
            {
                RenameOverlay.Draw(paper, UiId.Underscore("proj_tcrn", id));
            }
            else
            {
                // Name hugs its text so the sub-count tag sits right beside it; a trailing spacer fills the rest.
                paper.Box(UiId.Underscore("proj_tclbl", id)).Width(UnitValue.Auto).Height(TableRowH).Margin(6, 0, 0, 0).Clip()
                    .Text(DisplayName(item), font)
                    .TextColor(isSelected ? Origami.Current.SelectionText(_contentSelectionFocused)
                        : isSub ? EditorTheme.Ink400 : EditorTheme.Ink500)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);

                if (hasSubs)
                    paper.Box(UiId.Underscore("proj_tctag", id)).Width(UnitValue.Auto).Height(17).Rounded(5).Padding(6, 6, 0, 0).Margin(7, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                        .BackgroundColor(EditorTheme.Selected).BorderColor(Color.FromArgb(77, EditorTheme.Purple400)).BorderWidth(1)
                        .Text(item.Subs.Count.ToString(), semi).TextColor(EditorTheme.AccentText)
                        .FontSize(11f).Alignment(TextAlignment.MiddleCenter);
                else if (isSub)
                    paper.Box(UiId.Underscore("proj_tctag", id)).Width(UnitValue.Auto).Height(17).Rounded(5).Padding(6, 6, 0, 0).Margin(7, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                        .BackgroundColor(EditorTheme.Selected).BorderColor(Color.FromArgb(77, EditorTheme.Purple400)).BorderWidth(1)
                        .Text("sub", semi).TextColor(EditorTheme.AccentText)
                        .FontSize(10f).Alignment(TextAlignment.MiddleCenter);

                paper.Box(UiId.Underscore("proj_tcnsp", id)).Height(TableRowH).IsNotInteractable();
            }
        }
        else if (col == 1)
        {
            paper.Box(UiId.Underscore("proj_tctype", id)).Height(TableRowH).IsNotInteractable()
                .Text(item.IsFolder
                    ? (IsPackageRoot(item.RelativePath) ? item.TypeLabel : Loc.Get("inspector.folder"))
                    : item.TypeLabel, font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
        }
        else
        {
            paper.Box(UiId.Underscore("proj_tcsize", id)).Height(TableRowH).IsNotInteractable()
                .Text(item.IsFolder || isSub ? "-" : FormatSize(item.Size), mono)
                .TextColor(isSub ? EditorTheme.InkFaint : EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight);
        }
    }

    private void BuildItemContextMenu(Paper paper, string id, ContentItem item, bool inTree = false)
        => Origami.RightClickMenu(paper, id, ItemContextMenu(item, inTree));

    // The item context menu as a reusable ContextBuilder action (for RightClickMenu on an element, or
    // Origami.ContextMenu at a point - the table opens it from OnRowContext).
    private Action<ContextBuilder> ItemContextMenu(ContentItem item, bool inTree = false) => builder =>
        {
            // Right-click should select the item if not already selected
            if (!Selection.IsSelected(item))
                Selection.AddToSelection(item);

            bool isMulti = Selection.Count > 1;
            bool isRoot = string.IsNullOrEmpty(item.RelativePath);
            bool isPkg = IsPackagePath(item.RelativePath);
            bool isPkgTree = IsPackagesTreePath(item.RelativePath);
            bool isPkgRoot = IsPackageRoot(item.RelativePath);
            var titleStyle = item.IsFolder
                ? (isPkgRoot || item.RelativePath.Equals("Packages", StringComparison.OrdinalIgnoreCase)
                    ? AssetTypeStyles.Package
                    : AssetTypeStyles.Folder)
                : AssetTypeStyles.For(Path.GetExtension(item.Name), item.TypeLabel);

            builder.Title(isMulti ? Loc.Get("project.item_count", new { count = Selection.Count }) : item.Name, iconDraw: titleStyle.Icon);

            if (isPkgRoot)
            {
                string? sourceLine = PackageSourceLine(item.RelativePath);
                if (sourceLine != null)
                    builder.Item(sourceLine, () => { }, enabled: false);
            }

            if (item.IsFolder)
                builder.Item(Loc.Get("launcher.open"), () => NavigateTo(item.RelativePath), icon: EditorIcons.FolderOpen);
            else
                builder.Item(Loc.Get("launcher.open"), () => OpenWithSystem(item), icon: EditorIcons.FolderOpen);
            builder.Item(Loc.Get("project.show_in_explorer"), () => ShowInExplorer(item), icon: EditorIcons.FolderTree);
            builder.Item(Loc.Get("context.properties"), () => Panels.PropertiesPanel.Open(item), icon: EditorIcons.Sliders);

            if (!isPkgTree)
            {
                builder.Separator();
                builder.Submenu(Loc.Get("project.create"), sub => MenuItemAttribute.BuildContextMenu(sub, "Assets/Create"), icon: EditorIcons.FileCirclePlus);
            }

            builder.Separator();

            if (!isPkgTree)
                builder.Item(Loc.Get("project.rename"), () => StartRename(item, inTree), enabled: !isMulti && !isRoot && CanRename(item), icon: EditorIcons.PenToSquare);
            builder.Item(Loc.Get("project.copy_path"), () => paper_SetClipboard(item.RelativePath), icon: EditorIcons.Copy);
            if (isPkg && TryResolveAbsoluteItemPath(item.RelativePath, out string phys))
                builder.Item(Loc.Get("project.copy_physical_path"), () => paper_SetClipboard(phys), icon: EditorIcons.Copy);
            if (!item.IsFolder && item.Guid != Guid.Empty)
                builder.Item(Loc.Get("project.copy_guid"), () => paper_SetClipboard(item.Guid.ToString()), icon: EditorIcons.Fingerprint);
            if (!item.IsFolder)
                builder.Item(Loc.Get("project.reimport"), () =>
                {
                    var db = EditorAssetBackend.Instance;
                    if (db == null) return;
                    foreach (var sel in Selection.GetSelected<ContentItem>())
                        if (sel.Guid != Guid.Empty) db.Reimport(sel.Guid);
                }, icon: EditorIcons.ArrowsRotate);

            if (!isPkgTree)
            {
                builder.Separator();
                builder.Item(Loc.Get("project.export_package"), () =>
                {
                    var paths = Selection.GetSelected<ContentItem>()
                        .Where(c => !c.IsSubAsset)
                        .SelectMany(c =>
                        {
                            if (c.IsFolder)
                                return XEnginePackage.CollectFolderAssets(c.RelativePath);
                            if (c.Guid != Guid.Empty)
                                return new[] { c.RelativePath };
                            return Enumerable.Empty<string>();
                        })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (paths.Count > 0)
                        PackageExportDialog.Open(paths);
                }, icon: EditorIcons.FileZipper);

                builder.Separator();
                builder.Item(Loc.Get("project.delete"), () => DeleteSelectedItems(), enabled: !isRoot, danger: true, icon: EditorIcons.Trash);
            }
        };

    private void paper_SetClipboard(string text) => _paper?.SetClipboard(text);

    private void BuildFolderTreeContextMenu(Paper paper, string id, string relativePath)
    {
        var item = new ContentItem
        {
            Name = string.IsNullOrEmpty(relativePath) ? "Assets"
                : relativePath.Equals("Packages", StringComparison.OrdinalIgnoreCase) ? "Packages"
                : IsPackageRoot(relativePath) ? PackageDisplayName(Path.GetFileName(relativePath))
                : Path.GetFileName(relativePath),
            RelativePath = relativePath,
            IsFolder = true,
            Icon = FolderNodeIcon(relativePath).Glyph,
            TypeLabel = IsPackageRoot(relativePath) ? "Package" : "Folder"
        };
        BuildItemContextMenu(paper, id, item, inTree: true);
    }

    private void BuildBackgroundContextMenu(Paper paper, string id)
    {
        Origami.RightClickMenu(paper, id, builder =>
        {
            string folder = _currentFolder;
            bool inPackages = IsPackagesTreePath(folder);

            if (!inPackages)
            {
                builder.Submenu(Loc.Get("project.create"), sub => MenuItemAttribute.BuildContextMenu(sub, "Assets/Create"), icon: EditorIcons.FileCirclePlus);
                builder.Separator();
            }

            builder.Item(Loc.Get("project.show_in_explorer"), () =>
            {
                if (TryResolveAbsoluteItemPath(folder, out string absPath))
                    EditorUtils.OpenFileSystemPath(absPath);
            }, icon: EditorIcons.FolderOpen);

            builder.Separator();

            builder.Item(Loc.Get("menu.assets.reimport_all"), () =>
            {
                var db = EditorAssetBackend.Instance;
                if (db == null) return;
                foreach (var e in db.GetAllEntries().ToList())
                    db.Reimport(e.Guid);
                Runtime.Debug.Log("[AssetDatabase] Reimported all assets.");
            }, icon: EditorIcons.ArrowsRotate);
        });
    }

    private void OnCreated(string relativePath)
    {
        // Select the newly created item and enter rename
        var newItem = new ContentItem
        {
            Name = Path.GetFileName(relativePath),
            RelativePath = relativePath,
            IsFolder = Directory.Exists(Path.Combine(Project.Current!.AssetsPath, relativePath)),
            Icon = EditorIcons.File
        };
        Selection.Select(newItem);
        StartRename(newItem);
    }

    /// <summary>
    /// Arrow keys move the active selection through the visible entries: ±1 horizontally,
    /// ±columns vertically in grid mode (±1 in list mode). Shift extends the range.
    /// </summary>
    private void HandleArrowNavigation(Paper paper, List<ContentItem> entries, float width, bool isList)
    {
        if (entries.Count == 0) return;
        int dx = 0, dy = 0;
        if (paper.IsKeyPressedOrRepeating(PaperKey.Left)) dx = -1;
        else if (paper.IsKeyPressedOrRepeating(PaperKey.Right)) dx = 1;
        else if (paper.IsKeyPressedOrRepeating(PaperKey.Up)) dy = -1;
        else if (paper.IsKeyPressedOrRepeating(PaperKey.Down)) dy = 1;
        if (dx == 0 && dy == 0) return;

        // Column count mirrors the flex-wrap math of DrawGridView: cellSize-wide cards with a
        // 6px gap inside a 9px-margined container.
        float cellSize = _thumbnailSize + 8f;
        int cols = isList ? 1 : Math.Max(1, (int)((width - 18f) / (cellSize + 6f)));
        int step = dx != 0 ? dx : dy * cols;

        var active = Selection.GetActiveAs<ContentItem>();
        int idx = -1;
        if (active != null)
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Equals(active)) { idx = i; break; }
        int next = idx < 0 ? 0 : Math.Clamp(idx + step, 0, entries.Count - 1);
        if (next == idx) return;

        bool shift = paper.IsKeyDown(PaperKey.LeftShift) || paper.IsKeyDown(PaperKey.RightShift);
        if (shift)
        {
            var flat = new List<object>(entries.Count);
            foreach (var e in entries) flat.Add(e);
            Selection.HandleListClick(entries[next], flat, next, ctrl: false, shift: true);
        }
        else
            Selection.Select(entries[next]);
    }

    /// <summary>
    /// Ctrl+D: copy each selected file/folder next to itself under a unique name. The .meta is NOT
    /// copied, so the import mints a fresh GUID for the duplicate (folders copied recursively).
    /// An imported AnimationClip sub-asset is extracted as a standalone .anim in its model's
    /// sibling Animations folder instead of copying the parent model.
    /// </summary>
    private void DuplicateSelectedItems()
    {
        var db = EditorAssetBackend.Instance;
        if (db == null || Project.Current == null) return;
        int copied = 0;
        foreach (var sel in Selection.GetSelected<ContentItem>().ToList())
        {
            if (string.IsNullOrEmpty(sel.RelativePath)) continue;
            if (IsPackagesTreePath(sel.RelativePath)) continue;

            // Imported model clips are sub-assets and cannot be copied as files. Ctrl+D on an
            // AnimationClip extracts only that clip into the model's sibling Animations folder;
            // the FBX/GLTF parent and its mesh/material family are deliberately not duplicated.
            if (sel.IsSubAsset)
            {
                string? extractError = null;
                bool extracted = sel.Guid != Guid.Empty
                    && AnimationClipExtractionService.TryExtractSubAsset(
                        db, sel.Guid, out _, out extractError);
                if (extracted)
                {
                    copied++;
                }
                else if (!string.IsNullOrEmpty(extractError))
                {
                    Runtime.Debug.LogWarning($"Duplicate failed for sub-asset '{sel.Name}': {extractError}");
                }
                continue;
            }

            string srcAbs = Path.Combine(Project.Current.AssetsPath, sel.RelativePath);
            string parentAbs = Path.GetDirectoryName(srcAbs)!;
            try
            {
                if (sel.IsFolder && Directory.Exists(srcAbs))
                {
                    string unique = AssetCreateMenu.FindUniqueName(parentAbs, sel.Name, "");
                    CopyDirectoryWithoutMeta(srcAbs, Path.Combine(parentAbs, unique));
                    copied++;
                }
                else if (File.Exists(srcAbs))
                {
                    string unique = AssetCreateMenu.FindUniqueName(parentAbs,
                        Path.GetFileNameWithoutExtension(sel.Name), Path.GetExtension(sel.Name));
                    File.Copy(srcAbs, Path.Combine(parentAbs, unique));
                    copied++;
                }
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogWarning($"Duplicate failed for '{sel.RelativePath}': {ex.Message}");
            }
        }
        if (copied > 0)
            db.InvalidateFolderIndex();
    }

    private static void CopyDirectoryWithoutMeta(string srcDir, string dstDir)
    {
        Directory.CreateDirectory(dstDir);
        foreach (string file in Directory.GetFiles(srcDir))
        {
            if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dstDir, Path.GetFileName(file)));
        }
        foreach (string dir in Directory.GetDirectories(srcDir))
            CopyDirectoryWithoutMeta(dir, Path.Combine(dstDir, Path.GetFileName(dir)));
    }

    private void DeleteSelectedItems()
    {
        var selected = Selection.GetSelected<ContentItem>()
            .Where(s => !string.IsNullOrEmpty(s.RelativePath) && !IsPackagesTreePath(s.RelativePath))
            .ToList();
        if (selected.Count == 0) return;

        string names = selected.Count == 1 ? selected[0].Name : Loc.Get("project.item_count", new { count = selected.Count });

        Origami.Confirm(Loc.Get("dialog.delete_assets"), Loc.Get("project.delete_confirm_body", new { names = names }), () =>
        {
            var db = EditorAssetBackend.Instance;
            if (db == null) return;
            foreach (var sel in selected)
            {
                if (string.IsNullOrEmpty(sel.RelativePath)) continue;
                if (IsPackagesTreePath(sel.RelativePath)) continue;
                if (sel.IsFolder)
                {
                    string absPath = Path.Combine(Project.Current!.AssetsPath, sel.RelativePath);
                    if (Directory.Exists(absPath))
                    {
                        // The whole tree goes to the recycle bin FIRST, as one restorable unit; the
                        // per-asset DeleteAsset pass below then finds the files already gone and only
                        // cleans engine state (dispose instances, GUID index, recompile triggers).
                        string metaPath = MetaFile.GetMetaPath(absPath);
                        RecycleBin.TryDelete(absPath);
                        RecycleBin.TryDelete(metaPath);

                        string prefix = sel.RelativePath + "/";
                        var toDelete = db.GetAllAssetPaths()
                            .Where(p => p.Equals(sel.RelativePath, StringComparison.OrdinalIgnoreCase)
                                     || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var path in toDelete)
                            db.DeleteAsset(path);

                        // Hard-delete fallback when the recycle bin refused (non-Windows, locked file).
                        if (Directory.Exists(absPath))
                            Directory.Delete(absPath, true);
                        if (File.Exists(metaPath)) File.Delete(metaPath);
                        db.InvalidateFolderIndex();
                    }
                    if (_currentFolder == sel.RelativePath || _currentFolder.StartsWith(sel.RelativePath + "/"))
                        _currentFolder = "";
                }
                else
                {
                    db.DeleteAsset(sel.RelativePath);
                }
            }
            Selection.Clear();
        });
    }

    // Renaming a .cs file only renames the file, not the class it declares, breaking the file-name =
    // class-name convention and every serialized reference to that component type. Disabled until proper
    // script renaming (rename the class + serialized type aliasing) is supported.
    private static bool CanRename(ContentItem item)
        => !IsPackagesTreePath(item.RelativePath)
           && (item.IsFolder || !string.Equals(Path.GetExtension(item.Name), ".cs", StringComparison.OrdinalIgnoreCase));

    private void StartRename(ContentItem item, bool inTree = false)
    {
        if (IsPackagesTreePath(item.RelativePath))
            return;
        if (!CanRename(item))
        {
            Toasts.Show(Loc.Get("toast.rename_failed"), "Renaming script files isn't supported yet - it would break the class/file-name link.", ToastType.Warning, 4f);
            return;
        }

        string id = inTree ? $"proj_folder_{item.RelativePath}" : $"proj_asset_{item.RelativePath}";
        string editName = item.IsFolder ? item.Name : Path.GetFileNameWithoutExtension(item.Name);

        RenameOverlay.Begin(id, editName, newText =>
        {
            string ext = item.IsFolder ? "" : Path.GetExtension(item.Name);
            string newName = newText + ext;
            if (newName == item.Name || string.IsNullOrWhiteSpace(newText))
                return;

            string parentFolder = Path.GetDirectoryName(item.RelativePath)?.Replace('\\', '/') ?? "";
            string newRelPath = string.IsNullOrEmpty(parentFolder) ? newName : parentFolder + "/" + newName;

            if (item.IsFolder)
            {
                bool success = AssetUndo.MoveFolder(item.RelativePath, newRelPath);
                if (!success)
                    Toasts.Show(Loc.Get("toast.rename_failed"), Loc.Get("toast.rename_exists", new { name = newName }), ToastType.Warning, 3f);
                else if (_currentFolder == item.RelativePath)
                    _currentFolder = newRelPath;
            }
            else
            {
                bool success = AssetUndo.MoveAsset(item.RelativePath, newRelPath);
                if (!success)
                    Toasts.Show(Loc.Get("toast.rename_failed"), Loc.Get("toast.rename_exists", new { name = newName }), ToastType.Warning, 3f);
            }
        });
    }

    internal static bool IsPackagePath(string relativePath)
        => !string.IsNullOrEmpty(relativePath)
           && relativePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPackageRoot(string relativePath)
        => IsPackagePath(relativePath) && relativePath.IndexOf('/', "Packages/".Length) < 0;

    internal static bool IsPackagesTreePath(string relativePath)
        => !string.IsNullOrEmpty(relativePath)
           && (relativePath.Equals("Packages", StringComparison.OrdinalIgnoreCase) || IsPackagePath(relativePath));

    internal static (string Glyph, Color Color) FolderNodeIcon(string relativePath)
    {
        if (string.Equals(relativePath, "Packages", StringComparison.OrdinalIgnoreCase))
            return (EditorIcons.BoxOpen, EditorTheme.Purple400);
        if (IsPackageRoot(relativePath))
            return (EditorIcons.Box, EditorTheme.Purple400);
        return (EditorIcons.Folder, EditorTheme.Amber400);
    }

    internal static bool TryResolveAbsoluteItemPath(string relativePath, out string absPath)
    {
        absPath = "";
        var project = Project.Current;
        if (project == null) return false;
        relativePath = (relativePath ?? "").Replace('\\', '/');

        if (relativePath.Equals("Packages", StringComparison.OrdinalIgnoreCase))
        {
            absPath = project.PackagesPath;
            return true;
        }

        if (IsPackagePath(relativePath))
            return PackageManager.TryResolveLogicalAssetPath(project, relativePath, out absPath);

        absPath = string.IsNullOrEmpty(relativePath)
            ? project.AssetsPath
            : Path.Combine(project.AssetsPath, relativePath);
        return true;
    }

    private static string PackageDisplayName(string packageName)
    {
        var project = Project.Current;
        var packages = project != null ? PackageManager.PeekCached(project) : null;
        if (packages != null)
        {
            foreach (var pkg in packages)
            {
                if (!pkg.Name.Equals(packageName, StringComparison.OrdinalIgnoreCase)) continue;
                return string.IsNullOrWhiteSpace(pkg.DisplayName) ? pkg.Name : pkg.DisplayName;
            }
        }
        return packageName;
    }

    private static string? PackageSourceLine(string logicalRoot)
    {
        var project = Project.Current;
        var packages = project != null ? PackageManager.PeekCached(project) : null;
        if (packages == null) return null;
        foreach (var pkg in packages)
        {
            if (!pkg.LogicalRoot.Equals(logicalRoot, StringComparison.OrdinalIgnoreCase)) continue;
            return $"{pkg.Source} · {pkg.Version}";
        }
        return null;
    }

    private static void OpenWithSystem(ContentItem item)
    {
        if (!TryResolveAbsoluteItemPath(item.RelativePath, out string absPath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(absPath) { UseShellExecute = true });
        }
        catch { }
    }

    private static void ShowInExplorer(ContentItem item)
    {
        if (TryResolveAbsoluteItemPath(item.RelativePath, out string absPath))
            EditorUtils.OpenFileSystemPath(absPath);
    }

    // ================================================================
    //  Grid View
    // ================================================================

    // Range-selection anchor list for the grid. Rebuilt only when the entries stamp moves: the
    // previous per-frame Select(...).ToList() was a steady allocation on every presented frame.
    private List<object> GridItemObjects(List<ContentItem> entries)
    {
        if (_gridItemObjectsStamp != _entriesStamp || _gridItemObjects.Count != entries.Count)
        {
            _gridItemObjects.Clear();
            foreach (var e in entries) _gridItemObjects.Add(e);
            _gridItemObjectsStamp = _entriesStamp;
        }
        return _gridItemObjects;
    }

    private const float GridGapX = 6f;

    private void DrawGridView(Paper paper, Scribe.FontFile font, List<ContentItem> entries, float width,
        Action<ElementHandle>? postLayout = null)
    {
        float cellSize = _thumbnailSize + 8f;
        var itemObjects = GridItemObjects(entries);
        _gridRowsBuilt = 0;

        // Flex-wrap: cards flow left-to-right and wrap; each card auto-grows in height to fit its
        // (wrapping) name. A full-width sub-asset drawer inserted after an expanded card forces a
        // line break, so it lands on its own row (like the CSS grid's `grid-column: 1/-1`).
        var wrap = paper.Row("proj_grid_wrap").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
            .WrapContent().RowBetween(GridGapX);
        if (postLayout != null) wrap.OnPostLayout((handle, rect) => postLayout(handle));
        using (wrap.Enter())
        {
            for (int i = 0; i < entries.Count; i++)
            {
                DrawGridItem(paper, font, UiId.Cat("proj_gc_", i), entries[i], i, itemObjects, cellSize);
                if (entries[i].Subs.Count > 0 && _expandedAssets.Contains(entries[i].Guid))
                    DrawSubDrawer(paper, font, entries[i], width);
            }
        }
    }

    /// <summary>The grid sits inside <c>proj_content_inner</c>'s 9px margin, so row 0 starts this
    /// far below the scroll content origin.</summary>
    internal const float GridContentOffset = 9f;

    // Banded grid: only the rows intersecting the viewport are built; the rows above and below are
    // replaced by one spacer each whose height keeps the scroll extent identical to the full
    // layout. Cards are a fixed width, so every line holds the same number of cards, but a line
    // is as tall as its tallest label, so rows are not a uniform stride in mixed folders. The row
    // offsets therefore come from one full flow layout of the folder (measured after layout,
    // before paint, and keyed on everything the layout depends on); band frames then verify the
    // rows they built against that table and re-measure on any disagreement. An expanded sub-asset
    // drawer or an active rename changes a card's height, so those frames use the full layout.
    private void DrawGridViewBanded(Paper paper, Scribe.FontFile font, List<ContentItem> entries, float width, ScrollViewport vp)
    {
        if (RenameOverlay.IsActive) { DrawGridView(paper, font, entries, width); return; }
        foreach (var e in entries)
            if (e.Subs.Count > 0 && _expandedAssets.Contains(e.Guid)) { DrawGridView(paper, font, entries, width); return; }

        float cellSize = _thumbnailSize + 8f;
        var key = new ProjectGridRowTable.Key(_entriesStamp, cellSize, width, paper.DisplayFramebufferScale.Y, _showExtensions);
        if (_gridRows.IsDisabled(key)) { DrawGridView(paper, font, entries, width); return; }
        if (!_gridRows.IsValidFor(key))
        {
            // Measurement frame: the full layout once, recording every row's top and the total.
            DrawGridView(paper, font, entries, width, handle => RecordGridRows(paper, handle, key));
            _gridRowsBuilt = (entries.Count + Math.Max(1, _gridRows.Columns) - 1) / Math.Max(1, _gridRows.Columns);
            return;
        }

        var itemObjects = GridItemObjects(entries);
        int columns = _gridRows.Columns;
        var (first, last) = _gridRows.Band(vp.ScrollY, vp.Height, GridContentOffset);
        var (topH, botH) = _gridRows.Spacers(first, last);
        if (topH > 0f)
            paper.Box("proj_grid_vtop").Width(UnitValue.Stretch()).Height(topH).IsNotInteractable();

        _gridRowsBuilt = last >= first ? last - first + 1 : 0;
        using (paper.Row("proj_grid_wrap").Width(UnitValue.Stretch()).Height(UnitValue.Auto)
            .WrapContent().RowBetween(GridGapX)
            .OnPostLayout((this, key, first), static (cap, handle, rect) => cap.Item1.VerifyGridRows(handle.Owner, handle, cap.key, cap.first))
            .Enter())
        {
            int start = first * columns, end = Math.Min(entries.Count, (last + 1) * columns);
            for (int i = start; i < end; i++)
                DrawGridItem(paper, font, UiId.Cat("proj_gc_", i), entries[i], i, itemObjects, cellSize);
        }

        if (botH > 0f)
            paper.Box("proj_grid_vbot").Width(UnitValue.Stretch()).Height(botH).IsNotInteractable();
    }

    /// <summary>Row tops of the wrap row's laid-out children relative to the first card, followed
    /// by the wrap's total height; returns the cards on the first line and that line's height
    /// (its tallest card). Pure bookkeeping.</summary>
    private int CollectGridRowTops(Paper paper, ElementHandle wrap, List<float> rowTops, out float firstLineHeight)
    {
        rowTops.Clear();
        firstLineHeight = 0f;
        var children = wrap.Data.ChildIndices;
        if (children == null || children.Count == 0) return 0;
        float firstY = new ElementHandle(paper, children[0]).Data.Y;
        int columns = 0;
        float lineY = firstY;
        rowTops.Add(0f);
        for (int i = 0; i < children.Count; i++)
        {
            ref var child = ref new ElementHandle(paper, children[i]).Data;
            float y = child.Y;
            if (MathF.Abs(y - firstY) < 0.5f) { columns++; firstLineHeight = MathF.Max(firstLineHeight, (float)child.LayoutHeight); }
            else if (MathF.Abs(y - lineY) >= 0.5f) { lineY = y; rowTops.Add(y - firstY); }
        }
        rowTops.Add((float)wrap.Data.LayoutHeight);
        return columns;
    }

    private void RecordGridRows(Paper paper, ElementHandle wrap, ProjectGridRowTable.Key key)
    {
        int columns = CollectGridRowTops(paper, wrap, _gridRowScratch, out float firstLineHeight);
        // The wrap layout separates lines by a gap that is absent after the last line; a band
        // ending before the last row is therefore one gap shorter than the table distance.
        float lineGap = _gridRowScratch.Count > 2 ? _gridRowScratch[1] - firstLineHeight : 0f;
        _gridRows.Set(key, columns, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_gridRowScratch), lineGap);
    }

    private void VerifyGridRows(Paper paper, ElementHandle wrap, ProjectGridRowTable.Key key, int first)
    {
        int columns = CollectGridRowTops(paper, wrap, _gridRowScratch, out _);
        var tops = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_gridRowScratch);
        float height = tops.Length > 0 ? tops[^1] : 0f;
        if (!_gridRows.Verify(key, columns, first, tops[..Math.Max(0, tops.Length - 1)], height))
            EditorApplication.Instance?.RequestContinuousRedraw(this); // re-measure next frame
    }

    private void DrawSubDrawer(Paper paper, Scribe.FontFile font, ContentItem parent, float width)
    {
        using (paper.Column(UiId.Cat("proj_drawer_", parent.Guid)).Width(UnitValue.Percentage(100)).Height(UnitValue.Auto)
            .Margin(0, 0, 3, 7).Padding(11, 11, 10, 10).Rounded(10)
            .BackgroundColor(Color.FromArgb(61, 0, 0, 0)).BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
            .Enter())
        {
            using (paper.Row(UiId.Cat("proj_drawerlbl_", parent.Guid)).Height(18).Margin(0, 0, 0, 9).Enter())
            {
                paper.Box(UiId.Cat("proj_drawerico_", parent.Guid)).Width(14).Height(18).Margin(0, 6, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                    .Icon(paper, EditorIcons.LayerGroup_I, EditorTheme.AccentText, size: 12f);
                paper.Box(UiId.Cat("proj_drawernm_", parent.Guid)).Width(UnitValue.Auto).Height(18).Margin(0, 6, UnitValue.StretchOne, UnitValue.StretchOne)
                    .Text(parent.Name, EditorTheme.FontSemiBold ?? font).TextColor(EditorTheme.AccentText)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
                paper.Box(UiId.Cat("proj_drawercnt_", parent.Guid)).Width(UnitValue.Auto).Height(18).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                    .Text("- " + Loc.Get("project.sub_asset_count", new { count = parent.Subs.Count }), font).TextColor(EditorTheme.InkDim)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
            }

            using (paper.Row(UiId.Cat("proj_drawerrow_", parent.Guid)).Width(UnitValue.Stretch()).Height(UnitValue.Auto).WrapContent().Enter())
            {
                if (!_subObjectsCache.TryGetValue(parent.Guid, out var subObjects) || subObjects.Count != parent.Subs.Count
                    || (subObjects.Count > 0 && !ReferenceEquals(subObjects[0], parent.Subs[0])))
                {
                    subObjects = new List<object>(parent.Subs.Count);
                    foreach (var s in parent.Subs) subObjects.Add(s);
                    _subObjectsCache[parent.Guid] = subObjects;
                }
                for (int i = 0; i < parent.Subs.Count; i++)
                    DrawSubThumb(paper, font, parent.Subs[i], i, subObjects);
            }
        }
    }

    private void DrawSubThumb(Paper paper, Scribe.FontFile font, ContentItem sub, int idx, List<object> subObjects)
    {
        var style = AssetTypeStyles.For(Path.GetExtension(sub.Name), sub.TypeLabel);
        bool isSelected = Selection.IsSelected(sub);
        Color selectionFill = Origami.Current.SelectionBackground(_contentSelectionFocused);

        using (paper.Column(UiId.Cat("proj_sub_", sub.Guid)).Width(62).Height(UnitValue.Auto).Margin(0, 6, 0, 6)
            .Padding(3, 3, 5, 5).Rounded(8).ColBetween(5)
            .BackgroundColor(isSelected ? selectionFill : Color.Transparent)
            .Hovered.BackgroundColor(isSelected ? selectionFill : EditorTheme.Hover).End()
            .OnClick((sub, idx, subObjects), (cap, e) =>
            {
                e.StopPropagation();
                bool ctrl = _paper?.IsKeyDown(PaperKey.LeftControl) == true || _paper?.IsKeyDown(PaperKey.RightControl) == true;
                bool shift = _paper?.IsKeyDown(PaperKey.LeftShift) == true || _paper?.IsKeyDown(PaperKey.RightShift) == true;
                Selection.HandleListClick(cap.Item1, (IReadOnlyList<object>)cap.Item3, cap.Item2, ctrl, shift);
            })
            .OnDragStart(sub, (s, _) =>
            {
                var db = EditorAssetBackend.Instance;
                Type? subType = db?.GetSubAssets(s.ParentGuid).FirstOrDefault(x => x.Guid == s.Guid)?.Type;
                DragDrop.StartDrag(new AssetDragPayload(s.Guid, s.Name, subType));
            })
            .Tooltip(sub.Name)
            .Enter())
        {
            var thumbTex = EditorAssetBackend.Instance?.GetThumbnailTexture(sub.Guid);
            if (thumbTex != null)
            {
                paper.Box(UiId.Cat("proj_subth_", sub.Guid)).Width(42).Height(42).Margin(UnitValue.StretchOne, UnitValue.StretchOne, 0, 0)
                    .OnPostLayout((handle, rect) => DrawThumbnailTile(paper, ref handle, thumbTex, 8f));
            }
            else
            {
                paper.Box(UiId.Cat("proj_subth_", sub.Guid)).Width(42).Height(42).Margin(UnitValue.StretchOne, UnitValue.StretchOne, 0, 0).Rounded(8)
                    .BackgroundLinearGradient(0, 0, 1, 1, Color.FromArgb(58, style.Color), Color.FromArgb(16, style.Color))
                    .BorderColor(Color.FromArgb(68, style.Color)).BorderWidth(1)
                    .Icon(paper, style.Icon, style.IconColor, size: 20f);
            }

            paper.Box(UiId.Cat("proj_subnm_", sub.Guid)).Width(UnitValue.Stretch()).Height(14).Clip()
                .Text(sub.Name, font).TextColor(isSelected ? Origami.Current.SelectionText(_contentSelectionFocused) : EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);

            BuildItemContextMenu(paper, UiId.Cat("proj_sub_ctx_", sub.Guid), sub);
        }
    }

    private static readonly object s_pingPaintSource = new();

    /// <summary>Rounded thumbnail image plus a 1px soft border. Versioned on the texture identity
    /// (thumbnail regeneration creates a new <see cref="Runtime.Resources.Texture2D"/>), the corner
    /// radius and the border colour, so a compared paint region keeps the tile cached until one of
    /// those inputs actually changes.</summary>
    private static void DrawThumbnailTile(Paper paper, ref ElementHandle handle, Runtime.Resources.Texture2D thumbTex, float radius)
    {
        var border = EditorTheme.BorderSoft;
        ulong version = unchecked((uint)BitConverter.SingleToInt32Bits(radius)) | ((ulong)unchecked((uint)border.ToArgb()) << 32);
        paper.DrawVersioned(ref handle, "project.thumb", version, 0, thumbTex, null, (canvas, r) =>
        {
            float x = (float)r.Min.X, y = (float)r.Min.Y, w = (float)r.Size.X, h = (float)r.Size.Y;
            canvas.DrawImageRounded(thumbTex, x, y, w, h, radius);
            var bd = XEngine.Vector.Color32.FromArgb(border.A, border.R, border.G, border.B);
            canvas.SaveState();
            canvas.SetStrokeColor(bd);
            canvas.SetStrokeWidth(1f);
            canvas.BeginPath();
            canvas.RoundedRect(x + 0.5f, y + 0.5f, w - 1f, h - 1f, radius);
            canvas.Stroke();
            canvas.RestoreState();
        });
    }

    private void DrawGridItem(Paper paper, Scribe.FontFile font, string id, ContentItem item,
        int idx, List<object> itemObjects, float cellSize)
    {
        bool isSelected = Selection.IsSelected(item);
        bool isSubAsset = item.IsSubAsset;
        bool isPinged = item.Guid != Guid.Empty && item.Guid == Selection.PingedGuid;
        Color selectionFill = Origami.Current.SelectionBackground(_contentSelectionFocused);
        _gridCardsBuilt++;

        using (paper.Column(id)
            .Width(cellSize).Height(UnitValue.Auto)
            .BackgroundColor(isSelected ? selectionFill : (isSubAsset ? Color.FromArgb(20, EditorTheme.Accent) : Color.Transparent))
            .BorderColor(isSelected ? Color.FromArgb(102, EditorTheme.Purple400) : Color.Transparent).BorderWidth(1)
            .Hovered.BackgroundColor(isSelected ? selectionFill : EditorTheme.Hover).End()
            .Rounded(9)
            .OnClick((item, idx, itemObjects), (cap, e) =>
            {
                e.StopPropagation();
                bool ctrl = _paper?.IsKeyDown(PaperKey.LeftControl) == true || _paper?.IsKeyDown(PaperKey.RightControl) == true;
                bool shift = _paper?.IsKeyDown(PaperKey.LeftShift) == true || _paper?.IsKeyDown(PaperKey.RightShift) == true;
                Selection.HandleListClick(cap.Item1, (IReadOnlyList<object>)cap.Item3, cap.Item2, ctrl, shift);
            })
            .OnDoubleClick(item, (it, _) =>
            {
                if (it.IsFolder)
                    NavigateTo(it.RelativePath);
                else if (it.HasSubAssets)
                {
                    if (_expandedAssets.Contains(it.Guid)) _expandedAssets.Remove(it.Guid);
                    else _expandedAssets.Add(it.Guid);
                }
                else if (!EditorSceneManager.HandleAssetDoubleClick(it.RelativePath, it.Guid))
                    OpenWithSystem(it); // no registered handler: open with the OS association, like Unity
            })
            .OnDragStart(item, (it, _) =>
            {
                if (it.IsSubAsset && it.Guid != Guid.Empty)
                {
                    var db = EditorAssetBackend.Instance;
                    Type? subType = db?.GetSubAssets(it.ParentGuid).FirstOrDefault(s => s.Guid == it.Guid)?.Type;
                    DragDrop.StartDrag(new AssetDragPayload(it.Guid, it.Name, subType));
                    return;
                }

                var payload = BuildAssetDragPayload(it);
                if (payload != null) DragDrop.StartDrag(payload);
            })
            .OnHover(item, (it, _) =>
            {
                if (!it.IsFolder) return;
                if (DragDrop.IsDragging || DragDrop.IsDropFrame) _dragHoverFolderNext = it.RelativePath;
            })
            .Tooltip(item.Name)
            .OnPostLayout((handle, rect) =>
            {
                if (!isPinged) return;
                // Alpha sampled at build time so the versioned draw's inputs are exact: the ping
                // animation advances by rebuilding each frame, and the region recaptures only while
                // the alpha actually changes.
                float alpha = Selection.GetPingAlpha();
                paper.DrawVersioned(ref handle, "project.ping", unchecked((uint)BitConverter.SingleToInt32Bits(alpha)), 0, s_pingPaintSource, null, (canvas, r) =>
                {
                    if (alpha <= 0f) return;
                    int fillA = (int)(alpha * 60);
                    int borderA = (int)(alpha * 200);
                    var fillColor = Color.FromArgb(fillA, 255, 220, 50);
                    var borderColor = Color.FromArgb(borderA, 255, 200, 0);
                    float x = (float)r.Min.X, y = (float)r.Min.Y;
                    float w = (float)r.Size.X, h = (float)r.Size.Y;
                    canvas.RoundedRectFilled(x, y, w, h, 4, 4, 4, 4, fillColor);
                    canvas.SetStrokeColor(borderColor);
                    canvas.SetStrokeWidth(2f);
                    canvas.BeginPath();
                    canvas.RoundedRect(x + 1, y + 1, w - 2, h - 2, 3, 3, 3, 3);
                    canvas.Stroke();
                });
            })
            .Enter())
        {
            bool hasSubs = item.Subs.Count > 0;

            // Stacked "cards" peeking out behind the thumbnail signal that this asset has sub-assets.
            if (hasSubs)
            {
                float ts = cellSize - 8;
                paper.Box(UiId.Underscore(id, "stk2")).PositionType(PositionType.SelfDirected).Position(10, 10).Size(ts, ts)
                    .Rounded(10).BackgroundColor(Color.FromArgb(130, 30, 24, 44)).BorderColor(EditorTheme.BorderSoft).BorderWidth(1).IsNotInteractable();
                paper.Box(UiId.Underscore(id, "stk1")).PositionType(PositionType.SelfDirected).Position(7, 7).Size(ts, ts)
                    .Rounded(10).BackgroundColor(Color.FromArgb(235, 30, 24, 44)).BorderColor(EditorTheme.BorderSoft).BorderWidth(1).IsNotInteractable();
            }

            // Thumbnail area
            var thumbTex = EditorAssetBackend.Instance?.GetThumbnailTexture(item.Guid);
            if (thumbTex != null)
            {
                // Rounded image tile (texture-brushed rounded rect) + a matching rounded border.
                paper.Box(UiId.Underscore(id, "t"))
                    .Width(cellSize - 8).Height(cellSize - 8)
                    .Margin(4, 4, 4, 0)
                    .OnPostLayout((handle, rect) => DrawThumbnailTile(paper, ref handle, thumbTex, 10f));
            }
            else
            {
                var style = item.IsFolder
                    ? (IsPackageRoot(item.RelativePath) ? AssetTypeStyles.Package : AssetTypeStyles.Folder)
                    : item.IsSubAsset ? AssetTypeStyles.SubAsset
                    : AssetTypeStyles.For(Path.GetExtension(item.Name), item.TypeLabel);

                float tileSz = cellSize - 8;
                var tile = paper.Box(UiId.Underscore(id, "t")).Width(tileSz).Height(tileSz).Margin(4, 4, 4, 0).Rounded(10);

                if (style.Bare)
                {
                    // Folder: a bare icon, no tile.
                    tile.Icon(paper, style.Icon, style.IconColor, size: tileSz * 0.62f);
                }
                else
                {
                    // Tinted gradient tile
                    tile.BackgroundLinearGradient(0, 0, 1, 1, Color.FromArgb(58, style.Color), Color.FromArgb(16, style.Color))
                        .BorderColor(Color.FromArgb(80, style.Color)).BorderWidth(1);
                    if (style.Badge != null)
                        tile.Text(style.Badge, EditorTheme.FontMono ?? font).TextColor(style.Color)
                            .FontSize(tileSz * 0.4f).Alignment(TextAlignment.MiddleCenter);
                    else
                        tile.Icon(paper, style.Icon, style.IconColor, size: tileSz * 0.5f);
                }
            }

            // Expand indicator for items with sub-assets
            // Sub-asset count badge (top-right pill); clicking toggles the sub-asset drawer below the row.
            if (hasSubs)
            {
                bool expanded = _expandedAssets.Contains(item.Guid);
                using (paper.Row(UiId.Underscore(id, "sb")).PositionType(PositionType.SelfDirected).Position(cellSize - 34, -2)
                    .Width(UnitValue.Auto).Height(17).Rounded(9).Padding(6, 6, 0, 0).RowBetween(3)
                    .BackgroundColor(EditorTheme.Accent).DropShadow(0, 2, 8, 0, Color.FromArgb(128, 0, 0, 0))
                    .StopEventPropagation()
                    .OnClick(item.Guid, (guid, _) =>
                    {
                        if (_expandedAssets.Contains(guid)) _expandedAssets.Remove(guid);
                        else _expandedAssets.Add(guid);
                    })
                    .Enter())
                {
                    paper.Box(UiId.Underscore(id, "sbico")).Width(11).Height(17).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                        .Icon(paper, expanded ? EditorIcons.ChevronDown_I : EditorIcons.LayerGroup_I, Color.White, size: 10f);
                    paper.Box(UiId.Underscore(id, "sbn")).Width(UnitValue.Auto).Height(17).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).IsNotInteractable()
                        .Text(item.Subs.Count.ToString(), EditorTheme.FontBold ?? font).TextColor(Color.White)
                        .FontSize(9.5f).Alignment(TextAlignment.MiddleCenter);
                }
            }

            // Label - a flow child that WRAPS and grows the card's height (no clipping/truncation).
            if (RenameOverlay.IsRenaming(UiId.Underscore("proj_asset", item.RelativePath)))
            {
                RenameOverlay.Draw(paper, UiId.Underscore(id, "rename"), RenameOverlay.Position.Bottom);
            }
            else
            {
                paper.Box(UiId.Underscore(id, "l"))
                    .Width(UnitValue.Stretch()).Height(UnitValue.Auto)
                    .Margin(3, 3, 4, 6)
                    .Wrap(XEngine.Scribe.TextWrapMode.Wrap)
                    .Text(DisplayName(item), EditorTheme.FontMedium ?? font)
                    .TextColor(isSelected ? Origami.Current.SelectionText(_contentSelectionFocused)
                        : isSubAsset ? EditorTheme.AccentText : EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.Center);
            }

            BuildItemContextMenu(paper, UiId.Underscore(id, "ctx"), item);

            // Folder grid items are drop targets highlight overlay painted when the cursor
            // is over this item during a valid drag. Central dispatch in OnGUI.
            if (item.IsFolder && _dragHoverFolder == item.RelativePath
                && (DragDrop.IsDraggingType<GameObjectDragPayload>()
                    || (DragDrop.IsDraggingType<AssetDragPayload>() && CanAcceptAssetDropInto(item.RelativePath))))
            {
                paper.Box(UiId.Underscore(id, "drop"))
                    .PositionType(PositionType.SelfDirected)
                    .Position(0, 0).Size(UnitValue.Stretch(), UnitValue.Stretch())
                    .Rounded(4).IsNotInteractable()
                    .BackgroundColor(Color.FromArgb(40, EditorTheme.Purple400))
                    .BorderColor(EditorTheme.Purple400).BorderWidth(2);
            }
        }
    }

    // ================================================================
    //  Content Item Helpers
    // ================================================================

    internal long ComputeContentCacheStamp(EditorAssetBackend db)
        => ((long)db.FolderIndexVersion << 32)
           ^ (uint)_currentFolder.GetHashCode()
           ^ ((long)_searchText.GetHashCode() << 1)
           ^ ((long)_sortBy << 16)
           ^ (_showHidden ? 1L << 20 : 0)
           ^ (_groupByType ? 1L << 21 : 0)
           ^ ((long)VirtualContentItems.Count << 22);

    private long ComputeFolderTreeStamp()
    {
        var db = EditorAssetBackend.Instance;
        int packages = 0;
        var project = Project.Current;
        if (project != null)
            packages = PackageManager.PeekCached(project)?.Count ?? 0;
        return ((long)(db?.FolderIndexVersion ?? 0) << 32) ^ (uint)packages;
    }

    private IEnumerable<(ContentItem item, List<ContentItem> subs)> SortUnits(List<(ContentItem item, List<ContentItem> subs)> units)
    {
        IEnumerable<(ContentItem item, List<ContentItem> subs)> sorted = _sortBy switch
        {
            SortMode.Type     => units.OrderBy(u => u.item.TypeLabel, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.item.Name, StringComparer.OrdinalIgnoreCase),
            SortMode.Size     => units.OrderByDescending(u => u.item.Size),
            SortMode.Modified => units.OrderByDescending(u => u.item.Modified),
            _                 => units.OrderBy(u => u.item.Name, StringComparer.OrdinalIgnoreCase),
        };
        if (_groupByType && _sortBy != SortMode.Type)
            sorted = sorted.OrderBy(u => u.item.TypeLabel, StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    /// <summary>
    /// Flat search over the entire asset index (t:Type / l:Label / name words). Modified comes
    /// from the index; Size is a stat on matches only, so a broad query stays cheap.
    /// </summary>
    private List<ContentItem> SearchAllAssets(EditorAssetBackend db, SearchQuery query)
    {
        var units = new List<(ContentItem item, List<ContentItem> subs)>();
        foreach (var entry in db.GetAllEntries())
        {
            if (string.IsNullOrEmpty(entry.Path)) continue;
            string fileName = Path.GetFileName(entry.Path);
            if (!_showHidden && fileName.StartsWith('.')) continue;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            // Unimported assets have no MainAssetType yet; the extension's canonical type name
            // keeps t:AnimationClip etc. matching them anyway.
            string typeLabel = entry.MainAssetType?.Name
                ?? AssetTypeStyles.TypeNameForExtension(ext)
                ?? ext.TrimStart('.').ToUpperInvariant();
            if (!query.Matches(fileName, typeLabel, entry.Labels)) continue;

            bool hasSubAssets = entry.SubAssets != null && entry.SubAssets.Length > 0;
            var item = new ContentItem
            {
                Name = fileName, RelativePath = entry.Path, IsFolder = false,
                Icon = GetFileIcon(ext), TypeLabel = typeLabel,
                Guid = entry.Guid, HasSubAssets = hasSubAssets,
                Modified = new DateTime(entry.LastModifiedTicks, DateTimeKind.Utc),
            };
            try
            {
                if (TryResolveAbsoluteItemPath(entry.Path, out string abs) && File.Exists(abs))
                    item.Size = new FileInfo(abs).Length;
            }
            catch { }

            if (hasSubAssets)
                foreach (var sub in entry.SubAssets)
                    item.Subs.Add(new ContentItem
                    {
                        Name = sub.Name, RelativePath = $"{entry.Path}#{sub.Name}", IsFolder = false,
                        IsSubAsset = true, Icon = GetSubAssetIcon(sub.Type),
                        TypeLabel = sub.Type?.Name ?? "Unknown", Guid = sub.Guid, ParentGuid = entry.Guid,
                    });
            units.Add((item, item.Subs));
        }

        var items = new List<ContentItem>();
        foreach (var u in SortUnits(units)) items.Add(u.item);
        return items;
    }

    private List<ContentItem> GetContentEntries(EditorAssetBackend db)
    {
        long stamp = ComputeContentCacheStamp(db);
        if (stamp == _entriesStamp)
            return _cachedEntries;

        // The shown folder can vanish underneath the panel (deleted or renamed outside the editor,
        // package removed). Fall back to the nearest ancestor that still exists instead of showing
        // an empty view for a path that no longer resolves; the stamp mixes in the folder so this
        // re-runs only when the index or the folder changes. Package paths are exempt: the index
        // lists them only once the package manager has resolved, and that resolve can land later
        // than the first paint.
        if (_currentFolder.Length > 0 && !IsPackagesTreePath(_currentFolder) && !db.FolderExists(_currentFolder))
        {
            _currentFolder = NearestExistingAncestor(db, _currentFolder);
            stamp = ComputeContentCacheStamp(db);
        }

        // A non-empty search leaves folder browsing entirely: the whole project is searched flat
        // with Unity's filter dialect (bare words, t:Type, l:Label). The stamp above already mixes
        // in the search text, so this branch re-runs only when the query or the index changes.
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var results = SearchAllAssets(db, SearchQuery.Parse(_searchText));
            _cachedEntries.Clear();
            _cachedEntries.AddRange(results);
            _entriesStamp = stamp;
            return _cachedEntries;
        }

        // Folders and files come from the asset database's cached index (single source of truth),
        // not per-frame filesystem calls.
        var folders = new List<ContentItem>();
        foreach (var sub in db.GetSubFolders(_currentFolder ?? ""))
        {
            if (!_showHidden && sub.Name.StartsWith('.')) continue;
            bool pkgRoot = IsPackageRoot(sub.RelativePath);
            folders.Add(new ContentItem
            {
                Name = sub.Name, RelativePath = sub.RelativePath, IsFolder = true,
                Icon = FolderNodeIcon(sub.RelativePath).Glyph,
                TypeLabel = pkgRoot ? "Package" : "Folder",
            });
        }
        folders = folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // Files gathered as units (a top-level file plus any expanded sub-assets) so sorting keeps
        // sub-assets attached to their parent.
        var units = new List<(ContentItem item, List<ContentItem> subs)>();
        foreach (var fileRec in db.GetFolderFiles(_currentFolder ?? ""))
        {
            string fileName = fileRec.Name;
            if (!_showHidden && fileName.StartsWith('.')) continue;

            string relPath = fileRec.RelativePath;
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            var entry = db.GetEntry(relPath);
            bool hasSubAssets = entry?.SubAssets != null && entry.SubAssets.Length > 0;

            var item = new ContentItem
            {
                Name = fileName, RelativePath = relPath, IsFolder = false,
                Icon = GetFileIcon(ext),
                TypeLabel = entry?.MainAssetType?.Name
                    ?? AssetTypeStyles.TypeNameForExtension(ext)
                    ?? ext.TrimStart('.').ToUpperInvariant(),
                Guid = entry?.Guid ?? Guid.Empty, HasSubAssets = hasSubAssets,
                Size = fileRec.Size, Modified = fileRec.Modified,
            };

            // Sub-assets are ALWAYS gathered onto the parent (item.Subs); the views decide whether
            // to reveal them (grid drawer / expandable table rows) based on _expandedAssets.
            if (hasSubAssets && entry != null)
                foreach (var sub in entry.SubAssets)
                    item.Subs.Add(new ContentItem
                    {
                        Name = sub.Name, RelativePath = $"{relPath}#{sub.Name}", IsFolder = false,
                        IsSubAsset = true, Icon = GetSubAssetIcon(sub.Type),
                        TypeLabel = sub.Type?.Name ?? "Unknown", Guid = sub.Guid, ParentGuid = entry.Guid,
                    });

            units.Add((item, item.Subs));
        }

        var sorted = SortUnits(units);

        var items = new List<ContentItem>();
        items.AddRange(folders);
        items.AddRange(VirtualContentItems);
        foreach (var u in sorted) items.Add(u.item);  // subs live on item.Subs, not flattened

        _cachedEntries.Clear();
        _cachedEntries.AddRange(items);
        _entriesStamp = stamp;
        return _cachedEntries;
    }

    // Display name honouring the "Show Extensions" toggle (folders + sub-assets always show as-is).
    private string DisplayName(ContentItem item) =>
        (_showExtensions || item.IsFolder || item.IsSubAsset) ? item.Name : Path.GetFileNameWithoutExtension(item.Name);

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "-";
        string[] u = { "B", "KB", "MB", "GB" };
        double s = bytes; int i = 0;
        while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
        return $"{(i == 0 ? s.ToString("0") : s.ToString("0.#"))} {u[i]}";
    }

    private static string GetFileIcon(string ext) => EditorRegistries.GetFileIconForExtension(ext);

    private static string GetSubAssetIcon(Type? type)
    {
        if (type == null) return EditorIcons.File;
        if (typeof(Runtime.Resources.Mesh).IsAssignableFrom(type)) return EditorIcons.VectorSquare;
        if (typeof(Runtime.Resources.Material).IsAssignableFrom(type)) return EditorIcons.Palette;
        if (typeof(AnimationClip).IsAssignableFrom(type)) return EditorIcons.Film;
        if (typeof(Runtime.Resources.Texture2D).IsAssignableFrom(type)) return EditorIcons.FileImage;
        return EditorIcons.File;
    }

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        state["folder"] = _currentFolder;
        state["treeWidth"] = _treeWidth;
        state["thumbSize"] = _thumbnailSize;
        state["sortBy"] = (int)_sortBy;
        state["showExtensions"] = _showExtensions;
        state["showHidden"] = _showHidden;
        state["groupByType"] = _groupByType;
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        // A folder deleted since last session just renders empty; crumbs still navigate back up.
        _currentFolder = state["folder"]?.GetValue<string>() ?? _currentFolder;
        _treeWidth = Math.Clamp(state["treeWidth"]?.GetValue<float>() ?? _treeWidth, 120f, 400f);
        _thumbnailSize = Math.Clamp(state["thumbSize"]?.GetValue<float>() ?? _thumbnailSize, 20f, 128f);
        if (state["sortBy"]?.GetValue<int>() is int sort && sort >= 0 && sort <= (int)SortMode.Modified)
            _sortBy = (SortMode)sort;
        _showExtensions = state["showExtensions"]?.GetValue<bool>() ?? _showExtensions;
        _showHidden = state["showHidden"]?.GetValue<bool>() ?? _showHidden;
        _groupByType = state["groupByType"]?.GetValue<bool>() ?? _groupByType;
    }
}

/// <summary>
/// Represents a single item (file or folder) in the Project panel content view.
/// </summary>
public class ContentItem
{
    public string Name = "";
    public string RelativePath = "";
    public bool IsFolder;
    public bool IsSubAsset;
    public string Icon = "";
    public string TypeLabel = "";
    public Guid Guid;
    public Guid ParentGuid; // For sub-assets: the parent file's GUID
    public bool HasSubAssets; // True if this file has expandable sub-assets
    public long Size;         // File size in bytes (0 for folders/virtual)
    public DateTime Modified; // Last write time (UTC)
    public List<ContentItem> Subs = new(); // Sub-assets of this file (populated for both views)

    public override bool Equals(object? obj) => obj is ContentItem c && c.Guid == Guid && c.RelativePath == RelativePath;
    public override int GetHashCode() => Guid != Guid.Empty ? Guid.GetHashCode() : RelativePath.GetHashCode();
}
