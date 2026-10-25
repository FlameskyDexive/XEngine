using System;
using System.Collections.Generic;

using XEngine.Async;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Theming;
using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Spatial;

using Color = System.Drawing.Color;
// Aliased to avoid clashing with XEngine.PaperUI types used elsewhere in this file.
using RuntimeEventSystem = XEngine.Runtime.UI.EventSystem;

namespace XEngine.Editor.GUI.Panels;

public partial class GameViewPanel : DockPanel, IScriptReloadCleanup
{
    [MenuItem("Window/General/Game", priority: 1)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(GameViewPanel));

    /// <summary>★ runtime_screenshot — the most recently opened GameViewPanel instance (null when none
    /// open). Tracked so the in-process MCP screenshot tool can find the active render target without
    /// walking the DockSpace.</summary>
    public static GameViewPanel? ActiveInstance { get; private set; }

    /// <summary>Number of complete GameView frames rendered by this view.</summary>
    public long RenderedFrameCount { get; private set; }

    /// <summary>Request a fresh camera frame after an external or paused simulation change.</summary>
    public void RequestRender() => _renderSchedule.Invalidate();

    /// <summary>Current Game View color target (Frame Debugger preview).</summary>
    internal RenderTexture? RenderTarget => _rt;

    public override string Title => Loc.Get("panel.game");
    public override string Icon => EditorIcons.Gamepad;

    private RenderTexture? _rt;

    // XENGINE_PLAY_CAPTURE_DIR periodic capture (see the draw-site comment).
    private static readonly string? _captureDir = Environment.GetEnvironmentVariable("XENGINE_PLAY_CAPTURE_DIR");
    private static readonly System.Diagnostics.Stopwatch? _playCapture = _captureDir != null ? System.Diagnostics.Stopwatch.StartNew() : null;
    private static long _lastCaptureMs;
    internal static bool CaptureRequested => _captureDir != null;

    /// <summary>Frozen final image of the Frame Debugger's capture frame — restored into
    /// <see cref="_rt"/> every replay frame so this view keeps the finished picture while
    /// the debugger panel previews the truncated per-event state.</summary>
    private RenderTexture? _fdFinalFrame;

    private int _pendingRtWidth;
    private int _pendingRtHeight;
    private double _pendingRtSince;
    private int _resolutionIndex = 0;
    private bool _showStats;
    private RenderStats.Frame _gameStats; // snapshot from last game render (persists when paused)
    private bool _gpuFaulted;
    private string? _gpuFaultMessage;
    private Rect _displayAbsRect; // game-view rect in paper coords, cached for routing UI input next frame

    // Toolbar state (see GameViewPanel.Toolbar.cs).
    private GameViewMode _viewMode = GameViewMode.Game;
    private int _displayIndex;          // 0-based; matches Camera.TargetDisplay
    private float _zoom = 1f;           // scale multiplier on top of "fit to panel"
    private bool _showGizmos;
    private int _deviceIndex;           // index into GameViewDevices.All
    private bool _landscape;
    private bool _showSafeArea = true;
    private float _effectiveScale = 1f; // fit * zoom, shown next to the Scale slider

    private readonly GameViewRenderSchedule _renderSchedule = new();
    private readonly GameViewCaptureRequestQueue<(byte[] rgba, int width, int height)?> _captureRequests = new();

    internal GameViewCaptureRequest<(byte[] rgba, int width, int height)?> CaptureNextRenderedFrame()
    {
        GameViewCaptureRequest<(byte[] rgba, int width, int height)?> capture = _captureRequests.Request();
        RequestRender();
        return capture;
    }

    internal void CancelCaptureRequest(GameViewCaptureRequest<(byte[] rgba, int width, int height)?> capture)
        => _captureRequests.Cancel(capture);

    // Separate Paper instance for in-game UI
    private PaperRenderer? _gamePaperRenderer;
    private Paper? _gamePaper;

    /// <summary>Highest <see cref="Camera.TargetDisplay"/> the display picker offers.</summary>
    private const int MaxDisplays = 8;

    private const float ToolbarHeight = 24f;
    private UiPaperSubtreeRegion? _toolbarPaint;
    private Paper? _paintRegionPaper;
    private readonly UiMemoryBudget _paintGeometryBudget = new(4 * 1024 * 1024);
    private readonly UiMemoryBudget _paintAtlasBudget = new(8 * 1024 * 1024);
    private ulong _toolbarPaintKey;
    private bool _toolbarPointerOver;
    internal long ToolbarRegionCaptures => _toolbarPaint?.CaptureCount ?? -1;

    private static readonly (string name, int w, int h)[] Resolutions =
    {
        ("Free Aspect", 0, 0),
        ("16:9", -16, -9),
        ("16:10", -16, -10),
        ("4:3", -4, -3),
        ("5:4", -5, -4),
        ("21:9", -21, -9),
        ("1:1", -1, -1),
        ("Full HD (1920x1080)", 1920, 1080),
        ("QHD (2560x1440)", 2560, 1440),
        ("HD (1280x720)", 1280, 720),
        ("qHD (960x540)", 960, 540),
        ("SVGA (800x600)", 800, 600),
        ("VGA (640x480)", 640, 480),
    };

    // User-added fixed resolutions; indexed after the built-ins (_resolutionIndex >= Resolutions.Length).
    private readonly List<(string name, int w, int h)> _customResolutions = new();

    private (string name, int w, int h) CurrentResolution
    {
        get
        {
            if (_resolutionIndex < Resolutions.Length) return Resolutions[_resolutionIndex];
            int custom = _resolutionIndex - Resolutions.Length;
            return custom < _customResolutions.Count ? _customResolutions[custom] : Resolutions[0];
        }
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        using (paper.Column("gv_root").Size(width, height).Enter())
        {
            if (_gpuFaulted)
            {
                paper.Box("gv_gpu_fault").Size(width, height)
                    .BackgroundColor(EditorTheme.Neutral100)
                    .Text(_gpuFaultMessage ?? "Game View GPU device lost", font)
                    .TextColor(EditorTheme.Red400)
                    .Alignment(TextAlignment.MiddleCenter);
                return;
            }

            using (EditorProfiler.BeginCpuScope("GameChrome"))
                DrawToolbar(paper, font, ToolbarHeight);
            float viewHeight = MathF.Max(1f, height - ToolbarHeight);

            try
            {
                DrawGameView(paper, font, width, viewHeight);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("device loss", StringComparison.OrdinalIgnoreCase))
            {
                _gpuFaulted = true;
                _gpuFaultMessage = "Game View GPU device lost. Restart the editor to recover.";
                Runtime.Debug.LogError("Game View stopped after GPU device loss: " + exception.Message);
            }
        }
    }

    public override float HeaderWidth => 28f;
    public override void OnHeaderContent(Paper paper, float width, float height)
    {
        EditorGUI.HeaderIconButton(paper, "gv_hdr_settings", EditorIcons.Gear, () =>
            Origami.ContextMenu((float)paper.PointerPos.X, (float)paper.PointerPos.Y, b =>
            {
                b.Header(Loc.Get("panel.game"));
                b.Submenu(Loc.Get("game.resolution"), BuildResolutionMenu, EditorIcons.Expand);
                b.Toggle(Loc.Get("game.show_stats"), () => _showStats = !_showStats, () => _showStats);
                b.Toggle(Loc.Get("game.gizmos"), ToggleGizmos, () => _showGizmos);
                b.Separator();
                b.Item(Loc.Get("game.reset_view"), () =>
                {
                    _zoom = 1f;
                    _viewMode = GameViewMode.Game;
                    _resolutionIndex = 0;
                    InvalidateRT();
                }, icon: EditorIcons.RotateLeft);
            }));
    }

    private void DrawGameView(Paper paper, Scribe.FontFile font, float width, float height)
    {
        using (paper.Box("gv_game").Size(width, height).Clip().Enter())
        {
            var scene = Scene.Current;
            if (scene == null)
            {
                _captureRequests.Complete(null);
                paper.Box("gv_no_scene")
                    .Size(width, height)
                    .BackgroundColor(EditorTheme.Neutral100)
                    .Text(Loc.Get("panel.game") + " - " + Loc.Get("selector.no_scene"), font)
                    .TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter);
                return;
            }

            // The simulator renders at the device's own resolution; Game mode follows the
            // resolution picker (free / fixed aspect / fixed size).
            var (rtW, rtH) = _viewMode == GameViewMode.Simulator
                ? GameViewDevices.All[_deviceIndex].RenderSize(_landscape)
                : ResolveGameRenderSize(width, height);

            // A transient zero-sized panel (play-mode layout reflow, collapsed dock leaf) must
            // not reach EnsureRT: a 0-sized RenderTexture crashes the Vulkan image allocation
            // (DivideByZero in vkCreateImage) and takes the editor down. Skip a frame instead.
            if (rtW < 1 || rtH < 1)
            {
                _captureRequests.Complete(null);
                return;
            }

            EnsureRT(rtW, rtH);
            ActiveInstance = this; // ★ runtime_screenshot: keep a ref to the live panel

            // While a dock splitter is being dragged, keep rendering to the previous target until
            // the requested size settles. This avoids a synchronous GPU allocation for every pixel
            // crossed by the pointer and guarantees there is always a valid texture to display.
            rtW = _rt!.Width;
            rtH = _rt.Height;

            bool playing = Application.IsPlaying;

            // Collected every frame (not just render frames) so the "No cameras rendering" notice
            // appears the moment the last camera for this display goes away.
            List<Camera> cameras = CollectGameViewCameras(scene);
            bool hasCameras = cameras.Count > 0;
            if (!hasCameras)
                _captureRequests.Complete(null);

            // Frame Debugger armed: render every frame so the capture sees the cameras and the
            // truncated replay refreshes the target at the selected event (Unity behavior).
            // On-demand rendering off (default) also renders every presented frame.
            bool scheduled = _renderSchedule.ShouldRender(
                playing || FrameDebugger.IsEnabled || !EditorSettings.Instance.OnDemandRendering,
                scene,
                EditorSceneManager.ChangeVersion,
                QualitySettings.Version,
                rtW,
                rtH);
            bool dueForRender = hasCameras && EditorViewportRenderGate.ShouldRender(
                panelVisible: true,
                hostVisible: Window.IsVisible,
                width: rtW,
                height: rtH,
                scheduled: scheduled);

            if (dueForRender)
            {
                using var profilerScope = EditorProfiler.BeginCpuScope("GameView");

                RenderStats.BeginFrame();
                FrameDebugger.BeginTargetScope(FrameDebuggerScopeKind.GameView);
                try
                {
                    for (int i = 0; i < cameras.Count; i++)
                    {
                        Camera cam = cameras[i];
                        RenderTexture? originalTarget = cam.Target;
                        try
                        {
                            cam.Target = _rt;
                            cam.UpdateRenderData();

                            // Stacked overlays draw onto the Base camera's color target, so only the
                            // last camera of a stack blits to this view's render texture.
                            var renderData = new RenderingData
                            {
                                DisplayGizmos = _showGizmos,
                                StackShareColor = cam.RenderType == CameraRenderType.Overlay,
                                StackSkipFinalBlit = i + 1 < cameras.Count
                                    && cameras[i + 1].RenderType == CameraRenderType.Overlay,
                            };

                            var pipeline = cam.Pipeline ?? UniversalRenderPipeline.Default;
                            pipeline.Render(cam, renderData);
                        }
                        finally
                        {
                            cam.Target = originalTarget;
                        }
                    }
                }
                finally
                {
                    FrameDebugger.EndTargetScope();
                    RenderStats.EndFrame();
                }
                _gameStats = RenderStats.Last; // snapshot for stats overlay (persists when paused/stepped)
            }

            // Render game UI into the RT
            if (Application.IsPlaying && _rt != null)
            {
                EnsureGamePaper(rtW, rtH);
                _gamePaperRenderer!.UpdateProjection(rtW, rtH);
                _gamePaperRenderer.SetRenderTarget(_rt.frameBuffer);

                FrameDebugger.BeginTargetScope(FrameDebuggerScopeKind.GameView);
                FrameDebugger.BeginGroup("UI Overlays (Game)");
                bool paperFrameStarted = false;
                try
                {
                    try
                    {
                        _gamePaper!.BeginFrame(Time.DeltaTime, -1f);
                        paperFrameStarted = true;
                        try
                        {
                            scene.OnGui(_gamePaper);
                        }
                        catch (Exception exception)
                        {
                            // A user OnGui failure must not leave the editor bound to the game RT.
                            Runtime.Debug.LogError("Game View UI callback failed: " + exception);
                        }
                    }
                    finally
                    {
                        if (paperFrameStarted)
                            _gamePaper!.EndFrame();

                        using var unbind = Graphics.GetCommandBuffer("GameViewPanel.GUI Unbind");
                        unbind.SetRenderTarget(null);
                        unbind.SetViewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
                        Graphics.Submit(unbind);
                    }
                }
                finally
                {
                    FrameDebugger.EndGroup();
                    FrameDebugger.EndTargetScope();
                }
            }

            // Frame Debugger display model (Unity parity): the panel preview shows the state at
            // the selected event (live truncated RTs); THIS view always shows the frame's FINAL
            // image. The capture frame executes every op, so its end-of-frame content IS the
            // final image — snapshot it; replay frames (whose FinalBlit may be truncated away)
            // restore it. Submissions are scope-None, so never captured or truncated.
            if (_rt != null && FrameDebugger.IsEnabled
                && FrameDebugger.CaptureScope == FrameDebuggerCaptureScope.GameRendering)
            {
                if (FrameDebugger.IsCapturing)
                {
                    EnsureFdFinalFrame(rtW, rtH);
                    using var snapshot = Graphics.GetCommandBuffer("FrameDebugger.FinalFrameSnapshot");
                    snapshot.Blit(_rt, _fdFinalFrame);
                    Graphics.Submit(snapshot);
                }
                else if (FrameDebugger.LastCapture != null && _fdFinalFrame is { IsDisposed: false })
                {
                    using var restore = Graphics.GetCommandBuffer("FrameDebugger.FinalFrameRestore");
                    restore.Blit(_fdFinalFrame, _rt);
                    Graphics.Submit(restore);
                }
            }
            else if (_fdFinalFrame != null)
            {
                ReleaseFdFinalFrame();
            }

            if (dueForRender)
            {
                RenderedFrameCount++;
                _captureRequests.Complete(TryCapture());
            }

            // Display with letterboxing
            Texture2D? displayTexture = GetDisplayTexture(_rt);
            if (displayTexture != null)
            {
                // XENGINE_PLAY_CAPTURE_DIR: periodic GameView PNG dump while playing. Lets an
                // agent analyze a LIVE play session (animation motion, etc.) without focus or
                // MCP: every 600ms the GPU readback is written as PNG to the given directory.
                if (Application.IsPlaying && _captureDir != null && _playCapture is not null &&
                    _playCapture.ElapsedMilliseconds - _lastCaptureMs >= 600)
                {
                    _lastCaptureMs = _playCapture.ElapsedMilliseconds;
                    (byte[] rgba, int cw, int ch)? frame = TryCapture();
                    if (frame is not null)
                    {
                        try
                        {
                            GameViewScreenshotColorSpace.PreserveDisplayReferredRgbAndMakeOpaqueInPlace(frame.Value.rgba);
                            string path = System.IO.Path.Combine(_captureDir,
                                $"cap-{System.DateTimeOffset.UtcNow:HHmmssfff}.png");
                            var px = new ImageMagick.PixelReadSettings((uint)frame.Value.cw, (uint)frame.Value.ch,
                                ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA);
                            using var img = new ImageMagick.MagickImage();
                            img.ReadPixels(frame.Value.rgba, px);
                            if (RenderTargetPreview.RequiresYFlip) img.Flip();
                            img.Write(path, ImageMagick.MagickFormat.Png32);
                        }
                        catch { /* capture is best-effort diagnostics */ }
                    }
                }

                GameViewDevice? device = _viewMode == GameViewMode.Simulator
                    ? GameViewDevices.All[_deviceIndex]
                    : null;

                // Everything is laid out in "content" units: RT pixels in Game mode, device pixels
                // (screen plus bezel) in simulator mode. `fit` is the scale that just fills the panel
                // and the Scale slider multiplies it, so the readout matches Unity's "0.45x".
                Float2 content = device?.BodySize(_landscape) ?? new Float2(rtW, rtH);
                float fit = MathF.Min(width / content.X, height / content.Y);
                float scale = fit * _zoom;
                _effectiveScale = scale;

                float bodyW = content.X * scale;
                float bodyH = content.Y * scale;
                float bodyX = (width - bodyW) * 0.5f;
                float bodyY = (height - bodyH) * 0.5f;

                float bezel = (device?.Bezel ?? 0f) * scale;
                float displayW = bodyW - bezel * 2f;
                float displayH = bodyH - bezel * 2f;
                float offsetX = bodyX + bezel;
                float offsetY = bodyY + bezel;

                // Backdrop: black around the game image, neutral behind a simulated device.
                paper.Box("gv_backdrop").Size(width, height).IsNotInteractable()
                    .BackgroundColor(device != null ? EditorTheme.Neutral100 : Color.Black);

                if (device != null)
                    DrawDeviceBody(paper, device, bodyX, bodyY, bodyW, bodyH, scale);

                // Game viewport - rounded rect with purple border (matches SceneView)
                // `playing` (play-mode throttle) is computed above; reuse it here for border styling.
                Texture2D capturedTexture = displayTexture;
                bool fillsTop = offsetY <= 0.5f;
                float screenRound = device != null ? device.CornerRadius * scale : 0f;
                paper.Box("gv_display")
                    .PositionType(PositionType.SelfDirected)
                    .Position(offsetX, offsetY).Size(displayW, displayH)
                    .OnPostLayout((handle, rect) =>
                    {
                        // Cache the absolute paper-coord rect so next frame's UI input maps into RT space.
                        _displayAbsRect = rect;
                        paper.Draw(ref handle, (canvas, r) =>
                    {
                        if (capturedTexture.IsDisposed) return;
                        float rx = (float)r.Min.X;
                        float ry = (float)r.Min.Y;
                        float rw = (float)r.Size.X;
                        float rh = (float)r.Size.Y;
                        float round = device != null ? screenRound : EditorTheme.Roundness;

                        // When the image fills to the top (no letterbox bar above), keep the top corners
                        // square so it butts flush against the panel header; when letterboxed, round them.
                        float topRound = device != null || !fillsTop ? round : 0f;

                        canvas.ClearBrush();
                        canvas.SetBrushTexture(capturedTexture);
                        canvas.SetBrushTextureTransform(
                            RenderTargetPreview.Transform(rx, ry, rw, rh));
                        canvas.RoundedRectFilled(rx, ry, rw, rh, topRound, topRound, round, round, Color.White);
                        canvas.ClearBrushTexture();

                        // Border inset by half its width so the full stroke sits on top of the texture edge.
                        float bw = playing ? 2f : 1f;
                        float half = bw * 0.5f;
                        canvas.RoundedRect(rx + half, ry + half, rw - bw, rh - bw, topRound, topRound, round, round);
                        canvas.SetStrokeColor(playing ? EditorTheme.Purple500 : EditorTheme.Ink200);
                        canvas.SetStrokeWidth(bw);
                        canvas.Stroke();
                        });
                    });

                if (device != null && _showSafeArea)
                    DrawDeviceSafeArea(paper, device, offsetX, offsetY, displayW, displayH, scale);

                // Route game input into the new UI system. The cursor is rescaled from the letterboxed
                // display rect into the RT's pixel space so clicks map to the same coords the canvas was
                // laid out at (the pipeline pushed the RT size as GameCanvas.ScreenSizeOverride).
                bool hovered = paper.IsParentHovered;
                GameViewInputHandler.IsGameViewFocused = hovered;

                if (_displayAbsRect.Size.X > 0 && _displayAbsRect.Size.Y > 0)
                {
                    Float2 origin = new((float)_displayAbsRect.Min.X, (float)_displayAbsRect.Min.Y);
                    Float2 size = new((float)_displayAbsRect.Size.X, (float)_displayAbsRect.Size.Y);
                    Float2 local = paper.PointerPos - origin;
                    Float2 inRT = new(local.X * (rtW / size.X), local.Y * (rtH / size.Y));
                    bool inside = local.X >= 0 && local.Y >= 0 && local.X <= size.X && local.Y <= size.Y;

                    // Gameplay scripts read Input.MousePosition in render-target pixel space (matching
                    // Camera.PixelWidth/Height), so picking works from the editor like a standalone build.
                    // Published unconditionally (unlike the UI viewport below, which needs an EventSystem).
                    GameViewInputHandler.Viewport = new GameViewInputHandler.GameViewport(origin, size, new Int2(rtW, rtH));

                    if (RuntimeEventSystem.Current is { } es)
                        es.Viewport = new RuntimeEventSystem.HostViewport
                        {
                            ReferenceSize = new Float2(rtW, rtH),
                            PointerPosition = inRT,
                            ReceivesInput = hovered && inside,
                        };
                }
                else
                {
                    GameViewInputHandler.Viewport = null;
                    if (RuntimeEventSystem.Current is { } es)
                        es.Viewport = null;
                }

                if (!hasCameras)
                    DrawNoCamerasNotice(paper, font, width, height);

                // Stats overlay (clamped to the panel so it stays reachable when zoomed past the edge)
                if (_showStats)
                    DrawStats(paper, font, MathF.Min(offsetX + displayW, width), MathF.Max(offsetY, 0f), displayW);
            }
            else
            {
                paper.Box("gv_black")
                    .Size(width, height)
                    .BackgroundColor(Color.Black);

                if (!hasCameras)
                    DrawNoCamerasNotice(paper, font, width, height);
            }
        }
    }

    /// <summary>
    /// Unity's "Display N — No cameras rendering" notice, shown when no enabled camera targets the
    /// selected display. Drawn over the (stale or black) image rather than replacing the view so the
    /// simulator chrome and toolbar stay put.
    /// </summary>
    private void DrawNoCamerasNotice(Paper paper, Scribe.FontFile font, float width, float height)
        => paper.Box("gv_no_cams")
            .PositionType(PositionType.SelfDirected).Position(0, 0).Size(width, height)
            .BackgroundColor(Color.Black).IsNotInteractable()
            .Text($"{DisplayName(_displayIndex)}\n{Loc.Get("game.no_cameras")}", font)
            .TextColor(EditorTheme.Ink300)
            .FontSize(EditorTheme.FontSize + 4f).Alignment(TextAlignment.MiddleCenter);

    private void DrawStats(Paper paper, Scribe.FontFile font, float rightEdge, float top, float displayW)
    {
        const float panelW = 220f;
        const float graphH = 50f;
        const float rowH = 13f;
        float pad = 7f;
        float x = rightEdge - panelW - 6;
        float y = top + 6;
        float fs = EditorTheme.FontSizeSmall;

        var s = _gameStats;
        float fps = s.FrameTimeMs > 0 ? 1000f / s.FrameTimeMs : 0;

        using (paper.Column("gv_stats")
            .PositionType(PositionType.SelfDirected)
            .Position(x, y)
            .Width(panelW).Height(UnitValue.Auto)
            .BackgroundColor(EditorTheme.Neutral200)
            .Rounded(5).Padding(pad, pad, pad, pad).ColBetween(1)
            .Enter())
        {
            // FPS + frame time
            using (paper.Row("gv_st_fps").Height(16).Enter())
            {
                paper.Box("gv_st_fps_v")
                    .Text($"{fps:F0} FPS", font).TextColor(FpsColor(fps))
                    .FontSize(fs + 3).Alignment(TextAlignment.MiddleLeft);
                paper.Box("gv_st_fps_ms")
                    .Text(FormatMs(s.FrameTimeMs), font).TextColor(Dim)
                    .FontSize(fs).Alignment(TextAlignment.MiddleRight);
            }

            // Graph
            paper.Box("gv_st_graph")
                .Width(UnitValue.Stretch()).Height(graphH)
                .IsNotInteractable()
                .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
                    DrawFrameTimeGraph(canvas, r, font)));

            // General info
            if (_rt != null)
                Row(paper, font, "gv_r", $"{_rt.Width}x{_rt.Height}", $"{s.Cameras} cam", fs, rowH);

            int atlasSize = s.ShadowAtlasSize > 0 ? s.ShadowAtlasSize : QualitySettings.ShadowAtlasSize;
            Row(paper, font, "gv_quality", "Quality", s.QualityTier.ToString(), fs, rowH);
            Row(paper, font, "gv_atlas", $"Atlas {atlasSize}px",
                $"{s.ShadowAtlasClears} clear / {s.ShadowPassesSkipped} skip", fs, rowH);
            Row(paper, font, "gv_fx_mode", "Image FX",
                s.ImageEffectsEnabled ? "enabled" : $"preview skip ({s.ImageEffectsSkipped})", fs, rowH);

            // Target (color pass)
            int cullPct = s.RenderablesCollected > 0 ? (int)(s.RenderablesCulled * 100f / s.RenderablesCollected) : 0;
            long colorTris = s.Triangles - s.ShadowTriangles;
            long colorVerts = s.Vertices - s.ShadowVertices;

            Section(paper, font, "gv_ht", Loc.Get("gameview.sec_target"), FormatMainThread(s.ColorPassMs), fs);
            Row(paper, font, "gv_tdc", Loc.Get("gameview.draws"), $"{s.DrawCalls} ({s.InstancedDrawCalls} inst)", fs, rowH,
                "Individual GPU draw commands issued this frame. Each draw sends geometry to the GPU. Instanced draws render multiple copies in a single call.");
            Row(paper, font, "gv_tba", Loc.Get("gameview.batches"), s.Batches.ToString(), fs, rowH,
                "Groups of draw calls sharing the same material and render state. Fewer batches means less CPU overhead from state changes between draws.");
            Row(paper, font, "gv_ttr", Loc.Get("gameview.tris_verts"), $"{FormatCount(colorTris)} / {FormatCount(colorVerts)}", fs, rowH,
                "Total triangles and vertices submitted to the GPU for the color pass. High counts impact GPU fill rate and vertex processing.");
            Row(paper, font, "gv_tcu", Loc.Get("gameview.culled"), $"{s.RenderablesCulled}/{s.RenderablesCollected} ({cullPct}%)", fs, rowH,
                "Objects removed by frustum culling before rendering. Higher percentage means more objects are outside the camera view and skipped.");
            Row(paper, font, "gv_tlt", Loc.Get("gameview.lights"), $"D:{s.DirectionalLights} P:{s.PointLights} S:{s.SpotLights}", fs, rowH,
                "Active lights this frame. D = Directional (sun), P = Point (omni), S = Spot. Each light adds a lighting pass over affected geometry.");
            Row(paper, font, "gv_twa", Loc.Get("gameview.waited"), FormatMs(Graphics.LastFrameWaitMs), fs, rowH,
                "Time the main thread spent blocked at end of frame waiting for the render thread to finish executing this frame's command buffers. " +
                "High values mean the render thread is the bottleneck (GPU work or CB execution dominates the frame). " +
                "Near-zero values mean the main thread (update + encoding) takes longer than rendering, so the render thread was already idle when we asked.");

            // Shadows
            if (s.ShadowPasses > 0 || s.ShadowCasters > 0)
            {
                int shCullPct = s.ShadowRenderablesCollected > 0
                    ? (int)(s.ShadowRenderablesCulled * 100f / s.ShadowRenderablesCollected) : 0;

                Section(paper, font, "gv_hs", Loc.Get("gameview.sec_shadows"), FormatMainThread(s.ShadowPassMs), fs);
                Row(paper, font, "gv_sdc", Loc.Get("gameview.draws"), $"{s.ShadowDrawCalls} ({s.ShadowInstancedDrawCalls} inst)", fs, rowH,
                    "Draw calls for shadow map rendering. Each shadow-casting light renders the scene from its perspective to build depth maps.");
                Row(paper, font, "gv_spa", Loc.Get("gameview.passes"), s.ShadowPasses.ToString(), fs, rowH,
                    "Number of shadow map renders. Directional lights use cascaded shadow maps (multiple passes per light). Point lights use 6 passes (cube map).");
                Row(paper, font, "gv_str", Loc.Get("gameview.tris_verts"), $"{FormatCount(s.ShadowTriangles)} / {FormatCount(s.ShadowVertices)}", fs, rowH,
                    "Geometry rendered into shadow maps. This is additional to the color pass geometry and can be a major cost with many shadow casters.");
                Row(paper, font, "gv_scu", Loc.Get("gameview.culled"), $"{s.ShadowRenderablesCulled}/{s.ShadowRenderablesCollected} ({shCullPct}%)", fs, rowH,
                    "Objects culled from shadow map rendering. Each shadow pass has its own frustum so culling rates differ from the camera.");
                Row(paper, font, "gv_sca", Loc.Get("gameview.casters"), s.ShadowCasters.ToString(), fs, rowH,
                    "Lights with shadow mapping enabled. Each shadow caster adds one or more shadow passes to the frame.");
            }

            // Post FX (only when active)
            if (s.ImageEffects > 0)
            {
                Section(paper, font, "gv_hf", Loc.Get("gameview.sec_postfx"), FormatMainThread(s.PostFxMs), fs);
                Row(paper, font, "gv_fx", $"{s.ImageEffects} effects", $"{s.ImageEffectPasses} passes", fs, rowH,
                    "Post-processing effects applied after rendering (bloom, tone mapping, SSAO, etc). Each effect may use multiple full-screen passes.");
            }
        }
    }

    private static Color Dim => EditorTheme.Ink300;
    private static Color Val => EditorTheme.Ink500;
    private static Color Hdr => EditorTheme.Purple400;

    private static void DrawFrameTimeGraph(Quill.Canvas canvas, Rect r, Scribe.FontFile? font)
    {
        float x = (float)r.Min.X, y = (float)r.Min.Y;
        float w = (float)r.Size.X, h = (float)r.Size.Y;
        var history = RenderStats.FrameTimeHistory;
        int head = RenderStats.FrameTimeIndex;
        int len = history.Length;

        canvas.RoundedRectFilled(x, y, w, h, 3, 3, 3, 3,
            XEngine.Vector.Color32.FromArgb(255, 10, 10, 14));

        float maxMs = 8f;
        for (int i = 0; i < len; i++)
            if (history[i] > maxMs) maxMs = history[i];
        maxMs = MathF.Ceiling(maxMs / 8f) * 8f;

        float gx = x + 1, gy = y + 1, gw = w - 2, gh = h - 2;

        // Target lines
        DrawTargetLine(canvas, font, gx, gy, gw, gh, maxMs, 16.67f, "60",
            XEngine.Vector.Color32.FromArgb(35, 80, 200, 100));
        DrawTargetLine(canvas, font, gx, gy, gw, gh, maxMs, 33.33f, "30",
            XEngine.Vector.Color32.FromArgb(35, 220, 180, 50));

        // Bars
        float barW = gw / len;
        for (int i = 0; i < len; i++)
        {
            float ms = history[(head + i) % len];
            if (ms <= 0) continue;
            float barH = MathF.Min((ms / maxMs) * gh, gh);
            var col = ms < 16.67f ? XEngine.Vector.Color32.FromArgb(200, 70, 190, 110)
                : ms < 33.33f ? XEngine.Vector.Color32.FromArgb(200, 210, 170, 50)
                : XEngine.Vector.Color32.FromArgb(200, 210, 55, 55);
            canvas.RectFilled(gx + i * barW, gy + gh - barH, MathF.Max(1, barW - 0.5f), barH, col);
        }
    }

    private static void DrawTargetLine(Quill.Canvas canvas, Scribe.FontFile? font,
        float gx, float gy, float gw, float gh, float maxMs, float targetMs, string label,
        Color32 color)
    {
        float ly = gy + gh - (targetMs / maxMs) * gh;
        if (ly <= gy || ly >= gy + gh) return;
        canvas.SetStrokeColor(color); canvas.SetStrokeWidth(0.5f);
        canvas.BeginPath(); canvas.MoveTo(gx, ly); canvas.LineTo(gx + gw, ly); canvas.Stroke();
        if (font != null)
            canvas.DrawText(label, gx + 1, ly - 8, color, 7, font, 0);
    }

    private static void Section(Paper paper, Scribe.FontFile font, string id, string label, float fs)
        => Section(paper, font, id, label, null, fs);

    private static void Section(Paper paper, Scribe.FontFile font, string id, string label, string? timing, float fs)
    {
        paper.Box($"{id}_s").Height(2);
        if (timing != null)
        {
            using (paper.Row(id).Height(11).Enter())
            {
                paper.Box($"{id}_l").Text(label, font).TextColor(Hdr)
                    .FontSize(fs - 1).Alignment(TextAlignment.MiddleLeft);
                paper.Box($"{id}_t").Text(timing, font).TextColor(Dim)
                    .FontSize(fs - 1).Alignment(TextAlignment.MiddleRight);
            }
        }
        else
        {
            paper.Box(id).Height(11).Text(label, font).TextColor(Hdr)
                .FontSize(fs - 1).Alignment(TextAlignment.MiddleLeft);
        }
    }

    private static void Row(Paper paper, Scribe.FontFile font, string id,
        string left, string right, float fs, float h, string? tooltip = null)
    {
        using (paper.Row(id).Height(h).Enter())
        {
            var lbl = paper.Box($"{id}_l").Text(left, font).TextColor(Dim).FontSize(fs).Alignment(TextAlignment.MiddleLeft);
            if (tooltip != null) lbl.Tooltip(tooltip);
            paper.Box($"{id}_r").Text(right, font).TextColor(Val).FontSize(fs).Alignment(TextAlignment.MiddleRight);
        }
    }

    // Fixed-width "XX.XXms" so changing values don't reflow the row.
    private static string FormatMs(float ms) => $"{ms,5:0.00}ms";
    private static string FormatMainThread(float ms) => $"{Loc.Get("gameview.main_thread")} {ms,5:0.00}ms";

    private static Color FpsColor(float fps) =>
        fps >= 55 ? EditorTheme.Green400 :
        fps >= 28 ? EditorTheme.Amber400 :
        EditorTheme.Red400;

    private static string FormatCount(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" :
        n >= 1_000 ? $"{n / 1_000.0:F1}K" :
        n.ToString();

    private void EnsureGamePaper(int w, int h)
    {
        if (_gamePaperRenderer == null)
        {
            _gamePaperRenderer = new PaperRenderer();
            _gamePaperRenderer.Initialize(w, h);
        }

        if (_gamePaper == null)
            _gamePaper = new Paper(_gamePaperRenderer, w, h, new Quill.FontAtlasSettings());
        else
            _gamePaper.SetResolution(w, h);
    }

    /// <summary>
    /// Collect scene cameras for Game View without LINQ. Reads the scene's enabled-camera registry
    /// (<see cref="Scene.CollectEnabledCameras"/>) instead of walking every active GameObject with
    /// GetComponent: this runs every frame and the walk cost ~4 ms at 100k objects.
    /// </summary>
    private readonly List<Camera> _enabledCameras = new();
    private readonly List<Camera> _gameViewCameras = new();

    private readonly List<Camera> _cameraBases = new();
    private readonly List<Camera> _orderedCameras = new();
    private readonly HashSet<Camera> _usedOverlays = new();

    private List<Camera> CollectGameViewCameras(Scene scene)
    {
        _gameViewCameras.Clear();
        _enabledCameras.Clear();
        scene.CollectEnabledCameras(_enabledCameras);
        for (int i = 0; i < _enabledCameras.Count; i++)
        {
            Camera cam = _enabledCameras[i];
            GameObject? go = cam.GameObject;
            if (go == null || go.HideFlags.HasFlag(HideFlags.HideAndDontSave))
                continue;

            // Only cameras aimed at the display this view is showing. Overlays stay in the list —
            // BuildRenderOrder places each one right after the Base camera that stacks it.
            if (cam.TargetDisplay == _displayIndex)
                _gameViewCameras.Add(cam);
        }

        Camera.BuildRenderOrder(_gameViewCameras, _cameraBases, _orderedCameras, _usedOverlays);
        return _orderedCameras;
    }

    /// <summary>Render-target size for Game mode: panel-sized when free, else the picked aspect/resolution.</summary>
    private (int width, int height) ResolveGameRenderSize(float panelWidth, float panelHeight)
    {
        var (_, targetW, targetH) = CurrentResolution;

        // Free aspect — match the panel 1:1.
        if (targetW == 0 && targetH == 0)
            return ((int)MathF.Max(1, panelWidth), (int)MathF.Max(1, panelHeight));

        // Fixed aspect — largest box of that ratio that fits the panel.
        if (targetW < 0 && targetH < 0)
        {
            float aspect = (float)(-targetW) / (-targetH);
            float panelAspect = panelWidth / MathF.Max(1f, panelHeight);
            return aspect > panelAspect
                ? ((int)MathF.Max(1, panelWidth), (int)MathF.Max(1, panelWidth / aspect))
                : ((int)MathF.Max(1, panelHeight * aspect), (int)MathF.Max(1, panelHeight));
        }

        return (targetW, targetH);
    }

    private void EnsureFdFinalFrame(int w, int h)
    {
        if (_fdFinalFrame is { IsDisposed: false } && _fdFinalFrame.Width == w && _fdFinalFrame.Height == h)
            return;
        ReleaseFdFinalFrame();
        _fdFinalFrame = new RenderTexture(w, h, false, new[] { TextureImageFormat.Color4b });
        _fdFinalFrame.SetDebugName("FrameDebugger final frame");
    }

    private void ReleaseFdFinalFrame()
    {
        if (_fdFinalFrame is { IsDisposed: false })
            Graphics.DeferDispose(_fdFinalFrame);
        _fdFinalFrame = null;
    }

    private void EnsureRT(int w, int h)
    {
        if (_rt != null && _rt.Width == w && _rt.Height == h)
        {
            _pendingRtWidth = 0;
            _pendingRtHeight = 0;
            return;
        }

        // The settle delay exists so dragging a dock splitter doesn't reallocate a render texture every
        // frame, which only ever asks for a slightly different size. A resolution or device switch jumps
        // by a lot and should apply on the spot.
        bool incremental = _rt != null
            && Math.Abs(_rt.Width - w) < _rt.Width / 4
            && Math.Abs(_rt.Height - h) < _rt.Height / 4;
        if (incremental)
        {
            if (_pendingRtWidth != w || _pendingRtHeight != h)
            {
                _pendingRtWidth = w;
                _pendingRtHeight = h;
                _pendingRtSince = Time.UnscaledTotalTime;
                return;
            }

            if (Time.UnscaledTotalTime - _pendingRtSince < 0.12)
                return;
        }

        if (_rt != null)
            Graphics.DeferDispose(_rt);
        _rt = new RenderTexture(w, h, true, new[] { TextureImageFormat.Color4b });
        _rt.SetDebugName("Game view");
        _pendingRtWidth = 0;
        _pendingRtHeight = 0;
        _renderSchedule.Invalidate();
    }

    internal static Texture2D? GetDisplayTexture(RenderTexture? renderTexture)
    {
        if (renderTexture == null || renderTexture.IsDisposed)
            return null;

        Texture2D texture = renderTexture.MainTexture;
        return texture.IsDisposed ? null : texture;
    }

    /// <summary>★ runtime_screenshot — capture the current GameView frame as RGBA bytes (synchronous GPU
    /// readback via Graphics.GetTexImage). Returns null if no render target is active/disposed.</summary>
    public (byte[] rgba, int width, int height)? TryCapture()
    {
        Texture2D? tex = GetDisplayTexture(_rt);
        if (tex is null) return null;
        int size = tex.GetSize();
        byte[] data = new byte[size];
        unsafe
        {
            fixed (byte* ptr = data)
            {
                tex.GetDataPtr(ptr);
            }
        }
        return (data, (int)tex.Width, (int)tex.Height);
    }

    private void InvalidateRT()
    {
        if (_rt != null)
            Graphics.DeferDispose(_rt);
        _rt = null;
        _pendingRtWidth = 0;
        _pendingRtHeight = 0;
        _renderSchedule.Invalidate();
    }

    public void OnScriptReloadCleanup()
    {
        _captureRequests.Complete(null);
        DisposePaintRegions();
    }

    public override void OnClosed()
    {
        _captureRequests.Complete(null);
        InvalidateRT();
        if (ReferenceEquals(ActiveInstance, this)) ActiveInstance = null; // ★ runtime_screenshot
        _gamePaperRenderer?.Dispose();
        _gamePaperRenderer = null;
        _gamePaper = null;
        DisposePaintRegions();
    }

    private void AttachPaintRegion(Paper paper)
    {
        if (!Program.GamePaintIncremental || !paper.Renderer.SupportsRenderLayers) return;
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
        ulong key = (ulong)(int)_viewMode;
        key = key * 397 ^ (uint)_displayIndex;
        key = key * 397 ^ (uint)_resolutionIndex;
        key = key * 397 ^ (uint)_deviceIndex;
        key = key * 397 ^ (_landscape ? 1u : 0u);
        key = key * 397 ^ (_showSafeArea ? 1u : 0u);
        key = key * 397 ^ (uint)BitConverter.SingleToInt32Bits(_zoom);
        key = key * 397 ^ (uint)Math.Clamp((int)MathF.Round(_effectiveScale * 100f), 0, 100000);
        key = key * 397 ^ (uint)(int)PlayFocus;
        key = key * 397 ^ (EditorAudio.GameViewMuted ? 1u : 0u);
        key = key * 397 ^ (_showStats ? 1u : 0u);
        key = key * 397 ^ (_showGizmos ? 1u : 0u);
        return key;
    }
}
