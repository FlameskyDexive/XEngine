// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#if XENGINE_WEB
using System;
using System.Threading;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Backends.WebGL2;
using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;

namespace XEngine.Runtime.Platforms.Browser;

public readonly record struct BrowserPlatformHostStats(
    long RenderedFrames,
    int SurfaceAttachCount,
    int SurfaceDetachCount,
    bool IsSurfaceAttached,
    bool IsResumed);

/// <summary>
/// Serial lifecycle boundary driven by the generated JS host. All methods must be called from
/// the wasm main thread (the rAF callback) so canvas present cannot race frame submission.
/// </summary>
public sealed class BrowserPlatformHost : IDisposable
{
    private const float DefaultDeltaTime = 1f / 60f;
    private const float MaxDeltaTime = 0.25f;
    /// <summary>
    /// Magenta so a successful canvas present cannot be mistaken for CSS <c>#000</c>.
    /// </summary>
    private static readonly Color s_clearColor = Color.Magenta;

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private IWindowSurface? _surface;
    private nint _nativeContext;
    private long _renderedFrames;
    private int _attachCount;
    private int _detachCount;
    private bool _loaded;
    private bool _resumed;
    private bool _disposed;
    private bool _selfHostCubeReady;
    private Mesh? _selfHostCubeMesh;
    private Material? _selfHostCubeMaterial;
    private Float4x4 _selfHostCubeWorld;
    private int _framebufferWidth = 1;
    private int _framebufferHeight = 1;
    private string _canvasSizeSource = "unset";

    /// <summary>Last attach backing-store size and how it was resolved (env / native / fallback).</summary>
    public (int Width, int Height, string Source) LastCanvasAttach =>
        (_framebufferWidth, _framebufferHeight, _canvasSizeSource);

    /// <summary>True after a self-host cube scene presented at least once (Game/Paper still off).</summary>
    public bool HasSelfHostCube => _selfHostCubeReady;

    public BrowserPlatformHostStats Stats => new(
        Interlocked.Read(ref _renderedFrames),
        _attachCount,
        _detachCount,
        _surface != null,
        _resumed);

    /// <summary>
    /// Attaches the canvas. When <paramref name="preferWebGpu"/> is true, tries emdawnwebgpu
    /// first and falls back to WebGL2. Douyin and WeChat-without-<c>getGPU</c> pass false.
    /// </summary>
    public void AttachCanvas(string canvasSelector, int width, int height, bool preferWebGpu = false)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surface != null)
            throw new InvalidOperationException("A canvas is already attached.");

        string selector = string.IsNullOrWhiteSpace(canvasSelector) ? "#canvas" : canvasSelector;
        if (preferWebGpu)
        {
            try
            {
                GraphicsBackendSelection.Preferred = GraphicsBackend.WebGPU;
                AttachSurface(new WebGPUCanvasSurface(selector, width, height), width, height);
                return;
            }
            catch (Exception ex)
            {
                string detail = ex.InnerException != null
                    ? ex.GetType().Name + ": " + ex.Message + " (" + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message + ")"
                    : ex.GetType().Name + ": " + ex.Message;
                Debug.LogWarning("WebGPU unavailable, falling back to WebGL2: " + detail);
                try { Graphics.Dispose(); } catch { /* ignore */ }
                GraphicsBackendSelection.Preferred = GraphicsBackend.WebGL2;
            }
        }

        GraphicsBackendSelection.Preferred = GraphicsBackend.WebGL2;
        WasmGlInterpTrampolines.Keep();
        nint context = WebGL2Native.CreateContext(selector);
        if (context == nint.Zero)
        {
            throw new InvalidOperationException(
                "emscripten failed to create a WebGL2 context on '" + selector +
                "'. The host must provide a canvas (document / wx.createCanvas / tt.createCanvas) and Module.canvas.");
        }

        _nativeContext = context;
        var surface = new WebGL2CanvasSurface(
            context,
            width,
            height,
            WebGL2Native.GetProcAddress,
            makeCurrent: () => WebGL2Native.MakeCurrent(context),
            enableDesktopSrgbFramebuffer: false)
        {
            HtmlCanvasSelector = selector,
        };
        AttachSurface(surface, width, height);
    }

    /// <summary>
    /// Mini-game self-host: resolve backing-store size without JSExport.
    /// Prefers <c>XENGINE_CANVAS_WIDTH/HEIGHT</c> (set from game.js before <c>runMain</c>)
    /// so a restored native wasm that lacks <c>canvas_width</c> exports still fills the phone.
    /// </summary>
    public void AttachDefaultCanvas(bool preferWebGpu = false)
    {
        ResolveDefaultCanvasSize(out int width, out int height, out string source);
        _framebufferWidth = width;
        _framebufferHeight = height;
        _canvasSizeSource = source;
        AttachCanvas("#canvas", width, height, preferWebGpu);
    }

    /// <summary>
    /// Starts a native rAF present loop. Does not use JSExport / <c>OnAnimationFrame</c>.
    /// Game/Paper stay unloaded. After the self-host cube presents, the native tick
    /// must not magenta-clear or it will wipe the draw.
    /// </summary>
    public void StartSelfHostFramePump()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            WebGL2Native.StartMainLoop();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("self-host frame pump skipped: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void ResolveDefaultCanvasSize(out int width, out int height, out string source)
    {
        if (TryReadEnvCanvasSize(out width, out height))
        {
            source = "env";
            return;
        }

        try
        {
            width = Math.Max(1, WebGL2Native.CanvasWidth());
            height = Math.Max(1, WebGL2Native.CanvasHeight());
            source = "native";
            return;
        }
        catch
        {
            width = 1;
            height = 1;
            source = "fallback";
        }
    }

    private static bool TryReadEnvCanvasSize(out int width, out int height)
    {
        width = 1;
        height = 1;
        string? w = Environment.GetEnvironmentVariable("XENGINE_CANVAS_WIDTH");
        string? h = Environment.GetEnvironmentVariable("XENGINE_CANVAS_HEIGHT");
        if (!int.TryParse(w, out int parsedW) || !int.TryParse(h, out int parsedH))
            return false;
        if (parsedW < 1 || parsedH < 1)
            return false;
        width = parsedW;
        height = parsedH;
        return true;
    }

    /// <summary>Test / host hook when the caller already owns a <see cref="WebGL2CanvasSurface"/>.</summary>
    public void AttachSurface(WebGL2CanvasSurface surface, int width, int height) =>
        AttachSurface((IWindowSurface)surface, width, height);

    /// <summary>Attaches a WebGPU or WebGL2 canvas surface and presents the bootstrap clear.</summary>
    public void AttachSurface(IWindowSurface surface, int width, int height)
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(surface);
        if (_surface != null)
            throw new InvalidOperationException("A canvas is already attached.");

        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        _framebufferWidth = w;
        _framebufferHeight = h;
        if (surface is WebGL2CanvasSurface webglSurface)
            webglSurface.UpdateSize(w, h);
        else if (surface is WebGPUCanvasSurface webgpuSurface)
            webgpuSurface.UpdateSize(w, h);
        BrowserInputRuntime.Ensure();
        BrowserAudioRuntime.Ensure();
        BrowserInputRuntime.Handler.SetFramebufferSize(w, h);
        Window.NotifySurfaceCreated(surface, w, h);
        try
        {
            if (Graphics.Device is { IsInitialized: true } device)
                device.Resize(w, h);
            else
                Graphics.Initialize(debug: false);

            if (Graphics.Device?.Capabilities.Baseline is { } baseline)
                Debug.Log($"[Graphics] capability-baseline {baseline.ToStableLogString()}");

            _surface = surface;
            _attachCount++;

            // Color-only CommandBuffer clear needs the VFFFF interp trampoline on WebGL2.
            // Incremental wasm publishes can skip recompiling runtime.c; the player csproj
            // deletes runtime.o after ManagedToNativeGenerator so that table is actually linked.
            Graphics.BeginFrame();
            try
            {
                SubmitBootstrapClear();
            }
            finally
            {
                Graphics.EndFrameAndWait();
            }

            // Paper / BuiltInAssets pull many more Silk.NET signatures. Keep Game off until
            // those cookies are in the linked trampoline table.
            _loaded = false;
        }
        catch
        {
            Window.NotifySurfaceDestroyed();
            _surface = null;
            throw;
        }
    }

    public void ResizeCanvas(int width, int height)
    {
        VerifyThread();
        if (_surface == null)
            return;
        int w = Math.Max(1, width);
        int h = Math.Max(1, height);
        if (_surface is WebGL2CanvasSurface webglSurface)
            webglSurface.UpdateSize(w, h);
        else if (_surface is WebGPUCanvasSurface webgpuSurface)
            webgpuSurface.UpdateSize(w, h);
        BrowserInputRuntime.Handler.SetFramebufferSize(w, h);
        Window.NotifySurfaceChanged(w, h);
        Graphics.Resize(w, h);
    }

    public void DetachCanvas()
    {
        VerifyThread();
        if (_surface == null)
            return;
        try
        {
            if (Graphics.Device is { IsInitialized: true } device)
                device.WaitIdle();
        }
        finally
        {
            Window.NotifySurfaceDestroyed();
            _surface = null;
            _nativeContext = nint.Zero;
            _detachCount++;
        }
    }

    public void Resume()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _resumed = true;
        BrowserAudioRuntime.Handler.Resume();
        Window.NotifyFocus(true);
    }

    public void Suspend()
    {
        VerifyThread();
        if (_disposed)
            return;
        _resumed = false;
        BrowserInputRuntime.Handler.FocusLost();
        BrowserAudioRuntime.Handler.Suspend();
        Window.NotifyFocus(false);
    }

    /// <summary>
    /// Mini-game W5: procedural camera + directional light + lit cube. Does not start Paper
    /// or set <c>_loaded</c> (full Game still waits on more Silk trampolines).
    /// </summary>
    public void TryLoadSelfHostCubeScene()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surface == null)
            throw new InvalidOperationException("Attach a canvas before loading the self-host cube scene.");

        try
        {
            Debug.Log("[XEngine] self-host cube 1 start");
            _selfHostCubeMesh = Mesh.CreateCube(Float3.One);
            Debug.Log("[XEngine] self-host cube 6 mesh");
            _selfHostCubeMaterial = CreateSelfHostCubeMaterial();
            Debug.Log("[XEngine] self-host cube 7 material");
            _selfHostCubeWorld = Float4x4.CreateTRS(
                Float3.Zero,
                Quaternion.AxisAngle(Float3.UnitY, 0.55f),
                Float3.One);
            PresentSelfHostCube();
            _selfHostCubeReady = true;
            Debug.Log("[XEngine] self-host cube 10 presented");
        }
        catch (Exception ex)
        {
            _selfHostCubeReady = false;
            Exception inner = ex.InnerException ?? ex;
            Debug.LogWarning(
                "[XEngine] self-host cube fail " + inner.GetType().Name + ": " + inner.Message);
            if (!string.IsNullOrEmpty(inner.StackTrace))
                Debug.LogWarning("[XEngine] self-host cube stack " + inner.StackTrace);
            throw;
        }
    }

    private static Material CreateSelfHostCubeMaterial()
    {
        const string source =
            """
            Shader "WeChat/CubeLit"
            Properties
            {
                _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
            }
            Pass "Lit"
            {
                Tags { "RenderOrder" = "Opaque" }
                Cull Back
                GLSLPROGRAM
                    Vertex
                    {
                        layout (location = 0) in vec3 vertexPosition;
                        layout (location = 3) in vec3 vertexNormal;
                        uniform mat4 _Mvp;
                        uniform mat4 _Model;
                        out vec3 vN;
                        void main()
                        {
                            gl_Position = _Mvp * vec4(vertexPosition, 1.0);
                            vN = mat3(_Model) * vertexNormal;
                        }
                    }
                    Fragment
                    {
                        layout (location = 0) out vec4 fragColor;
                        in vec3 vN;
                        uniform vec4 _MainColor;
                        void main()
                        {
                            vec3 n = normalize(vN);
                            vec3 l = normalize(vec3(0.35, 0.80, 0.45));
                            float ndl = max(dot(n, l), 0.18);
                            fragColor = vec4(_MainColor.rgb * ndl, 1.0);
                        }
                    }
                ENDGLSL
            }
            """;
        Debug.Log("[XEngine] self-host cube 7a parse-tiny");
        if (!ShaderParser.ParseShader("WeChatCube.shader", source, ResolveSelfHostInclude, out Shader? shader)
            || shader == null || shader.IsNotValid())
        {
            throw new InvalidOperationException("WeChat cube shader parse failed");
        }
        Debug.Log("[XEngine] self-host cube 7b parsed");
        var material = new Material(shader);
        material.SetColor("_MainColor", new Color(0.82f, 0.28f, 0.12f, 1f));
        return material;
    }

    private static string? ResolveSelfHostInclude(string path)
    {
        try
        {
            return EmbeddedResources.ReadAllText(path);
        }
        catch
        {
            try
            {
                return EmbeddedResources.ReadAllText("Assets/Defaults/" + path);
            }
            catch
            {
                return null;
            }
        }
    }

    public void PresentSelfHostCube()
    {
        VerifyThread();
        if (_disposed || _surface == null || _selfHostCubeMesh == null || _selfHostCubeMaterial == null)
            return;

        Float3 eye = new(0f, 0.85f, -3.2f);
        Float4x4 view = Float4x4.CreateLookAt(eye, Float3.Zero, Float3.UnitY);
        float aspect = _framebufferWidth / (float)Math.Max(1, _framebufferHeight);
        Float4x4 projection = Float4x4.CreatePerspectiveFov(60f * Maths.Deg2Rad, aspect, 0.1f, 100f);
        Float4x4 viewProj = projection * view;
        GlobalUniforms.SetMatrixV(view);
        GlobalUniforms.SetMatrixIV(view.Invert());
        GlobalUniforms.SetMatrixP(projection);
        GlobalUniforms.SetMatrixVP(viewProj);
        GlobalUniforms.SetMatrixIP(projection.Invert());
        GlobalUniforms.SetMatrixIVP(viewProj.Invert());
        GlobalUniforms.SetMatrixVPNonJittered(viewProj);
        GlobalUniforms.SetPrevViewProj(viewProj);
        GlobalUniforms.SetWorldSpaceCameraPos(eye);
        GlobalUniforms.SetProjectionParams(new Float4(1f, 0.1f, 100f, 0.01f));
        GlobalUniforms.SetScreenParams(new Float4(
            _framebufferWidth,
            _framebufferHeight,
            1f + 1f / _framebufferWidth,
            1f + 1f / _framebufferHeight));
        GlobalUniforms.Upload();
        _selfHostCubeMaterial.SetMatrix("_Mvp", viewProj * _selfHostCubeWorld);
        _selfHostCubeMaterial.SetMatrix("_Model", _selfHostCubeWorld);

        Graphics.BeginFrame();
        try
        {
            using CommandBuffer frame = Graphics.GetCommandBuffer("wechat-cube");
            frame.SetRenderTarget(null);
            frame.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0.08f, 0.10f, 0.14f, 1f));
            Debug.Log("[XEngine] self-host cube draw");
            frame.DrawMesh(_selfHostCubeMesh, _selfHostCubeMaterial, 0, _selfHostCubeWorld);
            Debug.Log("[XEngine] self-host cube encoded");
            Graphics.Submit(frame);
            Debug.Log("[XEngine] self-host cube submitted");
        }
        finally
        {
            Graphics.EndFrameAndWait();
        }
        Interlocked.Increment(ref _renderedFrames);
    }

    [HotPath]
    public void Tick(float deltaTime)
    {
        VerifyThread();
        try
        {
            if (_disposed || !_resumed || _surface == null)
                return;
            if (_selfHostCubeReady)
            {
                PresentSelfHostCube();
                return;
            }
            if (!_loaded)
                return;

            float dt = deltaTime;
            if (dt <= 0f)
                dt = DefaultDeltaTime;
            else if (dt > MaxDeltaTime)
                dt = MaxDeltaTime;

            bool frameBegun = false;
            try
            {
                Graphics.BeginFrame();
                frameBegun = true;
                Window.NotifyFrame(dt);
            }
            finally
            {
                if (frameBegun)
                    Graphics.EndFrameAndWait();
            }
            Interlocked.Increment(ref _renderedFrames);
        }
        finally
        {
            BrowserInputRuntime.Handler.EndFrame();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        BrowserAudioRuntime.Handler.Suspend();
        if (_surface != null)
            DetachCanvas();
        Window.NotifyClosing();
    }

    private void SubmitBootstrapClear()
    {
        using CommandBuffer frame = Graphics.GetCommandBuffer("canvas-bootstrap");
        frame.SetRenderTarget(null);
        frame.ClearRenderTarget(ClearFlags.Color, s_clearColor);
        Graphics.Submit(frame);
    }

    private void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                "BrowserPlatformHost must be driven from the wasm main / requestAnimationFrame thread.");
        }
    }
}
#endif
