// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using ModelContextProtocol.Server;
using XEngine.Async;
using XEngine.Ai.Server;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Projects;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.Core;

/// <summary>
/// ★ Layer 2 (B3-cont) — Editor-side runtime MCP tools that reach LIVE engine state. Each hops onto
/// the main loop via <see cref="MainThreadDispatcher"/> (so Scene/GameObject/Application are touched
/// safely) and returns structured results. These are discovered by <c>AiServerHost</c> via the extra-
/// assembly scan registered at editor startup.
///
/// <para>Tools: <c>runtime_state</c> (current scene + playmode), <c>runtime_logs</c> (recent
/// Debug.OnLog entries), <c>runtime_playmode</c> (enter/exit), <c>runtime_live_scene</c> (the live
/// hierarchy of the currently-loaded scene).</para>
/// </summary>
[McpServerToolType]
public static partial class EditorRuntimeTools
{
    private const int MaxLogBuffer = 512;
    private static readonly ConcurrentQueue<LogEntry> _logBuffer = new();
    private static bool _subscribed;

    /// <summary>Subscribe to Debug.OnLog the first time any logs tool is used (idempotent).
    /// The <c>runtime_logs</c> tool reads from <see cref="Core.EditorLog"/>'s ring buffer (which
    /// is subscribed from editor start), not this queue — this is only a fallback for the case
    /// where EditorLog.Initialize was never called (e.g., a unit test host).</summary>
    private static void EnsureSubscribed()
    {
        if (_subscribed) return;
        _subscribed = true;
        Debug.OnLog += (msg, stack, severity) =>
        {
            string? trace = stack is { StackFrames.Length: > 0 }
                ? string.Join("\n", stack.StackFrames.Select(f => $"  at {f.Method ?? "?"} ({f.FileName}:{f.Line})"))
                : null;
            _logBuffer.Enqueue(new LogEntry(severity.ToString(), msg, trace, System.DateTimeOffset.UtcNow));
            while (_logBuffer.Count > MaxLogBuffer && _logBuffer.TryDequeue(out _)) { }
        };
    }

    [McpServerTool(Name = "runtime_state", UseStructuredContent = true)]
    [Description("Live editor state: current scene name/path, playmode, object count, plus editor busy state (project open in progress, script compiling, asset batch importing, and the running background tasks with progress). Reaches the running engine via the in-process server.")]
    public static Task<RuntimeState> GetState(MainThreadDispatcher dispatcher)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            var scene = Scene.Current;

            // Busy snapshot for agents: wait/poll instead of firing tool calls into a moving editor.
            var snapshot = Core.Progress.EditorProgress.Snapshot();
            var tasks = new RuntimeTaskView[snapshot.Tasks.Length];
            for (int i = 0; i < snapshot.Tasks.Length; i++)
            {
                var t = snapshot.Tasks[i];
                tasks[i] = new RuntimeTaskView(t.Title, t.Detail, t.Current, t.Total,
                    t.Kind.ToString(), t.ElapsedMs);
            }

            return new RuntimeState(
                scene?.Name ?? "(none)",
                EditorSceneManager.CurrentScenePath,
                Application.IsPlaying,
                scene?.AllObjects.Count() ?? 0,
                EditorAssetBackend.Instance is { IsInitializing: true },
                Projects.Scripting.ScriptAssemblyManager.IsCompiling,
                Projects.Scripting.ScriptAssemblyManager.IsStartupLoadInProgress,
                tasks);
        });

    [McpServerTool(Name = "runtime_logs", UseStructuredContent = true)]
    [Description("Recent runtime logs from the live editor's persistent log (Editor.log). Newest first. Also readable offline at {project}/Logs/Editor.log after a crash.")]
    public static Task<LogsPage> GetLogs(
        MainThreadDispatcher dispatcher,
        [Description("Max entries to return, 1-200.")] int limit = 50,
        [Description("Optional minimum severity (Success/Normal/Warning/Error/Exception).")] string? minimumSeverity = null)
    {
        // Read from EditorLog's ring buffer (subscribed from editor start, so startup logs are
        // captured). Falls back to the lazy local queue if EditorLog wasn't initialized.
        var (returned, total, entries) = Core.EditorLog.GetRecentEntries(
            System.Math.Clamp(limit, 1, 200), minimumSeverity);
        if (returned > 0 || total > 0)
        {
            var page = new LogEntry[returned];
            for (int i = 0; i < returned; i++)
                page[i] = new LogEntry(entries[i].Severity.ToString(), entries[i].Message,
                    entries[i].StackTrace, entries[i].TimestampUtc);
            return Task.FromResult(new LogsPage(returned, total, page));
        }

        // Fallback: the lazy local buffer (pre-EditorLog path, e.g. unit test host).
        EnsureSubscribed();
        return dispatcher.InvokeOnMainThreadAsync(() =>
        {
            var all = _logBuffer.ToArray();
            System.Array.Reverse(all); // newest first
            if (!string.IsNullOrEmpty(minimumSeverity))
                all = System.Array.FindAll(all, e => SeverityRank(e.Severity) >= SeverityRank(minimumSeverity!));
            var page = all.Take(System.Math.Clamp(limit, 1, 200)).ToArray();
            return new LogsPage(page.Length, _logBuffer.Count, page);
        });
    }

    [McpServerTool(Name = "runtime_playmode", UseStructuredContent = true)]
    [Description("Enter or exit playmode on the live editor. action = 'enter' | 'exit'.")]
    public static Task<PlaymodeResult> SetPlaymode(
        MainThreadDispatcher dispatcher,
        [Description("'enter' or 'exit'.")] string action)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            var app = EditorApplication.Instance;
            if (app is null) return new PlaymodeResult(false, Application.IsPlaying, "EditorApplication instance not available.");
            if (action == "enter") { if (!Application.IsPlaying) app.EnterPlayMode(); }
            else if (action == "exit") { if (Application.IsPlaying) app.ExitPlayMode(); }
            else return new PlaymodeResult(false, Application.IsPlaying, $"Unknown action '{action}'. Use 'enter' or 'exit'.");
            return new PlaymodeResult(true, Application.IsPlaying, null);
        });

    [McpServerTool(Name = "runtime_live_scene", UseStructuredContent = true)]
    [Description("The live hierarchy of the currently-loaded scene (root objects + children), read from the running engine.")]
    public static Task<LiveScene> GetLiveScene(
        MainThreadDispatcher dispatcher,
        [Description("Max objects to return, 1-500.")] int limit = 100)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            var scene = Scene.Current;
            if (scene is null) return new LiveScene("(none)", 0, System.Array.Empty<LiveObject>());
            var roots = scene.RootObjects
                .Take(System.Math.Clamp(limit, 1, 500))
                .Select(go => new LiveObject(go.Identifier.ToString(), go.Name, go.ChildCount, go.Tag ?? "", go.LayerIndex))
                .ToArray();
            return new LiveScene(scene.Name ?? "(unnamed)", scene.AllObjects.Count(), roots);
        });

    [McpServerTool(Name = "runtime_stats", UseStructuredContent = true)]
    [Description("Live rendering/performance counters from the running engine: draw calls, triangles, CPU pass timings (ms), render-thread wait proxy, lights, shadows, image effects, plus the last 120 frame times. GPU execution time is reported only when backend timestamp queries are available.")]
    public static Task<RenderStatsSnapshot> GetStats(MainThreadDispatcher dispatcher)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            var f = RenderStats.Last;
            // Surface metrics are sampled through the same bounded profiler path used by the
            // Profiler panel, keeping MCP captures aligned with editor-visible diagnostics.
            EditorSurfaceSample surface = EditorProfiler.SampleNow().Surface;
            var history = RenderStats.FrameTimeHistory;
            // Copy the ring buffer in chronological order (oldest-first) for the agent.
            int idx = RenderStats.FrameTimeIndex;
            var ordered = new float[history.Length];
            for (int i = 0; i < history.Length; i++)
            {
                ordered[i] = history[(idx + i) % history.Length];
            }

            return new RenderStatsSnapshot(
                FrameTimeMs: f.FrameTimeMs, ColorPassMs: f.ColorPassMs, ShadowPassMs: f.ShadowPassMs, PostFxMs: f.PostFxMs,
                GpuWaitMs: Graphics.LastFrameWaitMs,
                DrawCalls: f.DrawCalls, InstancedDrawCalls: f.InstancedDrawCalls, Batches: f.Batches,
                Triangles: f.Triangles, Vertices: f.Vertices,
                RenderablesCollected: f.RenderablesCollected, RenderablesCulled: f.RenderablesCulled, RenderablesDrawn: f.RenderablesDrawn,
                Cameras: f.Cameras,
                Lights: f.Lights, DirectionalLights: f.DirectionalLights, PointLights: f.PointLights, SpotLights: f.SpotLights,
                ShadowDrawCalls: f.ShadowDrawCalls, ShadowTriangles: f.ShadowTriangles, ShadowPasses: f.ShadowPasses, ShadowCasters: f.ShadowCasters,
                ShadowPassesCached: f.ShadowPassesCached,
                ImageEffects: f.ImageEffects, ImageEffectPasses: f.ImageEffectPasses,
                CommandBufferSubmissions: f.CommandBufferSubmissions,
                ColorPassSubmissions: f.ColorPassSubmissions,
                ShadowPassSubmissions: f.ShadowPassSubmissions,
                PostFxSubmissions: f.PostFxSubmissions,
                FinalPassSubmissions: f.FinalPassSubmissions,
                UiSubmissions: f.UiSubmissions,
                ClearOperations: f.ClearOperations,
                BlitOperations: f.BlitOperations,
                BlitPixels: f.BlitPixels,
                TransientRenderTargetBytes: f.TransientRenderTargetBytes,
                PrepassSubmissions: f.PrepassSubmissions,
                PrepassSkipped: f.PrepassSkipped,
                RecentFrameTimesMs: ordered,
                BackendName: Graphics.Device?.Capabilities.BackendName ?? "No device",
                GpuTimestampQueriesSupported: Graphics.Device?.Capabilities.SupportsTimestampQueries == true,
                HardwareTimestampQueriesSupported: Graphics.Device?.Capabilities.HardwareSupportsTimestampQueries == true,
                GpuFrameTimeMs: Graphics.LastGpuTimestampFrame?.DurationMs,
                GpuFrameId: Graphics.LastGpuTimestampFrame?.FrameId,
                SurfaceWidth: surface.Width,
                SurfaceHeight: surface.Height,
                DirtyReason: surface.DirtyReason.ToString(),
                SchedulerState: surface.SchedulerState.ToString(),
                UiDrawCalls: surface.UiDrawCalls,
                UiRenderTargetBytes: surface.UiRenderTargetBytes,
                PresentCount: surface.PresentCount,
                PresentWaitMs: surface.PresentWaitMs,
                PresentFps: surface.PresentFps,
                SustainedFps: surface.SustainedFps,
                CpuFrameMs: surface.CpuFrameMs);
        });

    [McpServerTool(Name = "runtime_render_debug", UseStructuredContent = true)]
    [Description("Universal Rendering Debugger snapshot from the live pipeline: pass list, RendererFeatures, RTHandle sizes, lighting/cascade counts, CPU overdraw proxy, and Volume stack. Requires a running editor (--serve). Screenshot the panel with runtime_panel_screenshot panelType='XEngine.Editor.GUI.Panels.RenderingDebuggerPanel'.")]
    public static Task<RenderDebugResult> GetRenderDebug(MainThreadDispatcher dispatcher)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            RenderingDebugSnapshot snap = RenderingDebugger.Capture(UniversalRenderPipeline.Default);
            var volumes = new RenderDebugVolume[snap.Volumes.Length];
            for (int i = 0; i < snap.Volumes.Length; i++)
            {
                VolumeDebugEntry volume = snap.Volumes[i];
                volumes[i] = new RenderDebugVolume(
                    volume.Name, volume.IsGlobal, volume.Weight, volume.Priority, volume.Enabled);
            }

            return new RenderDebugResult(
                snap.DrawCalls, snap.RenderablesDrawn, snap.OverdrawProxy,
                snap.Lights, snap.DirectionalLights, snap.PointLights, snap.SpotLights,
                snap.ShadowCasters, snap.ShadowCascades,
                snap.EnqueuedPasses, snap.ExecutedPasses, snap.CulledGraphPasses,
                snap.CopyColorPasses, snap.CopyDepthPasses,
                snap.RtWidth, snap.RtHeight, snap.OpaqueAllocated, snap.PrepassAllocated,
                snap.PassEvents, snap.Features, snap.VolumeStack, volumes);
        });

    [McpServerTool(Name = "runtime_frame_debug", UseStructuredContent = true)]
    [Description("Frame Debugger control + snapshot. action=status|enable|disable|refresh|select|set_scope. select requires eventIndex; set_scope requires scope=game|full (game = Unity-style game rendering only, full = whole frame incl. editor UI). Returns event tree summary and selected event GPU state. Requires --serve. Panel: XEngine.Editor.GUI.Panels.FrameDebuggerPanel.")]
    public static Task<FrameDebugResult> FrameDebug(
        MainThreadDispatcher dispatcher,
        [Description("status | enable | disable | refresh | select | set_scope")] string action = "status",
        [Description("Leaf event index for action=select")] int eventIndex = 0,
        [Description("Capture scope for action=set_scope: game | full")] string? scope = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            string act = (action ?? "status").Trim().ToLowerInvariant();
            switch (act)
            {
                case "enable":
                    FrameDebugger.Enable();
                    break;
                case "disable":
                    FrameDebugger.Disable();
                    break;
                case "refresh":
                    FrameDebugger.Refresh();
                    break;
                case "select":
                    FrameDebugger.SelectedEventIndex = eventIndex;
                    break;
                case "set_scope":
                    FrameDebugger.CaptureScope = string.Equals(scope?.Trim(), "full", StringComparison.OrdinalIgnoreCase)
                        ? FrameDebuggerCaptureScope.FullFrame
                        : FrameDebuggerCaptureScope.GameRendering;
                    break;
                case "status":
                    break;
                default:
                    return new FrameDebugResult(
                        false, FrameDebugger.IsEnabled, FrameDebugger.IsCapturing,
                        FrameDebugger.SelectedEventIndex, 0, 0, null, null, null,
                        $"Unknown action '{action}'. Use status|enable|disable|refresh|select|set_scope.");
            }

            FrameCapture? capture = FrameDebugger.LastCapture;
            FrameDebugEvent[] events;
            FrameDebugSelected? selected = null;
            if (capture == null)
            {
                events = [];
            }
            else
            {
                events = new FrameDebugEvent[capture.LeafEvents.Count];
                for (int i = 0; i < capture.LeafEvents.Count; i++)
                {
                    FrameEvent e = capture.LeafEvents[i];
                    events[i] = new FrameDebugEvent(
                        e.Index, e.Kind.ToString(), e.Label, e.CommandBufferName, e.Scope.ToString());
                }

                if (capture.LeafEvents.Count > 0)
                {
                    int sel = Math.Clamp(FrameDebugger.SelectedEventIndex, 0, capture.LeafEvents.Count - 1);
                    GpuStateSnapshot snap = GpuStateRebuilder.Rebuild(capture, sel);
                    string[] textures = SummarizeTextures(snap);
                    selected = new FrameDebugSelected(
                        sel, snap.EventKind.ToString(), snap.EventLabel, snap.CommandBufferName,
                        snap.RenderTarget.Name, (int)snap.RenderTarget.Width, (int)snap.RenderTarget.Height,
                        snap.RenderTarget.ColorFormat, snap.ShaderLabel, snap.ShaderAssetName,
                        snap.Annotation?.MeshName, snap.Annotation?.MaterialName,
                        snap.Topology.ToString(), (int)snap.IndexCount, textures);
                }
            }

            string[]? renderTargets = null;
            if (capture != null)
            {
                renderTargets = new string[capture.RenderTargets.Count];
                for (int i = 0; i < capture.RenderTargets.Count; i++)
                {
                    RenderTargetInfo info = capture.RenderTargets[i].Info;
                    renderTargets[i] = $"{info.Name} {info.Width}x{info.Height} {info.ColorFormat}"
                        + (info.IsTransient ? " (transient)" : "");
                }
            }

            return new FrameDebugResult(
                true, FrameDebugger.IsEnabled, FrameDebugger.IsCapturing,
                FrameDebugger.SelectedEventIndex,
                capture?.LeafEvents.Count ?? 0,
                capture?.DrawCount ?? 0,
                capture?.CapturedAt.ToString("O"),
                events,
                selected,
                null,
                FrameDebugger.CaptureScope.ToString(),
                FrameDebugger.IsCaptureStale,
                capture?.IndexMappingReliable ?? true,
                renderTargets);
        });

    private static string[] SummarizeTextures(GpuStateSnapshot snap)
    {
        var list = new List<string>(8);
        if (snap.MaterialProperties != null)
        {
            foreach (KeyValuePair<string, AssetRef<Texture2D>> kv in snap.MaterialProperties._textures)
            {
                Texture2D? tex = kv.Value.Res;
                list.Add(tex == null ? kv.Key + "=(null)" : $"{kv.Key}={tex.Name} {tex.Width}x{tex.Height}");
                if (list.Count >= 16) break;
            }
        }
        foreach (KeyValuePair<string, string> kv in snap.UniformTextures)
        {
            list.Add($"{kv.Key}={kv.Value}");
            if (list.Count >= 16) break;
        }
        return list.ToArray();
    }

    [McpServerTool(Name = "runtime_screenshot", UseStructuredContent = true)]
    [Description("Capture the next complete live GameView frame as a PNG after camera rendering, game UI composition, and final-target restoration. Returns the file path + dimensions. Requires a running editor (--serve) with a visible GameView panel.")]
    public static async Task<ScreenshotResult> Screenshot(MainThreadDispatcher dispatcher)
    {
        XEngine.Editor.GUI.Panels.GameViewPanel? panel = null;
        GameViewCaptureRequest<(byte[] rgba, int width, int height)?>? captureRequest = null;
        bool flip = false;
        string? projectRoot = null;

        await dispatcher.InvokeOnMainThreadAsync(() =>
        {
            panel = XEngine.Editor.GUI.Panels.GameViewPanel.ActiveInstance;
            if (panel is null || panel.RenderTarget is null)
                return;

            flip = XEngine.Editor.GUI.RenderTargetPreview.RequiresYFlip;
            projectRoot = Project.Current?.RootPath;
            captureRequest = panel.CaptureNextRenderedFrame();
        });

        if (panel is null || captureRequest is null)
            return new ScreenshotResult(null, 0, 0, "No active GameView panel or render target. Open a visible GameView panel first.");

        async XTask<(byte[] rgba, int width, int height)?> TimeoutAfterFiveSeconds()
        {
            await XTask.Delay(TimeSpan.FromSeconds(5), DelayType.Realtime);
            return null;
        }

        var completed = await XTask.WhenAny(captureRequest.Task, TimeoutAfterFiveSeconds());
        if (completed.Index != 0)
        {
            await dispatcher.InvokeOnMainThreadAsync(() => panel.CancelCaptureRequest(captureRequest));
            return new ScreenshotResult(null, 0, 0, "Timed out waiting for the next complete GameView frame. Make sure the GameView panel is visible.");
        }

        (byte[] rgba, int width, int height)? capture = completed.Result;
        if (capture is null)
            return new ScreenshotResult(null, 0, 0, "GameView has no active camera or render target for a completed frame.");

        await XTask.SwitchToThreadPool();
        (byte[] rgba, int width, int height) = capture.Value;
        GameViewScreenshotColorSpace.PreserveDisplayReferredRgbAndMakeOpaqueInPlace(rgba);
        string path = EditorScreenshotPaths.ResolveOutputPath(
            $"gameview-{System.DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.png", projectRoot);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        var settings = new ImageMagick.PixelReadSettings((uint)width, (uint)height,
            ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA);
        using var img = new ImageMagick.MagickImage();
        img.ReadPixels(rgba, settings);
        if (flip)
            img.Flip();
        img.Write(path, ImageMagick.MagickFormat.Png32);

        return new ScreenshotResult(path, width, height, null);
    }

    [McpServerTool(Name = "runtime_panel_screenshot", UseStructuredContent = true)]
    [Description("Render any DockPanel offscreen to a PNG (its own PaperRenderer + RenderTexture: SetRenderTarget → Paper frame → RenderCalls draw-call replay → GPU readback → Magick.NET PNG). panelType = full type name. Requires --serve with a GPU. Failure returns an error string, never crashes the editor (fully try/caught, standalone renderer).")]
    public static Task<ScreenshotResult> PanelScreenshot(
        MainThreadDispatcher dispatcher,
        [Description("Full type name of the DockPanel to capture, e.g. 'XEngine.Editor.GUI.Panels.AnimatorWindow'.")] string panelType,
        [Description("Capture width in pixels.")] int width = 900,
        [Description("Capture height in pixels.")] int height = 600)
        => dispatcher.InvokeOnMainThreadAsync(() => CapturePanelNow(panelType, width, height));

    /// <summary>Synchronous offscreen panel capture. Used by MCP and <c>--panel-screenshot</c>.
    /// A <c>Type:Page</c> suffix selects a page on panels that support it (ProjectSettingsPanel:
    /// entry name, e.g. <c>ProjectSettingsPanel:Graphics</c>).</summary>
    public static ScreenshotResult CapturePanelNow(string panelType, int width = 900, int height = 600, string? outputPath = null)
    {
            try
            {
                string? page = null;
                int colon = panelType.IndexOf(':');
                if (colon >= 0)
                {
                    page = panelType[(colon + 1)..];
                    panelType = panelType[..colon];
                }

                Type? type = ResolvePanelType(panelType);
                if (type == null)
                    return new ScreenshotResult(null, 0, 0, $"Panel type not found: {panelType}");
                if (!typeof(DockPanel).IsAssignableFrom(type))
                    return new ScreenshotResult(null, 0, 0, $"{panelType} is not a DockPanel.");

                // Prefer the live docked panel so inspector/graph selection from runtime_eval is visible.
                var app = EditorApplication.Instance;
                DockPanel panel = app?.FindOpenPanel(type)
                    ?? (DockPanel)Activator.CreateInstance(type)!;

                if (page != null && panel is GUI.Panels.ProjectSettingsPanel settingsPanel
                    && !settingsPanel.SelectSettingsByName(page))
                {
                    return new ScreenshotResult(null, 0, 0, $"Settings page not found: {page}");
                }

                using var rt = new RenderTexture(width, height, true, new[] { TextureImageFormat.Color4b });
                var renderer = new PaperRenderer();
                renderer.Initialize(width, height);
                renderer.SetRenderTarget(rt.frameBuffer);

                var paper = new Paper(renderer, width, height, new FontAtlasSettings());
                // A fresh Paper has no fallback chain, so icon glyphs and CJK text would render blank.
                foreach (Scribe.FontFile fallback in Theming.EditorTheme.FallbackFonts)
                    paper.AddFallbackFont(fallback);
                paper.DisplayFramebufferScale = new Float2(1f, 1f);

                // Tool handlers run at the top of the frame, before the editor records gizmos, so a
                // viewport panel captured here would render a scene with an empty gizmo builder.
                app?.RecordEditorGizmos(Runtime.Resources.Scene.Current);

                // The scene viewport is retained: without an invalidation signal a repeat capture at
                // the same size replays the previously cached render texture.
                (panel as GUI.Panels.SceneViewPanel)?.InvalidateRender();

                // The editor's Origami theme is only pushed around its own draw, so a tool handler sees
                // the default theme — which carries no font, and every font-dependent Origami widget
                // (text fields, dropdowns, buttons) silently skips drawing itself.
                using var themeScope = Theming.EditorTheme.PushOrigami();

                // This capture Paper must not stay Origami's active surface, or the next popup the
                // live editor opens would be pinned to it and never drawn.
                using var surfaceScope = Origami.PushOffscreenSurface();

                // Retained widgets (text fields, and anything sized from measured content) only settle
                // once their state exists in this Paper instance, so a single frame captures them
                // half-built. Pump a few frames and keep the last one's draw calls.
                for (int frame = 0; frame < 3; frame++)
                {
                    paper.BeginFrame(0.016f, 1f);
                    Origami.BeginFrame(paper, 0.016f);
                    try { panel.OnGUI(paper, width, height); }
                    catch (Exception drawEx) { Runtime.Debug.LogWarning($"[panel_screenshot] panel.OnGUI threw (capturing partial): {drawEx.Message}"); }
                    Origami.EndFrame(paper);
                    paper.EndFrame();
                }

                // Panels normally inherit the host background and need not paint every pixel.
                // A fresh capture target contains undefined data: clear it before the final replay,
                // rather than recording arbitrary previous GPU contents as a visual baseline.
                using (var clear = Graphics.GetCommandBuffer("Panel Screenshot Clear"))
                {
                    clear.SetRenderTarget(rt.frameBuffer);
                    clear.SetViewport(0, 0, (uint)width, (uint)height);
                    clear.DisableScissor();
                    var background = Theming.EditorTheme.BackgroundColorA;
                    clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil,
                        new XEngine.Vector.Color(background.R / 255f, background.G / 255f, background.B / 255f, 1f));
                    Graphics.Submit(clear);
                }

                // Replay the frame's draw calls to the capture target (RenderCalls submits internally).
                renderer.RenderCalls(paper.Canvas, paper.Canvas.DrawCalls);

                // GPU readback.
                Texture2D tex = rt.MainTexture;
                int size = tex.GetSize();
                byte[] data = new byte[size];
                unsafe { fixed (byte* p = data) tex.GetDataPtr(p); }

                string path = EditorScreenshotPaths.ResolveOutputPath(
                    $"{type.Name}-{System.DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.png",
                    Project.Current?.RootPath, outputPath);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var settings = new ImageMagick.PixelReadSettings((uint)width, (uint)height,
                    ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA);
                using var img = new ImageMagick.MagickImage();
                img.ReadPixels(data, settings);
                // No flip here, unlike runtime_screenshot: Paper renders with a top-left origin
                // projection, so this readback already comes out the right way up. Flipping it
                // produced upside-down panel captures.
                img.Write(path, ImageMagick.MagickFormat.Png32);

                renderer.Dispose();
                return new ScreenshotResult(path, width, height, null);
            }
            catch (Exception ex)
            {
                return new ScreenshotResult(null, 0, 0, $"Panel screenshot failed: {ex.Message}");
            }
    }

    /// <summary>
    /// Resolve a DockPanel type from loaded assemblies, then from Library/ScriptAssemblies
    /// (package Editor windows such as Timeline are compiled there and may not yet be loaded).
    /// </summary>
    /// <summary>Open the capture target panel when it is not already docked, so panels with async
    /// state (e.g. PackageManagerPanel's remote catalog refresh) settle before
    /// <see cref="CapturePanelNow"/> runs — the capture prefers the live instance and a fresh one
    /// would still be mid-refresh.</summary>
    public static void EnsurePanelOpenForCapture(string panelType)
    {
        var app = EditorApplication.Instance;
        if (app == null) return;
        string bare = panelType.Split(':')[0];
        Type? type = ResolvePanelType(bare);
        if (type != null && typeof(DockPanel).IsAssignableFrom(type) && app.FindOpenPanel(type) == null)
            app.OpenPanel(type);
    }

    private static Type? ResolvePanelType(string panelType)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? loaded = asm.GetType(panelType);
            if (loaded != null) return loaded;
        }

        string cwdAsm = Path.Combine(Directory.GetCurrentDirectory(), "Library", "ScriptAssemblies");
        string? projectAsm = Project.Current is null
            ? null
            : Path.Combine(Project.Current.RootPath, "Library", "ScriptAssemblies");

        Type? fromCwd = TryLoadPanelTypeFromDirectory(cwdAsm, panelType);
        if (fromCwd != null) return fromCwd;
        if (projectAsm != null && !string.Equals(projectAsm, cwdAsm, StringComparison.OrdinalIgnoreCase))
            return TryLoadPanelTypeFromDirectory(projectAsm, panelType);
        return null;
    }

    private static Type? TryLoadPanelTypeFromDirectory(string asmDir, string panelType)
    {
        if (!Directory.Exists(asmDir)) return null;

        string[] dlls = Directory.GetFiles(asmDir, "*.dll");
        for (int i = 0; i < dlls.Length; i++)
        {
            try
            {
                Type? fromDisk = Assembly.LoadFrom(dlls[i]).GetType(panelType);
                if (fromDisk != null) return fromDisk;
            }
            catch
            {
                // Skip native or incompatible DLLs copied next to script assemblies.
            }
        }

        return null;
    }

    [McpServerTool(Name = "runtime_selection", UseStructuredContent = true)]
    [Description("Query or set the editor's current selection. action='query' returns selected objects (identifier/name/type); action='set' selects one object by its Identifier (Guid).")]
    public static Task<SelectionResult> SelectionOp(
        MainThreadDispatcher dispatcher,
        [Description("'query' (read current selection) or 'set' (select by identifier).")] string action,
        [Description("For action='set': the GameObject Identifier (Guid) to select. Ignored for query.")] string? identifier = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            if (action == "query")
            {
                var selected = XEngine.Editor.Core.Selection.GetSelected<GameObject>()
                    .Select(go => new SelectedObject(go.Identifier.ToString(), go.Name, go.GetType().Name))
                    .ToArray();
                var active = XEngine.Editor.Core.Selection.ActiveObject as GameObject;
                return new SelectionResult(true, selected,
                    active is null ? null : new SelectedObject(active.Identifier.ToString(), active.Name, active.GetType().Name), null);
            }

            if (action == "set")
            {
                if (string.IsNullOrEmpty(identifier))
                {
                    return new SelectionResult(false, Array.Empty<SelectedObject>(), null, "action='set' requires identifier.");
                }

                if (!Guid.TryParse(identifier, out Guid guid))
                {
                    return new SelectionResult(false, Array.Empty<SelectedObject>(), null, $"Invalid identifier (not a Guid): {identifier}");
                }

                GameObject? go = Scene.Current?.FindObjectByIdentifier<GameObject>(guid);
                if (go is null)
                {
                    return new SelectionResult(false, Array.Empty<SelectedObject>(), null, $"GameObject '{identifier}' not found in the current scene.");
                }

                XEngine.Editor.Core.Selection.Select(go);
                return new SelectionResult(true, Array.Empty<SelectedObject>(),
                    new SelectedObject(go.Identifier.ToString(), go.Name, go.GetType().Name), null);
            }

            return new SelectionResult(false, Array.Empty<SelectedObject>(), null, $"Unknown action '{action}'. Use 'query' or 'set'.");
        });

    [McpServerTool(Name = "runtime_menu", UseStructuredContent = true)]
    [Description("List or invoke editor menu items by path (e.g. 'File/New Scene'). action='list' returns all paths; action='invoke' executes a menu item.")]
    public static Task<MenuResult> MenuOp(
        MainThreadDispatcher dispatcher,
        [Description("'list' (enumerate all menu paths) or 'invoke' (execute a menu item by path).")] string action,
        [Description("For action='invoke': the menu path (e.g. 'File/Save'). Ignored for list.")] string? path = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            if (action == "list")
            {
                var paths = new List<string>();
                foreach (var root in MenuRegistry.RootMenus)
                {
                    CollectMenuPaths(root, "", paths);
                }
                return new MenuResult(true, paths.ToArray(), null);
            }

            if (action == "invoke")
            {
                if (string.IsNullOrEmpty(path))
                {
                    return new MenuResult(false, Array.Empty<string>(), "action='invoke' requires path.");
                }

                var item = FindMenuItem(path!);
                if (item is null)
                {
                    return new MenuResult(false, Array.Empty<string>(), $"Menu item not found: {path}");
                }

                bool enabled = item.IsEnabledFunc is null ? item.IsEnabled : item.IsEnabledFunc();
                if (!enabled)
                {
                    return new MenuResult(false, Array.Empty<string>(), $"Menu item is disabled: {path}");
                }

                item.OnClick?.Invoke();
                return new MenuResult(true, Array.Empty<string>(), null);
            }

            return new MenuResult(false, Array.Empty<string>(), $"Unknown action '{action}'. Use 'list' or 'invoke'.");
        });

    [McpServerTool(Name = "runtime_eval", UseStructuredContent = true)]
    [Description("Evaluate a C# expression or statement in the LIVE editor (Roslyn REPL, no recompile). Globals: Scene, Editor, IsPlaying are in scope. SECURITY: full engine API access — credential-grade, local --serve only.")]
    public static Task<EvalResult> Eval(
        MainThreadDispatcher dispatcher,
        [Description("C# code to evaluate (expression or statement).")] string code)
    {
        // Marshal onto the main thread: capture globals synchronously (they read live state), then
        // run the Roslyn eval. ★ P1-4: use the async overload (Func<Task<T>>) so a slow compile
        // awaits on the main loop instead of doing a sync-over-async .GetAwaiter().GetResult() block.
        return dispatcher.InvokeOnMainThreadAsync(() => ScriptEvalService.EvaluateAsync(code));
    }

    [McpServerTool(Name = "runtime_refresh", UseStructuredContent = true)]
    [Description("Force a full asset-database refresh on the LIVE editor: rescan Assets/, import new/changed files, rebuild the resources map. Call this after an AI agent has written many files (scripts/assets/.meta) via the offline Host so the running editor picks them up without waiting for the debounced file watcher. Returns the asset count after refresh.")]
    public static Task<RefreshResult> RefreshAssets(MainThreadDispatcher dispatcher)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            var backend = EditorAssetBackend.Instance;
            if (backend is null)
            {
                return new RefreshResult(false, 0, "EditorAssetBackend instance not available.");
            }
            backend.RefreshAll();
            return new RefreshResult(true, backend.AssetCount, $"Refreshed {backend.AssetCount} assets.");
        });

    [McpServerTool(Name = "runtime_animation_bind", UseStructuredContent = true)]
    [Description("Bind the Animation window to a scene GameObject (and optional clip). identifier is the GameObject Guid; clipGuid is an optional AnimationClip asset Guid. Opens the Animation panel if needed.")]
    public static Task<AnimationBindResult> AnimationBind(
        MainThreadDispatcher dispatcher,
        [Description("GameObject Identifier (Guid) to bind as the animation root.")] string identifier,
        [Description("Optional AnimationClip asset Guid. When omitted, the first clip on the object's Animator/AnimationComponent is used.")] string? clipGuid = null)
        => dispatcher.InvokeOnMainThreadAsync(() => BindAnimationWindow(identifier, clipGuid));

    [McpServerTool(Name = "runtime_animation_record_key", UseStructuredContent = true)]
    [Description("Write a keyframe at the Animation window playhead (or an explicit time) for a bone channel (PosX..ScaleZ) or a generic float property. Lets an agent pose a bone and stamp a key. Opens/uses the live Animation window; clip must already be bound.")]
    public static Task<AnimationRecordKeyResult> AnimationRecordKey(
        MainThreadDispatcher dispatcher,
        [Description("Bone path relative to the animation root (e.g. Hips, Hips/Spine). Empty string is the root for generic properties.")] string path,
        [Description("Bone channel PosX/PosY/PosZ/RotX/RotY/RotZ/RotW/ScaleX/ScaleY/ScaleZ, a blendShape.<ShapeName> morph weight, or a generic property name (requires typeName).")] string channel,
        [Description("Keyframe value.")] float value,
        [Description("Optional time in seconds. When omitted, uses the Animation window playhead.")] float? time = null,
        [Description("Generic binding type name (e.g. DirectionalLight). Required when channel is not a TRS component.")] string? typeName = null)
        => dispatcher.InvokeOnMainThreadAsync(() =>
        {
            var app = EditorApplication.Instance;
            if (app == null)
                return new AnimationRecordKeyResult(false, 0f, path, channel, "EditorApplication is not running.");

            app.OpenPanel(typeof(AnimationClipWindow));
            if (app.FindOpenPanel(typeof(AnimationClipWindow)) is not AnimationClipWindow win)
                return new AnimationRecordKeyResult(false, 0f, path, channel, "Animation window could not be opened.");

            AnimationClip? clip = win.CurrentClip;
            if (clip == null)
                return new AnimationRecordKeyResult(false, win.PlaybackTime, path, channel, "No clip bound in the Animation window. Call runtime_animation_bind first.");
            if (AnimationWindowBinding.IsReadOnlyClip(clip))
                return new AnimationRecordKeyResult(false, win.PlaybackTime, path, channel, "Bound clip is read-only. Duplicate it before recording.");

            float t = time ?? win.PlaybackTime;
            if (t < 0f) t = 0f;
            bool ok = AnimationRecording.WriteKey(clip, win.CurrentRoot, path ?? "", channel, t, value, typeName);
            return new AnimationRecordKeyResult(ok, t, path, channel, ok ? null : "Could not write key (unknown channel or missing typeName).");
        });

    private static void CollectMenuPaths(AppMenuItem item, string parentPath, List<string> paths)
    {
        if (item.IsSeparator) return;
        string current = string.IsNullOrEmpty(parentPath) ? item.Label : parentPath + "/" + item.Label;
        if (item.OnClick is not null) paths.Add(current); // only leaves with an action
        foreach (var sub in item.SubItems)
        {
            CollectMenuPaths(sub, current, paths);
        }
    }

    private static AppMenuItem? FindMenuItem(string path)
    {
        string[] segments = path.Split('/');
        var current = MenuRegistry.RootMenus;
        AppMenuItem? found = null;
        foreach (string seg in segments)
        {
            found = null;
            foreach (var item in current)
            {
                if (item.Label == seg && !item.IsSeparator) { found = item; break; }
            }
            if (found is null) return null;
            current = found.SubItems;
        }
        return found;
    }

    private static int SeverityRank(string s) => s.Trim().ToLowerInvariant() switch
    {
        "success" => 0, "normal" or "info" => 1, "warning" or "warn" => 2,
        "error" => 3, "exception" => 4, _ => 1,
    };

    public sealed record LogEntry(string Severity, string Message, string? StackTrace, System.DateTimeOffset TimestampUtc);
    public sealed record LogsPage(int Returned, int TotalBuffered, IReadOnlyList<LogEntry> Logs);
    public sealed record RuntimeTaskView(string Title, string Detail, int Current, int Total, string Kind, long ElapsedMs);
    public sealed record RuntimeState(
        string SceneName, string? ScenePath, bool IsPlaying, int ObjectCount,
        bool IsOpeningProject, bool IsCompiling, bool IsImporting,
        IReadOnlyList<RuntimeTaskView> RunningTasks);
    public sealed record PlaymodeResult(bool Succeeded, bool IsPlayingNow, string? Error);
    public sealed record LiveScene(string Name, int TotalObjects, IReadOnlyList<LiveObject> Roots);
    public sealed record LiveObject(string Identifier, string Name, int ChildCount, string Tag, int Layer);
    /// <summary>★ runtime_stats — Live rendering/perf counters from RenderStats.Last + frame-time history.</summary>
    public sealed record RenderStatsSnapshot(
        float FrameTimeMs, float ColorPassMs, float ShadowPassMs, float PostFxMs,
        /// <summary>Legacy field name; this is render-thread wait, not GPU execution time.</summary>
        float GpuWaitMs,
        int DrawCalls, int InstancedDrawCalls, int Batches, long Triangles, long Vertices,
        int RenderablesCollected, int RenderablesCulled, int RenderablesDrawn,
        int Cameras,
        int Lights, int DirectionalLights, int PointLights, int SpotLights,
        int ShadowDrawCalls, long ShadowTriangles, int ShadowPasses, int ShadowCasters,
        int ImageEffects, int ImageEffectPasses,
        int CommandBufferSubmissions, int ColorPassSubmissions, int ShadowPassSubmissions,
        int PostFxSubmissions, int FinalPassSubmissions, int UiSubmissions,
        int ClearOperations, int BlitOperations, long BlitPixels,
        long TransientRenderTargetBytes,
        int PrepassSubmissions, int PrepassSkipped,
        IReadOnlyList<float> RecentFrameTimesMs,
        string BackendName = "No device",
        bool GpuTimestampQueriesSupported = false,
        bool HardwareTimestampQueriesSupported = false,
        int ShadowPassesCached = 0,
        float? GpuFrameTimeMs = null,
        ulong? GpuFrameId = null,
        int SurfaceWidth = 0,
        int SurfaceHeight = 0,
        string DirtyReason = "unavailable",
        string SchedulerState = "unavailable",
        int UiDrawCalls = 0,
        long UiRenderTargetBytes = 0,
        long PresentCount = 0,
        float PresentWaitMs = 0f,
        double PresentFps = 0,
        double SustainedFps = 0,
        double CpuFrameMs = 0);
    /// <summary>★ runtime_screenshot — PNG capture result (path + dimensions, or error).</summary>
    public sealed record ScreenshotResult(string? Path, int Width, int Height, string? Error);
    /// <summary>★ runtime_selection — selected objects + active, or an error.</summary>
    public sealed record SelectionResult(bool Succeeded, IReadOnlyList<SelectedObject> Selected, SelectedObject? Active, string? Error);
    public sealed record SelectedObject(string Identifier, string Name, string TypeName);
    /// <summary>★ runtime_menu — menu paths (list) or invoke result.</summary>
    public sealed record MenuResult(bool Succeeded, IReadOnlyList<string> Paths, string? Error);

    /// <summary>★ runtime_refresh — result of a forced asset-database refresh.</summary>
    public sealed record RefreshResult(bool Succeeded, int AssetCount, string Message);

    /// <summary>★ runtime_animation_bind — Animation window (root, clip) binding.</summary>
    public sealed record AnimationBindResult(bool Succeeded, string? RootName, string? ClipName, bool ClipIsReadOnly, string? Error);

    /// <summary>★ runtime_animation_record_key — stamp a key at playhead or an explicit time.</summary>
    public sealed record AnimationRecordKeyResult(bool Succeeded, float Time, string Path, string Channel, string? Error);

    /// <summary>★ runtime_render_debug — Universal pipeline debugger snapshot.</summary>
    public sealed record RenderDebugResult(
        int DrawCalls, int RenderablesDrawn, int OverdrawProxy,
        int Lights, int DirectionalLights, int PointLights, int SpotLights,
        int ShadowCasters, int ShadowCascades,
        int EnqueuedPasses, int ExecutedPasses, int CulledGraphPasses,
        int CopyColorPasses, int CopyDepthPasses,
        int RtWidth, int RtHeight, bool OpaqueAllocated, bool PrepassAllocated,
        IReadOnlyList<string> PassEvents, IReadOnlyList<string> Features,
        IReadOnlyList<string> VolumeStack, IReadOnlyList<RenderDebugVolume> Volumes);

    public sealed record RenderDebugVolume(string Name, bool IsGlobal, float Weight, int Priority, bool Enabled);

    /// <summary>★ runtime_frame_debug — Frame Debugger status / tree / selected state.</summary>
    public sealed record FrameDebugResult(
        bool Succeeded,
        bool Enabled,
        bool Capturing,
        int SelectedEventIndex,
        int LeafEventCount,
        int DrawCount,
        string? CapturedAt,
        IReadOnlyList<FrameDebugEvent>? Events,
        FrameDebugSelected? Selected,
        string? Error,
        string CaptureScope = "GameRendering",
        bool CaptureStale = false,
        bool IndexMappingReliable = true,
        IReadOnlyList<string>? RenderTargets = null);

    public sealed record FrameDebugEvent(
        int Index, string Kind, string Label, string? CommandBufferName, string Scope = "None");

    public sealed record FrameDebugSelected(
        int Index, string Kind, string Label, string? CommandBufferName,
        string RtName, int RtWidth, int RtHeight, string RtFormat,
        string ShaderLabel, string ShaderAssetName,
        string? MeshName, string? MaterialName,
        string Topology, int IndexCount,
        IReadOnlyList<string> Textures);
}
