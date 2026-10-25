// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Maps a <see cref="GraphicsFrameBuffer"/> back to its owning <see cref="RenderTexture"/> so
/// the Frame Debugger can resolve a live preview texture for a captured render target.
/// Weak entries — registration never extends a target's lifetime.
/// </summary>
internal static class RenderTargetRegistry
{
    private static readonly ConditionalWeakTable<GraphicsFrameBuffer, RenderTexture> s_byFramebuffer = new();

    public static void Register(GraphicsFrameBuffer? framebuffer, RenderTexture owner)
    {
        if (framebuffer == null || owner == null) return;
        s_byFramebuffer.AddOrUpdate(framebuffer, owner);
    }

    public static RenderTexture? Resolve(GraphicsFrameBuffer? framebuffer)
    {
        if (framebuffer == null) return null;
        return s_byFramebuffer.TryGetValue(framebuffer, out RenderTexture? owner) ? owner : null;
    }
}
