// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Net;

using Echo.Logging;

using XEngine.Async;
using XEngine.Runtime.AssetBundles;
using XEngine.Runtime.Audio;

using XEngine.PaperUI;
using XEngine.Runtime.GUI;
using XEngine.Runtime.Profiling;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;

namespace XEngine.Runtime;

public class EchoLogger : IEchoLogger
{
    public void Debug(string message) => XEngine.Runtime.Debug.Log(message);

    public void Error(string message, Exception? exception = null) => XEngine.Runtime.Debug.LogError(message);

    public void Info(string message) => XEngine.Runtime.Debug.Log(message);

    public void Warning(string message) => XEngine.Runtime.Debug.LogWarning(message);
}

public abstract class Game
{
    /// <summary>
    /// Swapchain clear before Paper/Scene draw. Editor chrome, GDI erase, and detached
    /// surfaces use #383838; a black clear flashes gray→black→gray when the idle
    /// scheduler wakes. Players stay black. Web stays magenta so the canvas present is
    /// distinguishable from CSS #000.
    /// </summary>
    public static Color FrameClearColor(bool isEditor)
    {
#if XENGINE_WEB
        return Color.Magenta;
#else
        return isEditor
            ? new Color(56f / 255f, 56f / 255f, 56f / 255f, 1f)
            : Color.Black;
#endif
    }

    private TimeData time = new();
    private float fixedTimeAccumulator = 0.0f;

    private PaperRenderer _paperRenderer;
    private Paper _paper;
    private int frameCounter;
    private bool _gpuFaulted;
    private ProfilerTelemetryPublisher? _profilerTelemetryPublisher;

    public Paper PaperInstance => _paper;
    public PaperRenderer? PaperRenderer => _paperRenderer;

    public bool DrawGizmos { get; set; }

    /// <summary>
    /// Added a separate method to initialize the window as it might be needed to restore the latest saved state of the window (size, position, etc.) when the game is launched again.
    /// This allows for better user experience by remembering their preferences.
    /// </summary>
    public virtual void InitializeWindow(string title, int width, int height)
    {
#if XENGINE_MOBILE
        Window.InitWindow(title, width, height, false);
#else
        Window.InitWindow(title, width, height, Silk.NET.Windowing.WindowState.Normal, false);
#endif
    } 

    public void Run(string title, int width, int height)
    {
        ConfigureLoop(title, width, height, initializeAudio: true);
        Window.Start();
    }

    /// <summary>
    /// Configures the normal Game lifecycle for a platform-owned mobile window. The native host
    /// drives <see cref="Window.Load"/>, <see cref="Window.Update"/> and
    /// <see cref="Window.Render"/> through <c>AndroidPlatformHost</c>; unlike <see cref="Run"/>,
    /// this method must not start a desktop window loop.
    /// </summary>
    public void InitializePlatformHost(string title, int width, int height)
    {
        ConfigureLoop(title, width, height, initializeAudio: false);
    }

    /// <summary>Enables bounded direct-IP profiler publishing for this player host.</summary>
    public void EnableProfilerTelemetry(IPEndPoint remoteEndpoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndpoint);
        _profilerTelemetryPublisher?.Dispose();
        _profilerTelemetryPublisher = new ProfilerTelemetryPublisher(remoteEndpoint);
    }

    private void ConfigureLoop(string title, int width, int height, bool initializeAudio)
    {
        // Invariant culture ensures consistent float parsing (dot decimal separator)
        // across all locales. Without this, embedded .mat/.shader files with "0.5"
        // break on cultures that use comma separators.
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

        // Capture the main thread so frame-driven XTask continuations and SwitchToMainThread can
        // identify it. Done here (and in RunHeadless) because both entry points run on the main
        // thread before the window/headless loop starts.
        XTaskLoopHelper.Initialize(Environment.CurrentManagedThreadId);

        // A bare standalone game is always "playing" so gameplay (component lifecycle, FixedUpdate,
        // physics stepping) runs. The editor overrides this back to false in its own Initialize and
        // drives play/pause itself. Set before Window.Load -> Initialize so that override wins.
        Application.IsPlaying = true;

        // Player quality baseline (Full tier, 4096 shadow atlas). Without this a standalone
        // player leaves QualitySettings untouched and ShadowAtlas falls back to the device
        // maximum (8192² ≈ 256+ MB of depth VRAM cleared on every cache miss). Same override
        // pattern as IsPlaying: the editor re-applies its own defaults in Initialize.
        Rendering.QualitySettings.ApplyPlayerDefaults();

        ConfigureProfilerTelemetryFromCommandLine();

        InitializeWindow(title, width, height);

        Window.Load += () =>
        {
            if (initializeAudio)
                AudioContext.Initialize(44100, 2, 2048);

            // Renderer projection uses framebuffer (physical) pixels;
            // Paper resolution uses window (logical) size.
            var fbSize = Window.InternalWindow.FramebufferSize;
            var winSize = Window.InternalWindow.Size;

            _paperRenderer = new PaperRenderer();
            _paperRenderer.Initialize(fbSize.X, fbSize.Y);
            _paper = new Paper(_paperRenderer, winSize.X, winSize.Y, new XEngine.Quill.FontAtlasSettings());
            _paper.SetClipboardHandler(new RuntimeClipboardHandler());
            // Apply the hovered element's requested cursor to the OS window.
            _paper.OnCursorChange += c => Input.SetCursorShape(c);

            _paper.DevTools.Enabled = true;

            BuiltInAssets.Initialize();

            Initialize();
        };

        Window.Update += (delta) =>
        {
            try
            {
                UpdatePaperInput();

                AudioContext.Update();

                time.Update();
                Time.TimeStack.Clear();
                Time.TimeStack.Push(time);

                // Input interaction timing (Hold/Tap/MultiTap windows) is real-world timing, so it uses
                // UNSCALED delta
                Input.UpdateActions(Time.UnscaledDeltaTime);

                BeginUpdate();

                SimulationStep(Time.DeltaTime);

                EndUpdate();
                _profilerTelemetryPublisher?.TryPublish();

                if (frameCounter++ % 60 == 0)
                {
                    double fps = delta > 0 ? 1.0 / delta : 0; // real (unscaled) frame rate
#if !XENGINE_MOBILE
                    Debug.TrySetConsoleTitle($"{title} - {Window.InternalWindow.FramebufferSize.X}x{Window.InternalWindow.FramebufferSize.Y} - FPS: {fps:F0}");
#endif
                }

            }
            catch (Exception e)
            {
                Debug.LogError("An exception occurred during the Update loop:");
                Debug.LogError(e.ToString());
                throw;
            }
        };

        Window.RenderRequested = ShouldRenderFrame;
        Window.Render += RenderFrame;

        Window.Resize += (size) =>
        {
            // Paper's resolution is resynced from PreparePaperFrame each render frame.
            Resize(size.X, size.Y);
        };

        Window.FramebufferResize += (size) =>
        {
            // Android can report its first surface size before Window.Load initializes Paper.
            // The Load callback reads the current framebuffer dimensions, so the early event
            // only needs to be retained by Window until the renderer exists.
            if (_paperRenderer != null)
                _paperRenderer.UpdateProjection(size.X, size.Y);
        };

        Window.Closing += () =>
        {
            Closing();

            // Unload the current scene
            Scene.Unload();

            AudioContext.Deinitialize();
            _profilerTelemetryPublisher?.Dispose();
            _profilerTelemetryPublisher = null;

            Debug.Log("Is terminating...");
        };

        Debug.LogSuccess("Initialization complete");
    }

    [HotPath]
    private void RenderFrame(float delta)
    {
        // A Vulkan device cannot be used after VK_ERROR_DEVICE_LOST. Keep the editor process and
        // its project state alive, but stop recording Paper/render commands against dead handles.
        if (_gpuFaulted || Graphics.IsDeviceLost)
            return;

        try
        {
            Scene? currentScene = Scene.Current;

            // === Start Graphics ===
            {
                if (XEngine.Runtime.Rendering.FrameDebugger.IsCapturing)
                    XEngine.Runtime.Rendering.FrameDebugger.BeginGroup("Frame Start");
                using var frameStart = Graphics.GetCommandBuffer("Frame Start");
                frameStart.SetRenderTarget(null);
                frameStart.SetViewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
                frameStart.SetRasterState(new RasterizerState());
                Color frameClear = FrameClearColor(Application.IsEditor);
                frameStart.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, frameClear);
                Graphics.Submit(frameStart);
                if (XEngine.Runtime.Rendering.FrameDebugger.IsCapturing)
                    XEngine.Runtime.Rendering.FrameDebugger.EndGroup();
            }

            BeginRender();
            OnRender(currentScene);
            EndRender();

            {
                if (XEngine.Runtime.Rendering.FrameDebugger.IsCapturing)
                    XEngine.Runtime.Rendering.FrameDebugger.BeginGroup("Pre-GUI");
                using var preGui = Graphics.GetCommandBuffer("Pre-GUI");
                preGui.SetRenderTarget(null);
                preGui.SetViewport(0, 0, (uint)Window.InternalWindow.FramebufferSize.X, (uint)Window.InternalWindow.FramebufferSize.Y);
                Graphics.Submit(preGui);
                if (XEngine.Runtime.Rendering.FrameDebugger.IsCapturing)
                    XEngine.Runtime.Rendering.FrameDebugger.EndGroup();
            }

            // Sync Paper's logical resolution + DPI to the window each frame.
            PreparePaperFrame();
            _paperRenderer.SetRenderTarget(null);
            _paper.BeginFrame(delta, -1f); // negative = keep DisplayFramebufferScale set by PreparePaperFrame

            BeginGui(_paper);
            OnGui(currentScene, _paper);
            EndGui(_paper);
            _paper.EndFrame();

            RenderTexture.UpdatePool();
            // Dispose any GPU resources that were replaced mid-frame (e.g.
            // grown instance buffers). This only ENQUEUES delete CBs; the render
            // thread is still draining this frame's queue. Because the deletes are
            // submitted after every draw that referenced the old handle, submit
            // order guarantees they execute last on the render thread.
            Graphics.FlushDeferredDisposes();

        }
        catch (InvalidOperationException e) when (
            e.Message.Contains("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase) ||
            e.Message.Contains("device loss", StringComparison.OrdinalIgnoreCase))
        {
            _gpuFaulted = true;
            Application.IsPlaying = false;
            Debug.LogError("GPU device lost; rendering has stopped for this host instance.");
            if (Application.IsEditor)
            {
                try
                {
                    if (Window.InternalWindow != null)
                        Window.InternalWindow.Title = "XEngine Editor - Vulkan device lost (restart required)";
                }
                catch
                {
                    // The native window may already be tearing down; keep the main loop alive.
                }
            }
            else
                Window.Stop();
        }
        catch (Exception e)
        {
            Debug.LogError("An exception occurred during the Render loop:");
            Debug.LogError(e.ToString());
            throw;
        }
    }

    private volatile bool _headlessQuitRequested;

    /// <summary>
    /// Runs the game without a window, graphics device or audio - only the simulation loop
    /// (gameplay, physics, scripts). Intended for dedicated servers and for headless build
    /// verification. The loop runs until <see cref="RequestHeadlessQuit"/> / Ctrl+C, or until the
    /// frame/time limit in <paramref name="options"/> is reached.
    /// </summary>
    public void RunHeadless(HeadlessRunOptions? options = null)
    {
        options ??= new HeadlessRunOptions();

        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

        // Headless loop runs on the main thread too; capture it for XTask frame scheduling.
        XTaskLoopHelper.Initialize(Environment.CurrentManagedThreadId);

        Application.IsPlaying = true;
        Application.IsHeadless = true;
        ConfigureProfilerTelemetryFromCommandLine();

        // Registers built-in asset loaders (no GPU work happens until something resolves them).
        BuiltInAssets.Initialize();
        Initialize();

        Debug.LogSuccess("Headless initialization complete");

        _headlessQuitRequested = false;
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; _headlessQuitRequested = true; };
        try { Console.CancelKeyPress += cancelHandler; } catch { /* no console in some hosts */ }

        float targetFrameTime = options.TargetFps > 0 ? 1.0f / options.TargetFps : 0.0f;
        var runClock = System.Diagnostics.Stopwatch.StartNew();
        long frame = 0;

        try
        {
            while (!_headlessQuitRequested)
            {
                long frameStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

                time.Update();
                Time.TimeStack.Clear();
                Time.TimeStack.Push(time);

                Input.UpdateActions(Time.UnscaledDeltaTime);

                BeginUpdate();
                SimulationStep(Time.DeltaTime);
                EndUpdate();
                _profilerTelemetryPublisher?.TryPublish();

                frame++;
                if (options.MaxFrames > 0 && frame >= options.MaxFrames) break;
                if (options.MaxSeconds > 0 && runClock.Elapsed.TotalSeconds >= options.MaxSeconds) break;

                // Throttle to the target tick rate so a server doesn't spin a core at 100%.
                if (targetFrameTime > 0.0f)
                {
                    float elapsed = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - frameStartTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
                    int sleepMs = (int)((targetFrameTime - elapsed) * 1000.0f);
                    if (sleepMs > 0) System.Threading.Thread.Sleep(sleepMs);
                }
            }
        }
        finally
        {
            try { Console.CancelKeyPress -= cancelHandler; } catch { }
            Closing();
            Scene.Unload();
            _profilerTelemetryPublisher?.Dispose();
            _profilerTelemetryPublisher = null;
            Application.IsHeadless = false;
        }
    }

    /// <summary>
    /// Consumes wall-clock time accumulated across a long synchronous editor operation (scene
    /// serialization on play-mode entry, script recompiles) so the NEXT simulation step doesn't
    /// inherit all of it as one giant <see cref="Time.DeltaTime"/> — first-frame teleports.
    /// </summary>
    public void ResetDeltaClock() => time.Update();

    /// <summary>Signals a running <see cref="RunHeadless"/> loop to exit after the current frame.</summary>
    public void RequestHeadlessQuit() => _headlessQuitRequested = true;

    private void ConfigureProfilerTelemetryFromCommandLine()
    {
        if (_profilerTelemetryPublisher == null
            && ProfilerTelemetryCommandLine.TryGetEndpoint(Environment.GetCommandLineArgs(), out IPEndPoint endpoint))
            _profilerTelemetryPublisher = new ProfilerTelemetryPublisher(endpoint);
    }

    /// <summary>
    /// Advances one simulation step: the fixed-update loop (when gameplay should run) followed by
    /// <see cref="OnUpdate"/>. Shared by the windowed and headless loops.
    /// </summary>
    private static readonly Profiling.ProfilerMarker s_fixedUpdateMarker = new("FixedUpdate");
    private static readonly Profiling.ProfilerMarker s_updateMarker = new("Update");
    private static readonly Profiling.ProfilerMarker s_frameTasksMarker = new("Tasks & Assets");

    [HotPath]
    private void SimulationStep(float delta)
    {
        // Frame boundary for the marker collector: publish last frame's scopes, start recording this one.
        Profiling.ProfilerFrames.BeginFrame();

        Scene? currentScene = Scene.Current;

        // Gizmos describe the current simulation state, not the number of updates since the last
        // presentation. Retained editor surfaces may skip rendering for many updates, so clearing
        // only after a successful render lets icon commands grow without bound.
        Debug.ClearGizmos();

        // Fixed update loop only when gameplay should run
        fixedTimeAccumulator += delta;
        if (Application.ShouldRunGameplay)
        {
            Application.IsGameplayExecuting = true;
            using var fixedScope = s_fixedUpdateMarker.Auto();
            int count = 0;
            while (fixedTimeAccumulator >= Time.FixedDeltaTime && count++ < Time.MaxFixedIterations)
            {
                // A fixed-step callback can load a scene (death respawn, UI gate), disposing the
                // scene captured at frame start; don't re-enter it on the next iteration.
                if (currentScene is { IsDisposed: false })
                    currentScene.FixedUpdate();
                XTaskLoopHelper.Tick(FrameTiming.FixedUpdate);
                fixedTimeAccumulator -= Time.FixedDeltaTime;
            }

            // If the iteration cap was hit there is still a backlog; discard it rather than replaying
            // it over the next frames (avoids post-hitch slow-motion and the spiral of death).
            if (fixedTimeAccumulator >= Time.FixedDeltaTime)
                fixedTimeAccumulator = 0f;

            Application.IsGameplayExecuting = false;
        }
        else
        {
            // Clamp accumulator to prevent burst when unpausing/starting play
            fixedTimeAccumulator = MathF.Min(fixedTimeAccumulator, Time.FixedDeltaTime);
        }

        // Publish what is left of the accumulator as the interpolation factor for this frame. It has to
        // be read after the fixed loop has drained: the leftover is precisely how far past the last
        // step the frame being presented sits. Clamped because the pause branch above can leave a full
        // step in the accumulator.
        Time.FixedAlpha = Time.FixedDeltaTime > 0f
            ? MathF.Min(fixedTimeAccumulator / Time.FixedDeltaTime, 1f)
            : 0f;

        // Pass the live current scene, not the frame-start capture: gameplay callbacks since then
        // (UI click -> Scene.Load) may have replaced it, and updating a disposed scene throws.
        using (s_updateMarker.Auto())
            OnUpdate(Scene.Current);

        using (s_frameTasksMarker.Auto())
        {
            // Pump the AssetBundles async-operation scheduler once per frame. Cheap when no packages are
            // active (early-out on empty queue); drives bundle/asset/manifest/download ops when there are.
            XAssets.Tick();
            // WASM has no AssetLoader worker thread; drain queued loads with a per-frame budget.
            AssetLoader.Tick();

            // Pump the frame-driven XTask scheduler. Update runs immediately after scene+asset updates;
            // LateUpdate follows it (v1 note: Scene.Update bundles Start/Update/LateUpdate together, so
            // LateUpdate here fires right after Update rather than strictly between component Update and
            // LateUpdate — acceptable for coroutine use); EndOfFrame runs last, before presentation.
            XTaskLoopHelper.Tick(FrameTiming.Update);
            XTaskLoopHelper.Tick(FrameTiming.LateUpdate);
            XTaskLoopHelper.Tick(FrameTiming.EndOfFrame);
        }

        // Consume step request re-pause after one frame
        if (Application.StepRequested)
        {
            Application.StepRequested = false;
            Application.IsPaused = true;
        }

        // Deferred EngineObject.Destroy(): dispose queued objects at the true end of the frame,
        // after every update/late-update/gui pass could have queued more.
        EngineObject.FlushDestroyed();
    }

    public virtual void Initialize() { }

    public virtual void BeginUpdate() { }
    public virtual void EndUpdate() { }
    public virtual void BeginRender() { }
    public virtual void EndRender() { }
    public virtual void BeginGui(Paper paper) { }
    public virtual void EndGui(Paper paper) { }

    /// <summary>
    /// Lets hosts with retained UI skip a graphics frame when no repaint is needed. The default
    /// preserves the normal game loop and renders every tick.
    /// </summary>
    protected virtual bool ShouldRenderFrame() => true;

    /// <summary>Called during update. Override to control scene update/gizmo behavior.</summary>
    public virtual void OnUpdate(Scene? scene)
    {
        scene?.Update();
        if (DrawGizmos)
            scene?.DrawGizmos();
    }

    /// <summary>Called during render. Override to control scene rendering.</summary>
    public virtual void OnRender(Scene? scene)
    {
        scene?.Render();
    }

    /// <summary>Called during GUI phase. Override to control scene GUI rendering.</summary>
    public bool ShowFpsOverlay { get; set; }
    private long _fpsSampleStart;
    private int _fpsSampleFrames;
    private string _fpsLabel = "fps:0";

    private void DrawFpsOverlay(Paper paper)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_fpsSampleStart == 0) _fpsSampleStart = now;
        _fpsSampleFrames++;
        double seconds = (now - _fpsSampleStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (seconds >= 1)
        {
            int fps = (int)Math.Round(_fpsSampleFrames / seconds);
            _fpsLabel = "fps:" + fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var stats = RenderStats.Last;
#if XENGINE_HARMONYOS
            if (Graphics.BackendDevice is Backends.Vulkan.VulkanGraphicsDevice vk)
                Debug.Log($"[PlayerWaits] fenceMs={vk.LastFrameFenceWaitMs:F1} acquireMs={vk.LastSwapchainAcquireMs:F1}");
            Debug.Log($"[PlayerPhases] updateMs={Window.LastUpdateMs:F1} renderMs={Window.LastRenderMs:F1} beginMs={Platforms.HarmonyOS.HarmonyOSPlatformHost.LastBeginFrameMs:F1} endMs={Platforms.HarmonyOS.HarmonyOSPlatformHost.LastEndFrameMs:F1}");
#endif
            Debug.Log($"[PlayerFps] fps={fps} draw={stats.DrawCalls} triangles={stats.Triangles} frameMs={stats.FrameTimeMs:F1} colorMs={stats.ColorPassMs:F1} shadowMs={stats.ShadowPassMs:F1} postMs={stats.PostFxMs:F1} uiMs={stats.UIPassCpuMs:F1} managedMB={GC.GetTotalMemory(false) / 1048576}");
            _fpsSampleFrames = 0;
            _fpsSampleStart = now;
        }
        paper.Box("player_debug_fps").PositionType(PositionType.SelfDirected)
            .Position(62, 62).Size(140, 36).IsNotInteractable()
            .Text(_fpsLabel, FontAsset.LoadDefault().FontFile).FontSize(24)
            .TextColor(Color.White).BackgroundColor(new Color(0, 0, 0, .65f));
    }

    public virtual void OnGui(Scene? scene, Paper paper)
    {
        scene?.OnGui(paper);
        if (ShowFpsOverlay) DrawFpsOverlay(paper);
    }

    public virtual void Resize(int width, int height) { }
    public virtual void Closing() { }

    /// <summary>
    /// Called each frame right before <c>Paper.BeginFrame</c>. Sets Paper's logical resolution
    /// to the window's logical size (OS points) and <c>DisplayFramebufferScale</c> to
    /// <see cref="Window.ContentScale"/>. Quill's <c>TransformPoint</c> multiplies every vertex
    /// by <c>DisplayFramebufferScale</c>, so vertices span <c>[0, winSize × cs]</c> =
    /// <c>[0, fbSize]</c>, exactly matching the orthographic projection set up by
    /// <see cref="PaperRenderer.UpdateProjection"/>.
    /// <para>
    /// A widget declared <c>Width(100)</c> occupies 100 logical points = 100 physical pixels at
    /// 1× DPI and 200 physical pixels at 2× (Retina) DPI the same physical size on screen
    /// regardless of display density, with higher pixel quality on HiDPI displays.
    /// </para>
    /// </summary>
    protected virtual void PreparePaperFrame()
    {
        var fbSize = Window.InternalWindow.FramebufferSize;
        float cs = Math.Max(0.01f, Window.ContentScale);
        // resolution × cs = fbSize, so vertices always span exactly [0, fbSize].
        // Using fbSize (not winSize) handles DPI-unaware Windows where winSize == fbSize
        // but glfwGetWindowContentScale still returns the system DPI factor (e.g. 1.25).
        _paper.SetResolution(fbSize.X / cs, fbSize.Y / cs);
        _paper.DisplayFramebufferScale = new Float2(cs, cs);
    }

    protected virtual Float2 GetPaperMousePosition()
    {
        var p = Input.MousePosition;
        var fb = Window.InternalWindow.FramebufferSize;
        var win = Window.InternalWindow.Size;
        float cs = Math.Max(0.01f, Window.ContentScale);
        // Mouse is in winSize coords; paper space is [0, fbSize/cs].
        // csFbWin converts winSize → fbSize; dividing by cs then lands in paper space.
        // On macOS cs == csFbWin so the ratio is 1. On DPI-unaware Windows csFbWin == 1
        // and cs is the system DPI, so we divide mouse by cs.
        float csFbWin = win.X > 0 ? (float)fb.X / win.X : 1f;
        return new Float2(p.X * csFbWin / cs, p.Y * csFbWin / cs);
    }

    /// <summary>Whether this game surface should receive the current mouse pointer input.</summary>
    /// <remarks>Editor multi-window hosts override this to keep an overlapped native child surface
    /// from also updating the primary surface's hover state.</remarks>
    protected virtual bool ShouldRoutePointerInput() => true;

    private void UpdatePaperInput()
    {
        // Mouse position in Paper-logical space.
        bool routePointer = ShouldRoutePointerInput();
        Float2 mousePos = routePointer ? GetPaperMousePosition() : new Float2(-1f, -1f);
        _paper.SetPointerState(PaperMouseBtn.Unknown, (float)mousePos.X, (float)mousePos.Y, false, true);

        if (routePointer)
        {
            if (Input.GetMouseButtonDown(0))
                _paper.SetPointerState(PaperMouseBtn.Left, (float)mousePos.X, (float)mousePos.Y, true, false);
            if (Input.GetMouseButtonUp(0))
                _paper.SetPointerState(PaperMouseBtn.Left, (float)mousePos.X, (float)mousePos.Y, false, false);

            if (Input.GetMouseButtonDown(1))
                _paper.SetPointerState(PaperMouseBtn.Right, (float)mousePos.X, (float)mousePos.Y, true, false);
            if (Input.GetMouseButtonUp(1))
                _paper.SetPointerState(PaperMouseBtn.Right, (float)mousePos.X, (float)mousePos.Y, false, false);

            if (Input.GetMouseButtonDown(2))
                _paper.SetPointerState(PaperMouseBtn.Middle, (float)mousePos.X, (float)mousePos.Y, true, false);
            if (Input.GetMouseButtonUp(2))
                _paper.SetPointerState(PaperMouseBtn.Middle, (float)mousePos.X, (float)mousePos.Y, false, false);
        }
        else
        {
            // Clear any primary-surface press/drag when the pointer enters a detached child.
            _paper.SetPointerState(PaperMouseBtn.Left, (float)mousePos.X, (float)mousePos.Y, false, false);
            _paper.SetPointerState(PaperMouseBtn.Middle, (float)mousePos.X, (float)mousePos.Y, false, false);
            _paper.SetPointerState(PaperMouseBtn.Right, (float)mousePos.X, (float)mousePos.Y, false, false);
        }

        // Handle mouse wheel
        if (routePointer)
        {
            float wheelDelta = Input.MouseWheelDelta;
            if (wheelDelta != 0)
                _paper.SetPointerWheel(wheelDelta);
        }

        // Handle keyboard input. InputString is read non-destructively, so the GameObject UI (and any
        // user UI) still sees the same characters this frame.
        foreach (char ch in Input.InputString)
            _paper.AddInputCharacter(ch);

        // Handle key states. The KeyCode -> PaperKey mapping is computed once (name-based parse
        // plus the manual differences); the previous per-frame Enum.GetValues + Enum.TryParse
        // cost a 123-element array allocation and 123 name-table parses every frame.
        foreach ((KeyCode key, PaperKey paperKey) in s_paperKeyMap)
            HandleKey(key, paperKey);
    }

    private static readonly (KeyCode Key, PaperKey Paper)[] s_paperKeyMap = BuildPaperKeyMap();

    private static (KeyCode, PaperKey)[] BuildPaperKeyMap()
    {
        var map = new List<(KeyCode, PaperKey)>(160);
        // Paper's key enum shares almost all names with KeyCode; parse once, map the rest manually.
        foreach (KeyCode k in Enum.GetValues<KeyCode>())
            if (k != KeyCode.Unknown && Enum.TryParse(k.ToString(), out PaperKey paperKey))
                map.Add((k, paperKey));

        map.Add((KeyCode.Equal, PaperKey.Equals));
        map.Add((KeyCode.BackSlash, PaperKey.Backslash));
        map.Add((KeyCode.GraveAccent, PaperKey.Grave));
        map.Add((KeyCode.KeypadEqual, PaperKey.KeypadEquals));

        map.Add((KeyCode.Number0, PaperKey.Num0));
        map.Add((KeyCode.Number1, PaperKey.Num1));
        map.Add((KeyCode.Number2, PaperKey.Num2));
        map.Add((KeyCode.Number3, PaperKey.Num3));
        map.Add((KeyCode.Number4, PaperKey.Num4));
        map.Add((KeyCode.Number5, PaperKey.Num5));
        map.Add((KeyCode.Number6, PaperKey.Num6));
        map.Add((KeyCode.Number7, PaperKey.Num7));
        map.Add((KeyCode.Number8, PaperKey.Num8));
        map.Add((KeyCode.Number9, PaperKey.Num9));

        map.Add((KeyCode.KeypadSubtract, PaperKey.KeypadMinus));
        map.Add((KeyCode.KeypadAdd, PaperKey.KeypadPlus));

        map.Add((KeyCode.LeftBracket, PaperKey.LeftBracket));
        map.Add((KeyCode.RightBracket, PaperKey.RightBracket));
        map.Add((KeyCode.ShiftLeft, PaperKey.LeftShift));
        map.Add((KeyCode.ShiftRight, PaperKey.RightShift));
        map.Add((KeyCode.AltLeft, PaperKey.LeftAlt));
        map.Add((KeyCode.AltRight, PaperKey.RightAlt));
        map.Add((KeyCode.ControlLeft, PaperKey.LeftControl));
        map.Add((KeyCode.ControlRight, PaperKey.RightControl));
        map.Add((KeyCode.SuperLeft, PaperKey.LeftSuper));
        map.Add((KeyCode.SuperRight, PaperKey.RightSuper));
        return map.ToArray();
    }

    void HandleKey(KeyCode silkKey, PaperKey paperKey)
    {
        if (Input.GetKeyDown(silkKey))
            _paper.SetKeyState(paperKey, true);
        else if (Input.GetKeyUp(silkKey))
            _paper.SetKeyState(paperKey, false);
    }

    /// <summary>Reset the fixed-update accumulator. Call when entering play mode to prevent a burst.</summary>
    protected void ResetFixedTimeAccumulator()
    {
        fixedTimeAccumulator = 0f;
        Time.FixedAlpha = 0f;
    }


    public static void Quit()
    {
        Window.Stop();
        Debug.Log("Is terminating...");
    }
}

/// <summary>
/// Options for <see cref="Game.RunHeadless"/>.
/// </summary>
public sealed class HeadlessRunOptions
{
    /// <summary>Stop after this many frames. 0 = run until quit (server mode).</summary>
    public long MaxFrames = 0;

    /// <summary>Stop after this many seconds of wall-clock time. 0 = no time limit.</summary>
    public double MaxSeconds = 0;

    /// <summary>Tick rate to throttle the loop to. 0 = run as fast as possible.</summary>
    public int TargetFps = 60;
}

/// <summary>
/// Clipboard handler that bridges Paper's clipboard interface to XEngine's Input system.
/// </summary>
internal class RuntimeClipboardHandler : PaperUI.IClipboardHandler
{
    public string GetClipboardText() => Input.Clipboard ?? "";
    public void SetClipboardText(string text) => Input.Clipboard = text;
}
