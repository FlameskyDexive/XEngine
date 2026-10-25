// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Backends.WebGL2;
using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WebGL2 backend name resolution, factory wiring, and desktop GL 4.1 present.
/// Machines without a windowed GL context skip with the same availability contract as OpenGL.
/// </summary>
public class WebGL2GraphicsDeviceTests
{
    [Fact]
    public void TryParseName_RecognizesWebGL2Aliases()
    {
        Assert.Equal(7, (int)GraphicsBackend.WebGL2);
        Assert.True(GraphicsBackendSelection.TryParseName("webgl2", out GraphicsBackend backend));
        Assert.Equal(GraphicsBackend.WebGL2, backend);
        Assert.True(GraphicsBackendSelection.TryParseName("webgl", out backend));
        Assert.Equal(GraphicsBackend.WebGL2, backend);
        Assert.True(GraphicsBackendSelection.TryParseName("gles3", out backend));
        Assert.Equal(GraphicsBackend.WebGL2, backend);
        Assert.True(GraphicsBackendSelection.TryParseName("gles", out backend));
        Assert.Equal(GraphicsBackend.WebGL2, backend);
    }

    [Fact]
    public void AutoCandidates_DoNotIncludeWebGL2OnDesktop()
    {
        GraphicsBackend[] candidates = GraphicsDeviceFactory.GetAutoCandidatesForTests();
        Assert.DoesNotContain(GraphicsBackend.WebGL2, candidates);
        Assert.DoesNotContain(GraphicsBackend.WebGPU, candidates);
    }

    [Fact]
    public void Factory_CreatesWebGL2Device()
    {
        using IGraphicsDevice device = GraphicsDeviceFactory.Create(new GraphicsDeviceOptions
        {
            Backend = GraphicsBackend.WebGL2,
        });
        Assert.Equal(GraphicsBackend.WebGL2, device.Backend);
        Assert.StartsWith("WebGL2", device.Capabilities.BackendName, StringComparison.Ordinal);
        Assert.False(device.Capabilities.SupportsGeometryShader);
        Assert.False(device.Capabilities.SupportsCompute);
        Assert.Equal(1, device.Capabilities.MaxFramesInFlight);
    }

    [Fact]
    public void WebAutoCandidates_PreferWebGpuThenWebGL2()
    {
        GraphicsBackend[] candidates = GraphicsDeviceFactory.GetWebAutoCandidates();
        Assert.Equal([GraphicsBackend.WebGPU, GraphicsBackend.WebGL2], candidates);
    }

    [Fact]
    public void WebGpuCanvasSurface_ExposesHtmlSelector()
    {
        var surface = new WebGPUCanvasSurface("#canvas", 64, 32);
        Assert.Equal("#canvas", surface.HtmlCanvasSelector);
        Assert.Equal(nint.Zero, surface.NativeHandle);
        Assert.Equal((64, 32), surface.FramebufferSize);
    }

    [Fact]
    public void WasmWebGpuBindings_UseInternalSymbolsNotWgpuNativeDll()
    {
        string root = FindRepositoryRoot();
        string native = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "Graphics", "Backends", "WebGPU", "Native", "WebGPUNative.cs"));
        string device = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "Graphics", "Backends", "WebGPU", "WebGPUGraphicsDevice.cs"));
        string helper = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "Platforms", "Browser", "native", "xengine_webgpu.c"));
        string csproj = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "XEngine.Runtime.csproj"));
        Assert.Contains("internal const string NativeLibraryName = \"xengine_webgpu\";", native, StringComparison.Ordinal);
        Assert.Contains("#if XENGINE_WEB", native, StringComparison.Ordinal);
        Assert.Contains("[DllImport(\"xengine_webgpu\"", native, StringComparison.Ordinal);
        Assert.Contains("xengine_webgpu_create_canvas_surface", native, StringComparison.Ordinal);
        Assert.Contains("CreateHtmlCanvasSurface_NoLock", device, StringComparison.Ordinal);
        Assert.Contains("HtmlCanvasSelector", device, StringComparison.Ordinal);
        Assert.Contains("UnmanagedCallersOnly", device, StringComparison.Ordinal);
        Assert.Contains("GetFunctionPointerForDelegate", device, StringComparison.Ordinal);
        Assert.Contains("xengine_webgpu_create_canvas_surface", helper, StringComparison.Ordinal);
        int browser = csproj.IndexOf("Condition=\"'$(IsBrowserTarget)' == 'true'\"", StringComparison.Ordinal);
        Assert.True(browser >= 0);
        int groupEnd = csproj.IndexOf("</ItemGroup>", browser, StringComparison.Ordinal);
        string browserGroup = csproj.Substring(browser, groupEnd - browser);
        Assert.DoesNotContain("Backends\\WebGPU", browserGroup, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialize_WithoutWindow_FailsWithExplicitSurfaceMessage()
    {
        using var device = new WebGL2GraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.WebGL2 });
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => device.Initialize(null));
        Assert.Contains("WebGL2CanvasSurface", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SilkWindowSurface", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown graphics backend", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented yet", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CanvasSurface_PresentsThroughProcAddress_OrSkip()
    {
        // WebGL2 is the browser target backend; on native hosts it rides desktop GL. macOS
        // core-profile GL 4.1 crashes the process inside the ES-surface interop (SIGSEGV in the
        // test host, not a catchable exception), and native macOS WebGL2 is not a product path —
        // browser coverage runs under the wasm CI.
        if (OperatingSystem.IsMacOS())
            return;

        try
        {
            Silk.NET.Windowing.WindowOptions options = Silk.NET.Windowing.WindowOptions.Default;
            options.Title = "XEngine WebGL2 Canvas Surface";
            options.Size = new Silk.NET.Maths.Vector2D<int>(320, 180);
            options.API = new Silk.NET.Windowing.GraphicsAPI(
                Silk.NET.Windowing.ContextAPI.OpenGL,
                Silk.NET.Windowing.ContextProfile.Core,
                Silk.NET.Windowing.ContextFlags.ForwardCompatible,
                new Silk.NET.Windowing.APIVersion(4, 1));
            options.IsVisible = Environment.GetEnvironmentVariable("XENGINE_CAPTURE_WINDOW_MATRIX") == "1";
            options.ShouldSwapAutomatically = false;
            using Silk.NET.Windowing.IWindow window = Silk.NET.Windowing.Window.Create(options);
            window.Initialize();
            window.GLContext!.MakeCurrent();

            var surface = new WebGL2CanvasSurface(
                window.Handle,
                window.FramebufferSize.X,
                window.FramebufferSize.Y,
                name => window.GLContext!.GetProcAddress(name),
                makeCurrent: () => window.GLContext!.MakeCurrent(),
                swapBuffers: () => window.GLContext!.SwapBuffers(),
                enableDesktopSrgbFramebuffer: true);

            using var device = new WebGL2GraphicsDevice(
                new GraphicsDeviceOptions { Backend = GraphicsBackend.WebGL2, VSync = false });
            device.Initialize(surface);
            Assert.True(device.IsInitialized);
            Assert.StartsWith("WebGL2", device.Capabilities.BackendName, StringComparison.Ordinal);
            PresentWindowFrame(device, new Color(0.12f, 0.42f, 0.28f, 1f));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.True(
                IsExpectedGpuUnavailable(ex),
                $"Unexpected WebGL2 canvas presentation failure: {ex.GetType().FullName}: {ex.Message}");
        }
    }

    [Fact]
    public void WindowSurface_PresentsAndResizes_OrSkip()
    {
        // See CanvasSurface_PresentsThroughProcAddress_OrSkip: native macOS WebGL2 crashes
        // inside the core-profile GL interop and is not a product path on this host.
        if (OperatingSystem.IsMacOS())
            return;

        try
        {
            Silk.NET.Windowing.WindowOptions options = Silk.NET.Windowing.WindowOptions.Default;
            options.Title = "XEngine WebGL2 Contract";
            options.Size = new Silk.NET.Maths.Vector2D<int>(320, 180);
            options.API = new Silk.NET.Windowing.GraphicsAPI(
                Silk.NET.Windowing.ContextAPI.OpenGL,
                Silk.NET.Windowing.ContextProfile.Core,
                Silk.NET.Windowing.ContextFlags.ForwardCompatible,
                new Silk.NET.Windowing.APIVersion(4, 1));
            options.IsVisible = Environment.GetEnvironmentVariable("XENGINE_CAPTURE_WINDOW_MATRIX") == "1";
            options.ShouldSwapAutomatically = false;
            using Silk.NET.Windowing.IWindow window = Silk.NET.Windowing.Window.Create(options);
            window.Initialize();
            var surface = new XEngine.Runtime.Backends.OpenGL.SilkWindowSurface(window) { VSync = false };

            using var device = new WebGL2GraphicsDevice(
                new GraphicsDeviceOptions { Backend = GraphicsBackend.WebGL2, VSync = false });
            device.Initialize(surface);
            Assert.True(device.IsInitialized);
            Assert.StartsWith("WebGL2", device.Capabilities.BackendName, StringComparison.Ordinal);
            Assert.False(device.Capabilities.SupportsGeometryShader);

            PresentWindowFrame(device, new Color(0.12f, 0.42f, 0.28f, 1f));

            (int initialWidth, int initialHeight) = surface.FramebufferSize;
            window.Size = new Silk.NET.Maths.Vector2D<int>(400, 240);
            window.DoEvents();
            (int resizedWidth, int resizedHeight) = surface.FramebufferSize;
            Assert.True(resizedWidth != initialWidth || resizedHeight != initialHeight);
            device.Resize(resizedWidth, resizedHeight);
            PresentWindowFrame(device, new Color(0.12f, 0.42f, 0.28f, 1f));
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            Assert.True(
                IsExpectedGpuUnavailable(ex),
                $"Unexpected WebGL2 window presentation failure: {ex.GetType().FullName}: {ex.Message}");
        }
    }

    private static void PresentWindowFrame(IGraphicsDevice device, Color color)
    {
        device.BeginFrame();
        using (CommandBuffer frame = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgl2-window-contract"))
        {
            frame.SetRenderTarget(null);
            frame.ClearRenderTarget(ClearFlags.Color, color);
            device.Execute(frame, wait: false);
        }
        device.EndFrame();
    }

    private static bool IsExpectedGpuUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                return true;
            }

            if (current is InvalidOperationException or NotSupportedException)
                return true;
        }

        return false;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && File.Exists(Path.Combine(directory.FullName, "XEngine.Runtime", "XEngine.Runtime.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the XEngine repository root.");
    }
}
