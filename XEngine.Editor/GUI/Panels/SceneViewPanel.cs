using System;
using System.Collections.Generic;
using System.Linq;

using XEngine.OrigamiUI;
using Gizmo = XEngine.OrigamiUI.Gizmo;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Spatial;

using Color = System.Drawing.Color;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Core;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;

namespace XEngine.Editor.GUI.Panels;

public class SceneViewPanel : DockPanel, IScriptReloadCleanup
{
    [MenuItem("Window/General/Scene", priority: 5)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(SceneViewPanel));

    public override string Title => Loc.Get("panel.scene");
    public override string Icon => EditorIcons.Shapes;

    private EditorCamera? _editorCamera;
    private readonly GameViewRenderSchedule _renderSchedule = new();

    private readonly SceneViewSettings _settings = new();
    private readonly SceneOverlayHost _overlays = new();
    private CameraPreview? _cameraPreview;
    // The preview is a second full pipeline render; without its own schedule it re-rendered on
    // every Scene View frame (editor-camera orbit included, which never changes what the
    // previewed camera sees). Keyed by scene/quality versions + the previewed camera's own
    // transform version; play mode renders continuously like the Game View.
    private readonly GameViewRenderSchedule _cameraPreviewSchedule = new();
    private Camera? _previewCamera;
    private bool _previewMatchGameAspect = true;
    private bool _overlaysBuilt;

    /// <summary>Height of the Unity-style toolbar strip above the viewport.</summary>
    private const float ToolbarHeight = 26f;
    private UiPaperSubtreeRegion? _toolbarPaint;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _paintGeometryBudget = new(4 * 1024 * 1024);
    private readonly UiMemoryBudget _paintAtlasBudget = new(8 * 1024 * 1024);
    private ulong _toolbarPaintKey;
    private bool _toolbarPointerOver;
    internal long ToolbarRegionCaptures => _toolbarPaint?.CaptureCount ?? -1;
    internal SceneViewSettings Settings => _settings;

    // Paper element id of the viewport box, captured during layout. Compared against the hovered
    // element so an overlay under the pointer doesn't also feed camera navigation and picking.
    private int _viewportElementId;

    // The editor camera caches the scene camera + cloned image effects (possibly user types).
    // Release them before a hot-reload so they don't pin the script AssemblyLoadContext.
    public void OnScriptReloadCleanup()
    {
        _editorCamera?.ReleaseSceneReferences();
        ActiveCamera = null;
        if (ReferenceEquals(ActiveInstance, this)) ActiveInstance = null;
        _previewCamera = null;
        _cameraPreview?.Dispose();
        _cameraPreview = null;
        DisposePaintRegions();
    }
    private Gizmo.TransformGizmo? _transformGizmo;
    private bool _wasGizmoActive;
    private Gizmo.ViewManipulatorGizmo? _viewManipulator;

    /// <summary>The most recently active SceneViewPanel's camera. Used by other panels for "Move to View" etc.</summary>
    public static EditorCamera? ActiveCamera { get; private set; }

    /// <summary>The Scene view that last drew, used to frame Hierarchy double-clicks.</summary>
    public static SceneViewPanel? ActiveInstance { get; private set; }

    public static ISceneViewEditor? ActiveSceneViewEditor { get; private set; }
    public static GameObject? ActiveSceneViewTarget { get; private set; }

    public static void UpdateSceneViewEditor()
    {
        var selectedGO = Selection.GetSelected<GameObject>().FirstOrDefault();
        if (selectedGO == null || selectedGO == ActiveSceneViewTarget)
        {
            if (selectedGO == null && ActiveSceneViewEditor != null) DeactivateSceneViewEditor();
            return;
        }
        var editor = EditorRegistries.FindSceneViewEditor(selectedGO);
        if (editor != null)
        {
            if (ActiveSceneViewEditor?.GetType() == editor.GetType() && ActiveSceneViewTarget == selectedGO) return;
            DeactivateSceneViewEditor();
            ActiveSceneViewEditor = editor;
            ActiveSceneViewTarget = selectedGO;
            ActiveSceneViewEditor.OnActivate(selectedGO);
        }
        else if (ActiveSceneViewEditor != null)
            DeactivateSceneViewEditor();
    }

    public static void DeactivateSceneViewEditor()
    {
        ActiveSceneViewEditor?.OnDeactivate();
        ActiveSceneViewEditor = null;
        ActiveSceneViewTarget = null;
    }
    private Gizmo.TransformGizmoMode _gizmoMode = Gizmo.TransformGizmoMode.Translate;
    private Rect _viewportAbsoluteRect; // Cached absolute screen rect from layout
    private bool _gizmoActive; // Whether the gizmo should draw (selection exists)

    private readonly RubberBandSelector _rubberBand = new();
    private bool _skipNextClick; // a finished marquee must not also count as a pick click

    // Shift+F focus lock: keep framing the selection until the user navigates the camera.
    private bool _followSelection;

    // Click-through cycling: repeated clicks on roughly the same point step through overlapping hits.
    private Float2 _lastPickPos = new(-10000f, -10000f);
    private readonly List<(GameObject go, float dist)> _pickHits = new();

    // Pose restored from disk by RestoreState; applied the first time the camera is created
    // (the camera itself is constructed lazily inside OnGUI because it needs graphics ready).
    private bool _hasPendingPose;
    private Float3 _pendingPos;
    private float _pendingYaw, _pendingPitch;

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        if (_editorCamera == null)
        {
            _editorCamera = new EditorCamera();
            if (_hasPendingPose)
            {
                _editorCamera.SetPose(_pendingPos, _pendingYaw, _pendingPitch);
                _hasPendingPose = false;
            }
        }
        ActiveCamera = _editorCamera;
        ActiveInstance = this;

        // Shift+F focus lock: any camera navigation breaks the follow; otherwise keep framing.
        if (_followSelection)
        {
            if (Input.GetMouseButton(1) || Input.GetMouseButton(2)
                || (Input.IsAltPressed && Input.GetMouseButton(0)))
                _followSelection = false;
            else
            {
                bool anySelected = false;
                foreach (var _ in Selection.GetSelected<GameObject>()) { anySelected = true; break; }
                if (anySelected) _editorCamera.FocusSelection();
                else _followSelection = false;
            }
        }

        if (EditorApplication.Instance?.FindOpenPanel(typeof(AnimationClipWindow)) is AnimationClipWindow clipWindow)
        {
            if (clipWindow.IsRecording)
                AnimationRecording.Instance.Tick(clipWindow.PlaybackTime);
            else
                AnimationPreviewSession.Instance.Sync(clipWindow.CurrentClip, clipWindow.PlaybackTime);
        }

        GizmoSettings.Install();
        BuildOverlays();
        ApplySettings();

        float viewHeight = MathF.Max(0, height - ToolbarHeight);

        using (paper.Column("sv_root").Size(width, height).Enter())
        {
            using (EditorProfiler.BeginCpuScope("SceneChrome"))
            using (paper.Box("sv_toolbar_paint").Height(ToolbarHeight).Width(width).Enter())
            {
                AttachPaintRegion(paper);
                SceneViewToolbar.Draw(paper, font, this, _settings, _overlays, ToolbarHeight);
            }

            // The viewport and its overlays get their own container so SelfDirected overlay
            // positions are measured from the viewport corners, not from under the toolbar.
            using (paper.Box("sv_view_area").Size(width, viewHeight).Enter())
                DrawViewport(paper, font, width, viewHeight);
        }
    }

    /// <summary>Invalidate the cached scene render (toolbar and overlays call this after a change).</summary>
    internal void InvalidateRender() => _renderSchedule.Invalidate();

    /// <summary>Frame the current selection in the active Scene view, including overlay UI.</summary>
    public static void FrameActiveSelection()
    {
        ActiveCamera?.FocusSelection();
        ActiveInstance?.InvalidateRender();
    }

    internal EditorCamera? Camera => _editorCamera;

    /// <summary>Push toolbar state onto the editor camera and the global gizmo settings.</summary>
    private void ApplySettings()
    {
        if (_editorCamera == null) return;

        _editorCamera.ShowGrid = _settings.ShowGrid;
        _editorCamera.ShowGizmos = _settings.ShowGizmos;
        _editorCamera.UseSceneEffects = _settings.EffectsEnabled;
        _editorCamera.DebugView = _settings.DrawMode;
        _editorCamera.Camera.ClearFlags = _settings.ShowBackground
            ? CameraClearFlags.Skybox
            : CameraClearFlags.SolidColor;
        if (!_settings.ShowBackground)
            _editorCamera.Camera.ClearColor = new XEngine.Vector.Color(0.22f, 0.22f, 0.22f, 1f);
        if (_settings.Is2D)
            _editorCamera.SetOrthographic(true);
        else
            _editorCamera.SetProjectionOrthographic(_settings.ViewOrthographic);
    }

    // The gear menu keeps the coarse view options; the toolbar owns the day-to-day toggles.
    public override float HeaderWidth => 28f;
    public override void OnHeaderContent(Paper paper, float width, float height)
    {
        EditorGUI.HeaderIconButton(paper, "sv_hdr_settings", EditorIcons.Gear, () =>
            Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, b =>
            {
                b.Header(Loc.Get("panel.scene"));
                b.Toggle(Loc.Get("scene.show_grid"),
                    () => { _settings.ShowGrid = !_settings.ShowGrid; _renderSchedule.Invalidate(); },
                    () => _settings.ShowGrid);
                b.Toggle(Loc.Get("scene.show_gizmos"),
                    () => { _settings.ShowGizmos = !_settings.ShowGizmos; _renderSchedule.Invalidate(); },
                    () => _settings.ShowGizmos);
                b.Toggle(Loc.Get("scene.selection_outline"),
                    () => { _settings.ShowSelectionOutline = !_settings.ShowSelectionOutline; _renderSchedule.Invalidate(); },
                    () => _settings.ShowSelectionOutline);
                b.Separator();
                b.Item(Loc.Get("overlay.reset_layout"), _overlays.ResetLayout, icon: EditorIcons.RotateLeft);
            }));
    }

    // ================================================================
    //  Overlays
    // ================================================================

    private void BuildOverlays()
    {
        if (_overlaysBuilt) return;
        _overlaysBuilt = true;

        _overlays.Add(new SceneOverlay("tools", Loc.Get("overlay.tools"),
            EditorIcons.ArrowsUpDownLeftRight, DrawToolsOverlay)
        {
            Corner = OverlayCorner.TopLeft,
            Offset = new Float2(12, 12),
            Width = 34,
            CompactHandle = true,
            IconDraw = SceneViewIcons.Move,
        });

        _overlays.Add(new SceneOverlay("grid", Loc.Get("overlay.grid_and_snap"),
            EditorIcons.BorderAll, DrawGridSnapOverlay)
        {
            Corner = OverlayCorner.TopRight,
            Offset = new Float2(12, 100),
            Width = 176,
            Visible = false,
            IconDraw = SceneViewIcons.Grid,
        });

        _overlays.Add(new SceneOverlay("cameras", Loc.Get("overlay.cameras"),
            EditorIcons.Camera, DrawCameraPreviewOverlay)
        {
            Corner = OverlayCorner.BottomRight,
            Offset = new Float2(12, 12),
            Width = 220,
            IconDraw = SceneViewIcons.Camera,
            ContextMenu = menu => menu.Toggle(Loc.Get("overlay.match_game_aspect"),
                () => _previewMatchGameAspect = !_previewMatchGameAspect, () => _previewMatchGameAspect),
        });

        _overlays.CaptureDefaults();
    }

    // The active scene-view editor may replace the default gizmo-mode buttons with its own toolbar.
    private void DrawToolsOverlay(Paper paper, Scribe.FontFile font)
    {
        var sceneEditor = ActiveSceneViewEditor;
        bool suppressDefault = sceneEditor != null && sceneEditor.DrawToolbar(paper, "sv_sce", font);
        if (!suppressDefault)
            DrawDefaultToolbar(paper, font);
    }

    private void DrawGridSnapOverlay(Paper paper, Scribe.FontFile font)
        => SceneViewToolbar.DrawGridSnapBody(paper, font, this, _settings);

    private void DrawCameraPreviewOverlay(Paper paper, Scribe.FontFile font)
    {
        List<Camera> cameras = CollectSceneCameras();
        Camera? camera = FindPreviewCamera(cameras);

        using (paper.Row("sv_cp_head").Height(22).RowBetween(4).Enter())
        {
            paper.Box("sv_cp_icon").Width(14).Height(22).IsNotInteractable()
                .Icon(paper, SceneViewIcons.Camera, EditorTheme.Ink300, size: 12f);

            if (cameras.Count == 0)
            {
                paper.Box("sv_cp_none").Width(UnitValue.Stretch()).Height(22).IsNotInteractable()
                    .Text(Loc.Get("overlay.no_cameras"), font).TextColor(EditorTheme.InkDim)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();
            }
            else
            {
                string[] names = new string[cameras.Count];
                int selected = 0;
                for (int i = 0; i < cameras.Count; i++)
                {
                    names[i] = cameras[i].GameObject?.Name ?? $"Camera {i}";
                    if (camera != null && ReferenceEquals(cameras[i], camera)) selected = i;
                }

                using (paper.Box("sv_cp_dd").Width(UnitValue.Stretch()).Height(22).Enter())
                    Origami.Dropdown(paper, "sv_cp_cam", selected, i =>
                    {
                        if ((uint)i < (uint)cameras.Count)
                            _previewCamera = cameras[i];
                    }, names).Width(UnitValue.Stretch()).Height(22).Show();
            }
        }

        if (camera == null)
        {
            paper.Box("sv_cp_empty").Width(UnitValue.Stretch()).Height(72).Rounded(4)
                .BackgroundColor(Color.Black)
                .Text(Loc.Get("overlay.no_preview"), font).TextColor(EditorTheme.InkDim)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);
            return;
        }

        var texture = _cameraPreview?.Texture;
        float previewWidth = 210f;
        float aspect = _previewMatchGameAspect && texture != null && texture.Height > 0
            ? texture.Width / (float)texture.Height
            : 16f / 9f;
        float previewHeight = MathF.Max(40f, previewWidth / MathF.Max(0.1f, aspect));

        var box = paper.Box("sv_cp_img").Width(previewWidth).Height(previewHeight).Rounded(4)
            .BackgroundColor(Color.Black);
        if (texture == null || texture.IsDisposed)
        {
            box.BackgroundColor(Color.Black)
                .Text(Loc.Get("overlay.no_preview"), font).TextColor(EditorTheme.InkDim)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);
            return;
        }

        Texture2D captured = texture;
        box.OnPostLayout((handle, _) => paper.Draw(ref handle, (canvas, r) =>
        {
            if (captured.IsDisposed) return;
            float rx = (float)r.Min.X, ry = (float)r.Min.Y;
            float rw = (float)r.Size.X, rh = (float)r.Size.Y;
            canvas.ClearBrush();
            canvas.SetBrushTexture(captured);
            canvas.SetBrushTextureTransform(
                RenderTargetPreview.Transform(rx, ry, rw, rh));
            canvas.RoundedRectFilled(rx, ry, rw, rh, 4, Color.White);
            canvas.ClearBrushTexture();
        }));
    }

    /// <summary>
    /// Camera the overlay previews: the last dropdown pick if it is still in the scene, otherwise
    /// the tagged Main Camera, otherwise the first scene camera. Selection is not required.
    /// </summary>
    private Camera? FindPreviewCamera() => FindPreviewCamera(CollectSceneCameras());

    private Camera? FindPreviewCamera(List<Camera> cameras)
    {
        if (_previewCamera != null && !_previewCamera.IsDisposed && _previewCamera.GameObject != null
            && cameras.Contains(_previewCamera))
            return _previewCamera;

        _previewCamera = PreferredSceneCamera(cameras);
        return _previewCamera;
    }

    internal static List<Camera> CollectSceneCameras() => CollectSceneCameras(Scene.Current);

    /// <summary>Enabled scene cameras for the camera preview overlay. Runs every frame the overlay is
    /// open, so it reads the scene's camera registry instead of walking all objects (~4 ms at 100k
    /// objects). Cameras on disabled objects or with the component disabled are not listed.</summary>
    internal static List<Camera> CollectSceneCameras(Scene? scene)
    {
        var list = new List<Camera>();
        if (scene == null) return list;
        scene.CollectEnabledCameras(list);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            GameObject? go = list[i].GameObject;
            if (go == null || go.HideFlags.HasFlag(HideFlags.HideAndDontSave)) list.RemoveAt(i);
        }
        return list;
    }

    internal static Camera? PreferredSceneCamera(List<Camera> cameras)
    {
        foreach (Camera camera in cameras)
        {
            if (camera.GameObject != null && camera.GameObject.CompareTag("Main Camera"))
                return camera;
        }
        return cameras.Count > 0 ? cameras[0] : null;
    }

    private void DrawDefaultToolbar(Paper paper, Scribe.FontFile font)
    {
        bool isTranslate = _gizmoMode == Gizmo.TransformGizmoMode.Translate;
        bool isRotate = _gizmoMode == Gizmo.TransformGizmoMode.Rotate;
        bool isScale = _gizmoMode == Gizmo.TransformGizmoMode.ScaleAll;
        bool isUniversal = _gizmoMode == Gizmo.TransformGizmoMode.Universal;

        paper.Box("sv_move_btn")
            .Width(24).Height(24).Rounded(6)
            .BackgroundColor(isTranslate ? EditorTheme.ToolbarActive : Color.Transparent)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Icon(paper, SceneViewIcons.Move, EditorTheme.Ink500, size: 14f)
            .OnClick(0, (_, _) => SetGizmoMode(Gizmo.TransformGizmoMode.Translate));

        paper.Box("sv_rotate_btn")
            .Width(24).Height(24).Rounded(6)
            .BackgroundColor(isRotate ? EditorTheme.ToolbarActive : Color.Transparent)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Icon(paper, SceneViewIcons.Rotate, EditorTheme.Ink500, size: 14f)
            .OnClick(0, (_, _) => SetGizmoMode(Gizmo.TransformGizmoMode.Rotate));

        paper.Box("sv_scale_btn")
            .Width(24).Height(24).Rounded(6)
            .BackgroundColor(isScale ? EditorTheme.ToolbarActive : Color.Transparent)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Icon(paper, SceneViewIcons.Scale, EditorTheme.Ink500, size: 14f)
            .OnClick(0, (_, _) => SetGizmoMode(Gizmo.TransformGizmoMode.ScaleAll));

        paper.Box("sv_universal_btn")
            .Width(24).Height(24).Rounded(6)
            .BackgroundColor(isUniversal ? EditorTheme.ToolbarActive : Color.Transparent)
            .Hovered.BackgroundColor(EditorTheme.Hover).End()
            .Icon(paper, SceneViewIcons.Universal, EditorTheme.Ink500, size: 14f)
            .OnClick(0, (_, _) => SetGizmoMode(Gizmo.TransformGizmoMode.Universal));
    }

    private void DrawViewport(Paper paper, Scribe.FontFile font, float width, float height)
    {
        if (_editorCamera == null || width <= 0 || height <= 0) return;

        uint rtWidth = (uint)MathF.Max(1, width);
        uint rtHeight = (uint)MathF.Max(1, height);
        _editorCamera.EnsureRenderTarget(rtWidth, rtHeight);
        PublishGizmoFadeReference(rtHeight);

        var scene = Scene.Current;
        var rt = _editorCamera.RenderTarget;

        if (scene == null)
        {
            // No scene show message and create button
            using (paper.Column("sv_no_scene")
                .Size(width, height)
                .BackgroundColor(EditorTheme.Neutral300)
                .Enter())
            {
                paper.Box("sv_no_scene_spacer");

                paper.Box("sv_no_scene_text")
                    .Height(30)
                    .Text(Loc.Get("hierarchy.no_scene_loaded"), font)
                    .TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSize)
                    .Alignment(TextAlignment.MiddleCenter);

                using (paper.Row("sv_no_scene_btn_row")
                    .Height(30).RowBetween(8)
                    .Enter())
                {
                    paper.Box("sv_btn_spacer_l");
                    Origami.Button(paper, "sv_create_scene", $"{EditorIcons.Plus}  {Loc.Get("hierarchy.new_scene")}", () => CreateAndLoadDefaultScene()).Width(120).Show();
                    paper.Box("sv_btn_spacer_r");
                }

                paper.Box("sv_no_scene_spacer2");
            }
            return;
        }

        // Update scene view editor registry based on selection
        UpdateSceneViewEditor();

        // Camera nav and gizmo drags must run *before* the RT cache check. Wheel zoom is a
        // one-frame event and the editor does not keep presenting after it (AnimatedBackground
        // is off), so applying input after the blit left the zoom in camera state but never
        // redrew the viewport until the next click.
        bool sceneEditorConsumedInput = false;
        bool sceneEditorSuppressesGizmo = false;
        bool gizmoMoved = false;
        bool cameraMoved = false;
        bool dueForRender = false;
        bool isHovered = IsViewportHovered(paper);
        bool prevWorldSpace = GameCanvas.EditorWorldSpaceOverride;
        GameCanvas.EditorWorldSpaceOverride = true;
        try
        {
            var activeSceneEditor = ActiveSceneViewEditor;
            sceneEditorSuppressesGizmo = activeSceneEditor?.SuppressesTransformGizmo == true;
            if (activeSceneEditor != null && _editorCamera != null)
            {
                var cam = _editorCamera.Camera;
                if (cam != null)
                {
                    Float2 mouseLocal = paper.PointerPos - new Float2((float)_viewportAbsoluteRect.Min.X, (float)_viewportAbsoluteRect.Min.Y);
                    Float2 viewSize = new Float2(width, height);
                    Ray mouseRay = cam.ScreenPointToRay(mouseLocal, viewSize);
                    Rect viewport = new Rect(0, 0, (float)viewSize.X, (float)viewSize.Y);
                    sceneEditorConsumedInput = activeSceneEditor.OnSceneInput(cam, scene, viewport, mouseRay, mouseLocal, isHovered);
                }
            }

            if (!sceneEditorConsumedInput && _editorCamera.ProcessInput(
                (float)Time.UnscaledDeltaTime,
                isHovered,
                paper.PointerPos,
                new Float2((float)_viewportAbsoluteRect.Min.X, (float)_viewportAbsoluteRect.Min.Y),
                new Float2(width, height)))
            {
                cameraMoved = true;
                _renderSchedule.Invalidate();
            }

            if (!sceneEditorConsumedInput && !sceneEditorSuppressesGizmo)
                gizmoMoved = UpdateTransformGizmo(paper, scene, width, height);

            // A scene editor draws hover-reactive handles every frame, so keep the cached RT fresh
            // while the pointer is over the viewport - not only on frames it consumed input (idle
            // hover no longer consumes, see UISceneEditor.OnSceneInput).
            if (sceneEditorConsumedInput || (activeSceneEditor != null && isHovered))
                _renderSchedule.Invalidate();

            // Play mode repaints the Scene View every frame, exactly like the Game View. Gameplay
            // (animation, physics, Spine skeletons) advances without touching scene ChangeVersion,
            // selection or the editor camera pose, so the cached RT would otherwise freeze on the
            // frame play started and keep showing a static pose. Unity keeps the Scene View live.
            // On-demand rendering off (default) likewise keeps the cached RT re-rendering every
            // presented frame so Inspector edits are visible immediately.
            bool scheduled = _renderSchedule.ShouldRender(
                Runtime.Application.IsPlaying || !EditorSettings.Instance.OnDemandRendering,
                scene,
                EditorSceneManager.ChangeVersion,
                QualitySettings.Version,
                (int)rtWidth,
                (int)rtHeight,
                Selection.ChangeVersion + ((long)_editorCamera.PoseVersion << 20));
            dueForRender = EditorViewportRenderGate.ShouldRender(
                panelVisible: true,
                hostVisible: Window.IsVisible,
                width: (int)rtWidth,
                height: (int)rtHeight,
                scheduled: scheduled) || gizmoMoved || cameraMoved;

            if (dueForRender)
            {
                using var profilerScope = EditorProfiler.BeginCpuScope("SceneView");
                PublishSelectionOutlineTargets();
                DrawSelectionGizmos();
                FrameDebugger.BeginTargetScope(FrameDebuggerScopeKind.SceneView);
                try
                {
                    _editorCamera.Render(scene);
                }
                finally
                {
                    FrameDebugger.EndTargetScope();
                }
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[SceneView] Scene render/input threw and was skipped: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            GameCanvas.EditorWorldSpaceOverride = prevWorldSpace;
        }

        if (dueForRender)
            RenderCameraPreview();

        if (rt != null && rt.MainTexture != null)
        {
            paper.Box("sv_viewport")
                .Size(width, height)
                .OnPostLayout((handle, rect) =>
                {
                    // Cache absolute rect for gizmo coordinate space
                    _viewportAbsoluteRect = rect;
                    _viewportElementId = handle.Data.ID;

                    // Draw RT
                    paper.Draw(ref handle, (canvas, r) =>
                    {
                        float rx = (float)r.Min.X;
                        float ry = (float)r.Min.Y;
                        float rw = (float)r.Size.X;
                        float rh = (float)r.Size.Y;

                        // Top corners stay square so the viewport butts flush against the panel header;
                        // only the bottom two are rounded.
                        float rnd = EditorTheme.Roundness;

                        // Camera target orientation depends on the raster backend.
                        canvas.ClearBrush();
                        canvas.SetBrushTexture(rt.MainTexture);
                        canvas.SetBrushTextureTransform(
                            RenderTargetPreview.Transform(rx, ry, rw, rh));
                        canvas.RoundedRectFilled(rx, ry, rw, rh, 0, 0, rnd, rnd, Color.White);
                        canvas.ClearBrushTexture();

                        // Inset the border by half its width so the full stroke sits on top of the texture edge.
                        canvas.RoundedRect(rx + 1, ry + 1, rw - 2, rh - 2, 0, 0, rnd, rnd);
                        bool recording = AnimationRecording.Instance.IsRecording;
                        canvas.SetStrokeColor(recording ? EditorTheme.Red500 : EditorTheme.BorderSoft);
                        canvas.SetStrokeWidth(recording ? 4 : 1);
                        canvas.Stroke();
                    });

                    // Draw transform gizmo as 2D overlay (hidden when a scene editor consumes input
                    // or replaces the transform tools outright, e.g. the UI rect handles)
                    if (_transformGizmo != null && _gizmoActive && !sceneEditorConsumedInput && !sceneEditorSuppressesGizmo)
                    {
                        paper.DrawForeground(ref handle, (canvas2, r2) =>
                        {
                            _transformGizmo.Draw(canvas2);
                        });
                    }

                    // Draw scene view editor overlay
                    var sceneEditorOverlay = ActiveSceneViewEditor;
                    if (sceneEditorOverlay != null)
                    {
                        paper.DrawForeground(ref handle, (canvas2, r2) =>
                        {
                            sceneEditorOverlay.DrawOverlay(canvas2, r2);
                        });
                    }

                    if (_rubberBand.Active)
                    {
                        paper.DrawForeground(ref handle, (canvas2, r2) =>
                        {
                            float ox = (float)r2.Min.X, oy = (float)r2.Min.Y;
                            float x = ox + MathF.Min(_rubberBand.Start.X, _rubberBand.End.X);
                            float y = oy + MathF.Min(_rubberBand.Start.Y, _rubberBand.End.Y);
                            float w2 = MathF.Abs(_rubberBand.End.X - _rubberBand.Start.X);
                            float h2 = MathF.Abs(_rubberBand.End.Y - _rubberBand.Start.Y);
                            canvas2.RectFilled(x, y, w2, h2, Color.FromArgb(40, 74, 144, 226));
                            canvas2.SetStrokeColor(Color.FromArgb(200, 74, 144, 226));
                            canvas2.SetStrokeWidth(1f);
                            canvas2.BeginPath();
                            canvas2.Rect(x, y, w2, h2);
                            canvas2.Stroke();
                        });
                    }
                })
                .OnClick(0, (_, e) =>
                {
                    if (_skipNextClick) { _skipNextClick = false; return; }
                    if (!Input.IsAltPressed && !sceneEditorConsumedInput)
                    {
                        Float2 localPos = new Float2((float)e.RelativePosition.X, (float)e.RelativePosition.Y);
                        Float2 panelSize = new Float2(width, height);
                        PickObject(scene, localPos, panelSize);
                    }
                })
                .OnDragStart(e =>
                {
                    // Marquee only from empty space: not while orbiting (Alt), flying (RMB), panning
                    // (MMB), over a transform-gizmo handle, or when a scene editor owns the input.
                    if (Input.IsAltPressed || sceneEditorConsumedInput) return;
                    if (Input.GetMouseButton(1) || Input.GetMouseButton(2)) return;
                    if (_transformGizmo != null && _gizmoActive && _transformGizmo.IsOver) return;
                    _rubberBand.Begin(new Float2((float)e.StartPosition.X, (float)e.StartPosition.Y));
                })
                .OnDragging(e =>
                {
                    if (!_rubberBand.Active || _editorCamera == null) return;
                    // The gizmo may have claimed the drag on a later frame (its input is polled).
                    if (_transformGizmo != null && _gizmoActive && _transformGizmo.IsOver) { _rubberBand.Cancel(); return; }
                    _rubberBand.Update(scene, _editorCamera.Camera,
                        new Float2((float)e.RelativePosition.X, (float)e.RelativePosition.Y),
                        new Float2(width, height), Input.IsCtrlPressed, Input.IsShiftPressed);
                    _renderSchedule.Invalidate();
                })
                .OnDragEnd(_ =>
                {
                    if (!_rubberBand.Active) return;
                    _rubberBand.EndDrag();
                    _skipNextClick = true;
                });

            // Scene view keyboard shortcuts (only when hovered and not right-click flying)
            if (isHovered && !ShortcutManager.IsRebinding && !Input.GetMouseButton(1))
            {
                if (ShortcutManager.IsPressed("Scene/Delete"))
                {
                    // Shared with HierarchyPanel so the viewport enforces the same prefab
                    // structural-child protection and undo registration as the Hierarchy's Delete.
                    foreach (var go in HierarchyPanel.ExcludeNestedSelections(Selection.GetSelected<GameObject>().ToList()))
                        HierarchyPanel.DeleteGameObject(go);
                    Selection.Clear();
                    EditorSceneManager.MarkDirty();
                }
                else if (ShortcutManager.IsPressed("Scene/Duplicate"))
                {
                    var dupes = GameObjectClipboard.Duplicate(Selection.GetSelected<GameObject>().ToList());
                    foreach (var d in dupes) Undo.RegisterCreatedObject(d, "Duplicate");
                }
                else if (ShortcutManager.IsPressed("Scene/Copy"))
                {
                    GameObjectClipboard.Copy(Selection.GetSelected<GameObject>().ToList());
                }
                else if (ShortcutManager.IsPressed("Scene/Paste"))
                {
                    var pasted = GameObjectClipboard.Paste();
                    foreach (var p in pasted) Undo.RegisterCreatedObject(p, "Paste");
                }
                else if (ShortcutManager.IsPressed("Scene/AlignWithView"))
                {
                    AlignSelectionWithView();
                }
                else if (ShortcutManager.IsPressed("Scene/FocusLock"))
                {
                    _followSelection = true;
                    _editorCamera?.FocusSelection();
                }

                // Gizmo tool switching
                if (ShortcutManager.IsPressed("Scene/ToolTranslate"))
                    SetGizmoMode(Gizmo.TransformGizmoMode.Translate);
                else if (ShortcutManager.IsPressed("Scene/ToolRotate"))
                    SetGizmoMode(Gizmo.TransformGizmoMode.Rotate);
                else if (ShortcutManager.IsPressed("Scene/ToolScale"))
                    SetGizmoMode(Gizmo.TransformGizmoMode.ScaleAll);
                else if (ShortcutManager.IsPressed("Scene/ToolUniversal"))
                    SetGizmoMode(Gizmo.TransformGizmoMode.Universal);
            }

            // Accept asset drops via registry-discovered handlers
            if (isHovered && DragDrop.IsDraggingType<AssetDragPayload>())
            {
                var dragPayload = (AssetDragPayload)DragDrop.Payload!;
                var handler = EditorRegistries.FindSceneDropHandler(dragPayload.AssetType);
                DragDrop.SetVisualMode(paper, handler == null ? DragVisualMode.Rejected
                    : dragPayload.AssetType == typeof(Material) ? DragVisualMode.Link : DragVisualMode.Copy);

                if (handler != null)
                {
                    paper.Box("sv_drop_indicator")
                        .PositionType(PositionType.SelfDirected)
                        .Position(0, height - 24).Size(width, 24)
                        .BackgroundColor(Color.FromArgb(150, EditorTheme.Neutral400))
                        .IsNotInteractable()
                        .Text(handler.DropHint, font)
                        .TextColor(EditorTheme.Purple400)
                        .FontSize(EditorTheme.FontSizeSmall)
                        .Alignment(TextAlignment.MiddleCenter);
                }
            }

            if (isHovered && DragDrop.IsDropFrameFor(paper) && DragDrop.Payload is AssetDragPayload assetDrop)
            {
                var handler = EditorRegistries.FindSceneDropHandler(assetDrop.AssetType);
                if (handler != null)
                {
                    // Convert Paper-space pointer to viewport-local using the cached viewport
                    // rect. CurrentParent here is an enclosing container (toolbar row etc.),
                    // so subtracting its origin put the drop off to the side of the camera.
                    Float2 mouseLocal = paper.PointerPos - new Float2(
                        (float)_viewportAbsoluteRect.Min.X, (float)_viewportAbsoluteRect.Min.Y);

                    handler.Handle(assetDrop, new SceneDropContext
                    {
                        Scene = scene,
                        Camera = _editorCamera!,
                        MouseLocal = mouseLocal,
                        PanelSize = new Float2(width, height),
                    });
                    DragDrop.EndDrag();
                }
            }

            // Speed indicator (shows briefly when scroll changes fly speed)
            DrawSpeedIndicator(paper, font, width, height);

            // View manipulator (orientation cube) drawn as 2D overlay on top-right
            DrawViewManipulator(paper, font, width, height);

            // Movable overlays (tools, grid & snap, camera preview) + the overlay bar
            _overlays.Draw(paper, font, width, height);
        }
    }

    /// <summary>
    /// True only when the pointer is over the render texture itself. Overlays are children of the
    /// same container, so <c>IsParentHovered</c> would also be true while the pointer is on an
    /// overlay slider — which would dolly the camera instead of dragging the slider.
    /// </summary>
    private bool IsViewportHovered(Paper paper)
    {
        // Silk CursorMode.Disabled warps the OS pointer; keep look-mode input alive even when
        // Paper's hover id is no longer the viewport.
        if (Input.CursorLocked)
            return true;
        if (_viewportElementId != 0 && paper.HoveredElementId == _viewportElementId)
            return true;
        if (paper.HoveredElementId != 0) return false;
        if (_viewportAbsoluteRect.Size.X <= 0f || _viewportAbsoluteRect.Size.Y <= 0f)
            return false;
        Float2 p = paper.PointerPos;
        return p.X >= (float)_viewportAbsoluteRect.Min.X && p.X < (float)_viewportAbsoluteRect.Max.X
            && p.Y >= (float)_viewportAbsoluteRect.Min.Y && p.Y < (float)_viewportAbsoluteRect.Max.Y;
    }

    private void RenderCameraPreview()
    {
        var overlay = _overlays.Find("cameras");
        if (overlay is not { Visible: true, Collapsed: false })
            return;

        Camera? camera = FindPreviewCamera();
        if (camera == null) return;

        // "Match Game aspect" frames the preview exactly like the Game view is currently framed;
        // otherwise fall back to 16:9 so the overlay keeps a stable shape.
        float aspect = 16f / 9f;
        if (_previewMatchGameAspect && GameViewPanel.ActiveInstance?.RenderTarget is { } gameTarget
            && gameTarget.Height > 0)
            aspect = gameTarget.Width / (float)gameTarget.Height;

        const int previewWidth = 320;
        int previewHeight = Math.Clamp((int)MathF.Round(previewWidth / MathF.Max(0.1f, aspect)), 32, 640);

        _cameraPreview ??= new CameraPreview();
        Scene? previewScene = camera.GameObject?.Scene;
        bool previewDue = previewScene != null && _cameraPreviewSchedule.ShouldRender(
            Runtime.Application.IsPlaying,
            previewScene,
            EditorSceneManager.ChangeVersion,
            QualitySettings.Version,
            previewWidth,
            previewHeight,
            camera.InstanceID + ((long)camera.Transform.Version << 20));
        if (previewDue || _cameraPreview.LastCamera != camera || _cameraPreview.Texture == null)
            _cameraPreview.Render(camera, previewWidth, previewHeight);
    }

    internal static GameObject? PickObjectAt(Scene scene, EditorCamera camera, Float2 screenPos, Float2 panelSize)
    {
        var ray = camera.ScreenPointToRay(screenPos, panelSize);

        GameObject? bestHit = null;
        float bestDist = float.MaxValue;

        foreach (var go in scene.ActiveObjects)
        {
            if (go.HideFlags.HasFlag(HideFlags.Hide)) continue;
            if (!SceneVisibilityManager.IsPickingEnabledInHierarchy(go)) continue;

            var meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.EnabledInHierarchy && meshRenderer.Raycast(ray, out float dist))
            {
                if (dist < bestDist) { bestDist = dist; bestHit = go; }
                continue;
            }

        }

        // UI elements aren't visible to MeshRenderer raycasts but should still be selectable from the
        // scene view. The scene view lays UI out in world space (ReferenceResolution + transform), so
        // pick with the same world-space override or the design-space hit test would disagree with
        // what's drawn.
        bool prevWorldSpace = GameCanvas.EditorWorldSpaceOverride;
        GameCanvas.EditorWorldSpaceOverride = true;
        try
        {
            GameObject? uiHit = UIPicker.Pick(scene, ray);
            if (uiHit != null && SceneVisibilityManager.IsPickingEnabledInHierarchy(uiHit))
                return uiHit;
        }
        finally
        {
            GameCanvas.EditorWorldSpaceOverride = prevWorldSpace;
        }

        return bestHit;
    }

    private void PickObject(Scene scene, Float2 screenPos, Float2 panelSize)
    {
        _pickHits.Clear();
        CollectHits(scene, _editorCamera!, screenPos, panelSize, _pickHits);

        bool samePoint = MathF.Abs(screenPos.X - _lastPickPos.X) < 4f
                      && MathF.Abs(screenPos.Y - _lastPickPos.Y) < 4f;
        _lastPickPos = screenPos;

        if (_pickHits.Count == 0)
        {
            if (!Input.IsCtrlPressed && !Input.IsShiftPressed)
                Selection.Clear();
            return;
        }

        // Unity's click-through: clicking the same spot again steps to the hit behind the
        // currently selected one, wrapping around.
        int index = 0;
        if (samePoint && _pickHits.Count > 1)
        {
            for (int i = 0; i < _pickHits.Count; i++)
            {
                if (Selection.IsSelected(_pickHits[i].go))
                {
                    index = (i + 1) % _pickHits.Count;
                    break;
                }
            }
        }

        var bestHit = _pickHits[index].go;
        if (Input.IsCtrlPressed)
            Selection.ToggleSelection(bestHit);
        else
            Selection.Select(bestHit);

        // Ping so the Hierarchy scrolls to and briefly highlights the clicked GO.
        Selection.Ping(bestHit.Identifier);
    }

    /// <summary>All pickable hits under a screen point, nearest first (UI hits lead, matching
    /// <see cref="PickObjectAt"/>'s priority). Fills the caller's buffer — no allocation.</summary>
    private static void CollectHits(Scene scene, EditorCamera camera, Float2 screenPos, Float2 panelSize,
        List<(GameObject go, float dist)> hits)
    {
        var ray = camera.ScreenPointToRay(screenPos, panelSize);

        foreach (var go in scene.ActiveObjects)
        {
            if (go.HideFlags.HasFlag(HideFlags.Hide)) continue;
            if (!SceneVisibilityManager.IsPickingEnabledInHierarchy(go)) continue;

            var meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.EnabledInHierarchy && meshRenderer.Raycast(ray, out float dist))
                hits.Add((go, dist));
        }
        hits.Sort(static (a, b) => a.dist.CompareTo(b.dist));

        // UI wins over mesh hits, same as single-pick (see PickObjectAt).
        bool prevWorldSpace = GameCanvas.EditorWorldSpaceOverride;
        GameCanvas.EditorWorldSpaceOverride = true;
        try
        {
            GameObject? uiHit = UIPicker.Pick(scene, ray);
            if (uiHit != null && SceneVisibilityManager.IsPickingEnabledInHierarchy(uiHit))
                hits.Insert(0, (uiHit, 0f));
        }
        finally
        {
            GameCanvas.EditorWorldSpaceOverride = prevWorldSpace;
        }
    }

    /// <summary>Ctrl+Shift+F: move every selected object to the editor camera's position and
    /// orientation (Unity's Align With View), as a single undo step.</summary>
    private void AlignSelectionWithView()
    {
        var cam = _editorCamera;
        if (cam == null) return;
        var targetEuler = new Float3(cam.Pitch, cam.Yaw, 0);
        var targetPos = cam.Position;

        var snapshots = new List<(Guid id, Float3 pos, Float3 euler)>();
        foreach (var go in Selection.GetSelected<GameObject>())
            snapshots.Add((go.Identifier, go.Transform.Position, go.Transform.LocalEulerAngles));
        if (snapshots.Count == 0) return;

        Undo.RegisterAction("Align With View",
            undo: () =>
            {
                foreach (var (id, pos, euler) in snapshots)
                {
                    var g = Undo.FindGO(id);
                    if (g == null) continue;
                    g.Transform.Position = pos;
                    g.Transform.LocalEulerAngles = euler;
                }
            },
            redo: () =>
            {
                foreach (var (id, _, _) in snapshots)
                {
                    var g = Undo.FindGO(id);
                    if (g == null) continue;
                    g.Transform.Position = targetPos;
                    g.Transform.LocalEulerAngles = targetEuler;
                }
            });

        foreach (var go in Selection.GetSelected<GameObject>())
        {
            go.Transform.Position = targetPos;
            go.Transform.LocalEulerAngles = targetEuler;
        }
        EditorSceneManager.MarkDirty();
        _renderSchedule.Invalidate();
    }

    public override bool SerializeState(System.Text.Json.Nodes.JsonObject state)
    {
        if (_editorCamera == null) return false;
        var p = _editorCamera.Position;
        state["px"] = p.X; state["py"] = p.Y; state["pz"] = p.Z;
        state["yaw"] = _editorCamera.Yaw;
        state["pitch"] = _editorCamera.Pitch;
        state["grid"] = _settings.ShowGrid;
        state["gizmos"] = _settings.ShowGizmos;
        state["gridSize"] = _settings.GridSize;
        state["pivot"] = (int)_settings.Pivot;
        state["handleOrientation"] = (int)_settings.Orientation;
        state["snap"] = _settings.SnapEnabled;
        state["snapMove"] = _settings.MoveSnap;
        state["snapRotate"] = _settings.RotateSnap;
        state["snapScale"] = _settings.ScaleSnap;
        state["drawMode"] = (int)_settings.DrawMode;
        state["is2D"] = _settings.Is2D;
        state["viewOrtho"] = _settings.ViewOrthographic;
        state["showBackground"] = _settings.ShowBackground;
        state["audio"] = _settings.AudioEnabled;
        state["fx"] = _settings.EffectsEnabled;
        state["outline"] = GizmoSettings.SelectionOutline;
        state["selectionWire"] = GizmoSettings.SelectionWire;
        state["icons3d"] = GizmoSettings.Show3DIcons;
        state["iconScale"] = GizmoSettings.IconScale;
        state["gizmoScale"] = GizmoSettings.GizmoScale;
        state["fadeGizmos"] = GizmoSettings.FadeGizmos;
        state["fadeSize"] = GizmoSettings.FadeSize;

        var hidden = new System.Text.Json.Nodes.JsonArray();
        foreach (string name in GizmoSettings.HiddenTypeNames())
            hidden.Add(name);
        state["gizmosHidden"] = hidden;

        var hiddenIcons = new System.Text.Json.Nodes.JsonArray();
        foreach (string name in GizmoSettings.HiddenIconTypeNames())
            hiddenIcons.Add(name);
        state["gizmoIconsHidden"] = hiddenIcons;

        state["overlays"] = _overlays.Serialize();
        return true;
    }

    public override void RestoreState(System.Text.Json.Nodes.JsonObject state)
    {
        float px = state["px"]?.GetValue<float>() ?? 0f;
        float py = state["py"]?.GetValue<float>() ?? 5f;
        float pz = state["pz"]?.GetValue<float>() ?? -15f;
        _pendingPos = new Float3(px, py, pz);
        _pendingYaw = state["yaw"]?.GetValue<float>() ?? 0f;
        _pendingPitch = state["pitch"]?.GetValue<float>() ?? 15f;
        _hasPendingPose = true;

        _settings.ShowGrid = state["grid"]?.GetValue<bool>() ?? _settings.ShowGrid;
        _settings.ShowGizmos = state["gizmos"]?.GetValue<bool>() ?? _settings.ShowGizmos;
        _settings.GridSize = state["gridSize"]?.GetValue<float>() ?? _settings.GridSize;
        if (state["pivot"]?.GetValue<int>() is int pivot && pivot is 0 or 1)
            _settings.Pivot = (PivotMode)pivot;
        if (state["handleOrientation"]?.GetValue<int>() is int orientation && orientation is 0 or 1)
            _settings.Orientation = (HandleOrientation)orientation;
        _settings.SnapEnabled = state["snap"]?.GetValue<bool>() ?? _settings.SnapEnabled;
        _settings.MoveSnap = state["snapMove"]?.GetValue<float>() ?? _settings.MoveSnap;
        _settings.RotateSnap = state["snapRotate"]?.GetValue<float>() ?? _settings.RotateSnap;
        _settings.ScaleSnap = state["snapScale"]?.GetValue<float>() ?? _settings.ScaleSnap;
        if (state["drawMode"]?.GetValue<int>() is int drawMode && drawMode is 0 or 1)
            _settings.DrawMode = (Runtime.Rendering.DebugViewMode)drawMode;
        _settings.Is2D = state["is2D"]?.GetValue<bool>() ?? _settings.Is2D;
        _settings.ViewOrthographic = state["viewOrtho"]?.GetValue<bool>() ?? _settings.ViewOrthographic;
        _settings.ShowBackground = state["showBackground"]?.GetValue<bool>() ?? _settings.ShowBackground;
        _settings.AudioEnabled = state["audio"]?.GetValue<bool>() ?? _settings.AudioEnabled;
        _settings.EffectsEnabled = state["fx"]?.GetValue<bool>() ?? _settings.EffectsEnabled;
        GizmoSettings.SelectionOutline = state["outline"]?.GetValue<bool>() ?? GizmoSettings.SelectionOutline;
        // Pre-split layouts only stored "outline" for both; fall back to it so an upgrade keeps the
        // wire box the user already had rather than silently dropping it.
        GizmoSettings.SelectionWire = state["selectionWire"]?.GetValue<bool>()
            ?? state["outline"]?.GetValue<bool>() ?? GizmoSettings.SelectionWire;
        GizmoSettings.Show3DIcons = state["icons3d"]?.GetValue<bool>() ?? GizmoSettings.Show3DIcons;
        GizmoSettings.IconScale = state["iconScale"]?.GetValue<float>() ?? GizmoSettings.IconScale;
        GizmoSettings.GizmoScale = state["gizmoScale"]?.GetValue<float>() ?? GizmoSettings.GizmoScale;
        GizmoSettings.FadeGizmos = state["fadeGizmos"]?.GetValue<bool>() ?? GizmoSettings.FadeGizmos;
        GizmoSettings.FadeSize = state["fadeSize"]?.GetValue<float>() ?? GizmoSettings.FadeSize;

        if (state["gizmosHidden"] is System.Text.Json.Nodes.JsonArray hidden)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var node in hidden)
                if (node?.GetValue<string>() is string name) names.Add(name);
            GizmoSettings.RestoreHiddenTypeNames(names);
        }

        if (state["gizmoIconsHidden"] is System.Text.Json.Nodes.JsonArray hiddenIcons)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var node in hiddenIcons)
                if (node?.GetValue<string>() is string name) names.Add(name);
            GizmoSettings.RestoreHiddenIconTypeNames(names);
        }

        // Overlay placement needs the overlay list, which is built on the first OnGUI.
        BuildOverlays();
        _overlays.Restore(state["overlays"] as System.Text.Json.Nodes.JsonArray);
    }

    /// <summary>
    /// Create an empty Universal scene (camera, light, global volume) and load it.
    /// </summary>
    public static void CreateAndLoadDefaultScene()
    {
        Guid volumeGuid = Guid.Empty;
        if (EditorAssetBackend.Instance != null)
            volumeGuid = EditorAssetBackend.Instance.PathToGuid(UniversalProjectTemplate.VolumeProfilePath);

        Scene scene = UniversalProjectTemplate.CreateEmptyScene(volumeGuid, "Untitled Scene");
        Scene.Load(scene);
        Undo.Clear();
        Runtime.Debug.Log("Created default Universal scene.");
    }

    /// <summary>
    /// Raycast into the scene to find a drop position. Falls back to the XZ plane at Y=0.
    /// </summary>
    internal static Float3 GetDropPosition(Scene scene, EditorCamera camera, Float2 screenPos, Float2 panelSize)
    {
        var ray = camera.ScreenPointToRay(screenPos, panelSize);

        // Try raycasting against scene objects first
        float bestDist = float.MaxValue;
        Float3 bestPos = Float3.Zero;
        bool hit = false;

        foreach (var go in scene.ActiveObjects)
        {
            if (go.HideFlags.HasFlag(HideFlags.Hide)) continue;

            var meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer != null && meshRenderer.Raycast(ray, out float dist))
            {
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPos = ray.Origin + ray.Direction * dist;
                    hit = true;
                }
            }

        }

        if (hit) return bestPos;

        // Fallback: intersect with XZ plane at Y=0
        if (MathF.Abs(ray.Direction.Y) > 0.0001f)
        {
            float t = -ray.Origin.Y / ray.Direction.Y;
            if (t > 0)
                return ray.Origin + ray.Direction * t;
        }

        // Last resort: place 10 units in front of camera
        return ray.Origin + ray.Direction * 10f;
    }

    private readonly List<GameObject> _outlineTargets = new();

    /// <summary>
    /// Give "Fade Gizmos" this view's pixel metric, so a gizmo's screen size can be judged in the
    /// view gizmos exist for. Gizmos are recorded once at the top of the editor frame, before any
    /// panel draws, so the reference published here is consumed by the *next* frame's recording —
    /// a lag of one frame on a fade ramp, which is not observable.
    /// </summary>
    private void PublishGizmoFadeReference(uint rtHeight)
    {
        if (_editorCamera == null || rtHeight == 0) return;

        Camera cam = _editorCamera.Camera;
        if (_editorCamera.IsOrthographic)
        {
            // OrthographicSize is the full vertical extent (Camera.GetProjectionMatrix).
            float orthoSize = MathF.Max(0.0001f, cam.OrthographicSize);
            GizmoSettings.SetFadeReference(_editorCamera.Position, orthographic: true, rtHeight / orthoSize);
        }
        else
        {
            float halfFov = MathF.Max(0.0001f, cam.FieldOfView * 0.5f * (MathF.PI / 180f));
            GizmoSettings.SetFadeReference(_editorCamera.Position, orthographic: false,
                rtHeight / (2f * MathF.Tan(halfFov)));
        }
    }

    /// <summary>
    /// ★ M7 — Hands the current selection to the pipeline so it can draw the screen-space outline.
    /// The list is reused frame to frame; an empty list turns the outline passes off.
    /// </summary>
    private void PublishSelectionOutlineTargets()
    {
        if (_editorCamera == null) return;
        _outlineTargets.Clear();
        if (GizmoSettings.SelectionOutline)
        {
            var selected = Selection.Selected;
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is GameObject go)
                    _outlineTargets.Add(go);
            }
        }
        _editorCamera.SelectionOutlineTargets = _outlineTargets;
    }

    private void DrawSelectionGizmos()
    {
        if (!GizmoSettings.SelectionWire) return;

        foreach (var obj in Selection.Selected)
        {
            if (obj is not GameObject go) continue;

            var col = new Vector.Color(0.3f, 0.6f, 1f, 1f);

            // Collect world-space AABB from all renderers in this GO and its children
            Float3 min = new(float.MaxValue), max = new(float.MinValue);
            bool found = false;
            CollectRendererBounds(go, ref min, ref max, ref found);

            if (found)
            {
                Float3 center = (min + max) * 0.5f;
                Float3 halfExtents = (max - min) * 0.5f;
                Debug.DrawWireCube(center, halfExtents, col);
            }
            else
            {
                Float3 pos = go.Transform.Position;
                float s = 0.3f;
                Debug.DrawLine(pos - Float3.UnitX * s, pos + Float3.UnitX * s, col);
                Debug.DrawLine(pos - Float3.UnitY * s, pos + Float3.UnitY * s, col);
                Debug.DrawLine(pos - Float3.UnitZ * s, pos + Float3.UnitZ * s, col);
            }
        }
    }

    private static void CollectRendererBounds(GameObject go, ref Float3 min, ref Float3 max, ref bool found)
    {
        // Check MeshRenderer
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null && mr.Mesh.Res != null)
            ExpandBounds(mr.Mesh.Res.bounds, go.Transform.LocalToWorldMatrix, ref min, ref max, ref found);

        // Check SkinnedMeshRenderer
        var smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr != null && smr.SharedMesh.Res != null)
            ExpandBounds(smr.SharedMesh.Res.bounds, go.Transform.LocalToWorldMatrix, ref min, ref max, ref found);

        // Recurse into children
        foreach (var child in go.Children)
            CollectRendererBounds(child, ref min, ref max, ref found);
    }

    private static void ExpandBounds(AABB localBounds, Float4x4 matrix, ref Float3 min, ref Float3 max, ref bool found)
    {
        // Transform all 8 corners to world space and expand the combined AABB
        for (int i = 0; i < 8; i++)
        {
            Float3 corner = Float4x4.TransformPoint(new Float3(
                (i & 1) == 0 ? localBounds.Min.X : localBounds.Max.X,
                (i & 2) == 0 ? localBounds.Min.Y : localBounds.Max.Y,
                (i & 4) == 0 ? localBounds.Min.Z : localBounds.Max.Z), matrix);
            min = new Float3(MathF.Min(min.X, corner.X), MathF.Min(min.Y, corner.Y), MathF.Min(min.Z, corner.Z));
            max = new Float3(MathF.Max(max.X, corner.X), MathF.Max(max.Y, corner.Y), MathF.Max(max.Z, corner.Z));
        }
        found = true;
    }

    // ================================================================
    //  Transform Gizmo
    // ================================================================

    private void SetGizmoMode(Gizmo.TransformGizmoMode mode)
    {
        _gizmoMode = mode;
        _transformGizmo?.SetMode(mode);
        _renderSchedule.Invalidate();
    }

    private bool UpdateTransformGizmo(Paper paper, Scene scene, float width, float height)
    {
        _gizmoActive = false;
        if (_editorCamera == null) return false;

        // Only show gizmo when GameObjects are selected
        var selectedGOs = Selection.GetSelected<GameObject>().GetEnumerator();
        if (!selectedGOs.MoveNext()) return false;

        _gizmoActive = true;

        var firstGO = selectedGOs.Current;
        if (firstGO == null) return false;

        // Create gizmo if needed
        _transformGizmo ??= new Gizmo.TransformGizmo(_gizmoMode);

        // Center pivots on the whole selection; Pivot mode uses the active object's own origin.
        Float3 center = firstGO.Transform.Position;
        if (_settings.Pivot == PivotMode.Center)
        {
            Float3 sum = Float3.Zero;
            int count = 0;
            foreach (var go in Selection.GetSelected<GameObject>())
            {
                sum += go.Transform.Position;
                count++;
            }
            if (count > 0) center = sum / count;
        }

        // Handles align to the active object in Local mode and to the world axes in Global mode.
        Quaternion rotation = _settings.Orientation == HandleOrientation.Local
            ? firstGO.Transform.Rotation
            : Quaternion.Identity;
        Float3 scale = firstGO.Transform.LossyScale;
        _transformGizmo.Orientation = _settings.Orientation == HandleOrientation.Local
            ? Gizmo.TransformGizmo.GizmoOrientation.Local
            : Gizmo.TransformGizmo.GizmoOrientation.Global;

        // Update gizmo use absolute screen rect so coordinates match DrawForeground
        var cam = _editorCamera.Camera;
        var camGo = cam.GameObject;

        _transformGizmo.UpdateCamera(_viewportAbsoluteRect, cam.ViewMatrix, cam.ProjectionMatrix,
            camGo.Transform.Up, camGo.Transform.Forward, camGo.Transform.Right, camGo.Transform.Position);
        _transformGizmo.SetTransform(center, rotation, scale);

        // Mouse in Paper-logical space, matching _viewportAbsoluteRect (also Paper-logical).
        Float2 mouseAbs = paper.PointerPos;
        Float2 mouseLocal = mouseAbs - new Float2((float)_viewportAbsoluteRect.Min.X, (float)_viewportAbsoluteRect.Min.Y);
        var ray = _editorCamera.ScreenPointToRay(mouseLocal, new Float2(width, height));

        // Alt is the camera orbit modifier, so the gizmo must not capture the mouse while it is held.
        // Otherwise Alt+LMB drags the selected object instead of orbiting the view.
        bool cameraNav = Input.IsAltPressed;
        bool blockPicking = Input.GetMouseButton(1) || Input.GetMouseButton(2) || cameraNav; // Don't pick while camera moving

        // Input state for the gizmo (Origami gizmo has no Runtime dependency). Ctrl still snaps on
        // demand even when the toolbar's snap toggle is off.
        _transformGizmo.SnapDistance = MathF.Max(0.0001f, _settings.MoveSnap);
        _transformGizmo.SnapAngle = MathF.Max(0.01f, _settings.RotateSnap);
        _transformGizmo.SnapScale = MathF.Max(0.0001f, _settings.ScaleSnap);
        _transformGizmo.Snapping = _settings.SnapEnabled
            || Input.GetKey(KeyCode.ControlLeft) || Input.GetKey(KeyCode.ControlRight);
        _transformGizmo.IsShiftDown = Input.GetKey(KeyCode.ShiftLeft) || Input.GetKey(KeyCode.ShiftRight);
        _transformGizmo.IsMouseDown = Input.GetMouseButtonDown(0) && !cameraNav;
        _transformGizmo.IsMouseUp = Input.GetMouseButtonUp(0);
        _transformGizmo.IsMouseHeld = Input.GetMouseButton(0) && !cameraNav;

        var result = _transformGizmo.Update(ray, mouseAbs, blockPicking);

        // Gizmo drawing happens in the viewport's DrawForeground callback (needs canvas)

        // Continuous undo: track gizmo drag start/end
        bool gizmoActive = result.HasValue;
        if (gizmoActive && !_wasGizmoActive)
            Undo.BeginContinuous(Selection.GetSelected<GameObject>().ToArray(), "Transform");
        if (!gizmoActive && _wasGizmoActive)
            Undo.EndContinuous();
        _wasGizmoActive = gizmoActive;

        if (result.HasValue)
        {
            var r = result.Value;

            // Apply translation
            if (r.TranslationDelta.HasValue)
            {
                foreach (var go in Selection.GetSelected<GameObject>())
                    go.Transform.Position += r.TranslationDelta.Value;

                // V vertex snapping: pull the selection so the gizmo center lands on the mesh
                // vertex of an unselected object nearest to the cursor (Unity's V snap, simplified
                // to the pivot rather than a grabbed source vertex).
                if (Input.GetKey(KeyCode.V))
                {
                    var target = VertexSnap.FindNearestVertexToCursor(
                        scene, cam, mouseLocal, new Float2(width, height), 25f);
                    if (target.HasValue)
                    {
                        Float3 snapOffset = target.Value - (center + r.TranslationDelta.Value);
                        foreach (var go in Selection.GetSelected<GameObject>())
                            go.Transform.Position += snapOffset;
                    }
                }
            }

            // Apply rotation. In Center pivot mode the whole selection turns around the shared
            // gizmo center (Unity behavior); positions orbit it while each object also spins.
            // Pivot mode rotates every object around its own origin.
            if (r.RotationDelta.HasValue && r.RotationAxis.HasValue)
            {
                var rotDelta = Quaternion.AxisAngle(r.RotationAxis.Value, r.RotationDelta.Value);
                bool aroundCenter = _settings.Pivot == PivotMode.Center;
                foreach (var go in Selection.GetSelected<GameObject>())
                {
                    if (aroundCenter)
                        go.Transform.Position = center + rotDelta * (go.Transform.Position - center);
                    go.Transform.Rotation = rotDelta * go.Transform.Rotation;
                }
            }

            // Apply scale; Center pivot also scales the offsets from the shared center.
            if (r.ScaleDelta.HasValue)
            {
                bool aroundCenter = _settings.Pivot == PivotMode.Center;
                foreach (var go in Selection.GetSelected<GameObject>())
                {
                    if (aroundCenter)
                        go.Transform.Position = center + (go.Transform.Position - center) * r.ScaleDelta.Value;
                    go.Transform.LocalScale *= r.ScaleDelta.Value;
                }
            }

            EditorSceneManager.MarkDirty();
            _renderSchedule.Invalidate();
            return true;
        }

        return false;
    }

    // ================================================================
    //  View Manipulator (orientation cube)
    // ================================================================

    private void DrawSpeedIndicator(Paper paper, Scribe.FontFile font, float width, float height)
    {
        if (_editorCamera == null) return;

        double elapsed = Time.UnscaledTotalTime - _editorCamera.SpeedChangedTime;
        if (elapsed > 1.5) return; // Show for 1.5 seconds

        float alpha = elapsed < 1.0 ? 1f : 1f - (float)(elapsed - 1.0) / 0.5f;
        byte a = (byte)(alpha * 180);
        byte ta = (byte)(alpha * 255);

        float boxW = 80, boxH = 32;
        float x = (width - boxW) / 2f;
        float y = (height - boxH) / 2f;

        paper.Box("sv_speed_hud")
            .PositionType(PositionType.SelfDirected)
            .Position(x, y).Size(boxW, boxH)
            .BackgroundColor(Color.FromArgb(a, EditorTheme.Neutral400))
            .Rounded(6)
            .IsNotInteractable()
            .Text($"{_editorCamera.MoveSpeed:F1}", font)
            .TextColor(Color.FromArgb(ta, EditorTheme.Ink500))
            .FontSize(18f)
            .Alignment(TextAlignment.MiddleCenter);
    }

    public override void OnClosed()
    {
        if (ReferenceEquals(ActiveInstance, this))
        {
            ActiveInstance = null;
            ActiveCamera = null;
        }
        _cameraPreview?.Dispose();
        _cameraPreview = null;
        _previewCamera = null;
        DisposePaintRegions();
    }

    private void AttachPaintRegion(Paper paper)
    {
        if (!Program.ScenePaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
        if (!ReferenceEquals(_paintRegionPaper, paper))
        {
            _toolbarPaint?.Dispose();
            _toolbarPaint = null;
            _paintRegionPaper = paper;
        }
        if (_toolbarPaint == null)
        {
            paper.StabilizeFontAtlasBeforeRender = true;
            paper.Canvas.EnableFontAtlasRetirement();
            _toolbarPaint = new UiPaperSubtreeRegion(paper, _paintGeometryBudget, _paintAtlasBudget, 512,
                shareHostFonts: true, compareVisualState: false);
        }
        var rect = paper.CurrentParent.Data.LayoutRect;
        bool pointerOver = paper.PointerPos.X >= rect.Min.X && paper.PointerPos.X <= rect.Max.X
            && paper.PointerPos.Y >= rect.Min.Y && paper.PointerPos.Y <= rect.Max.Y;
        ulong key = ToolbarPaintKey();
        if (_toolbarPaint.CaptureCount == 0 || key != _toolbarPaintKey || pointerOver || _toolbarPointerOver)
            _toolbarPaint.Invalidate();
        _toolbarPaintKey = key;
        _toolbarPointerOver = pointerOver;
        _toolbarPaint.Attach(paper.CurrentParent);
    }

    private void DisposePaintRegions()
    {
        _toolbarPaint?.Dispose();
        _toolbarPaint = null;
        _paintRegionPaper = null;
        _toolbarPaintKey = 0;
        _toolbarPointerOver = false;
    }

    private ulong ToolbarPaintKey()
    {
        ulong key = (ulong)(int)_settings.DrawMode;
        key = key * 397 ^ (ulong)(int)_settings.Pivot;
        key = key * 397 ^ (ulong)(int)_settings.Orientation;
        key = key * 397 ^ (_settings.ShowGrid ? 1u : 0u);
        key = key * 397 ^ (uint)BitConverter.SingleToInt32Bits(_settings.GridSize);
        key = key * 397 ^ (_settings.SnapEnabled ? 1u : 0u);
        key = key * 397 ^ (uint)BitConverter.SingleToInt32Bits(_settings.MoveSnap);
        key = key * 397 ^ (uint)BitConverter.SingleToInt32Bits(_settings.RotateSnap);
        key = key * 397 ^ (uint)BitConverter.SingleToInt32Bits(_settings.ScaleSnap);
        key = key * 397 ^ (_settings.Is2D ? 1u : 0u);
        key = key * 397 ^ (_settings.SceneLightingEnabled ? 1u : 0u);
        key = key * 397 ^ (_settings.AudioEnabled ? 1u : 0u);
        key = key * 397 ^ (_settings.EffectsEnabled ? 1u : 0u);
        key = key * 397 ^ (_settings.ShowGizmos ? 1u : 0u);
        key = key * 397 ^ (GizmosWindow.IsOpenFor(this) ? 1u : 0u);
        foreach (var overlay in _overlays.Overlays)
            key = key * 397 ^ (overlay.Visible ? 1u : 0u);
        return key;
    }

    private void DrawViewManipulator(Paper paper, Scribe.FontFile font, float width, float height)
    {
        if (_editorCamera == null) return;

        _viewManipulator ??= new Gizmo.ViewManipulatorGizmo();

        const float gizmoSize = 96f;
        _viewManipulator.SetCamera(_editorCamera.Camera.GameObject.Transform.Forward,
            _editorCamera.Camera.GameObject.Transform.Up);

        paper.Box("sv_view_manip")
            .PositionType(PositionType.SelfDirected)
            .Position(width - gizmoSize - 4, 8)
            .Size(gizmoSize, gizmoSize + 18)
            .OnRightClick(_ => ShowViewCubeMenu(paper))
            .OnPostLayout((handle, rect) => paper.DrawForeground(ref handle, (canvas, r) =>
            {
                _viewManipulator.SetRect(r);

                bool blockPicking = _transformGizmo?.IsOver ?? false;
                bool clicked = Input.GetMouseButtonDown(0);
                Float2 mousePos = paper.PointerPos;

                if (_viewManipulator.Update(canvas, font, mousePos, clicked, blockPicking,
                    _editorCamera.IsOrthographic, out var newForward, out bool toggleOrtho))
                {
                    if (toggleOrtho)
                        SetViewOrthographic(!_settings.ViewOrthographic);
                    else
                        SnapView(newForward);
                }
            }));
    }

    private void ShowViewCubeMenu(Paper paper)
    {
        Float3 forward = _editorCamera != null
            ? _editorCamera.Camera.GameObject.Transform.Forward
            : Float3.UnitZ;
        Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, menu =>
        {
            menu.Item(Loc.Get("scene.view_free"), () => { }, on: !IsStandardView(forward));
            menu.Item(Loc.Get("scene.view_right"), () => SnapView(new Float3(-1, 0, 0)),
                on: IsLookingAlong(forward, new Float3(-1, 0, 0)));
            menu.Item(Loc.Get("scene.view_top"), () => SnapView(new Float3(0, -1, 0)),
                on: IsLookingAlong(forward, new Float3(0, -1, 0)));
            menu.Item(Loc.Get("scene.view_front"), () => SnapView(new Float3(0, 0, -1)),
                on: IsLookingAlong(forward, new Float3(0, 0, -1)));
            menu.Item(Loc.Get("scene.view_left"), () => SnapView(new Float3(1, 0, 0)),
                on: IsLookingAlong(forward, new Float3(1, 0, 0)));
            menu.Item(Loc.Get("scene.view_bottom"), () => SnapView(new Float3(0, 1, 0)),
                on: IsLookingAlong(forward, new Float3(0, 1, 0)));
            menu.Item(Loc.Get("scene.view_back"), () => SnapView(new Float3(0, 0, 1)),
                on: IsLookingAlong(forward, new Float3(0, 0, 1)));
            menu.Separator();
            menu.Toggle(Loc.Get("scene.view_perspective"),
                () => SetViewOrthographic(!_settings.ViewOrthographic),
                () => !_settings.ViewOrthographic);
            menu.Toggle(Loc.Get("scene.view_show_background"),
                () =>
                {
                    _settings.ShowBackground = !_settings.ShowBackground;
                    InvalidateRender();
                },
                () => _settings.ShowBackground);
        });
    }

    private void SnapView(Float3 forward)
    {
        if (_editorCamera == null) return;
        _editorCamera.LookAlong(forward);
        InvalidateRender();
    }

    private void SetViewOrthographic(bool orthographic)
    {
        _settings.ViewOrthographic = orthographic;
        if (!orthographic) _settings.Is2D = false;
        _editorCamera?.SetProjectionOrthographic(orthographic);
        InvalidateRender();
    }

    internal static bool IsLookingAlong(Float3 forward, Float3 axis, float epsilon = 0.02f)
    {
        float len = Float3.Length(forward);
        if (len < 1e-6f) return false;
        return Float3.Dot(forward / len, axis) > 1f - epsilon;
    }

    private static bool IsStandardView(Float3 forward)
        => IsLookingAlong(forward, new Float3(-1, 0, 0))
        || IsLookingAlong(forward, new Float3(0, -1, 0))
        || IsLookingAlong(forward, new Float3(0, 0, -1))
        || IsLookingAlong(forward, new Float3(1, 0, 0))
        || IsLookingAlong(forward, new Float3(0, 1, 0))
        || IsLookingAlong(forward, new Float3(0, 0, 1));
}
