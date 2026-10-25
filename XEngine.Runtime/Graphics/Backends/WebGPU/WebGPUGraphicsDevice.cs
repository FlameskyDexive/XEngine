// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using XEngine.Runtime.Backends.WebGPU.Native;
using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Backends.WebGPU;

/// <summary>
/// WebGPU <see cref="IGraphicsDevice"/> for wgpu-native (desktop) and emdawnwebgpu (WASM).
/// WG1 owns instance / adapter / device / queue, optional surface configure, frame begin/end, and
/// fence-via-queue-work-done. WG2 owns buffer / texture / sampler / mipgen. WG3 owns bind-group
/// layouts. WG4 owns PSO cache. WG5 owns draw / render-pass translation.
/// </summary>
public sealed unsafe partial class WebGPUGraphicsDevice : IGraphicsDevice
{
#if XENGINE_WEB
    // Mono-WASM interp cannot Marshal.GetFunctionPointerForDelegate; UnmanagedCallersOnly
    // emits a compile-time native export the pinvoke table can call back into.
    private static readonly nint s_requestAdapterFn = (nint)(delegate* unmanaged[Cdecl]<WGPURequestAdapterStatus, nint, WGPUStringView, void*, void*, void>)&OnRequestAdapterNative;
    private static readonly nint s_requestDeviceFn = (nint)(delegate* unmanaged[Cdecl]<WGPURequestDeviceStatus, nint, WGPUStringView, void*, void*, void>)&OnRequestDeviceNative;
    private static readonly nint s_queueWorkDoneFn = (nint)(delegate* unmanaged[Cdecl]<WGPUQueueWorkDoneStatus, WGPUStringView, void*, void*, void>)&OnQueueWorkDoneNative;
    private static readonly nint s_deviceLostFn = (nint)(delegate* unmanaged[Cdecl]<nint*, WGPUDeviceLostReason, WGPUStringView, void*, void*, void>)&OnDeviceLostNative;
    private static readonly nint s_uncapturedErrorFn = (nint)(delegate* unmanaged[Cdecl]<nint*, WGPUErrorType, WGPUStringView, void*, void*, void>)&OnUncapturedErrorNative;
    private static readonly nint s_bufferMapFn = (nint)(delegate* unmanaged[Cdecl]<WGPUMapAsyncStatus, WGPUStringView, void*, void*, void>)&OnBufferMapNative;
#else
    private static readonly WGPURequestAdapterCallback s_requestAdapterCallback = OnRequestAdapter;
    private static readonly WGPURequestDeviceCallback s_requestDeviceCallback = OnRequestDevice;
    private static readonly WGPUQueueWorkDoneCallback s_queueWorkDoneCallback = OnQueueWorkDone;
    private static readonly WGPUDeviceLostCallback s_deviceLostCallback = OnDeviceLost;
    private static readonly WGPUUncapturedErrorCallback s_uncapturedErrorCallback = OnUncapturedError;
    private static readonly WGPUBufferMapCallback s_bufferMapCallback = OnBufferMap;
    private static readonly nint s_requestAdapterFn = Marshal.GetFunctionPointerForDelegate(s_requestAdapterCallback);
    private static readonly nint s_requestDeviceFn = Marshal.GetFunctionPointerForDelegate(s_requestDeviceCallback);
    private static readonly nint s_queueWorkDoneFn = Marshal.GetFunctionPointerForDelegate(s_queueWorkDoneCallback);
    private static readonly nint s_deviceLostFn = Marshal.GetFunctionPointerForDelegate(s_deviceLostCallback);
    private static readonly nint s_uncapturedErrorFn = Marshal.GetFunctionPointerForDelegate(s_uncapturedErrorCallback);
    private static readonly nint s_bufferMapFn = Marshal.GetFunctionPointerForDelegate(s_bufferMapCallback);
#endif

    private readonly GraphicsDeviceOptions _options;
    private readonly GraphicsDeviceLossState _deviceLoss = new();
    private readonly object _gate = new();
    private GCHandle _selfHandle;

    private nint _instance;
    private nint _adapter;
    private nint _device;
    private nint _queue;
    private nint _surface;
    private nint _currentSurfaceTexture;
    private bool _initialized;
    private bool _shutdown;
    private bool _frameBegun;
    private bool _surfaceConfigured;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private WGPUTextureFormat _surfaceFormat = WGPUTextureFormat.BGRA8Unorm;
    private IWindowSurface? _windowSurface;
    private ulong _submittedFence;
    private ulong _completedFence;
    private GraphicsDeviceCapabilities _capabilities = new() { BackendName = "WebGPU" };
    private uint _nextHandle = 1;
    private WebGPUCommandTranslator? _translator;
    private WebGPUMipGenerator? _mipGenerator;
    private readonly Dictionary<int, WebGPUShaderModuleResource> _shaderModules = new();
    private readonly Dictionary<int, WebGPUShaderLayoutResource> _shaderLayouts = new();
    private readonly Dictionary<WebGPUGraphicsPipelineKey, nint> _graphicsPipelines = new();
    private int _pipelineCacheHitCount;
    private int _pipelineCreateCount;

    internal Dictionary<uint, WebGPUBufferResource> Buffers { get; } = new();
    internal Dictionary<uint, WebGPUTextureResource> Textures { get; } = new();
    internal Dictionary<uint, WebGPUVertexArrayResource> VertexArrays { get; } = new();
    internal Dictionary<uint, WebGPUFramebufferResource> Framebuffers { get; } = new();
    internal WebGPUMipGenerator MipGenerator => _mipGenerator!;
    internal nint CurrentSurfaceTexture => _currentSurfaceTexture;
    internal WGPUTextureFormat SurfaceFormat => _surfaceFormat;
    internal uint SurfaceWidth => (uint)Math.Max(1, _surfaceWidth);
    internal uint SurfaceHeight => (uint)Math.Max(1, _surfaceHeight);

    public WebGPUGraphicsDevice(GraphicsDeviceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _selfHandle = GCHandle.Alloc(this);

        try
        {
            CreateInstanceAndDevice();
            _translator = new WebGPUCommandTranslator(this);
            _mipGenerator = new WebGPUMipGenerator(this);
        }
        catch (Exception ex) when (ex is not PlatformNotSupportedException and not InvalidOperationException)
        {
            DisposeCore();
            throw new InvalidOperationException(
                "Failed to create WebGPU device. wgpu-native is unavailable on this machine (missing native library, adapter, or driver).",
                ex);
        }
        catch (InvalidOperationException)
        {
            DisposeCore();
            throw;
        }
    }

    public GraphicsBackend Backend => GraphicsBackend.WebGPU;
    public GraphicsDeviceCapabilities Capabilities => _capabilities;
    public bool IsInitialized => _initialized;
    public bool IsDeviceLost => _deviceLoss.IsLost;
    internal nint NativeInstance => _instance;
    internal nint NativeAdapter => _adapter;
    internal nint NativeDevice => _device;
    internal nint NativeQueue => _queue;
    internal nint NativeSurface => _surface;
    internal bool HasSurface => _surface != 0 && _surfaceConfigured;

    internal uint AllocateHandle()
    {
        uint handle = _nextHandle++;
        if (handle == 0)
            handle = _nextHandle++;
        return handle;
    }

        internal nint CreateGpuTexture(
        uint width,
        uint height,
        uint mipLevels,
        TextureImageFormat engineFormat,
        WGPUTextureFormat format,
        uint arrayLayers = 1)
    {
        WGPUTextureUsage usage = WGPUTextureUsage.CopySrc | WGPUTextureUsage.TextureBinding;
        if (WebGPUFormats.IsCompressed(engineFormat))
        {
            usage |= WGPUTextureUsage.CopyDst;
        }
        else if (WebGPUFormats.IsDepth(engineFormat))
        {
            usage |= WGPUTextureUsage.CopyDst | WGPUTextureUsage.RenderAttachment;
        }
        else
        {
            usage |= WGPUTextureUsage.CopyDst | WGPUTextureUsage.RenderAttachment;
        }

        WGPUTextureDescriptor desc = new()
        {
            usage = usage,
            dimension = WGPUTextureDimension.D2,
            size = new WGPUExtent3D { width = width, height = height, depthOrArrayLayers = Math.Max(1u, arrayLayers) },
            format = format,
            mipLevelCount = Math.Max(1u, mipLevels),
            sampleCount = 1,
        };
        nint texture = WebGPUNative.wgpuDeviceCreateTexture(_device, &desc);
        if (texture == 0)
            throw new InvalidOperationException("wgpuDeviceCreateTexture returned null.");
        return texture;
    }

    internal void SubmitEncoder(nint encoder, bool wait)
    {
        nint cmd = WebGPUNative.wgpuCommandEncoderFinish(encoder, null);
        try
        {
            if (cmd == 0)
                throw new InvalidOperationException("wgpuCommandEncoderFinish returned null.");
            WebGPUNative.wgpuQueueSubmit(_queue, 1, &cmd);
            if (wait)
                WaitQueueIdle_NoLock();
        }
        finally
        {
            if (cmd != 0)
                WebGPUNative.wgpuCommandBufferRelease(cmd);
            WebGPUNative.wgpuCommandEncoderRelease(encoder);
        }
    }

    internal byte[] ReadBuffer(WebGPUBufferResource resource, ulong offset, int length)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            return ReadBuffer_NoLock(resource, offset, length);
        }
    }

    internal byte[] ReadTexture2D(WebGPUTextureResource resource, uint mip)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            return ReadTexture2D_NoLock(resource, mip);
        }
    }

    public void Initialize(IWindowSurface? surface)
    {
        lock (_gate)
        {
            if (_shutdown)
                throw new ObjectDisposedException(nameof(WebGPUGraphicsDevice));
            _deviceLoss.ThrowIfLost();
            if (_device == 0)
                throw new InvalidOperationException("WebGPU device was not created.");

            DestroySurface_NoLock();
            _windowSurface = surface;
            if (surface != null)
                ConfigureSurface_NoLock(surface);

            _initialized = true;
            _frameBegun = false;
        }
    }

    public void Shutdown() => DisposeCore();

    public void BeginFrame()
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            if (_frameBegun)
                throw new InvalidOperationException("WebGPU BeginFrame was called while a frame is already in progress.");

            if (HasSurface)
            {
                AcquireSurfaceTexture_NoLock();
            }

            _frameBegun = true;
        }
    }

    public void EndFrame()
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            if (!_frameBegun)
                throw new InvalidOperationException("WebGPU EndFrame was called without BeginFrame.");

            if (HasSurface)
            {
                WGPUStatus present = WebGPUNative.wgpuSurfacePresent(_surface);
                if (present != WGPUStatus.Success)
                    throw new InvalidOperationException($"wgpuSurfacePresent failed: {present}.");
                ReleaseCurrentSurfaceTexture_NoLock();
            }

            _frameBegun = false;
            _submittedFence++;
            _completedFence = _submittedFence;
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        lock (_gate)
        {
            if (!_initialized || _windowSurface == null)
                return;

            _surfaceWidth = width;
            _surfaceHeight = height;
            if (HasSurface)
                ConfigureExistingSurface_NoLock();
        }
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
                _translator!.Translate(commandBuffer);
                _submittedFence++;
                if (wait)
                {
                    WaitQueueIdle_NoLock();
                    _completedFence = _submittedFence;
                }
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
            EnsureReady_NoLock();
            WaitQueueIdle_NoLock();
            _completedFence = _submittedFence;
        }
    }

    public ulong GetFenceValue()
    {
        lock (_gate)
            return _completedFence;
    }

    public void WaitFence(ulong fenceValue)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            if (fenceValue <= _completedFence)
                return;
            WaitQueueIdle_NoLock();
            _completedFence = _submittedFence;
        }
    }

    public void Dispose() => DisposeCore();

    private void CreateInstanceAndDevice()
    {
        // wgpu-native v29 panics if TimedWaitAny is required (WaitAny is unimplemented).
        // Pass a null descriptor so both wgpu-native and emdawnwebgpu take the default path.
        _instance = WebGPUNative.wgpuCreateInstance(null);
        if (_instance == 0)
            throw new InvalidOperationException("wgpuCreateInstance returned null. wgpu-native is unavailable on this machine.");

        WGPURequestAdapterOptions adapterOptions = new()
        {
            featureLevel = WGPUFeatureLevel.Core,
            powerPreference = WGPUPowerPreference.HighPerformance,
        };
        var adapterResult = new RequestAdapterResult();
        GCHandle adapterHandle = GCHandle.Alloc(adapterResult);
        try
        {
            WGPUCallbackInfo adapterCallback = new()
            {
                mode = WGPUCallbackMode.AllowProcessEvents,
                callback = s_requestAdapterFn,
                userdata1 = (void*)GCHandle.ToIntPtr(adapterHandle),
            };
            WebGPUNative.wgpuInstanceRequestAdapter(_instance, &adapterOptions, adapterCallback);
            PumpUntil(() => adapterResult.Completed);
        }
        finally
        {
            adapterHandle.Free();
        }

        if (adapterResult.Status != WGPURequestAdapterStatus.Success || adapterResult.Adapter == 0)
        {
            throw new InvalidOperationException(
                $"Failed to request a WebGPU adapter: {adapterResult.Status}. {adapterResult.Message}");
        }

        _adapter = adapterResult.Adapter;

        var deviceResult = new RequestDeviceResult();
        GCHandle deviceHandle = GCHandle.Alloc(deviceResult);
        WGPUDeviceDescriptor deviceDescriptor = new()
        {
            deviceLostCallbackInfo = new WGPUCallbackInfo
            {
                mode = WGPUCallbackMode.AllowSpontaneous,
                callback = s_deviceLostFn,
                userdata1 = (void*)GCHandle.ToIntPtr(_selfHandle),
            },
            uncapturedErrorCallbackInfo = new WGPUUncapturedErrorCallbackInfo
            {
                callback = s_uncapturedErrorFn,
                userdata1 = (void*)GCHandle.ToIntPtr(_selfHandle),
            },
        };
        try
        {
            WGPUCallbackInfo deviceCallback = new()
            {
                mode = WGPUCallbackMode.AllowProcessEvents,
                callback = s_requestDeviceFn,
                userdata1 = (void*)GCHandle.ToIntPtr(deviceHandle),
            };
            WebGPUNative.wgpuAdapterRequestDevice(_adapter, &deviceDescriptor, deviceCallback);
            PumpUntil(() => deviceResult.Completed);
        }
        finally
        {
            deviceHandle.Free();
        }

        if (deviceResult.Status != WGPURequestDeviceStatus.Success || deviceResult.Device == 0)
        {
            throw new InvalidOperationException(
                $"Failed to request a WebGPU device: {deviceResult.Status}. {deviceResult.Message}");
        }

        _device = deviceResult.Device;
        _queue = WebGPUNative.wgpuDeviceGetQueue(_device);
        if (_queue == 0)
            throw new InvalidOperationException("wgpuDeviceGetQueue returned null.");

        _capabilities = QueryCapabilities();
    }

    private GraphicsDeviceCapabilities QueryCapabilities()
    {
        WGPULimits limits = default;
        if (WebGPUNative.wgpuAdapterGetLimits(_adapter, &limits) != WGPUStatus.Success)
            WebGPUNative.wgpuDeviceGetLimits(_device, &limits);

        WGPUAdapterInfo info = default;
        string vendor = string.Empty;
        string device = "WebGPU";
        string architecture = string.Empty;
        WGPUBackendType backendType = WGPUBackendType.Undefined;
        WGPUAdapterType adapterType = WGPUAdapterType.Unknown;
        uint vendorId = 0;
        uint deviceId = 0;
        if (WebGPUNative.wgpuAdapterGetInfo(_adapter, &info) == WGPUStatus.Success)
        {
            vendor = info.vendor.ToManagedString();
            device = info.device.ToManagedString();
            architecture = info.architecture.ToManagedString();
            backendType = info.backendType;
            adapterType = info.adapterType;
            vendorId = info.vendorID;
            deviceId = info.deviceID;
            WebGPUNative.wgpuAdapterInfoFreeMembers(info);
        }

        string backendName = string.IsNullOrEmpty(device)
            ? $"WebGPU ({backendType})"
            : $"WebGPU ({backendType} / {device})";

        return new GraphicsDeviceCapabilities
        {
            MaxTextureSize = (int)Math.Min(int.MaxValue, limits.maxTextureDimension2D),
            MaxCubeMapTextureSize = (int)Math.Min(int.MaxValue, limits.maxTextureDimension2D),
            MaxArrayTextureLayers = (int)Math.Min(int.MaxValue, limits.maxTextureArrayLayers),
            MaxFramebufferColorAttachments = (int)Math.Min(int.MaxValue, limits.maxColorAttachments),
            MaxFramesInFlight = 2,
            SupportsCompute = limits.maxComputeInvocationsPerWorkgroup > 0,
            SupportsGeometryShader = false,
            BackendName = backendName,
            Baseline = new GraphicsCapabilityBaseline
            {
                Backend = "WebGPU",
                DeviceName = string.IsNullOrEmpty(device) ? "Unknown" : device,
                DeviceType = adapterType.ToString(),
                VendorId = vendorId,
                DeviceId = deviceId,
                ApiVersionName = WebGPUNative.HeaderRevision,
                DriverVersionName = string.IsNullOrEmpty(architecture) ? vendor : $"{vendor} {architecture}",
                MaxTextureSize = (int)Math.Min(int.MaxValue, limits.maxTextureDimension2D),
            },
        };
    }

    private void ConfigureSurface_NoLock(IWindowSurface surface)
    {
        string? selector = surface.HtmlCanvasSelector;
        if (!string.IsNullOrEmpty(selector))
        {
            CreateHtmlCanvasSurface_NoLock(selector);
            (int width, int height) = surface.FramebufferSize;
            _surfaceWidth = Math.Max(1, width);
            _surfaceHeight = Math.Max(1, height);
            ConfigureExistingSurface_NoLock();
            return;
        }

#if XENGINE_WEB
        throw new InvalidOperationException(
            "WebGPU on WASM requires IWindowSurface.HtmlCanvasSelector (usually #canvas).");
#else
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "WebGPU surface configure currently supports Windows HWND or an HTML canvas selector.");
        }

        nint hwnd = surface.NativeHandle;
        if (hwnd == 0)
            throw new InvalidOperationException("WebGPU surface configure requires a non-zero HWND from IWindowSurface.NativeHandle.");

        WGPUSurfaceSourceWindowsHWND source = new()
        {
            chain = new WGPUChainedStruct { sType = WGPUSType.SurfaceSourceWindowsHWND },
            hinstance = GetModuleHandleW(0),
            hwnd = hwnd,
        };
        WGPUSurfaceDescriptor descriptor = new()
        {
            nextInChain = (WGPUChainedStruct*)&source,
        };
        _surface = WebGPUNative.wgpuInstanceCreateSurface(_instance, &descriptor);
        if (_surface == 0)
            throw new InvalidOperationException("wgpuInstanceCreateSurface returned null.");

        (int fbWidth, int fbHeight) = surface.FramebufferSize;
        _surfaceWidth = Math.Max(1, fbWidth);
        _surfaceHeight = Math.Max(1, fbHeight);
        ConfigureExistingSurface_NoLock();
#endif
    }

    private void CreateHtmlCanvasSurface_NoLock(string selector)
    {
#if XENGINE_WEB
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(selector);
        Span<byte> buffer = stackalloc byte[byteCount + 1];
        System.Text.Encoding.UTF8.GetBytes(selector, buffer);
        buffer[byteCount] = 0;
        nint surface;
        fixed (byte* ptr = buffer)
            surface = WebGPUNative.CreateCanvasSurface(_instance, ptr);
        if (surface == 0)
        {
            throw new InvalidOperationException(
                "emdawnwebgpu failed to create a canvas surface on '" + selector + "'.");
        }
        _surface = surface;
#else
        _ = selector;
        throw new PlatformNotSupportedException("HTML canvas WebGPU surfaces are only available on the WASM player.");
#endif
    }

    private void ConfigureExistingSurface_NoLock()
    {
        WGPUSurfaceCapabilities capabilities = default;
        if (WebGPUNative.wgpuSurfaceGetCapabilities(_surface, _adapter, &capabilities) != WGPUStatus.Success)
            throw new InvalidOperationException("wgpuSurfaceGetCapabilities failed.");

        try
        {
            _surfaceFormat = capabilities.formatCount > 0 ? capabilities.formats[0] : WGPUTextureFormat.BGRA8Unorm;
            WGPUPresentMode presentMode = _options.VSync || (_windowSurface?.VSync ?? true)
                ? WGPUPresentMode.Fifo
                : WGPUPresentMode.Immediate;
            if (!ContainsPresentMode(&capabilities, presentMode))
                presentMode = WGPUPresentMode.Fifo;

            WGPUSurfaceConfiguration config = new()
            {
                device = _device,
                format = _surfaceFormat,
                usage = WGPUTextureUsage.RenderAttachment,
                width = (uint)Math.Max(1, _surfaceWidth),
                height = (uint)Math.Max(1, _surfaceHeight),
                alphaMode = WGPUCompositeAlphaMode.Auto,
                presentMode = presentMode,
            };
            WebGPUNative.wgpuSurfaceConfigure(_surface, &config);
            _surfaceConfigured = true;
        }
        finally
        {
            WebGPUNative.wgpuSurfaceCapabilitiesFreeMembers(capabilities);
        }
    }

    private static bool ContainsPresentMode(WGPUSurfaceCapabilities* capabilities, WGPUPresentMode mode)
    {
        for (nuint i = 0; i < capabilities->presentModeCount; i++)
        {
            if (capabilities->presentModes[i] == mode)
                return true;
        }

        return false;
    }

    private void AcquireSurfaceTexture_NoLock()
    {
        ReleaseCurrentSurfaceTexture_NoLock();
        WGPUSurfaceTexture surfaceTexture = default;
        WebGPUNative.wgpuSurfaceGetCurrentTexture(_surface, &surfaceTexture);
        if (surfaceTexture.status is WGPUSurfaceGetCurrentTextureStatus.Outdated
            or WGPUSurfaceGetCurrentTextureStatus.Lost
            or WGPUSurfaceGetCurrentTextureStatus.Timeout)
        {
            if (surfaceTexture.texture != 0)
                WebGPUNative.wgpuTextureRelease(surfaceTexture.texture);
            ConfigureExistingSurface_NoLock();
            surfaceTexture = default;
            WebGPUNative.wgpuSurfaceGetCurrentTexture(_surface, &surfaceTexture);
        }

        if (surfaceTexture.status is not WGPUSurfaceGetCurrentTextureStatus.SuccessOptimal
            and not WGPUSurfaceGetCurrentTextureStatus.SuccessSuboptimal)
        {
            if (surfaceTexture.texture != 0)
                WebGPUNative.wgpuTextureRelease(surfaceTexture.texture);
            throw new InvalidOperationException($"wgpuSurfaceGetCurrentTexture failed: {surfaceTexture.status}.");
        }

        _currentSurfaceTexture = surfaceTexture.texture;
    }

    private void ReleaseCurrentSurfaceTexture_NoLock()
    {
        if (_currentSurfaceTexture == 0)
            return;
        WebGPUNative.wgpuTextureRelease(_currentSurfaceTexture);
        _currentSurfaceTexture = 0;
    }

    private void DestroySurface_NoLock()
    {
        ReleaseCurrentSurfaceTexture_NoLock();
        if (_surface != 0)
        {
            if (_surfaceConfigured)
                WebGPUNative.wgpuSurfaceUnconfigure(_surface);
            WebGPUNative.wgpuSurfaceRelease(_surface);
            _surface = 0;
            _surfaceConfigured = false;
        }
    }

    private void WaitQueueIdle_NoLock()
    {
        var result = new QueueWorkDoneResult();
        GCHandle handle = GCHandle.Alloc(result);
        try
        {
            WGPUCallbackInfo callback = new()
            {
                mode = WGPUCallbackMode.AllowProcessEvents,
                callback = s_queueWorkDoneFn,
                userdata1 = (void*)GCHandle.ToIntPtr(handle),
            };
            WebGPUNative.wgpuQueueOnSubmittedWorkDone(_queue, callback);
            PumpUntil(() => result.Completed);
        }
        finally
        {
            handle.Free();
        }

        if (result.Status != WGPUQueueWorkDoneStatus.Success)
        {
            throw new InvalidOperationException(
                $"wgpuQueueOnSubmittedWorkDone failed: {result.Status}. {result.Message}");
        }
    }

    internal void PumpUntil(Func<bool> completed)
    {
        if (completed())
            return;

        Stopwatch clock = Stopwatch.StartNew();
        while (!completed())
        {
            WebGPUNative.wgpuInstanceProcessEvents(_instance);
            if (completed())
                return;
            if (clock.ElapsedMilliseconds > 5000)
            {
#if XENGINE_WEB
                throw new InvalidOperationException(
                    "WebGPU async operation timed out: emdawnwebgpu requestAdapter/requestDevice finish on the browser event loop, and wgpuInstanceProcessEvents cannot run JS promises while C# is blocked on AttachCanvas.");
#else
                throw new InvalidOperationException(
                    "WebGPU async operation timed out waiting for wgpuInstanceProcessEvents. wgpu-native v29 does not implement wgpuInstanceWaitAny.");
#endif
            }

            Thread.Sleep(1);
        }
    }

    private byte[] ReadBuffer_NoLock(WebGPUBufferResource resource, ulong offset, int length)
    {
        if (resource.Buffer == 0)
            throw new InvalidOperationException("WebGPU buffer has no GPU storage.");
        if (length <= 0)
            return [];
        if (offset + (ulong)length > resource.Size)
            throw new ArgumentOutOfRangeException(nameof(length));

        ulong stagingSize = WebGPUFormats.Align((ulong)length, 4);
        nint staging = CreateStagingBuffer(stagingSize);
        nint encoder = WebGPUNative.wgpuDeviceCreateCommandEncoder(_device, null);
        try
        {
            WebGPUNative.wgpuCommandEncoderCopyBufferToBuffer(encoder, resource.Buffer, offset, staging, 0, (ulong)length);
            SubmitEncoder(encoder, wait: true);
            encoder = 0;
            byte[] mapped = MapReadBytes(staging, (int)stagingSize);
            if (mapped.Length == length)
                return mapped;
            byte[] trimmed = new byte[length];
            Array.Copy(mapped, trimmed, length);
            return trimmed;
        }
        finally
        {
            if (encoder != 0)
                WebGPUNative.wgpuCommandEncoderRelease(encoder);
            WebGPUNative.wgpuBufferRelease(staging);
        }
    }

    internal byte[] ReadTexture2D_NoLock(WebGPUTextureResource resource, uint mip)
    {
        if (resource.Texture == 0)
            throw new InvalidOperationException("WebGPU texture has no GPU storage.");
        if (mip >= resource.MipLevels)
            throw new ArgumentOutOfRangeException(nameof(mip));

        uint width = Math.Max(1u, resource.Width >> (int)mip);
        uint height = Math.Max(1u, resource.Height >> (int)mip);
        int bpp = WebGPUFormats.BytesPerPixel(resource.EngineFormat);
        uint unpaddedRow = width * (uint)bpp;
        uint bytesPerRow = WebGPUFormats.Align(unpaddedRow, WebGPUNative.BytesPerRowAlignment);
        ulong stagingSize = WebGPUFormats.Align((ulong)bytesPerRow * height, 4);
        nint staging = CreateStagingBuffer(stagingSize);

        WGPUTexelCopyTextureInfo src = new()
        {
            texture = resource.Texture,
            mipLevel = mip,
            aspect = WGPUTextureAspect.All,
        };
        WGPUTexelCopyBufferInfo dst = new()
        {
            layout = new WGPUTexelCopyBufferLayout
            {
                bytesPerRow = bytesPerRow,
                rowsPerImage = height,
            },
            buffer = staging,
        };
        WGPUExtent3D extent = new()
        {
            width = width,
            height = height,
            depthOrArrayLayers = 1,
        };

        nint encoder = WebGPUNative.wgpuDeviceCreateCommandEncoder(_device, null);
        try
        {
            WebGPUNative.wgpuCommandEncoderCopyTextureToBuffer(encoder, &src, &dst, &extent);
            SubmitEncoder(encoder, wait: true);
            encoder = 0;
            byte[] mapped = MapReadBytes(staging, (int)stagingSize);
            byte[] pixels = new byte[checked((int)(unpaddedRow * height))];
            if (bytesPerRow == unpaddedRow)
            {
                Array.Copy(mapped, pixels, pixels.Length);
                return pixels;
            }
            for (uint row = 0; row < height; row++)
                Array.Copy(mapped, (int)(row * bytesPerRow), pixels, (int)(row * unpaddedRow), (int)unpaddedRow);
            return pixels;
        }
        finally
        {
            if (encoder != 0)
                WebGPUNative.wgpuCommandEncoderRelease(encoder);
            WebGPUNative.wgpuBufferRelease(staging);
        }
    }

    private nint CreateStagingBuffer(ulong size)
    {
        WGPUBufferDescriptor desc = new()
        {
            usage = WGPUBufferUsage.MapRead | WGPUBufferUsage.CopyDst,
            size = size,
        };
        nint buffer = WebGPUNative.wgpuDeviceCreateBuffer(_device, &desc);
        if (buffer == 0)
            throw new InvalidOperationException("wgpuDeviceCreateBuffer returned null for a staging buffer.");
        return buffer;
    }

    private byte[] MapReadBytes(nint buffer, int length)
    {
        var result = new BufferMapResult();
        GCHandle handle = GCHandle.Alloc(result);
        try
        {
            WGPUCallbackInfo callback = new()
            {
                mode = WGPUCallbackMode.AllowProcessEvents,
                callback = s_bufferMapFn,
                userdata1 = (void*)GCHandle.ToIntPtr(handle),
            };
            WebGPUNative.wgpuBufferMapAsync(buffer, WGPUMapMode.Read, 0, (nuint)length, callback);
            PumpUntil(() => result.Completed);
        }
        finally
        {
            handle.Free();
        }

        if (result.Status != WGPUMapAsyncStatus.Success)
        {
            throw new InvalidOperationException(
                $"wgpuBufferMapAsync failed: {result.Status}. {result.Message}");
        }

        void* mapped = WebGPUNative.wgpuBufferGetConstMappedRange(buffer, 0, (nuint)length);
        if (mapped == null)
            throw new InvalidOperationException("wgpuBufferGetConstMappedRange returned null.");
        try
        {
            byte[] destination = new byte[length];
            Marshal.Copy((nint)mapped, destination, 0, length);
            return destination;
        }
        finally
        {
            WebGPUNative.wgpuBufferUnmap(buffer);
        }
    }

    private void EnsureReady_NoLock()
    {
        if (_shutdown)
            throw new ObjectDisposedException(nameof(WebGPUGraphicsDevice));
        if (!_initialized)
            throw new InvalidOperationException("WebGPUGraphicsDevice has not been initialized.");
        _deviceLoss.ThrowIfLost();
    }

    private void DisposeCore()
    {
        lock (_gate)
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _initialized = false;
            try
            {
                _mipGenerator?.Dispose();
                _mipGenerator = null;
                _translator?.ReleaseGpu();
                ReleasePipelines_NoLock();
                foreach (WebGPUFramebufferResource framebuffer in Framebuffers.Values)
                    framebuffer.ReleaseViews();
                Framebuffers.Clear();
                VertexArrays.Clear();
                foreach (WebGPUBufferResource buffer in Buffers.Values)
                    buffer.Release();
                Buffers.Clear();
                foreach (WebGPUTextureResource texture in Textures.Values)
                    texture.Release();
                Textures.Clear();
                DestroySurface_NoLock();
                if (_queue != 0)
                {
                    WebGPUNative.wgpuQueueRelease(_queue);
                    _queue = 0;
                }
                if (_device != 0)
                {
                    WebGPUNative.wgpuDeviceRelease(_device);
                    _device = 0;
                }
                if (_adapter != 0)
                {
                    WebGPUNative.wgpuAdapterRelease(_adapter);
                    _adapter = 0;
                }
                if (_instance != 0)
                {
                    WebGPUNative.wgpuInstanceRelease(_instance);
                    _instance = 0;
                }
            }
            finally
            {
                if (_selfHandle.IsAllocated)
                    _selfHandle.Free();
            }
        }
    }

#if XENGINE_WEB
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRequestAdapterNative(
        WGPURequestAdapterStatus status,
        nint adapter,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
        => OnRequestAdapter(status, adapter, message, userdata1, userdata2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRequestDeviceNative(
        WGPURequestDeviceStatus status,
        nint device,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
        => OnRequestDevice(status, device, message, userdata1, userdata2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnQueueWorkDoneNative(
        WGPUQueueWorkDoneStatus status,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
        => OnQueueWorkDone(status, message, userdata1, userdata2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceLostNative(
        nint* device,
        WGPUDeviceLostReason reason,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
        => OnDeviceLost(device, reason, message, userdata1, userdata2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUncapturedErrorNative(
        nint* device,
        WGPUErrorType type,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
        => OnUncapturedError(device, type, message, userdata1, userdata2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBufferMapNative(
        WGPUMapAsyncStatus status,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
        => OnBufferMap(status, message, userdata1, userdata2);
#endif

    private static void OnRequestAdapter(
        WGPURequestAdapterStatus status,
        nint adapter,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
    {
        if (userdata1 != null && GCHandle.FromIntPtr((nint)userdata1).Target is RequestAdapterResult result)
        {
            result.Status = status;
            result.Adapter = adapter;
            result.Message = message.ToManagedString();
            result.Completed = true;
        }
    }

    private static void OnRequestDevice(
        WGPURequestDeviceStatus status,
        nint device,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
    {
        if (userdata1 != null && GCHandle.FromIntPtr((nint)userdata1).Target is RequestDeviceResult result)
        {
            result.Status = status;
            result.Device = device;
            result.Message = message.ToManagedString();
            result.Completed = true;
        }
    }

    private static void OnQueueWorkDone(
        WGPUQueueWorkDoneStatus status,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
    {
        if (userdata1 != null && GCHandle.FromIntPtr((nint)userdata1).Target is QueueWorkDoneResult result)
        {
            result.Status = status;
            result.Message = message.ToManagedString();
            result.Completed = true;
        }
    }

    private static void OnDeviceLost(
        nint* device,
        WGPUDeviceLostReason reason,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
    {
        if (reason == WGPUDeviceLostReason.Destroyed)
            return;
        if (TryGetDevice(userdata1, out WebGPUGraphicsDevice graphicsDevice) && !graphicsDevice._shutdown)
        {
            graphicsDevice._deviceLoss.RecordLoss(
                "WebGPU device lost",
                $"{reason}: {message.ToManagedString()}");
        }
    }

    private static void OnUncapturedError(
        nint* device,
        WGPUErrorType type,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
    {
        if (type == WGPUErrorType.NoError)
            return;
        XEngine.Runtime.Debug.LogError($"WebGPU uncaptured error ({type}): {message.ToManagedString()}");
    }

    private static void OnBufferMap(
        WGPUMapAsyncStatus status,
        WGPUStringView message,
        void* userdata1,
        void* userdata2)
    {
        if (userdata1 != null && GCHandle.FromIntPtr((nint)userdata1).Target is BufferMapResult result)
        {
            result.Status = status;
            result.Message = message.ToManagedString();
            result.Completed = true;
        }
    }

    private static bool TryGetDevice(void* userdata, out WebGPUGraphicsDevice device)
    {
        device = null!;
        if (userdata == null)
            return false;
        GCHandle handle = GCHandle.FromIntPtr((nint)userdata);
        if (!handle.IsAllocated || handle.Target is not WebGPUGraphicsDevice graphicsDevice)
            return false;
        device = graphicsDevice;
        return true;
    }

    private sealed class RequestAdapterResult
    {
        public bool Completed;
        public WGPURequestAdapterStatus Status;
        public nint Adapter;
        public string Message = string.Empty;
    }

    private sealed class RequestDeviceResult
    {
        public bool Completed;
        public WGPURequestDeviceStatus Status;
        public nint Device;
        public string Message = string.Empty;
    }

    private sealed class QueueWorkDoneResult
    {
        public bool Completed;
        public WGPUQueueWorkDoneStatus Status;
        public string Message = string.Empty;
    }

    private sealed class BufferMapResult
    {
        public bool Completed;
        public WGPUMapAsyncStatus Status;
        public string Message = string.Empty;
    }

#if !XENGINE_WEB
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(nint moduleName);
#endif
}
