// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;
using System.Threading;

using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace XEngine.Runtime;

public static class Window
{
    static Window()
    {
        // Silk windowing platforms self-register via a module initializer when their assembly
        // loads. Player builds trim Silk.NET.Windowing.Glfw out of the startup closure (nothing
        // references it before Window.Create), so the initializer never runs and Window.Create
        // throws PlatformNotSupportedException. Force the assembly to load here.
        try
        {
            _ = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(
                new System.Reflection.AssemblyName("Silk.NET.Windowing.Glfw"));
        }
        catch (Exception) { /* non-GLFW platform build (mobile/browser) */ }
    }

    public static IWindow InternalWindow { get; internal set; }
    public static IInputContext InternalInput { get; internal set; }
    public static long PresentCount { get; private set; }

    /// <summary>
    /// Per-present cadence telemetry, measured inside the render branch only — so idle sleeps and
    /// skipped presents never inflate it (unlike <c>Time.UnscaledDeltaTime</c>, which spans whole
    /// idle periods). Exposes two honest numbers for display:
    /// <list type="bullet">
    /// <item><b>PresentFps</b> — presents over the trailing second = the rate the editor is
    /// actually delivering right now (lower under on-demand throttling, by design).</item>
    /// <item><b>SustainedFps</b> — EMA over back-to-back rendered frames = the rate the pipeline
    /// achieves when it IS rendering continuously (the "capability" number).</item>
    /// </list>
    /// A gap &gt; <see cref="StreamBreakMs"/> between presents is treated as a stream break and
    /// restarts the sustained EMA instead of poisoning it with an idle-inflated delta.
    /// </summary>
    public static class PresentStats
    {
        internal const int RingSize = 240;
        /// <summary>Deltas above this are stream breaks (idle/user pause), not render cost.</summary>
        internal const double StreamBreakMs = 250.0;
        private const double EmaAlpha = 0.1;

        private static readonly double[] s_ring = new double[RingSize];
        private static int s_ringIndex;
        private static int s_ringCount;
        private static long s_lastPresentTick;
        private static double s_sustainedEmaMs;
        private static double s_lastCpuMs;

        public static int RingCount => s_ringCount;
        /// <summary>Present-to-present delta in ms; 0 = newest, larger = older.</summary>
        public static double GetDelta(int ageFromNewest)
        {
            if (ageFromNewest < 0 || ageFromNewest >= s_ringCount) return 0;
            int idx = ((s_ringIndex - 1 - ageFromNewest) % RingSize + RingSize) % RingSize;
            return s_ring[idx];
        }
        /// <summary>EMA of consecutive-render deltas (ms); 0 until a stream exists.</summary>
        public static double SustainedEmaMs => s_sustainedEmaMs;
        public static double SustainedFps => s_sustainedEmaMs > 0 ? 1000.0 / s_sustainedEmaMs : 0;
        /// <summary>Last frame's CPU encode cost (Update-end → EndFrameAndWait), in ms.</summary>
        public static double LastCpuMs => s_lastCpuMs;

        /// <summary>Presents whose occurrence time falls inside the trailing second.</summary>
        public static double PresentFps
        {
            get
            {
                int count = 0;
                double ago = 0; // how long ago this present happened (sum of newer deltas)
                for (int age = 0; age < s_ringCount; age++)
                {
                    if (ago > 1000.0) break;
                    count++;
                    ago += GetDelta(age);
                }
                return count;
            }
        }

        /// <summary>Pure EMA step: a stream break restarts the estimate instead of polluting it.</summary>
        internal static double NextEma(double emaMs, double deltaMs)
            => deltaMs > StreamBreakMs || emaMs <= 0 ? deltaMs : emaMs + EmaAlpha * (deltaMs - emaMs);

        /// <summary>Record one presented frame. <paramref name="cpuMs"/> = CPU encode cost.</summary>
        internal static void Record(long nowTick, double freq, double cpuMs)
        {
            double deltaMs = 0;
            if (s_lastPresentTick != 0 && freq > 0)
                deltaMs = (nowTick - s_lastPresentTick) * 1000.0 / freq;
            s_lastPresentTick = nowTick;
            s_lastCpuMs = cpuMs;
            s_ring[s_ringIndex] = deltaMs;
            s_ringIndex = (s_ringIndex + 1) % RingSize;
            if (s_ringCount < RingSize) s_ringCount++;
            if (deltaMs > 0)
                s_sustainedEmaMs = NextEma(s_sustainedEmaMs, deltaMs);
        }

        internal static void Reset()
        {
            Array.Clear(s_ring);
            s_ringIndex = s_ringCount = 0;
            s_lastPresentTick = 0;
            s_sustainedEmaMs = 0;
            s_lastCpuMs = 0;
        }

        // Test seams (internal): pure-function and state injection without Stopwatch coupling.
        internal static double PresentStatsNextEmaForTests(double emaMs, double deltaMs) => NextEma(emaMs, deltaMs);
        internal static void PresentStatsRecordForTests(long nowTick, double freq, double cpuMs) => Record(nowTick, freq, cpuMs);
        internal static void PresentStatsResetForTests() => Reset();
        internal static int RingCountForTests() => RingCount;
        internal static double DeltaForTests(int age) => GetDelta(age);
        internal static double PresentFpsForTests() => PresentFps;
        internal static double CpuMsForTests() => s_lastCpuMs;
    }

    private static WindowSafeAreaInsets s_safeAreaInsets;

    // --- Per-monitor DPI awareness on Windows ----------------------------------------------
    // [DllImport("user32.dll")]
    // private static extern bool SetProcessDpiAwarenessContext(nint dpiContext);

    // [DllImport("shcore.dll")]
    // private static extern int SetProcessDpiAwareness(int level);

    // [DllImport("user32.dll")]
    // private static extern bool SetProcessDPIAware();

    // private static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // private static void EnsureDpiAwareOnWindows()
    // {
    //     if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
    //     try { if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return; } catch { }
    //     try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }
    //     try { SetProcessDPIAware(); } catch { }
    // }

    public static event Action? Load;
    public static event Action<float>? Update;
    public static event Action<float>? Render;
    public static event Action<float>? PostRender;

    /// <summary>
    /// Invokes the Render chain from outside the main loop (e.g. an import progress batch pumping
    /// a modal repaint between entries). Full frame: BeginFrame -> Render -> PostRender -> EndFrame.
    /// </summary>
    /// <summary>True while a nested RenderOnce pump (import progress) is executing. The launcher
    /// UI must not be rebuilt during it — the same-frame rebuild is what threw
    /// "Element already exists: pl_window" and failed the project open.</summary>
    public static bool InPumpRender { get; private set; }

    /// <summary>True while a nested PumpOsMessages call is executing.</summary>
    public static bool InOsPump { get; private set; }

    /// <summary>
    /// Pumps the OS message queue from inside a long synchronous section (e.g. the script-reload
    /// GC loop) so the window keeps answering move/resize/paint instead of Windows flagging it
    /// "not responding". Renders nothing — repaint still needs the frame loop (or RenderOnce);
    /// this only keeps the queue drained. Never call while a graphics frame is being recorded.
    /// </summary>
    public static void PumpOsMessages()
    {
        if (InternalWindow == null || InternalWindow.IsClosing || InOsPump) return;
        InOsPump = true;
        try
        {
            InternalWindow.DoEvents();
        }
        finally
        {
            InOsPump = false;
        }
    }

    public static void RenderOnce(float delta)
    {
        if (InternalWindow == null || InternalWindow.IsClosing) return;
        if (InPumpRender) return; // a deeper pump is already rendering this batch
        InPumpRender = true;
        try
        {
            Graphics.BeginFrame();
            Render?.Invoke(delta);
            PostRender?.Invoke(delta);
            Graphics.EndFrameAndWait();
        }
        finally
        {
            InPumpRender = false;
        }
    }
    /// <summary>Raised after the primary presentation frame completes.</summary>
    public static event Action<float>? PostPresent;
    /// <summary>
    /// Optional per-tick render gate. A false result skips graphics frame creation, command
    /// recording, and presentation while update/input processing continue.
    /// </summary>
    public static Func<bool>? RenderRequested { get; set; }
    public static event Action<bool>? FocusChanged;
    public static event Action<Vector2D<int>>? Resize;
    public static event Action<Vector2D<int>>? FramebufferResize;
    /// <summary>Raised when the usable framebuffer rectangle changes because of resize or platform insets.</summary>
    public static event Action<WindowSafeArea>? SafeAreaChanged;
    public static event Action? Closing;

    public static event Action<Vector2D<int>>? Move;
    public static event Action<WindowState>? StateChanged;
    public static event Action<string[]>? FileDrop;

    /// <summary>Insets reserved by system UI, in physical framebuffer pixels.</summary>
    public static WindowSafeAreaInsets SafeAreaInsets => s_safeAreaInsets;

    /// <summary>The visible, unobscured rectangle in physical framebuffer pixels.</summary>
    public static WindowSafeArea SafeArea
    {
        get
        {
            if (InternalWindow == null)
                return default;
            var framebuffer = InternalWindow.FramebufferSize;
            return s_safeAreaInsets.ToSafeArea(framebuffer.X, framebuffer.Y);
        }
    }

    private static nint s_contentScaleProc;
    private static bool s_contentScaleResolved;

    /// <summary>
    /// Physical-to-logical pixel ratio for the window's current display.
    /// <para>
    /// On Windows, resolves <c>glfwGetWindowContentScale</c> via the native GLFW context
    /// to capture the system DPI factor (e.g. 1.25 at 125%). This is needed because
    /// <c>FramebufferSize / Size</c> only reflects the ratio when the process is
    /// per-monitor DPI-aware; if DPI awareness failed at startup the OS virtualises
    /// the framebuffer (fb == win) and the ratio collapses to 1.
    /// </para>
    /// <para>
    /// On macOS and Linux the GLFW proc address is not reachable via this path, so we
    /// fall back to <c>FramebufferSize / Size</c> which is always correct on those
    /// platforms (Retina: 3024 fb / 1512 points = 2; 1× monitor: 1).
    /// </para>
    /// </summary>
    public static unsafe float ContentScale
    {
        get
        {
            if (InternalWindow == null) return 1f;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                nint? nativeGlfw = InternalWindow.Native?.Glfw;
                if (nativeGlfw.HasValue && nativeGlfw.Value != 0 && TryGetContentScaleViaProc(nativeGlfw.Value, out float scale))
                    return scale;
            }

            var fb = InternalWindow.FramebufferSize;
            var win = InternalWindow.Size;
            return win.X > 0 ? (float)fb.X / win.X : 1f;
        }
    }

    private static unsafe bool TryGetContentScaleViaProc(nint glfwWindow, out float scale)
    {
        scale = 1f;

        if (!s_contentScaleResolved)
        {
            s_contentScaleResolved = true;
            try
            {
                s_contentScaleProc = Silk.NET.GLFW.GlfwProvider.GLFW.Value.Context.GetProcAddress("glfwGetWindowContentScale");
            }
            catch { }
        }

        if (s_contentScaleProc == 0) return false;

        float x, y;
        ((delegate* unmanaged[Cdecl]<nint, float*, float*, void>)s_contentScaleProc)(glfwWindow, &x, &y);
        if (x <= 0) return false;
        scale = x;
        return true;
    }

    public static Vector2D<int> Position
    {
        get { return InternalWindow.Position; }
        set { InternalWindow.Position = value; }
    }

    public static Vector2D<int> Size
    {
        get { return InternalWindow.Size; }
        set { InternalWindow.Size = value; }
    }

    public static bool IsVisible
    {
        get { return InternalWindow.IsVisible; }
        set { InternalWindow.IsVisible = value; }
    }

    public static bool VSync
    {
        get { return InternalWindow.VSync; }
        set { InternalWindow.VSync = !Application.IsEditor && value; }
    }

    public static float FramesPerSecond
    {
        get { return (float)InternalWindow.FramesPerSecond; }
        set { InternalWindow.FramesPerSecond = value; InternalWindow.UpdatesPerSecond = value; }
    }

    public static nint Handle
    {
        get { return InternalWindow.Handle; }
    }

    private static bool isFocused = true;
    private static DefaultInputHandler WindowInputHandler;

    public static bool IsFocused
    {
        get { return isFocused; }
    }

    public static void InitWindow(string title, int width, int height, WindowState startState = WindowState.Normal, bool VSync = true)
    {
        WindowOptions options = WindowOptions.Default;
        options.Title = title;
        options.Size = new Vector2D<int>(width, height);
        options.WindowState = startState;
        options.VSync = VSync;
        options.API = ResolveGraphicsAPI();
        // Update / Render are driven manually from MainLoop SwapBuffers happens
        // on the render thread.
        options.ShouldSwapAutomatically = false;
        InternalWindow = Silk.NET.Windowing.Window.Create(options);

        InternalWindow.Load += OnLoad;
        InternalWindow.Resize += OnResize;
        InternalWindow.FramebufferResize += OnFramebufferResize;
        InternalWindow.Move += OnMove;
        InternalWindow.StateChanged += (state) => StateChanged?.Invoke(state);
        InternalWindow.FileDrop += (files) => FileDrop?.Invoke(files);
        InternalWindow.FocusChanged += (focused) =>
        {
            isFocused = focused;
            FocusChanged?.Invoke(focused);
        };
    }

    /// <summary>
    /// Silk window context API for the preferred backend. Vulkan/D3D12 use
    /// <see cref="ContextAPI.None"/> so the host creates the surface/device explicitly.
    /// </summary>
    private static GraphicsAPI ResolveGraphicsAPI()
    {
        RHI.GraphicsBackend backend = RHI.GraphicsBackendSelection.Preferred
            ?? RHI.GraphicsBackendSelection.Parse(Environment.GetCommandLineArgs());

        backend = RHI.GraphicsBackendSelection.ResolveWindowedAuto(backend);

        // Only forced DXC runtime compilation needs the legacy SPIR-V probe. Auto/Slang use Slang,
        // while packaged Players load offline artifacts and do not carry either runtime compiler.
        if (backend == RHI.GraphicsBackend.Vulkan &&
            !RHI.Shaders.ShaderArtifactRuntimeLibrary.IsConfigured &&
            RHI.Shaders.ShaderCompilerFactory.GetConfiguredMode() == RHI.Shaders.ShaderCompilerMode.Dxc &&
            !RHI.Shaders.DxcShaderCompiler.CanCompileSpirv())
        {
            Debug.LogWarning(
                "Vulkan backend selected but DXC cannot emit SPIR-V (Vulkan SDK not installed or "
                + "dxc.exe lacks SPIR-V CodeGen). Falling back to Direct3D 12. "
                + "Install the Vulkan SDK to use the Vulkan backend.");
            backend = OperatingSystem.IsWindows()
                ? RHI.GraphicsBackend.Direct3D12
                : RHI.GraphicsBackend.OpenGL;
        }

        return backend switch
        {
            RHI.GraphicsBackend.OpenGL or RHI.GraphicsBackend.WebGL2 => new GraphicsAPI(
                ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 1)),
            RHI.GraphicsBackend.Vulkan => new GraphicsAPI(
                // GLFW needs its own view of the Vulkan loader for the Vulkan client-API flag
                // (glfwVulkanSupported + glfwCreateWindowSurface). On macOS the loader lives in
                // Homebrew paths that dyld does not search by default, so the editor host
                // re-execs with DYLD_FALLBACK_LIBRARY_PATH pointing there (see Program.Main).
                ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(1, 2)),
            RHI.GraphicsBackend.Direct3D12 => new GraphicsAPI(
                ContextAPI.None, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(12, 0)),
            _ => GraphicsAPI.None,
        };
    }

    private static void OnMove(Vector2D<int> d) => Move?.Invoke(d);

    public static void Start()
    {
        PresentCount = 0;
        // P7-A: D3D12/Vulkan translate + submit move to a dedicated render thread so the main
        // thread only encodes byte-stream command buffers. Must be decided before the window's
        // Load event runs Graphics.Initialize (which creates the device). --render-thread=off
        // restores the historical synchronous path for A/B measurement and bisecting.
        Graphics.UseDedicatedRenderThread = Graphics.ResolveRenderThreadPreference(fallback: true);
        InternalWindow.Initialize();
        // Silk.NET's automatic Render path (which would apply options.VSync) doesn't
        // run under our manual loop, so apply the swap interval directly.
        InternalWindow.GLContext?.SwapInterval(InternalWindow.VSync ? 1 : 0);
        Graphics.StartRenderThread();

        // Load runs as a warmup frame so SubmitAndWait (shader compiles, FBO checks)
        // has a live render thread to drain its CBs; otherwise it would deadlock.
        Graphics.BeginFrame();
        Load?.Invoke();
        Graphics.EndFrameAndWait();
        PostPresent?.Invoke(0f);

        try { MainLoop(); }
        finally
        {
            OnClose();
            InternalWindow.Reset();
        }
    }

    private static void MainLoop()
    {
        long lastTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long freq = System.Diagnostics.Stopwatch.Frequency;
        while (!InternalWindow.IsClosing)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            float delta = (float)((now - lastTicks) / (double)freq);
            lastTicks = now;

            InternalWindow.DoEvents();
            Update?.Invoke(delta);
            WindowInputHandler?.LateUpdate();

            if (ShouldRenderCurrentFrame())
            {
                long frameStartTick = System.Diagnostics.Stopwatch.GetTimestamp();
                Graphics.BeginFrame();
                Render?.Invoke(delta);
                PostRender?.Invoke(delta);

                // SwapBuffers runs on the render thread as part of the frame-end
                // sentinel no context handoff per frame.
                long encodeDoneTick = System.Diagnostics.Stopwatch.GetTimestamp();
                Graphics.EndFrameAndWait();
                PresentCount++;
                double stopFreq = System.Diagnostics.Stopwatch.Frequency;
                PresentStats.Record(System.Diagnostics.Stopwatch.GetTimestamp(), stopFreq,
                    (encodeDoneTick - frameStartTick) * 1000.0 / stopFreq);
            }
            else
            {
                // Skip-present used to busy-spin Update+DoEvents at tens of kHz. Unity
                // waits on the message pump; we cap idle ticks so the editor does not
                // cook the CPU (and hitch) with a handful of dock panels open.
                int waitMs = IdleWaitMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - now, freq, IdleLoopTargetHz);
                PumpIdleWait(waitMs);
                IdleStatsTick();
            }

            PostPresent?.Invoke(delta);
            int playerWaitMs = CalculatePlayerFrameWaitMilliseconds(Application.IsEditor,
                Application.TargetFrameRate,
                (System.Diagnostics.Stopwatch.GetTimestamp() - now) * 1000.0 / freq);
            while (playerWaitMs > 0 && !InternalWindow.IsClosing)
            {
                PumpIdleWait(playerWaitMs);
                // Input can wake the pump early, but must not bypass the player's cap.
                playerWaitMs = CalculatePlayerFrameWaitMilliseconds(Application.IsEditor,
                    Application.TargetFrameRate,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - now) * 1000.0 / freq);
            }
        }
    }

    internal static int CalculatePlayerFrameWaitMilliseconds(bool isEditor, int targetFrameRate, double elapsedMs)
        => isEditor || targetFrameRate <= 0 ? 0
            : (int)Math.Max(0, Math.Ceiling(1000.0 / targetFrameRate - elapsedMs));

    /// <summary>Idle loop target when a retained UI host skips present. 60 Hz is enough for input and watchers.</summary>
    public const double IdleLoopHz = 60.0;

    /// <summary>
    /// Live idle-loop target. Hosts may lower this when they are truly idle (no rendering, no
    /// simulation) to cut idle CPU like Unity's editor does. Input still wakes immediately on
    /// Windows because the idle pump waits on the message queue, not a blind sleep.
    /// </summary>
    public static double IdleLoopTargetHz { get; set; } = IdleLoopHz;

    /// <summary>Max sleep slice while idle so <c>DoEvents</c> can wake the HWND on a click.</summary>
    public const int IdlePumpSliceMilliseconds = 4;

    /// <summary>One sleep slice for an idle wait budget. Extracted so tests do not need a live window.</summary>
    public static int IdlePumpSlice(int remainingMs)
        => remainingMs <= 0 ? 0 : Math.Min(remainingMs, IdlePumpSliceMilliseconds);

    private const uint QsAllInput = 0x04FF;
    private const uint QsKey = 0x0001;
    private const uint QsMouseMove = 0x0002;
    private const uint QsMouseButton = 0x0004;
    private const uint QsUserInput = QsKey | QsMouseMove | QsMouseButton;
    private const uint WaitTimeout = 0x102;

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(
        uint count, nint[]? handles, bool waitAll, uint milliseconds, uint wakeMask);

    [DllImport("user32.dll")]
    private static extern uint GetQueueStatus(uint flags);

    /// <summary>
    /// Whether an idle-pump wake should hand control back to the main loop instead of waiting out
    /// its budget. Only pending user input (mouse/keyboard) qualifies: those transitions must reach
    /// <c>LateUpdate</c> while the button is still held, or a click pressed and released inside one
    /// pump window coalesces into nothing. Paint/timer/posted wakes keep pumping here.
    /// </summary>
    internal static bool ShouldExitIdlePump(uint waitResult, uint pendingInput)
        => waitResult != WaitTimeout && (pendingInput & QsUserInput) != 0;

    /// <summary>
    /// Wait for the idle remainder without parking the UI thread on <c>Thread.Sleep</c>.
    /// Sleep showed as <c>Wait/ExecutionDelay</c> and missed the click that should wake
    /// an unfocused editor. Windows waits on the message queue; other platforms slice Sleep.
    /// </summary>
    internal static void PumpIdleWait(int waitMs)
    {
        if (waitMs <= 0) return;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp()
            + waitMs * System.Diagnostics.Stopwatch.Frequency / 1000;
        while (System.Diagnostics.Stopwatch.GetTimestamp() < deadline && !InternalWindow.IsClosing)
        {
            InternalWindow.DoEvents();
            int left = (int)((deadline - System.Diagnostics.Stopwatch.GetTimestamp())
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            if (left <= 0) break;
            int slice = IdlePumpSlice(left);
            if (OperatingSystem.IsWindows())
            {
                uint result = MsgWaitForMultipleObjects(0, null, false, (uint)slice, QsAllInput);
                if (ShouldExitIdlePump(result, GetQueueStatus(QsUserInput)))
                    break;
            }
            else
                Thread.Sleep(slice);
        }
        LogIdlePumpOvershoot(start, waitMs);
    }

    private static long s_lastIdleOvershootLogTicks;

    /// <summary>
    /// A single pump is budgeted to <paramref name="waitMs"/>; every iteration inside it must
    /// finish in milliseconds. If wall-clock massively overruns the budget, one <c>DoEvents</c>
    /// never returned — a nested OS modal loop or blocking native call parked the main thread
    /// while the window keeps answering messages, which reads as "editor frozen". Log it once
    /// per 30s so the freeze localizes to windowing instead of renderer/simulation.
    /// </summary>
    private static void LogIdlePumpOvershoot(long startTicks, int budgetMs)
    {
        double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedMs < Math.Max(budgetMs * 4.0, 250.0)) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (s_lastIdleOvershootLogTicks != 0
            && (now - s_lastIdleOvershootLogTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < 30000)
            return;
        s_lastIdleOvershootLogTicks = now;
        Debug.LogWarning(
            $"[IdlePump] overshoot {elapsedMs:F0}ms vs {budgetMs}ms budget — main thread stalled inside DoEvents "
            + "(nested modal loop or blocking driver/windowing call). Editor appears frozen while message-only responsive.");
    }

    /// <summary>
    /// Optional idle diagnostics: set XENGINE_IDLE_STATS=1 to log the idle tick rate once per
    /// 5 seconds while the surface is retained (verifies the idle throttle end-to-end).
    /// Zero cost when disabled.
    /// </summary>
    private static readonly bool s_idleStats = Environment.GetEnvironmentVariable("XENGINE_IDLE_STATS") == "1";
    private static long s_idleStatTicks;
    private static long s_idleStatStart = System.Diagnostics.Stopwatch.GetTimestamp();

    private static void IdleStatsTick()
    {
        if (!s_idleStats) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        s_idleStatTicks++;
        double seconds = (now - s_idleStatStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (seconds >= 5.0)
        {
            Debug.Log($"[IdleStats] ticks={s_idleStatTicks} over {seconds:F1}s = {s_idleStatTicks / seconds:F1} Hz (target {IdleLoopTargetHz:F0} Hz)");
            s_idleStatTicks = 0;
            s_idleStatStart = now;
        }
    }

    /// <summary>Milliseconds to sleep so an idle tick stays near <paramref name="idleHz"/>.</summary>
    public static int IdleWaitMilliseconds(long elapsedTicks, long frequency, double idleHz = IdleLoopHz)
    {
        if (elapsedTicks <= 0 || frequency <= 0 || idleHz <= 0)
            return 0;
        double elapsedMs = elapsedTicks * 1000.0 / frequency;
        int sleepMs = (int)(1000.0 / idleHz - elapsedMs);
        return sleepMs > 0 ? sleepMs : 0;
    }

    internal static bool ShouldRenderCurrentFrame() => RenderRequested?.Invoke() != false;

    public static void Stop() => InternalWindow.Close();

    public static void OnLoad()
    {
        InternalInput = InternalWindow.CreateInput();
        WindowInputHandler = new DefaultInputHandler(InternalInput);
        Graphics.Initialize(false);
        Input.PushHandler(WindowInputHandler);
        ApplyEditorIcon(InternalWindow);
    }

    /// <summary>Apply the embedded XEngine icon pack to a native window (main or detached).</summary>
    public static void ApplyEditorIcon(IWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Silk.NET.Core.RawImage[]? icons = LoadEditorIcons();
        if (icons == null || icons.Length == 0) return;
        try { window.SetWindowIcon(icons); }
        catch { /* platform or GLFW may not accept custom icons */ }
    }

    private static Silk.NET.Core.RawImage[]? s_editorIcons;

    private static Silk.NET.Core.RawImage[]? LoadEditorIcons()
    {
        if (s_editorIcons != null) return s_editorIcons;
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            string? name = System.Array.Find(asm?.GetManifestResourceNames() ?? System.Array.Empty<string>(),
                n => n.EndsWith(".xengine-icon.rgba", StringComparison.Ordinal));
            if (name == null) return null;

            using var stream = asm!.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var br = new System.IO.BinaryReader(stream);

            int count = br.ReadInt32();
            var icons = new Silk.NET.Core.RawImage[count];
            for (int i = 0; i < count; i++)
            {
                int w = br.ReadInt32();
                int h = br.ReadInt32();
                byte[] pixels = br.ReadBytes(w * h * 4);
                icons[i] = new Silk.NET.Core.RawImage(w, h, pixels);
            }
            s_editorIcons = icons;
            return s_editorIcons;
        }
        catch
        {
            return null;
        }
    }

    public static void OnResize(Vector2D<int> size) => Resize?.Invoke(size);
    public static void OnFramebufferResize(Vector2D<int> size)
    {
        Graphics.Resize(size.X, size.Y);
        FramebufferResize?.Invoke(size);
        SafeAreaChanged?.Invoke(SafeArea);
    }

    internal static void NotifySafeAreaInsetsChanged(WindowSafeAreaInsets insets)
    {
        if (s_safeAreaInsets == insets)
            return;
        s_safeAreaInsets = insets;
        SafeAreaChanged?.Invoke(SafeArea);
    }

    public static void OnClose()
    {
        // Stop background asset loading first so no load runs during teardown (it would
        // otherwise race scene unload and try to submit GPU work after the render thread exits).
        AssetLoader.Stop();
        Closing?.Invoke();
        WindowInputHandler.Dispose();
        Graphics.Dispose();
    }
}
