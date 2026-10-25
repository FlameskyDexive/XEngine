using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using XEngine.Editor.AnimationEditor;
using XEngine.Animation;
using XEngine.OrigamiUI;
using XEngine.Editor.Inspector;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Color = System.Drawing.Color;
using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.Editor.Projects;
using XEngine.Editor.GUI;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
namespace XEngine.Editor.GUI.Panels;

public class InspectorPanel : DockPanel, IScriptReloadCleanup
{
    [MenuItem("Window/General/Inspector", priority: 3)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(InspectorPanel));

    public override string Title => Loc.Get("panel.inspector");
    public override string Icon => EditorIcons.Sliders;

    // ── Incremental paint (Program.InspectorPaintIncremental) ────────────────────────────
    // One compared subtree region wraps the property scroll body (header, transform, component
    // grids, add-component). The preview bar stays on the host: it blits a live scene / clip
    // texture and its transport widgets animate. Regions are recreated when the panel changes
    // host Paper and released on close / before a script reload.
    private UiPaperSubtreeRegion? _scrollPaintRegion;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _paintGeometryBudget = new(8 * 1024 * 1024);
    private readonly UiMemoryBudget _paintAtlasBudget = new(16 * 1024 * 1024);

    internal static bool PaintIncremental => Program.InspectorPaintIncremental;
    internal long ScrollRegionCaptures => _scrollPaintRegion?.CaptureCount ?? -1;

    private void DisposePaintRegions()
    {
        _scrollPaintRegion?.Dispose(); _scrollPaintRegion = null;
        _paintRegionPaper = null;
    }

    /// <summary>Attach (creating on demand) the property-scroll paint region to the current parent.
    /// No-op unless the incremental flag is set and the renderer can paint to a separate layer.</summary>
    private void AttachPaintRegion(Paper paper)
    {
        if (!Program.InspectorPaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
        if (!ReferenceEquals(_paintRegionPaper, paper))
        {
            _scrollPaintRegion?.Dispose(); _scrollPaintRegion = null;
            _paintRegionPaper = paper;
        }
        if (_scrollPaintRegion == null)
        {
            paper.StabilizeFontAtlasBeforeRender = true;
            paper.Canvas.EnableFontAtlasRetirement();
            _scrollPaintRegion = new UiPaperSubtreeRegion(paper, _paintGeometryBudget, _paintAtlasBudget, 512,
                shareHostFonts: true, compareVisualState: true);
        }
        _scrollPaintRegion.Attach(paper.CurrentParent);
    }

    // Remember the last non-folder selection so navigating folders doesn't clear the inspector.
    private object? _lastInspectable;
    private bool _subscribed;

    // Inspector lock: while locked, the panel keeps editing the selection captured at lock time
    // and ignores Selection changes (Unity's inspector lock pin).
    private bool _locked;
    private readonly List<object> _lockedSelection = new();
    private static readonly List<GameObject> s_lockedGameObjects = new();

    // Debug mode: raw field grids (private fields included, read-only unless [SerializeField])
    // instead of custom editors, and raw asset data instead of importer editors (Unity ⋮ > Debug).
    private bool _debugMode;

    // ── Universal preview bar (bottom of inspector, collapsible + drag-resizable) ──
    private bool _previewExpanded = true;
    private const float PreviewHeaderHeight = 24f;
    private const float PreviewContentHeight = 256f;   // default height
    private const float PreviewMinHeight = 96f;
    private const float PreviewMaxHeight = 720f;
    private const float PreviewOverlayHeight = AnimationEditorChrome.ToolbarHeight;
    // Session-persistent drag height (survives panel re-creates; Unity keeps the same via layout).
    private static float s_previewHeight = PreviewContentHeight;
    private static bool s_draggingPreviewSplit;
    private static float s_previewDragStartY;
    private static float s_previewDragStartHeight;
    private static float s_lastPreviewW, s_lastPreviewH = PreviewContentHeight;
    private PreviewWidget? _previewWidget;
    private static readonly List<GameObject> s_selectedGameObjects = new();
    private Guid _previewAvatarKey;
    // Last package asset editor that drew into the preview bar; its render resources must be
    // released on hot-reload (the editor singleton itself lives in EditorRegistries' cache).
    private Inspector.AssetImporterEditor? _customPreviewEditor;

    // The cached inspectable is usually a scene GameObject/component (a user type), which would
    // pin the script AssemblyLoadContext across a hot-reload. Drop it before the unload.
    // The preview widget must also be disposed — its PreviewRenderer holds a Scene with
    // instantiated model/prefab GameObjects that may carry user-script Component types.
    public void OnScriptReloadCleanup()
    {
        if (_subscribed)
        {
            Selection.OnSelectionChanged -= OnSelectionChanged;
            _subscribed = false;
        }
        DisposePaintRegions();
        _lastInspectable = null;
        _locked = false;
        _lockedSelection.Clear();
        _previewWidget?.Dispose();
        _previewWidget = null;
        _customPreviewEditor?.OnPreviewCleanup();
        _customPreviewEditor = null;
        XEngine.OrigamiUI.PropertyGridRenderer.ClearTransientCaches();
    }

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        // Selection is global, so the Inspector is the natural owner of its persistence.
        // Only GameObjects round-trip here arbitrary objects (assets, etc.) would need
        // their own addressing scheme and currently aren't stable enough to restore.
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var go in Selection.GetSelected<GameObject>())
            arr.Add(go.Identifier.ToString());
        if (arr.Count > 0)
            state["selection"] = arr;
        state["debugMode"] = _debugMode;
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        if (state["debugMode"] is System.Text.Json.Nodes.JsonValue dbg && dbg.TryGetValue<bool>(out var dm))
            _debugMode = dm;

        if (state["selection"] is not System.Text.Json.Nodes.JsonArray arr) return;

        var scene = Runtime.Resources.Scene.Current;
        if (scene == null) return;

        bool first = true;
        foreach (var node in arr)
        {
            string? guidStr = node?.GetValue<string>();
            if (!Guid.TryParse(guidStr, out var guid)) continue;
            var go = scene.FindObjectByIdentifier<GameObject>(guid);
            if (go == null) continue;

            if (first) { Selection.Select(go); first = false; }
            else Selection.AddToSelection(go);
        }
    }

    private static bool IsFolderSelection(object? obj)
        => obj is ContentItem ci && ci.IsFolder;

    /// <summary>
    /// Resolve a multi-selection to same-concrete-type EngineObjects (ContentItems resolve to
    /// their loaded main asset). Returns false for mixed kinds, dead lookups, or non-asset objects.
    /// </summary>
    private static bool TryResolveMultiAssets(IReadOnlyList<object> selected, out List<EngineObject> assets)
    {
        assets = new List<EngineObject>(selected.Count);
        Type? commonType = null;
        for (int i = 0; i < selected.Count; i++)
        {
            EngineObject? eo = selected[i] switch
            {
                EngineObject direct => direct,
                ContentItem { IsFolder: false, IsSubAsset: false } ci => ResolveAsset(ci),
                _ => null,
            };
            if (eo == null) return false;

            var t = eo.GetType();
            if (commonType == null) commonType = t;
            else if (t != commonType) return false;
            assets.Add(eo);
        }
        return assets.Count > 1;
    }

    private static EngineObject? ResolveAsset(ContentItem ci)
    {
        Guid guid = ci.Guid;
        if (guid == Guid.Empty)
            guid = EditorAssetBackend.Instance?.GetEntry(ci.RelativePath)?.Guid ?? Guid.Empty;
        return guid == Guid.Empty ? null : Runtime.AssetDatabase.Get(guid);
    }

    public override float HeaderWidth => 56f;

    public override void OnHeaderContent(Paper paper, float width, float height)
    {
        DrawHeaderToggle(paper, "insp_hdr_lock", _locked ? EditorIcons.Lock : EditorIcons.LockOpen,
            _locked, ToggleLock);
        DrawHeaderToggle(paper, "insp_hdr_debug", EditorIcons.Bug, _debugMode, () => _debugMode = !_debugMode);
    }

    /// <summary>A 24x24 tab-bar toggle that tints while active (lock / debug mode).</summary>
    private static void DrawHeaderToggle(Paper paper, string id, string icon, bool active, Action onClick)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        paper.Box(id).Width(24).Height(24).Rounded(6).Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
            .BackgroundColor(active ? EditorTheme.Selected : Color.Transparent)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Text(icon, font).TextColor(active ? EditorTheme.AccentText : EditorTheme.Ink300)
            .FontSize(13f).Alignment(TextAlignment.MiddleCenter)
            .OnClick(_ => onClick());
    }

    private void ToggleLock()
    {
        _locked = !_locked;
        _lockedSelection.Clear();
        if (!_locked) return;
        foreach (var o in Selection.Selected)
            _lockedSelection.Add(o);
        if (_lockedSelection.Count == 0)
            _locked = false; // nothing to pin
    }

    /// <summary>Unity-style debug-mode banner with an exit button.</summary>
    private void DrawDebugBanner(Paper paper, Scribe.FontFile font, float width)
    {
        using (paper.Row("insp_debug_banner").Width(width).Height(24)
            .BackgroundColor(Color.FromArgb(48, EditorTheme.Amber400))
            .Rounded(4).ChildLeft(8).RowBetween(6).Enter())
        {
            paper.Box("insp_debug_icon").Width(16).Height(24)
                .Text(EditorIcons.Bug, font).TextColor(EditorTheme.Amber400).FontSize(11f)
                .Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
            paper.Box("insp_debug_title").Width(UnitValue.Stretch()).Height(24)
                .Text(Loc.Get("inspector.debug_mode"), font).TextColor(EditorTheme.Amber400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            paper.Box("insp_debug_exit").Width(UnitValue.Auto).Height(20).Rounded(4).Padding(8, 8, 0, 0)
                .Hovered.BackgroundColor(Color.FromArgb(60, EditorTheme.Amber400)).End()
                .Text(Loc.Get("inspector.exit_debug"), font).TextColor(EditorTheme.Amber400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter)
                .OnClick(_ => _debugMode = false).Cursor(PaperUI.PaperCursor.Pointer);
        }
    }

    public override void OnClosed()
    {
        if (_subscribed)
        {
            Selection.OnSelectionChanged -= OnSelectionChanged;
            _subscribed = false;
        }
        OnScriptReloadCleanup();
    }

    private void OnSelectionChanged()
    {
        var active = Selection.ActiveObject;

        // If the new selection is a folder (or all selected are folders), keep the
        // previous inspectable so browsing folders doesn't wipe the inspector.
        if (IsFolderSelection(active))
            return;

        _lastInspectable = active;
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        using var profilerScope = EditorProfiler.BeginCpuScope("Inspector");

        if (!_subscribed)
        {
            Selection.OnSelectionChanged += OnSelectionChanged;
            _subscribed = true;
            OnSelectionChanged();
        }

        // While locked, edit the selection captured at lock time; Selection changes are ignored.
        object? active;
        if (_locked && _lockedSelection.Count > 0)
        {
            active = _lockedSelection[0];
        }
        else
        {
            // Determine what to inspect: current selection, unless it's a folder
            active = Selection.ActiveObject;
            if (IsFolderSelection(active))
                active = _lastInspectable;
        }

        // Determine whether a previewable asset is selected.
        var previewAsset = TryGetPreviewAsset(active);
        bool showPreview = previewAsset != null;
        // Fit the preview bar inside the pane: a persisted/dragged preview height larger than the
        // available space used to overflow the panel bottom and vanish under the status bar.
        float previewH = 0f;
        if (showPreview)
        {
            const float ScrollViewMinHeight = 72f;
            float wanted = _previewExpanded ? s_previewHeight + PreviewHeaderHeight : PreviewHeaderHeight;
            float available = Math.Max(PreviewHeaderHeight, height - ScrollViewMinHeight);
            previewH = Math.Min(wanted, available);
        }

        using (paper.Column("insp_root").Size(width, height).Enter())
        {
            if (_debugMode)
                DrawDebugBanner(paper, font, width);

            // The debug banner and the preview bar stay on the host (banner is a one-row chrome;
            // preview samples a live texture). The scroll body is the compared paint region.
            float scrollH = height - previewH - (_debugMode ? 26f : 0f);
            using (paper.Column("insp_scroll_paint_body").Size(width, scrollH).Enter())
            {
            AttachPaintRegion(paper);
            Origami.ScrollView(paper, "insp_scroll", width, scrollH).Padding(0, 0, 0, 0).Body(() =>
            {
            // Unity parity: while the Animator graph owns the browsing context (its .controller asset
            // is the current selection, or nothing else is selected), a picked state / transition
            // renders its properties HERE instead of the object grid.
            var animatorWindow = AnimatorWindow.ActiveInstance;
            if (animatorWindow != null && animatorWindow.OwnsInspectorContext(active)
                && animatorWindow.TryDrawSelectionInspector(paper, width))
            {
                paper.Box("insp_bottom_pad").Height(20);
                return;
            }

            if (Selection.Count == 0 && _lastInspectable == null && !_locked)
            {
                DrawEmpty(paper, font, width);
                return;
            }

            if (active == null)
            {
                DrawEmpty(paper, font, width);
                return;
            }

            // Set when the same-type multi-asset editor rendered below (suppresses the name list).
            bool multiAssetsRendered = false;

            // Draw based on type GameObject has its own header
            if (active is GameObject gameObject)
            {
                if (_locked)
                {
                    // Multi-edit the pinned selection; a destroyed GO auto-unlocks the panel.
                    s_lockedGameObjects.Clear();
                    foreach (var o in _lockedSelection)
                    {
                        if (o is GameObject lg) s_lockedGameObjects.Add(lg);
                    }
                    try
                    {
                        if (s_lockedGameObjects.Count > 1)
                            GameObjectInspector.DrawMulti(paper, font, s_lockedGameObjects, _debugMode);
                        else
                            GameObjectInspector.Draw(paper, font, gameObject, _debugMode);
                    }
                    catch (Exception)
                    {
                        _locked = false;
                        _lockedSelection.Clear();
                        throw;
                    }
                }
                else
                {
                    Selection.GetSelected(s_selectedGameObjects);
                    if (s_selectedGameObjects.Count > 1)
                        GameObjectInspector.DrawMulti(paper, font, s_selectedGameObjects, _debugMode);
                    else
                        GameObjectInspector.Draw(paper, font, gameObject, _debugMode);
                }
            }
            else
            {
                // Multi-selection of same-type assets (ContentItems resolve to their loaded
                // EngineObject): Unity-style shared property grid with mixed-value flags.
                var selected = _locked ? (IReadOnlyList<object>)_lockedSelection : Selection.Selected;
                if (selected.Count > 1 && TryResolveMultiAssets(selected, out var multiAssets))
                {
                    multiAssetsRendered = true;
                    DrawSelectionHeader(paper, font, multiAssets[0]);
                    Origami.Separator(paper, "insp_sep_header").Show();
                    Origami.Label(paper, "insp_multi_assets_count",
                        $"{multiAssets.Count} {multiAssets[0].GetType().Name} {Loc.Get("inspector.objects_selected")}").Show();
                    try
                    {
                        PropertyGridUtils.DrawMulti(paper, "insp_multi_assets", multiAssets);
                    }
                    catch (Exception ex)
                    {
                        Runtime.Debug.LogError($"[Inspector] Multi-asset editor threw: {ex.Message}");
                    }
                }
                else
                {
                    DrawSelectionHeader(paper, font, active);
                    Origami.Separator(paper, "insp_sep_header").Show();

                    if (active is ContentItem contentItem)
                        DrawAssetInspector(paper, font, contentItem, _debugMode);
                    else if (active is EngineObject engineObj)
                        DrawEngineObjectInspector(paper, font, engineObj);
                    else
                        DrawGenericInspector(paper, font, active);
                }
            }

            // Multi-selection summary (GameObjects and same-type asset multi-editors already get
            // a full multi-object inspector above).
            if (!_locked && !multiAssetsRendered && Selection.Count > 1 && active is not GameObject)
            {
                                Origami.Header(paper, "insp_h_multi", Loc.Get("inspector.selection")).Underline().Show();
                Origami.Label(paper, "insp_multi_count", $"{Selection.Count} {Loc.Get("inspector.objects_selected")}").Show();

                for (int i = 0; i < Selection.Count && i < 20; i++)
                {
                    var obj = Selection.Selected[i];
                    string name = obj switch
                    {
                        ContentItem ci => $"{(ci.IsFolder ? EditorIcons.Folder : GetExtensionIcon(Path.GetExtension(ci.Name).ToLowerInvariant()))} {ci.Name}",
                        EngineObject eo => $"{EditorIcons.Cube} {eo.Name}",
                        _ => obj.ToString() ?? Loc.Get("inspector.unknown")
                    };
                    Origami.Label(paper, UiId.Cat("insp_sel_", i), name).Show();
                }

                if (Selection.Count > 20)
                    Origami.Label(paper, "insp_more", Loc.Get("inspector.and_more", new { count = Selection.Count - 20 })).Show();
            }

            paper.Box("insp_bottom_pad").Height(20);
        });
            } // insp_scroll_paint_body

            // Universal preview bar at the bottom (collapsible).
            if (showPreview)
                DrawPreviewBar(paper, width, previewH, previewAsset!);
        } // end Column

        // Drag a script (.cs) from the Project panel onto the inspector to add it as a component.
        DrawScriptComponentDropZone(paper, font, width, height);
    }

    /// <summary>
    /// While a single GameObject is selected and a script asset that resolves to a component type is
    /// being dragged, overlays the inspector with a drop target that adds that component on drop.
    /// </summary>
    private static void DrawScriptComponentDropZone(Paper paper, Scribe.FontFile font, float width, float height)
    {
        if (!DragDrop.IsDragging && !DragDrop.IsDropFrame) return;
        if (DragDrop.Payload is not AssetDragPayload payload) return;

        var go = Selection.ActiveObject as GameObject;
        if (go == null || Selection.GetSelected<GameObject>().Count() != 1) return;

        Type? componentType = XEngine.Editor.Projects.Scripting.ScriptComponentResolver.ResolveComponentType(payload);
        if (componentType == null) return;

        using (paper.Box("insp_script_drop")
            .PositionType(PositionType.SelfDirected).Position(0, 0).Size(width, height)
            .Layer(Layer.Overlay)
            .BackgroundColor(Color.FromArgb(38, EditorTheme.Purple400))
            .BorderColor(EditorTheme.Purple400).BorderWidth(2).Rounded(6)
            .Enter())
        {
            if (DragDrop.IsDragging && paper.IsParentHovered) DragDrop.SetVisualMode(paper, DragVisualMode.Copy);
            paper.Box("insp_script_drop_lbl")
                .Width(UnitValue.Stretch()).Height(UnitValue.Stretch()).IsNotInteractable()
                .Text($"{EditorIcons.Plus}  {Loc.Get("inspector.add_component")}: {componentType.Name}", font)
                .TextColor(EditorTheme.Purple400)
                .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter);

            // Complete the drop: the drag has ended (released) while over this overlay.
            if (paper.IsParentHovered && DragDrop.IsDropFrameFor(paper))
            {
                GameObjectInspector.AddComponentWithUndo(go, componentType);
                DragDrop.EndDrag();
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Universal Preview Bar
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// GameObject materials stay in the embedded inspector. A 256px pipeline preview
    /// on every Hierarchy click was compiling/running post+shadows on the select frame.
    /// </summary>
    internal static bool ShouldPreviewSelection(object? active)
        => active is not GameObject and not null;

    /// <summary>Resolves the currently inspected asset (locked selection or live selection) to a
    /// previewable EngineObject, or null.</summary>
    private Runtime.EngineObject? TryGetPreviewAsset(object? active)
    {
        if (!ShouldPreviewSelection(active))
            return null;
        if (active is not ContentItem ci || ci.IsFolder)
            return null;

        Guid guid = ci.Guid;
        if (guid == Guid.Empty)
        {
            var entry = EditorAssetBackend.Instance?.GetEntry(ci.RelativePath);
            guid = entry?.Guid ?? Guid.Empty;
        }
        if (guid == Guid.Empty && ci.IsSubAsset)
            guid = ci.Guid;
        if (guid == Guid.Empty) return null;

        var asset = Runtime.AssetDatabase.Get(guid);
        if (asset == null) return null;
        if (AnimationPreviewAvatar.IsPreviewable(asset)) return asset;

        // Optional-package assets (Spine, ...) preview through their own asset editor's hook.
        var assetEditor = EditorRegistries.GetAssetEditor(asset.GetType());
        return assetEditor != null && assetEditor.HasPreview(asset) ? asset : null;
    }

    /// <summary>Draws the collapsible preview bar at the bottom of the inspector. The top edge is a
    /// drag splitter (Unity's resizable asset-preview footer): drag up/down to resize the surface.</summary>
    private void DrawPreviewBar(Paper paper, float width, float height, Runtime.EngineObject asset)
    {
        // `height` is the fitted space from OnGUI (already clamped to the pane); derive content
        // from it directly so the bar can never paint past the panel bottom into the status bar.
        float contentH = Math.Max(0f, height - PreviewHeaderHeight);
        if (_previewWidget == null || Math.Abs(s_lastPreviewW - width) > 2 || Math.Abs(s_lastPreviewH - contentH) > 2)
        {
            _previewWidget?.Dispose();
            _previewWidget = new PreviewWidget((int)width, (int)contentH, showGrid: true);
            s_lastPreviewW = width; s_lastPreviewH = contentH;
        }

        // Drag splitter along the bar's top edge.
        using (paper.Box("insp_preview_split").Width(width).Height(6)
            .BackgroundColor(s_draggingPreviewSplit ? EditorTheme.Blue400 : AnimationEditorChrome.Panel)
            .OnDragStart(e =>
            {
                s_draggingPreviewSplit = true;
                s_previewDragStartY = e.StartPosition.Y;
                s_previewDragStartHeight = s_previewHeight;
            })
            .OnDragging(e =>
            {
                // DragEvent.Delta is the per-event pointer delta in screen coords.
                s_previewHeight = Math.Clamp(s_previewHeight - e.Delta.Y, PreviewMinHeight, PreviewMaxHeight);
            })
            .OnDragEnd(_ => s_draggingPreviewSplit = false)
            .Enter())
        { }

        using (paper.Column("insp_preview_bar").Width(width).Height(height)
            .BackgroundColor(AnimationEditorChrome.Panel).Enter())
        {
            // Foldout header — click to toggle expand/collapse.
            string arrow = _previewExpanded ? "\u25bc" : "\u25b6";
            using (paper.Row("insp_preview_hdr").Width(width).Height(PreviewHeaderHeight)
                .Padding(8, 2, 4, 2).OnClick(0, (_, _) => _previewExpanded = !_previewExpanded).Enter())
            {
                Origami.Label(paper, "insp_preview_arrow", arrow).Show();
                Origami.Label(paper, "insp_preview_title", "Preview").Show();
                paper.Box("insp_preview_spacer").Width(width).Height(1);
                Origami.Label(paper, "insp_preview_type", asset.GetType().Name).Show();
                if (AnimationPreviewAvatar.IsAnimationPreview(asset))
                {
                    EngineObject? avatar = AnimationPreviewAvatar.ResolveAvatar(asset.AssetID);
                    Origami.Label(paper, "insp_preview_av",
                        avatar != null ? avatar.Name : "Avatar: (none)").Show();
                }
            }

            // Preview content.
            if (_previewExpanded)
            {
                using (paper.Row("insp_preview_content").Width(width).Height(contentH).Enter())
                {
                    SetupAndDrawPreview(paper, asset, width, contentH);
                }
            }
        }
    }

    /// <summary>Dispatches to the right PreviewRenderer setup based on asset type.</summary>
    private void SetupAndDrawPreview(Paper paper, Runtime.EngineObject asset, float width, float contentH)
    {
        float previewW = Math.Min(width, contentH);
        switch (asset)
        {
            case Runtime.Resources.Material mat:
                _previewWidget!.Get(mat, p => p.SetupForMaterial(mat)).DrawPreview(paper, "insp_pv", previewW, contentH);
                break;
            case Runtime.Resources.Mesh mesh:
                _previewWidget!.Get(mesh, p => p.SetupForMesh(mesh)).DrawPreview(paper, "insp_pv", previewW, contentH);
                break;
            // Model files import as PrefabAsset (prowl-sync M4.1) — one preview path covers both.
            case Runtime.Resources.PrefabAsset prefab:
                // Animated model preview: when the import-settings editor tracks this model and
                // holds clips, play its selected clip on the model itself with the shared
                // transport (Unity parity: selecting an animated FBX animates the preview).
                if (TryGetModelPreviewClip(prefab.AssetID, out AnimationClip? modelClip))
                {
                    DrawAnimationAssetPreview(paper, modelClip, width, width, avatarOverride: prefab, contentH: contentH);
                    break;
                }
                _previewWidget!.Get(prefab, p => p.SetupForPrefab(prefab)).DrawPreview(paper, "insp_pv", previewW, contentH);
                DrawModelClipList(paper, width, prefab.AssetID);
                break;
            case AnimationClip animClip:
                DrawAnimationAssetPreview(paper, animClip, width, previewW, contentH: contentH);
                break;
            case AnimatorController controller:
                DrawAnimationAssetPreview(paper, controller, width, previewW, contentH: contentH);
                break;
            case BlendTree blendTree:
                DrawAnimationAssetPreview(paper, blendTree, width, previewW, contentH: contentH);
                break;
            case AvatarMask:
                DrawAvatarMaskPreview(paper, previewW, contentH);
                break;
            case Runtime.Resources.Texture2D tex:
                // Render the texture image with a checkerboard backdrop and aspect-fit, mirroring
                // TextureAssetEditor's SetBrushTexture technique.
                paper.Box("insp_pv_tex").Width(previewW).Height(contentH)
                    .BackgroundColor(EditorTheme.Neutral600)
                    .OnPostLayout((handle, r) => paper.Draw(ref handle, (canvas, rect) =>
                    {
                        DrawTexturePreview(canvas, rect, tex);
                    }));
                break;
            default:
                var assetEditor = EditorRegistries.GetAssetEditor(asset.GetType());
                if (assetEditor != null && assetEditor.HasPreview(asset))
                {
                    _customPreviewEditor = assetEditor;
                    assetEditor.OnPreviewGUI(paper, "insp_pv_pkg", asset, width, contentH);
                }
                else if (asset.GetType().Name == "TimelineAsset")
                {
                    DrawTimelinePreview(paper, previewW, contentH);
                }
                break;
        }
    }

    private void DrawAnimationAssetPreview(Paper paper, EngineObject asset, float width, float previewW,
        EngineObject? avatarOverride = null, float contentH = PreviewContentHeight)
    {
        EngineObject? avatar = avatarOverride ?? AnimationPreviewAvatar.ResolveAvatar(asset.AssetID);
        Guid avatarKey = avatar?.AssetID ?? Guid.Empty;
        if (avatarKey != _previewAvatarKey)
        {
            _previewWidget!.Invalidate();
            _previewAvatarKey = avatarKey;
        }

        // Narrow panes fold the transport into two stacked rows — reserve the extra height.
        bool compactTransport = previewW < 300f;
        float transportH = PreviewOverlayHeight * (compactTransport ? 2 : 1);
        float viewH = contentH - transportH;
        var renderer = _previewWidget!.Get(asset, p =>
        {
            switch (asset)
            {
                case AnimationClip clip:
                    p.SetupForAnimationClip(clip, avatar);
                    break;
                case AnimatorController controller:
                    p.SetupForAnimatorController(controller, avatar);
                    break;
                case BlendTree tree:
                    p.SetupForBlendTree(tree, avatar);
                    break;
            }
        });

        renderer.TickPreview((float)Time.UnscaledDeltaTime);

        using (paper.Column("insp_pv_anim_col").Width(previewW).Height(contentH).Enter())
        {
            renderer.DrawPreview(paper, "insp_pv", previewW, viewH);
            DrawPreviewDropOverlay(paper, previewW, viewH);
            DrawPreviewTransport(paper, renderer, previewW, compactTransport);

            if (!renderer.HasSkinnedAvatar)
            {
                Origami.Label(paper, "insp_pv_empty", AnimationEditorChrome.EmptyPreview).Show();
            }
        }
    }

    private void DrawPreviewTransport(Paper paper, PreviewRenderer renderer, float width, bool compact)
    {
        if (compact)
        {
            // Two stacked rows so every control stays usable in narrow docks: play + scrub on
            // top, clip/time/speed beneath. The preview viewport already reserved 2 rows.
            using (paper.Row("insp_pv_tr").Width(width).Height(PreviewOverlayHeight).ColBetween(6).Padding(6, 2, 6, 2).Enter())
            {
                string playLabel = renderer.PreviewPlaying ? "Pause" : "Play";
                Origami.Button(paper, "insp_pv_play", playLabel, () =>
                {
                    renderer.PreviewPlaying = !renderer.PreviewPlaying;
                }).Show();
                Origami.Slider(paper, "insp_pv_scrub", renderer.PreviewNormalizedTime, v =>
                {
                    renderer.PreviewPlaying = false;
                    renderer.SeekPreview(v);
                }, 0f, 1f).Show();
            }
            using (paper.Row("insp_pv_tr2").Width(width).Height(PreviewOverlayHeight).ColBetween(6).Padding(6, 0, 6, 2).Enter())
            {
                if (!string.IsNullOrEmpty(renderer.PreviewClipName))
                {
                    Origami.Label(paper, "insp_pv_clipn", renderer.PreviewClipName).Show();
                    paper.Box("insp_pv_sp2").Width(6).Height(1);
                }
                float dur = renderer.PreviewDuration;
                float t = renderer.PreviewTime;
                int frame = dur > 0f ? (int)(t * 30f) : 0;
                int m = (int)(t / 60f);
                int sec = (int)t % 60;
                Origami.Label(paper, "insp_pv_time",
                    $"{m}:{sec:D2} ({renderer.PreviewNormalizedTime * 100f:F1}%) Frame {frame}").Show();
                paper.Box("insp_pv_sp3").Width(UnitValue.StretchOne).Height(1);
                Origami.Label(paper, "insp_pv_spdl", $"{renderer.PreviewSpeed:F2}x").Show();
            }
            return;
        }

        using (paper.Row("insp_pv_tr").Width(width).Height(PreviewOverlayHeight).ColBetween(6).Padding(6, 2, 6, 2).Enter())
        {
            string playLabel = renderer.PreviewPlaying ? "Pause" : "Play";
            Origami.Button(paper, "insp_pv_play", playLabel, () =>
            {
                renderer.PreviewPlaying = !renderer.PreviewPlaying;
            }).Show();

            if (!string.IsNullOrEmpty(renderer.PreviewClipName))
                Origami.Label(paper, "insp_pv_clipn", renderer.PreviewClipName).Show();

            float norm = renderer.PreviewNormalizedTime;
            Origami.Slider(paper, "insp_pv_scrub", norm, v =>
            {
                renderer.PreviewPlaying = false;
                renderer.SeekPreview(v);
            }, 0f, 1f).Show();

            float dur = renderer.PreviewDuration;
            float t = renderer.PreviewTime;
            int frame = dur > 0f ? (int)(t * 30f) : 0;
            int m = (int)(t / 60f);
            int s = (int)t % 60;
            float pct = norm * 100f;
            Origami.Label(paper, "insp_pv_time",
                $"{m}:{s:D2} ({pct:F1}%) Frame {frame}").Show();

            Origami.Slider(paper, "insp_pv_spd", renderer.PreviewSpeed, v => renderer.PreviewSpeed = v,
                0.1f, 2f).Show();
            Origami.Label(paper, "insp_pv_spdl", $"{renderer.PreviewSpeed:F2}x").Show();

            if (renderer.HasBlendParameter)
            {
                Origami.Slider(paper, "insp_pv_blend", renderer.BlendParameter, v => renderer.BlendParameter = v,
                    0f, 1f).Show();
            }
        }
    }

    private void DrawPreviewDropOverlay(Paper paper, float width, float height)
    {
        if (!DragDrop.IsDragging && !DragDrop.IsDropFrame) return;
        if (DragDrop.Payload is not AssetDragPayload) return;

        using (paper.Box("insp_pv_drop")
            .PositionType(PositionType.SelfDirected).Position(0, PreviewHeaderHeight)
            .Size(width, height)
            .Layer(Layer.Overlay)
            .BackgroundColor(Color.FromArgb(40, 44, 93, 135))
            .Enter())
        {
            if (DragDrop.IsDragging && paper.IsParentHovered && DragDrop.Payload is AssetDragPayload drag)
                DragDrop.SetVisualMode(paper, drag.AssetType == typeof(PrefabAsset) ? DragVisualMode.Link : DragVisualMode.Rejected);
            if (paper.IsParentHovered && DragDrop.IsDropFrameFor(paper)
                && AnimationPreviewAvatar.TryAcceptDrop(DragDrop.Payload, out _))
            {
                _previewWidget?.Invalidate();
                DragDrop.EndDrag();
            }
        }
    }

    private void DrawAvatarMaskPreview(Paper paper, float previewW, float contentH)
    {
        paper.Box("insp_pv_mask").Width(previewW).Height(contentH)
            .BackgroundColor(AnimationEditorChrome.PreviewEmpty)
            .Text("Avatar Mask", EditorTheme.DefaultFont!)
            .TextColor(AnimationEditorChrome.TextMuted)
            .Alignment(XEngine.PaperUI.TextAlignment.MiddleCenter);
    }

    private static void DrawTimelinePreview(Paper paper, float previewW, float contentH)
    {
        paper.Box("insp_pv_tl").Width(previewW).Height(contentH)
            .BackgroundColor(AnimationEditorChrome.Canvas)
            .OnPostLayout((handle, r) => paper.Draw(ref handle, (canvas, rect) =>
            {
                float rx = (float)rect.Min.X, ry = (float)rect.Min.Y;
                float rw = (float)rect.Size.X, rh = (float)rect.Size.Y;
                canvas.RectFilled(rx, ry, rw, rh, AnimationEditorChrome.Canvas32);
                float row = MathF.Max(8f, (rh - 16f) / 4f);
                var colors = new[]
                {
                    new XEngine.Vector.Color32(70, 130, 180, 255),
                    new XEngine.Vector.Color32(60, 160, 90, 255),
                    new XEngine.Vector.Color32(180, 140, 50, 255),
                    new XEngine.Vector.Color32(160, 70, 90, 255),
                };
                for (int i = 0; i < 4; i++)
                    canvas.RectFilled(rx + 8f, ry + 8f + i * row, rw - 16f, row - 4f, colors[i]);
            }));
    }

    /// <summary>
    /// The Model Import Settings editor is resolved through the registry's per-type instance cache,
    /// so the Animation-tab clip selection and the preview bar share one editor instance. The guid
    /// guard rejects the instance's stale clip list when a plain prefab (never tracked by it) or a
    /// not-yet-drawn model is selected — those fall back to the static prefab preview.
    /// </summary>
    private static bool TryGetModelPreviewClip(Guid prefabGuid, out AnimationClip? clip)
    {
        clip = null;
        if (prefabGuid == Guid.Empty) return false;
        if (EditorRegistries.GetAssetEditorForImporter(nameof(XEngine.Editor.Importers.EditorModelImporter))
            is not ModelAssetEditor editor)
            return false;
        return editor.TryGetPreviewClip(prefabGuid, out clip!);
    }

    private void DrawModelClipList(Paper paper, float width, Guid parentGuid)
    {
        var db = EditorAssetBackend.Instance;
        if (db == null || parentGuid == Guid.Empty) return;
        SubAssetEntry[] subs = db.GetSubAssets(parentGuid);
        int shown = 0;
        for (int i = 0; i < subs.Length && shown < 8; i++)
        {
            if (subs[i].Type != typeof(AnimationClip)) continue;
            Guid clipGuid = subs[i].Guid;
            string name = subs[i].Name;
            Origami.Button(paper, UiId.Cat("insp_pv_clip_", shown), name, () =>
            {
                if (AssetDatabase.Get(clipGuid) is AnimationClip clip)
                {
                    EngineObject? avatar = AnimationPreviewAvatar.ResolveAvatar(clipGuid)
                        ?? AssetDatabase.Get(parentGuid);
                    _previewWidget?.Invalidate();
                    _previewWidget?.Get(clip, p => p.SetupForAnimationClip(clip, avatar));
                }
            }).Show();
            shown++;
        }
    }

    /// <summary>Renders a Texture2D with a checkerboard backdrop and aspect-fit, using Quill's brush-texture technique.</summary>
    private static void DrawTexturePreview(XEngine.Quill.Canvas canvas, XEngine.Vector.Rect rect, Runtime.Resources.Texture2D tex)
    {
        // The inspector holds a raw Texture2D across frames; the asset database's idle sweep can
        // dispose it between draws (crash: ObjectDisposedException on Width). Skip the preview
        // frame instead — the next selection refresh re-resolves the asset.
        if (tex == null || tex.IsDisposed)
            return;

        float rx = (float)rect.Min.X, ry = (float)rect.Min.Y;
        float rw = (float)(rect.Max.X - rect.Min.X), rh = (float)(rect.Max.Y - rect.Min.Y);

        // Checkerboard so alpha reads clearly.
        const float cell = 10f;
        var ca = XEngine.Vector.Color32.FromArgb(255, 44, 40, 54);
        var cb = XEngine.Vector.Color32.FromArgb(255, 34, 30, 44);
        int cols = (int)MathF.Ceiling(rw / cell);
        int crows = (int)MathF.Ceiling(rh / cell);
        for (int cy = 0; cy < crows; cy++)
            for (int cx = 0; cx < cols; cx++)
            {
                float px = rx + cx * cell, py = ry + cy * cell;
                float cw = MathF.Min(cell, rx + rw - px), ch = MathF.Min(cell, ry + rh - py);
                canvas.RectFilled(px, py, cw, ch, ((cx + cy) & 1) == 0 ? ca : cb);
            }

        // Aspect-fit the texture within the preview area (8px margin).
        float maxW = rw - 16f, maxH = rh - 16f;
        float aspect = tex.Width / MathF.Max(1f, tex.Height);
        float drawW = maxW, drawH = drawW / aspect;
        if (drawH > maxH) { drawH = maxH; drawW = drawH * aspect; }
        float drawX = rx + (rw - drawW) / 2f;
        float drawY = ry + (rh - drawH) / 2f;

        // Brush-texture fill with Y-flip (textures are stored Y-up).
        canvas.SetBrushTexture(tex);
        canvas.SetBrushTextureTransform(
            XEngine.Vector.Spatial.Transform2D.CreateTranslation(drawX, drawY + drawH) *
            XEngine.Vector.Spatial.Transform2D.CreateScale(drawW, -drawH));
        canvas.RectFilled(drawX, drawY, drawW, drawH, XEngine.Vector.Color32.FromArgb(255, 255, 255, 255));
        canvas.ClearBrushTexture();
    }

    private void DrawEmpty(Paper paper, Scribe.FontFile font, float width)
    {
        paper.Box("insp_empty").Height(80)
            .Text(Loc.Get("inspector.nothing_selected"), font)
            .TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSize)
            .Alignment(TextAlignment.MiddleCenter);

        paper.Box("insp_hint").Height(30)
            .Text(Loc.Get("inspector.nothing_selected_hint"), font)
            .TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSizeSmall)
            .Alignment(TextAlignment.MiddleCenter);
    }

    internal static void DrawSelectionHeader(Paper paper, Scribe.FontFile font, object active)
    {
        string icon;
        string name;
        string typeName;

        if (active is ContentItem ci)
        {
            icon = ci.IsFolder ? EditorIcons.Folder : GetExtensionIcon(Path.GetExtension(ci.Name).ToLowerInvariant());
            name = ci.Name;
            typeName = ci.IsFolder ? Loc.Get("inspector.folder") : ci.TypeLabel;
        }
        else if (active is EngineObject eo)
        {
            icon = EditorIcons.Cube;
            name = eo.Name;
            typeName = eo.GetType().Name;
        }
        else
        {
            icon = EditorIcons.CircleInfo;
            name = active.ToString() ?? Loc.Get("inspector.unknown");
            typeName = active.GetType().Name;
        }

        using (paper.Row("insp_header")
            .Height(40).Padding(4, 0, 4, 4).RowBetween(8)
            .Enter())
        {
            // Large icon
            paper.Box("insp_h_icon")
                .Width(32).Height(32)
                .BackgroundColor(EditorTheme.Hover)
                .Rounded(6)
                .Text(icon, font)
                .TextColor(EditorTheme.Purple400)
                .FontSize(18f)
                .Alignment(TextAlignment.MiddleCenter);

            using (paper.Column("insp_h_info").Height(32).ColBetween(1).Enter())
            {
                paper.Box("insp_h_name")
                    .Height(18)
                    .Text(name, font)
                    .TextColor(EditorTheme.Ink500)
                    .FontSize(EditorTheme.FontSize)
                    .Alignment(TextAlignment.MiddleLeft);

                paper.Box("insp_h_type")
                    .Height(14)
                    .Text(typeName, font)
                    .TextColor(EditorTheme.Ink400)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft);
            }
        }
    }

    internal static void DrawAssetInspector(Paper paper, Scribe.FontFile font, ContentItem item, bool debug = false)
    {
        if (item.IsFolder)
        {
            DrawFolderInfo(paper, font, item);
            return;
        }

        var db = EditorAssetBackend.Instance;
        if (db == null) return;

        // Sub-asset: show read-only view with Extract button
        if (item.IsSubAsset)
        {
            DrawSubAssetInspector(paper, font, item, db);
            return;
        }

        var entry = db.GetEntry(item.RelativePath);

        // Debug mode: raw imported asset data instead of the importer/editor UI (Unity behavior).
        if (debug && entry?.MainAssetType != null)
        {
            var dbgAsset = Runtime.AssetDatabase.Get(item.Guid != Guid.Empty ? item.Guid : entry.Guid);
            if (dbgAsset != null)
            {
                Origami.Header(paper, "insp_h_dbg_asset",
                    $"{Loc.Get("inspector.type")}: {dbgAsset.GetType().Name}  ({Loc.Get("inspector.debug_mode")})").Show();
                using (Origami.BeginReadOnlyScope())
                    Origami.PropertyGrid(paper, "insp_dbg_asset_grid", dbgAsset, EditorApplication.DebugGridConfig).Show();
                return;
            }
        }

        // Check for custom asset editor
        if (entry?.MainAssetType != null)
        {
            // Importer-keyed editors first (Model Import Settings): a model imports as a plain
            // PrefabAsset, so an asset-type lookup alone would hand .fbx/.gltf files the generic
            // prefab editor and drop the import-settings tabs + animation preview wiring.
            var assetEditor = EditorRegistries.GetAssetEditorForImporter(entry.ImporterType)
                ?? EditorRegistries.GetAssetEditor(entry.MainAssetType);
            if (assetEditor != null)
            {
                var asset = Runtime.AssetDatabase.Get(item.Guid != Guid.Empty ? item.Guid : entry.Guid);
                try { assetEditor.OnGUI(paper, "insp_asset_editor", entry, asset); }
                catch (Exception ex)
                {
                    Runtime.Debug.LogError($"[Inspector] Asset editor for {entry.MainAssetType.Name} threw and was skipped: {ex.Message}");
                }
                return;
            }
        }

        // Fallback: generic asset info
        Origami.Header(paper, "insp_h_asset", Loc.Get("inspector.asset_info")).Show();

        Origami.Label(paper, "insp_path", $"{Loc.Get("inspector.path")}: {item.RelativePath}").Show();

        if (entry != null)
        {
            Origami.Label(paper, "insp_guid", $"{Loc.Get("inspector.guid")}: {entry.Guid}").Show();
            Origami.Label(paper, "insp_importer", $"{Loc.Get("inspector.importer")}: {entry.ImporterType}").Show();

            if (entry.MainAssetType != null)
                Origami.Label(paper, "insp_maintype", $"{Loc.Get("inspector.type")}: {entry.MainAssetType.Name}").Show();

            // Last modified
            var lastMod = new DateTime(entry.LastModifiedTicks, DateTimeKind.Utc).ToLocalTime();
            Origami.Label(paper, "insp_lastmod", $"{Loc.Get("inspector.modified")}: {lastMod:yyyy-MM-dd HH:mm:ss}").Show();

            // Dependencies
            if (entry.Dependencies.Length > 0)
            {
                Origami.Separator(paper, "insp_sep_deps").Show();
                Origami.Header(paper, "insp_h_deps", $"{Loc.Get("inspector.dependencies")} ({entry.Dependencies.Length})").Show();
                for (int i = 0; i < entry.Dependencies.Length && i < 20; i++)
                {
                    var depGuid = entry.Dependencies[i];
                    DrawAssetLink(paper, font, UiId.Cat("insp_dep_", i), depGuid, db);
                }
            }

            // Dependents (who references this asset)
            var dependents = db.Dependencies.GetDependents(entry.Guid);
            if (dependents.Count > 0)
            {
                Origami.Separator(paper, "insp_sep_refs").Show();
                Origami.Header(paper, "insp_h_refs", $"{Loc.Get("inspector.used_by")} ({dependents.Count})").Show();
                int count = 0;
                foreach (var depGuid in dependents)
                {
                    if (count >= 20) break;
                    DrawAssetLink(paper, font, UiId.Cat("insp_ref_", count), depGuid, db);
                    count++;
                }
            }
        }

        // Import Settings
        if (entry != null && Project.Current != null)
        {
            string absPath = Path.Combine(Project.Current.AssetsPath, entry.Path);
            string metaPath = MetaFile.GetMetaPath(absPath);

            if (File.Exists(metaPath))
            {
                // Re-reading + parsing the .meta from disk every rendered frame both cost I/O and
                // dropped un-saved import-setting edits on the next repaint. Cache the parsed file
                // keyed by path + on-disk timestamp (one stat per frame instead of read+parse);
                // widget callbacks mutate the cached Settings object so edits survive until Save.
                DateTime metaStamp = File.GetLastWriteTimeUtc(metaPath);
                if (_metaCache == null || _metaCachePath != metaPath || _metaCacheStamp != metaStamp)
                {
                    _metaCache = MetaFile.Read(metaPath);
                    _metaCachePath = metaPath;
                    _metaCacheStamp = metaStamp;
                }
                var meta = _metaCache;
                var importer = EditorRegistries.CreateImporterByName(entry.ImporterType);

                if (importer != null)
                {
                    // Ensure settings exist (use defaults if missing). Persist the resolved
                    // defaults onto the cached meta so edits accumulate on one object across
                    // frames instead of mutating a fresh throwaway DefaultSettings() per repaint.
                    var settings = meta.Settings ?? importer.DefaultSettings();
                    meta.Settings ??= settings;
                    if (settings != null && settings.TagType == Echo.EchoType.Compound)
                    {
                                                Origami.Header(paper, "insp_h_settings", $"{EditorIcons.Gear}  {Loc.Get("inspector.import_settings")}").Underline().Show();

                        foreach (var kvp in settings.Tags.ToList())
                        {
                            string key = kvp.Key;
                            var val = kvp.Value;
                            string setId = UiId.Cat("insp_set_", key);

                            switch (val.TagType)
                            {
                                case Echo.EchoType.Bool:
                                    Origami.Checkbox(paper, setId, val.BoolValue,
                                            v => { settings[key] = new Echo.EchoObject(v); })
                                        .LabelRight(PropertyGridUtils.NicifyName(key)).Show();
                                    break;

                                case Echo.EchoType.Int:
                                    EditorGUI.Row(paper, setId, PropertyGridUtils.NicifyName(key), () =>
                                        Origami.NumericField<int>(paper, UiId.Underscore(setId, "v"), val.IntValue,
                                            v => { settings[key] = new Echo.EchoObject(v); }).Show());
                                    break;

                                case Echo.EchoType.Float:
                                    EditorGUI.Row(paper, setId, PropertyGridUtils.NicifyName(key), () =>
                                        Origami.NumericField<float>(paper, UiId.Underscore(setId, "v"), val.FloatValue,
                                            v => { settings[key] = new Echo.EchoObject(v); }).Show());
                                    break;

                                case Echo.EchoType.String:
                                    EditorGUI.Row(paper, setId, PropertyGridUtils.NicifyName(key), () =>
                                        Origami.TextField(paper, UiId.Underscore(setId, "v"), val.StringValue,
                                            v => { settings[key] = new Echo.EchoObject(v); }).Show());
                                    break;
                            }
                        }

                        // Save & Reimport button
                        paper.Box("insp_set_sp").Height(4);
                        Origami.Button(paper, "insp_set_save", $"{EditorIcons.FloppyDisk}  {Loc.Get("inspector.save_and_reimport")}", () =>
                        {
                            meta.Settings = settings;
                            MetaFile.Write(metaPath, meta);
                            db.Reimport(entry.Guid);
                        }).Width(150).Show();
                    }
                }
            }
        }

        // Reimport button
        Origami.Separator(paper, "insp_sep_actions").Show();
        if (entry != null)
        {
            Origami.Button(paper, "insp_reimport", $"{EditorIcons.ArrowsRotate}  {Loc.Get("inspector.reimport")}", () => db.Reimport(entry.Guid)).Show();
        }
    }



    private static void DrawFolderInfo(Paper paper, Scribe.FontFile font, ContentItem item)
    {
        Origami.Header(paper, "insp_h_folder", Loc.Get("inspector.folder")).Show();
        Origami.Label(paper, "insp_folder_path", $"{Loc.Get("inspector.path")}: {item.RelativePath}").Show();

        string absPath = Path.Combine(Project.Current!.AssetsPath, item.RelativePath);
        if (Directory.Exists(absPath))
        {
            try
            {
                // The recursive walk was previously executed every rendered frame while a folder
                // was selected (the single worst per-frame I/O in the editor); refresh at 1 Hz.
                double now = Runtime.Time.UnscaledTotalTime;
                if (_folderInfoPath != absPath || now - _folderInfoTime >= 1.0)
                {
                    _folderInfoFiles = Directory.GetFiles(absPath, "*", SearchOption.AllDirectories)
                        .Count(f => !f.EndsWith(".meta"));
                    _folderInfoFolders = Directory.GetDirectories(absPath, "*", SearchOption.AllDirectories).Length;
                    _folderInfoPath = absPath;
                    _folderInfoTime = now;
                }
                Origami.Label(paper, "insp_folder_files", $"{Loc.Get("inspector.files")}: {_folderInfoFiles}").Show();
                Origami.Label(paper, "insp_folder_folders", $"{Loc.Get("inspector.subfolders")}: {_folderInfoFolders}").Show();
            }
            catch { }
        }
    }

    private static string? _folderInfoPath;
    private static double _folderInfoTime = double.MinValue;
    private static int _folderInfoFiles;
    private static int _folderInfoFolders;

    private static MetaFileData? _metaCache;
    private static string? _metaCachePath;
    private static DateTime _metaCacheStamp;

    private static void DrawSubAssetInspector(Paper paper, Scribe.FontFile font, ContentItem item, EditorAssetBackend db)
    {
        // Find the parent entry
        AssetEntry? parentEntry = null;
        SubAssetEntry? subEntry = null;
        foreach (var e in db.GetAllEntries())
        {
            if (e.SubAssets == null) continue;
            var match = e.SubAssets.FirstOrDefault(s => s.Guid == item.Guid);
            if (match != null)
            {
                parentEntry = e;
                subEntry = match;
                break;
            }
        }

        // Header with sub-asset badge
        using (paper.Row("insp_sub_header").Height(28).ChildLeft(8).RowBetween(6).Enter())
        {
            paper.Box("insp_sub_badge")
                .Width(UnitValue.Auto).Height(20)
                .ChildLeft(6).ChildRight(6)
                .BackgroundColor(EditorTheme.Selected)
                .Rounded(4)
                .Text(Loc.Get("inspector.sub_asset"), font)
                .TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleCenter);

            paper.Box("insp_sub_name")
                .Height(28)
                .Text(item.Name, font)
                .TextColor(EditorTheme.Ink500)
                .FontSize(EditorTheme.FontSize)
                .Alignment(TextAlignment.MiddleLeft);
        }

        Origami.Separator(paper, "insp_sub_sep1").Show();

        // Info
        Origami.Label(paper, "insp_sub_type", $"{Loc.Get("inspector.type")}: {item.TypeLabel}").Show();
        Origami.Label(paper, "insp_sub_guid", $"{Loc.Get("inspector.guid")}: {item.Guid}").Show();
        if (parentEntry != null)
            Origami.Label(paper, "insp_sub_parent", $"{Loc.Get("inspector.parent")}: {parentEntry.Path}").Show();

        Origami.Separator(paper, "insp_sub_sep2").Show();

        // A sub-asset uses the SAME custom editor as a main asset of its type, just wrapped in a read-only
        // scope so Origami widgets are disabled. Editors that draw non-Origami interactive elements gate them
        // on Origami.IsReadOnly themselves (e.g. TextureAssetEditor's Save button).
        var asset = Runtime.AssetDatabase.Get(item.Guid);
        if (asset != null)
        {
            var subEditor = parentEntry != null ? EditorRegistries.GetAssetEditor(asset.GetType()) : null;
            if (subEditor != null)
            {
                Origami.BeginReadOnly();
                try { subEditor.OnGUI(paper, "insp_sub_editor", parentEntry!, asset); }
                finally { Origami.EndReadOnly(); }
            }
            else
            {
                // Generic read-only property grid for types without a custom editor.
                Origami.Header(paper, "insp_sub_h_props", Loc.Get("inspector.properties_readonly")).Show();
                var fields = GUI.PropertyGridUtils.GetSerializableFields(asset.GetType());
                foreach (var field in fields)
                {
                    object? val = field.GetValue(asset);
                    string label = GUI.PropertyGridUtils.NicifyName(field.Name);
                    Origami.Label(paper, UiId.Cat("insp_sub_prop_", field.Name), $"{label}: {val ?? "(null)"}").Show();
                }
            }
        }

        // Dependencies (what this sub-asset itself references, e.g. a Sprite's own Texture)
        if (subEntry?.Dependencies.Length > 0)
        {
            Origami.Separator(paper, "insp_sub_sep_deps").Show();
            Origami.Header(paper, "insp_sub_h_deps", $"{Loc.Get("inspector.dependencies")} ({subEntry.Dependencies.Length})").Show();
            for (int i = 0; i < subEntry.Dependencies.Length && i < 20; i++)
                DrawAssetLink(paper, font, UiId.Cat("insp_sub_dep_", i), subEntry.Dependencies[i], db);
        }

        // Dependents (who references this sub-asset directly, e.g. a UIImage pointing at this Sprite)
        var subDependents = db.Dependencies.GetDependents(item.Guid);
        if (subDependents.Count > 0)
        {
            Origami.Separator(paper, "insp_sub_sep_refs").Show();
            Origami.Header(paper, "insp_sub_h_refs", $"{Loc.Get("inspector.used_by")} ({subDependents.Count})").Show();
            int refCount = 0;
            foreach (var depGuid in subDependents)
            {
                if (refCount >= 20) break;
                DrawAssetLink(paper, font, UiId.Cat("insp_sub_ref_", refCount), depGuid, db);
                refCount++;
            }
        }

        Origami.Separator(paper, "insp_sub_sep3").Show();

        // Extract button clone sub-asset to a standalone file
        Origami.Button(paper, "insp_sub_extract", $"{EditorIcons.FileExport}  {Loc.Get("inspector.extract_as_asset")}", () => ExtractSubAsset(item, parentEntry, subEntry, asset)).Show();
    }

    private static void ExtractSubAsset(ContentItem item, AssetEntry? parentEntry, SubAssetEntry? subEntry, EngineObject? asset)
    {
        if (asset == null || parentEntry == null || Project.Current == null) return;

        var db = EditorAssetBackend.Instance;
        if (db == null) return;

        // Determine target path same folder as parent, with sub-asset name
        string parentDir = Path.GetDirectoryName(parentEntry.Path)?.Replace('\\', '/') ?? "";
        string ext = asset switch
        {
            XEngine.Runtime.Resources.Material => ".mat",
            XEngine.Runtime.Resources.Mesh => ".mesh",
            XEngine.Runtime.AnimationClip => ".anim",
            XEngine.Runtime.Resources.Texture2D => ".png",
            _ => ".asset"
        };

        string fileName = $"{item.Name}{ext}";
        string relativePath = string.IsNullOrEmpty(parentDir) ? fileName : $"{parentDir}/{fileName}";

        // Serialize the asset to the file
        try
        {
            // Clear the sub-asset's AssetID so it serializes as a full object, not a reference
            var originalId = asset.AssetID;
            asset.AssetID = Guid.Empty;

            var echo = Echo.Serializer.Serialize(typeof(object), asset);
            asset.AssetID = originalId; // Restore

            if (echo != null)
            {
                string absPath = Path.Combine(Project.Current.AssetsPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
                File.WriteAllText(absPath, echo.WriteToString());
                Runtime.Debug.Log($"Extracted sub-asset '{item.Name}' to '{relativePath}'");
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"Failed to extract sub-asset: {ex.Message}");
        }
    }

    internal static void DrawEngineObjectInspector(Paper paper, Scribe.FontFile font, EngineObject obj)
    {
        Origami.Header(paper, "insp_h_eo", obj.GetType().Name).Show();

        Origami.Label(paper, "insp_eo_name", $"{Loc.Get("inspector.name")}: {obj.Name}").Show();
        Origami.Label(paper, "insp_eo_id", $"{Loc.Get("inspector.instance_id")}: {obj.InstanceID}").Show();

        if (obj.AssetID != Guid.Empty)
            Origami.Label(paper, "insp_eo_assetid", $"{Loc.Get("inspector.asset_id")}: {obj.AssetID}").Show();
        if (!string.IsNullOrEmpty(obj.AssetPath))
            Origami.Label(paper, "insp_eo_assetpath", $"{Loc.Get("inspector.asset_path")}: {obj.AssetPath}").Show();

        // Use PropertyGrid for reflection-based editing
                Origami.Header(paper, "insp_h_props", Loc.Get("inspector.properties")).Underline().Show();
        PropertyGridUtils.Draw(paper, "insp_pg", obj);
    }

    internal static void DrawGenericInspector(Paper paper, Scribe.FontFile font, object obj)
    {
        Origami.Header(paper, "insp_h_generic", obj.GetType().Name).Show();
        Origami.Label(paper, "insp_generic_str", obj.ToString() ?? "null").Show();

                Origami.Header(paper, "insp_h_gprops", Loc.Get("inspector.properties")).Underline().Show();
        PropertyGridUtils.Draw(paper, "insp_gpg", obj);
    }

    private static string GetExtensionIcon(string ext) => EditorRegistries.GetFileIconForExtension(ext);

    private static void DrawAssetLink(Paper paper, Scribe.FontFile font, string id, Guid guid, EditorAssetBackend db)
    {
        string? path = db.GuidToPath(guid);
        bool isBuiltIn = Runtime.BuiltInAssets.IsBuiltIn(guid);
        string displayName;
        string icon;

        if (isBuiltIn)
        {
            var entries = Runtime.BuiltInAssets.Entries;
            displayName = entries.TryGetValue(guid, out var bi) ? bi.Name : guid.ToString()[..8];
            icon = EditorIcons.Star;
        }
        else if (path != null)
        {
            displayName = System.IO.Path.GetFileName(path);
            icon = GetExtensionIcon(System.IO.Path.GetExtension(path).ToLowerInvariant());
        }
        else
        {
            displayName = guid.ToString()[..8] + "...";
            icon = EditorIcons.CircleQuestion;
        }

        paper.Box(id)
            .Height(EditorTheme.RowHeight).ChildLeft(8).Rounded(3)
            .Hovered.BackgroundColor(EditorTheme.Ink200).End()
            .Text($"{icon}  {displayName}", font)
            .TextColor(EditorTheme.Ink500)
            .FontSize(EditorTheme.FontSizeSmall)
            .Alignment(PaperUI.TextAlignment.MiddleLeft)
            .OnClick(guid, (g, _) => Selection.Ping(g));
    }
}
