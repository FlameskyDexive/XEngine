// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Backends.Metal;

/// <summary>
/// CPU-side pending load-op state for Metal render-pass recombination.
/// Universal Pipeline does <c>SetRT</c> → <c>Clear</c> → <c>SetRT</c> (same target)
/// before any draw; rebinding the same framebuffer must keep the pending clear.
/// </summary>
internal sealed class MetalPassRecombiner
{
    public GraphicsFrameBuffer? PendingTarget;
    public GraphicsFrameBuffer? PendingReadTarget;
    public bool ColorLoadClear;
    public bool DepthLoadClear;
    public bool StencilLoadClear;
    public float ColorR;
    public float ColorG;
    public float ColorB;
    public float ColorA;
    public float DepthClear = 1f;
    public int StencilClear;

    public bool HasPendingLoadOps => ColorLoadClear || DepthLoadClear || StencilLoadClear;

    public enum BindAction
    {
        KeepPending,
        FlushThenSwitch,
        SwitchAndReset,
    }

    public BindAction Bind(GraphicsFrameBuffer? framebuffer, bool passOpen)
    {
        bool same = ReferenceEquals(framebuffer, PendingTarget);
        if (same && !passOpen)
            return BindAction.KeepPending;
        if (!same && !passOpen && HasPendingLoadOps)
            return BindAction.FlushThenSwitch;
        return BindAction.SwitchAndReset;
    }

    public void ApplyBind(GraphicsFrameBuffer? framebuffer, BindAction action)
    {
        if (action == BindAction.KeepPending)
            return;
        PendingTarget = framebuffer;
        if (action == BindAction.SwitchAndReset)
            ResetLoadOps();
    }

    public void RecordClear(ClearFlags flags, float r, float g, float b, float a, float depth, int stencil)
    {
        if ((flags & ClearFlags.Color) != 0)
        {
            ColorLoadClear = true;
            ColorR = r;
            ColorG = g;
            ColorB = b;
            ColorA = a;
        }

        if ((flags & ClearFlags.Depth) != 0)
        {
            DepthLoadClear = true;
            DepthClear = depth;
        }

        if ((flags & ClearFlags.Stencil) != 0)
        {
            StencilLoadClear = true;
            StencilClear = stencil;
        }
    }

    public void ResetLoadOps()
    {
        ColorLoadClear = false;
        DepthLoadClear = false;
        StencilLoadClear = false;
        ColorR = 0;
        ColorG = 0;
        ColorB = 0;
        ColorA = 0;
        DepthClear = 1f;
        StencilClear = 0;
    }

    public void Reset()
    {
        PendingTarget = null;
        PendingReadTarget = null;
        ResetLoadOps();
    }
}
