using System;

namespace XEngine.Runtime.RHI;

/// <summary>Stable identity for one presentation surface owned by a graphics device.</summary>
public readonly record struct PresentationSurfaceId(ulong Value)
{
    public bool IsValid => Value != 0;
}

public readonly record struct PresentationSurfaceInfo(
    PresentationSurfaceId Id,
    int Width,
    int Height,
    bool IsVisible);

/// <summary>
/// Optional multi-surface boundary. Existing single-surface devices continue using
/// <see cref="IGraphicsDevice.Initialize(IWindowSurface?)"/> and can adopt this contract per backend.
/// </summary>
public interface IPresentationSurfaceLifecycle
{
    bool TryGetPrimaryPresentationSurface(out PresentationSurfaceId surface);
    PresentationSurfaceId CreatePresentationSurface(IWindowSurface surface);
    bool DestroyPresentationSurface(PresentationSurfaceId surface);
    bool ResizePresentationSurface(PresentationSurfaceId surface, int width, int height);
    bool TryGetPresentationSurface(PresentationSurfaceId surface, out PresentationSurfaceInfo info);
    bool BeginFrame(PresentationSurfaceId surface);
    bool EndFrame(PresentationSurfaceId surface);
}
