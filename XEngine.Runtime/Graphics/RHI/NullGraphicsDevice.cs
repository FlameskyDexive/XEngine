// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.RHI;

/// <summary>
/// No-op <see cref="IGraphicsDevice"/> for headless / dedicated-server runs.
/// Validates and recycles command buffers without performing GPU work.
/// </summary>
public sealed class NullGraphicsDevice : IGraphicsDevice, IPresentationSurfaceLifecycle
{
    private bool _initialized;
    private ulong _fenceValue;
    private readonly PresentationSurfaceRegistry _surfaces = new();

    public GraphicsBackend Backend => GraphicsBackend.Null;

    public GraphicsDeviceCapabilities Capabilities { get; } = new()
    {
        MaxTextureSize = 16384,
        MaxCubeMapTextureSize = 16384,
        MaxArrayTextureLayers = 2048,
        MaxFramebufferColorAttachments = 8,
        MaxFramesInFlight = 1,
        SupportsCompute = false,
        SupportsGeometryShader = false,
        BackendName = "Null",
    };

    public bool IsInitialized => _initialized;

    public void Initialize(IWindowSurface? surface)
    {
        // Surface is ignored; Null is always headless-compatible.
        _initialized = true;
        _fenceValue = 0;
    }

    public void Shutdown()
    {
        _initialized = false;
        _surfaces.Clear();
    }

    public PresentationSurfaceId CreatePresentationSurface(IWindowSurface surface)
    {
        EnsureInitialized();
        return _surfaces.Create(surface);
    }

    public bool TryGetPrimaryPresentationSurface(out PresentationSurfaceId surface)
    {
        surface = default;
        return false;
    }

    public bool DestroyPresentationSurface(PresentationSurfaceId surface) =>
        _surfaces.Destroy(surface);

    public bool ResizePresentationSurface(PresentationSurfaceId surface, int width, int height)
    {
        return _surfaces.Resize(surface, width, height);
    }

    public bool TryGetPresentationSurface(PresentationSurfaceId surface, out PresentationSurfaceInfo info)
    {
        return _surfaces.TryGet(surface, out info);
    }

    public bool BeginFrame(PresentationSurfaceId surface) => _surfaces.Begin(surface);

    public bool EndFrame(PresentationSurfaceId surface)
    {
        if (!_surfaces.End(surface)) return false;
        _fenceValue++;
        return true;
    }

    public void BeginFrame()
    {
        EnsureInitialized();
    }

    public void EndFrame()
    {
        EnsureInitialized();
        _fenceValue++;
    }

    public void Execute(CommandBuffer commandBuffer, bool wait)
    {
        ArgumentNullException.ThrowIfNull(commandBuffer);
        EnsureInitialized();

        if (commandBuffer._inPool)
            throw new InvalidOperationException("CommandBuffer has already been submitted (it's in the pool).");

        // Mirror Graphics.Submit / SubmitAndWait headless path: drop GPU work and recycle.
        // wait is ignored — Null completes synchronously.
        _ = wait;
        commandBuffer._ownerReleased = true;
        CommandBufferPool.Return(commandBuffer);
        _fenceValue++;
    }

    public void WaitIdle()
    {
        // Nothing pending.
    }

    public ulong GetFenceValue() => _fenceValue;

    public void WaitFence(ulong fenceValue)
    {
        // Work completes synchronously in Execute / EndFrame.
        if (fenceValue > _fenceValue)
            _fenceValue = fenceValue;
    }

    public void Dispose() => Shutdown();

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("NullGraphicsDevice has not been initialized.");
    }

}
