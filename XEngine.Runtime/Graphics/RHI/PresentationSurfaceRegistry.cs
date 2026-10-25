using System;
using System.Collections.Generic;

namespace XEngine.Runtime.RHI;

/// <summary>
/// Backend-neutral lifecycle state for presentation surfaces.
/// WSI resource creation remains backend-owned; this class keeps identity,
/// visibility, and frame ownership rules consistent across backends.
/// </summary>
internal sealed class PresentationSurfaceRegistry
{
    private readonly Dictionary<PresentationSurfaceId, SurfaceState> _surfaces = new();
    private ulong _nextId;

    public PresentationSurfaceId Create(IWindowSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        (int width, int height) = surface.FramebufferSize;
        ValidateSize(width, height);
        PresentationSurfaceId id = new(++_nextId);
        _surfaces.Add(id, new SurfaceState(width, height));
        return id;
    }

    public bool Destroy(PresentationSurfaceId id)
    {
        if (!_surfaces.TryGetValue(id, out SurfaceState? state) || state.FrameBegun)
            return false;
        return _surfaces.Remove(id);
    }

    public bool Resize(PresentationSurfaceId id, int width, int height)
    {
        if (!_surfaces.TryGetValue(id, out SurfaceState? state))
            return false;
        ValidateSize(width, height);
        state.Width = width;
        state.Height = height;
        return true;
    }

    public bool TryGet(PresentationSurfaceId id, out PresentationSurfaceInfo info)
    {
        if (_surfaces.TryGetValue(id, out SurfaceState? state))
        {
            info = new PresentationSurfaceInfo(id, state.Width, state.Height, state.IsVisible);
            return true;
        }
        info = default;
        return false;
    }

    public bool Begin(PresentationSurfaceId id)
    {
        if (!_surfaces.TryGetValue(id, out SurfaceState? state)
            || !state.IsVisible
            || state.FrameBegun)
            return false;
        state.FrameBegun = true;
        return true;
    }

    public bool End(PresentationSurfaceId id)
    {
        if (!_surfaces.TryGetValue(id, out SurfaceState? state) || !state.FrameBegun)
            return false;
        state.FrameBegun = false;
        return true;
    }

    public void Clear() => _surfaces.Clear();

    private static void ValidateSize(int width, int height)
    {
        if (width < 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 0) throw new ArgumentOutOfRangeException(nameof(height));
    }

    private sealed class SurfaceState
    {
        public SurfaceState(int width, int height)
        {
            Width = width;
            Height = height;
        }

        public int Width;
        public int Height;
        public bool FrameBegun;
        public bool IsVisible => Width > 0 && Height > 0;
    }
}
