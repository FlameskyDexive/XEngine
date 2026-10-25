// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Silk.NET.OpenGL;

using XEngine.Runtime.Backends.OpenGL;
using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Backends.WebGL2;

/// <summary>
/// Same-thread WebGL2 / OpenGL ES 3.0 device. Desktop hosts a Core 4.1 context and
/// reuses <see cref="OpenGLCommandExecutor"/>; browser / mini-game canvas surfaces
/// supply an emscripten (or test) <c>getProcAddress</c> and keep <c>#version 300 es</c>.
/// </summary>
public sealed class WebGL2GraphicsDevice : IGraphicsDevice
{
    private readonly GraphicsDeviceOptions _options;
    private readonly object _gate = new();
    private IWindowSurface? _surface;
    private GL? _gl;
    private OpenGLCommandExecutor? _executor;
    private GraphicsDeviceCapabilities _capabilities = new()
    {
        BackendName = "WebGL2",
        MaxFramesInFlight = 1,
        SupportsCompute = false,
        SupportsGeometryShader = false,
    };
    private bool _initialized;
    private bool _shutdown;
    private bool _frameBegun;
    private ulong _fenceValue;

    public WebGL2GraphicsDevice(GraphicsDeviceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public GraphicsBackend Backend => GraphicsBackend.WebGL2;

    public GraphicsDeviceCapabilities Capabilities => _capabilities;

    public bool IsInitialized => _initialized;

    public void Initialize(IWindowSurface? surface)
    {
        lock (_gate)
        {
            if (_shutdown)
                throw new ObjectDisposedException(nameof(WebGL2GraphicsDevice));
            if (_initialized)
                return;

            GL gl;
            bool enableDesktopSrgb;
            string backendName;
            ResolveContext(surface, out gl, out enableDesktopSrgb, out backendName);

            _surface = surface;
            _surface!.MakeCurrent();
            _gl = gl;
            _executor = new OpenGLCommandExecutor(_gl);

            // Match the desktop OpenGL present path: Vulkan/D3D12 swapchains are sRGB.
            // WebGL2 itself has no GL_FRAMEBUFFER_SRGB; this only applies to the desktop host.
            if (enableDesktopSrgb)
                _gl.Enable((EnableCap)0x8DB9);

            _capabilities = new GraphicsDeviceCapabilities
            {
                MaxTextureSize = _gl.GetInteger(GLEnum.MaxTextureSize),
                MaxCubeMapTextureSize = _gl.GetInteger(GLEnum.MaxCubeMapTextureSize),
                MaxArrayTextureLayers = _gl.GetInteger(GLEnum.MaxArrayTextureLayers),
                MaxFramebufferColorAttachments = _gl.GetInteger(GLEnum.MaxColorAttachments),
                MaxFramesInFlight = 1,
                SupportsCompute = false,
                SupportsGeometryShader = false,
                HardwareSupportsTimestampQueries = false,
                BackendName = backendName,
            };
            _initialized = true;
        }
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            if (_shutdown)
                return;
            _shutdown = true;
            try { _surface?.MakeCurrent(); } catch { /* context may already be gone */ }
            _gl?.Dispose();
            _gl = null;
            _executor = null;
            _surface = null;
            _initialized = false;
            _frameBegun = false;
        }
    }

    public void BeginFrame()
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            if (_frameBegun)
                throw new InvalidOperationException("WebGL2 BeginFrame was called while a frame is already in progress.");
            _surface!.MakeCurrent();
            _frameBegun = true;
        }
    }

    public void EndFrame()
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            if (!_frameBegun)
                throw new InvalidOperationException("WebGL2 EndFrame was called without BeginFrame.");
            try { _surface!.SwapBuffers(); }
            catch (Exception ex) { Debug.LogError($"WebGL2 SwapBuffers failed: {ex}"); }
            _frameBegun = false;
            _fenceValue++;
        }
    }

    public void Resize(int width, int height)
    {
        if (_surface is WebGL2CanvasSurface canvas)
            canvas.UpdateSize(Math.Max(1, width), Math.Max(1, height));
    }

    public void Execute(CommandBuffer commandBuffer, bool wait)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        if (commandBuffer._inPool)
            throw new InvalidOperationException("CommandBuffer has already been submitted (it's in the pool).");

        commandBuffer._submitted = true;
        commandBuffer._ownerReleased = true;
        try
        {
            lock (_gate)
            {
                EnsureReady_NoLock();
                _surface!.MakeCurrent();
                (_executor ?? throw new InvalidOperationException(
                    "WebGL2 command execution requires an initialized graphics device."))
                    .Execute(commandBuffer);
                if (wait)
                    _gl?.Finish();
                _fenceValue++;
            }
        }
        finally
        {
            CommandBufferPool.Return(commandBuffer);
        }
    }

    public void WaitIdle()
    {
        lock (_gate)
        {
            if (!_initialized || _shutdown)
                return;
            _surface?.MakeCurrent();
            _gl?.Finish();
        }
    }

    public ulong GetFenceValue() => _fenceValue;

    public void WaitFence(ulong fenceValue)
    {
        if (fenceValue > _fenceValue)
            WaitIdle();
    }

    public void Dispose() => Shutdown();

    private static void ResolveContext(
        IWindowSurface? surface,
        out GL gl,
        out bool enableDesktopSrgb,
        out string backendName)
    {
        if (surface is WebGL2CanvasSurface canvas)
        {
            canvas.MakeCurrent();
            gl = GL.GetApi(canvas);
            enableDesktopSrgb = canvas.EnableDesktopSrgbFramebuffer;
            backendName = enableDesktopSrgb ? "WebGL2 (desktop GL 4.1)" : "WebGL2";
            return;
        }

#if !XENGINE_WEB
        if (surface is SilkWindowSurface silk)
        {
            surface.MakeCurrent();
            gl = GL.GetApi(silk.Window);
            enableDesktopSrgb = true;
            backendName = "WebGL2 (desktop GL 4.1)";
            return;
        }
#endif

        throw new InvalidOperationException(
            "WebGL2GraphicsDevice.Initialize requires a WebGL2CanvasSurface (canvas / emscripten GL loader)" +
#if !XENGINE_WEB
            " or a SilkWindowSurface (desktop OpenGL 4.1)" +
#endif
            ".");
    }

    private void EnsureReady_NoLock()
    {
        if (_shutdown)
            throw new ObjectDisposedException(nameof(WebGL2GraphicsDevice));
        if (!_initialized)
            throw new InvalidOperationException("WebGL2GraphicsDevice has not been initialized.");
    }
}
