// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine.Runtime.Backends.Metal.Native;
using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Backends.Metal;

/// <summary>
/// Metal <see cref="IGraphicsDevice"/>. M1 owns device/queue/drawable/fence. M2 owns
/// buffer / texture / sampler resources and blit mipgen. M3 owns format mapping, explicit
/// bind-points, and the <see cref="GraphicsPipelineKey"/> + attachment-format PSO cache.
/// M4 owns opcode translation and render-pass recombination.
/// </summary>
public sealed partial class MetalGraphicsDevice : IGraphicsDevice
{
    private readonly object _gate = new();
    private nint _device;
    private nint _queue;
    private nint _metalLayer;
    private nint _scratchTexture;
    private nint _currentDrawable;
    private bool _initialized;
    private bool _shutdown;
    private bool _frameBegun;
    private int _scratchWidth = 64;
    private int _scratchHeight = 64;
    private ulong _submittedFence;
    private ulong _completedFence;
    private GraphicsDeviceCapabilities _capabilities = new() { BackendName = "Metal" };
    private uint _nextHandle = 1;
    private MetalCommandTranslator? _translator;
    private bool _supportsAstc;
    private readonly Dictionary<MetalGraphicsPipelineKey, nint> _graphicsPipelines = new();
    private readonly Dictionary<(MTLCompareFunction CompareFunction, bool DepthWriteEnabled), nint> _depthStencilStates = new();
    private readonly Dictionary<int, (nint Vertex, nint Fragment)> _shaderFunctions = new();
    private readonly Dictionary<(int VertexLayoutId, int InstanceLayoutId, int ShaderVariantId), nint> _vertexDescriptors = new();
    private int _pipelineCacheHitCount;
    private int _pipelineCreateCount;
    private int _executeDepth;

    internal Dictionary<uint, MetalBufferResource> Buffers { get; } = new();
    internal Dictionary<uint, MetalTextureResource> Textures { get; } = new();
    internal Dictionary<uint, MetalVertexArrayResource> VertexArrays { get; } = new();
    internal Dictionary<uint, MetalFramebufferResource> Framebuffers { get; } = new();
    internal int ScratchWidth => _scratchWidth;
    internal int ScratchHeight => _scratchHeight;

    public MetalGraphicsDevice(GraphicsDeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!MetalAvailability.IsAppleMetalHost)
        {
            throw new PlatformNotSupportedException(
                "Metal requires iOS or macOS. The I0 binding is objc_msgSend P/Invoke; this OS has no Metal.framework.");
        }

        try
        {
            _device = MetalNative.CreateSystemDefaultDevice();
            if (_device == 0)
            {
                throw new InvalidOperationException(
                    "MTLCreateSystemDefaultDevice returned nil. Metal is unavailable on this machine.");
            }

            _queue = MetalNative.NewCommandQueue(_device);
            if (_queue == 0)
            {
                throw new InvalidOperationException("MTLDevice newCommandQueue returned nil.");
            }

            string? name = MetalNative.GetDeviceName(_device);
            _supportsAstc = MetalNative.SupportsFamily(_device, MTLGPUFamily.Apple2);
            _capabilities = new GraphicsDeviceCapabilities
            {
                MaxTextureSize = 16384,
                MaxCubeMapTextureSize = 16384,
                MaxArrayTextureLayers = 2048,
                MaxFramebufferColorAttachments = 8,
                MaxFramesInFlight = 3,
                SupportsCompute = true,
                SupportsGeometryShader = false,
                BackendName = string.IsNullOrEmpty(name) ? "Metal" : $"Metal ({name})",
            };
            _translator = new MetalCommandTranslator(this);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            DisposeCore();
            throw new PlatformNotSupportedException(
                "Metal native libraries (libobjc / Metal.framework) are unavailable.",
                ex);
        }
        catch
        {
            DisposeCore();
            throw;
        }
    }

    public GraphicsBackend Backend => GraphicsBackend.Metal;
    public GraphicsDeviceCapabilities Capabilities => _capabilities;
    public bool IsInitialized => _initialized;
    internal nint NativeDevice => _device;
    internal nint NativeQueue => _queue;
    internal nint ScratchTexture => _scratchTexture;
    internal nint CurrentDrawable => _currentDrawable;
    internal bool HasMetalLayer => _metalLayer != 0;
    internal bool SupportsAstc => _supportsAstc;

    internal uint AllocateHandle()
    {
        uint handle = _nextHandle++;
        if (handle == 0)
            handle = _nextHandle++;
        return handle;
    }

    /// <summary>
    /// Metal presents through a <c>CAMetalLayer*</c> when the surface provides one; a zero
    /// layer means headless (scratch-target) operation. There is deliberately no fallback to
    /// <see cref="IWindowSurface.NativeHandle"/>: a GLFW/Win32 handle is not a layer, and
    /// calling ObjC methods on it crashes <c>objc_msgSend</c>.
    /// </summary>
    internal static nint ResolveMetalLayer(IWindowSurface? surface) => surface?.MetalLayer ?? 0;

    public void Initialize(IWindowSurface? surface)
    {
        lock (_gate)
        {
            if (_shutdown)
                throw new ObjectDisposedException(nameof(MetalGraphicsDevice));
            if (_device == 0)
                throw new InvalidOperationException("Metal device was not created.");

            using var autoreleasePool = MetalNative.CreateAutoreleasePool();
            ReleaseFrameResources_NoLock();
            ReleaseScratch_NoLock();
            _metalLayer = ResolveMetalLayer(surface);

            if (_metalLayer != 0)
            {
                (int width, int height) = surface!.FramebufferSize;
                if (width > 0)
                    _scratchWidth = width;
                if (height > 0)
                    _scratchHeight = height;
            }
            else
            {
                EnsureScratch_NoLock();
            }

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
                throw new InvalidOperationException("Metal BeginFrame was called while a frame is already in progress.");

            using var autoreleasePool = MetalNative.CreateAutoreleasePool();
            ReleaseFrameResources_NoLock();
            if (_metalLayer != 0)
            {
                // A freshly attached CAMetalLayer has no device and a zero drawable size —
                // nextDrawable returns nil until both are set.
                MetalNative.SetLayerDevice(_metalLayer, _device);
                MetalNative.SetLayerDrawableSize(_metalLayer, Math.Max(1, _scratchWidth), Math.Max(1, _scratchHeight));
            }
            if (_metalLayer != 0)
            {
                nint drawable = MetalNative.NextDrawable(_metalLayer);
                if (drawable == 0)
                    throw new InvalidOperationException("CAMetalLayer nextDrawable returned nil.");
                _currentDrawable = MetalNative.Retain(drawable);
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
                throw new InvalidOperationException("Metal EndFrame was called without BeginFrame.");

            using var autoreleasePool = MetalNative.CreateAutoreleasePool();
            SubmitEmpty_NoLock(present: _currentDrawable != 0, wait: true);
            ReleaseFrameResources_NoLock();
            _frameBegun = false;
        }
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        lock (_gate)
        {
            using var autoreleasePool = MetalNative.CreateAutoreleasePool();
            _scratchWidth = width;
            _scratchHeight = height;
            if (_initialized && _metalLayer == 0)
            {
                ReleaseScratch_NoLock();
                EnsureScratch_NoLock();
            }
            else if (_metalLayer != 0)
            {
                MetalNative.SetLayerDrawableSize(_metalLayer, width, height);
            }
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
                int executeDepth = ++_executeDepth;
                try
                {
                    EnsureReady_NoLock();
                    using var autoreleasePool = MetalNative.CreateAutoreleasePool();
                    nint native = MetalNative.NewCommandBuffer(_queue);
                    if (native == 0)
                        throw new InvalidOperationException("MTLCommandQueue commandBuffer returned nil.");
                    _translator!.Translate(native, commandBuffer);
                    MetalNative.Commit(native);
                    _submittedFence++;
                    if (wait)
                    {
                        MetalNative.WaitUntilCompleted(native);
                        _completedFence = _submittedFence;
                    }
                    // GPU readbacks (GetTextureData) must run after the draws that produced the
                    // pixels complete — getBytes is a host-side immediate copy with no implicit
                    // sync, so mid-encode reads return stale data.
                    if (_translator!.HasPendingReadbacks)
                    {
                        if (!wait)
                        {
                            MetalNative.WaitUntilCompleted(native);
                            _completedFence = _submittedFence;
                        }
                        _translator!.FlushPendingReadbacks_NoLock();
                    }
                }
                finally
                {
                    _executeDepth--;
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
            _completedFence = _submittedFence;
        }
    }

    public void Dispose() => DisposeCore();

    internal byte[] ReadBuffer(MetalBufferResource resource, ulong offset, int length)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            if (resource.Buffer == 0)
                throw new InvalidOperationException("Metal buffer has been released.");
            nint contents = MetalNative.BufferContents(resource.Buffer);
            if (contents == 0)
                throw new InvalidOperationException("MTLBuffer contents returned null.");
            byte[] destination = new byte[length];
            Marshal.Copy(contents + (nint)offset, destination, 0, length);
            return destination;
        }
    }

    internal byte[] ReadTexture2D(MetalTextureResource resource, uint mip)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            if (resource.Texture == 0)
                throw new InvalidOperationException("Metal texture has been released.");
            uint width = Math.Max(1u, resource.Width >> (int)mip);
            uint height = Math.Max(1u, resource.Height >> (int)mip);
            int bpp = MetalFormats.BytesPerPixel(resource.EngineFormat);
            int row = checked((int)(width * (uint)bpp));
            byte[] destination = new byte[checked(row * (int)height)];
            unsafe
            {
                fixed (byte* dest = destination)
                {
                    MetalNative.GetBytes(
                        resource.Texture,
                        mip,
                        0,
                        0,
                        width,
                        height,
                        (nint)dest,
                        (nuint)row);
                }
            }
            return destination;
        }
    }

    /// <summary>Read back one cube face (<paramref name="face"/> 0..5 = +X, -X, +Y, -Y, +Z, -Z).</summary>
    internal byte[] ReadTextureCubeFace(MetalTextureResource resource, int face, int mip)
    {
        lock (_gate)
        {
            EnsureReady_NoLock();
            if (resource.Texture == 0)
                throw new InvalidOperationException("Metal texture has been released.");
            uint level = (uint)Math.Max(0, mip);
            uint width = Math.Max(1u, resource.Width >> (int)level);
            uint height = Math.Max(1u, resource.Height >> (int)level);
            int bpp = MetalFormats.BytesPerPixel(resource.EngineFormat);
            int row = checked((int)(width * (uint)bpp));
            byte[] destination = new byte[checked(row * (int)height)];
            unsafe
            {
                fixed (byte* dest = destination)
                {
                    MetalNative.GetBytesCube(
                        resource.Texture,
                        (uint)Math.Max(0, face),
                        level,
                        width,
                        height,
                        (nint)dest,
                        (nuint)row);
                }
            }
            return destination;
        }
    }

    private void SubmitEmpty_NoLock(bool present, bool wait)
    {
        nint commandBuffer = MetalNative.NewCommandBuffer(_queue);
        if (commandBuffer == 0)
            throw new InvalidOperationException("MTLCommandQueue commandBuffer returned nil.");

        if (present && _currentDrawable != 0)
            MetalNative.PresentDrawable(commandBuffer, _currentDrawable);

        MetalNative.Commit(commandBuffer);
        _submittedFence++;
        if (wait)
        {
            MetalNative.WaitUntilCompleted(commandBuffer);
            _completedFence = _submittedFence;
        }
    }

    private void EnsureScratch_NoLock()
    {
        if (_scratchTexture != 0)
            return;
        _scratchTexture = MetalNative.NewScratchTexture(_device, _scratchWidth, _scratchHeight);
        if (_scratchTexture == 0)
        {
            throw new InvalidOperationException(
                "Failed to create a headless Metal scratch texture (MTLTextureDescriptor / newTextureWithDescriptor).");
        }
    }

    private void EnsureReady_NoLock()
    {
        if (_shutdown)
            throw new ObjectDisposedException(nameof(MetalGraphicsDevice));
        if (!_initialized)
            throw new InvalidOperationException("MetalGraphicsDevice has not been initialized.");
        if (_device == 0 || _queue == 0)
            throw new InvalidOperationException("Metal device or queue is nil.");
    }

    private void ReleaseFrameResources_NoLock()
    {
        if (_currentDrawable != 0)
        {
            MetalNative.Release(_currentDrawable);
            _currentDrawable = 0;
        }
    }

    private void ReleaseScratch_NoLock()
    {
        if (_scratchTexture != 0)
        {
            MetalNative.Release(_scratchTexture);
            _scratchTexture = 0;
        }
    }

    private void DisposeCore()
    {
        lock (_gate)
        {
            if (_shutdown)
                return;
            _shutdown = true;
            _initialized = false;
            _frameBegun = false;
            foreach (MetalBufferResource buffer in Buffers.Values)
                buffer.Release();
            Buffers.Clear();
            foreach (MetalTextureResource texture in Textures.Values)
                texture.Release();
            Textures.Clear();
            VertexArrays.Clear();
            Framebuffers.Clear();
            ReleasePipelines_NoLock();
            ReleaseDepthStencilStates_NoLock();
            foreach ((nint vertex, nint fragment) in _shaderFunctions.Values)
            {
                if (vertex != 0)
                    MetalNative.Release(vertex);
                if (fragment != 0)
                    MetalNative.Release(fragment);
            }
            _shaderFunctions.Clear();
            foreach (nint descriptor in _vertexDescriptors.Values)
                MetalNative.Release(descriptor);
            _vertexDescriptors.Clear();
            _translator?.ReleaseMslFunctions();
            ReleaseFrameResources_NoLock();
            ReleaseScratch_NoLock();
            if (_queue != 0)
            {
                MetalNative.Release(_queue);
                _queue = 0;
            }
            if (_device != 0)
            {
                MetalNative.Release(_device);
                _device = 0;
            }
        }
    }
}
