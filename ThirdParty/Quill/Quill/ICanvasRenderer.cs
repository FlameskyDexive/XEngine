// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Quill;

/// <summary>
/// Interface for implementing a canvas renderer backend.
/// Implement this interface to provide rendering support for different graphics APIs (OpenGL, DirectX, Vulkan, etc.).
/// </summary>
public interface ICanvasRenderer : IDisposable
{
    /// <summary>
    /// Creates a new texture with the specified dimensions.
    /// </summary>
    /// <param name="width">The width of the texture in pixels.</param>
    /// <param name="height">The height of the texture in pixels.</param>
    /// <returns>A backend-specific texture object.</returns>
    public object CreateTexture(uint width, uint height);

    /// <summary>
    /// Gets the dimensions of a texture.
    /// </summary>
    /// <param name="texture">The texture object to query.</param>
    /// <returns>The size of the texture in pixels.</returns>
    public Int2 GetTextureSize(object texture);

    /// <summary>
    /// Updates a region of a texture with new pixel data.
    /// </summary>
    /// <param name="texture">The texture to update.</param>
    /// <param name="bounds">The rectangular region to update.</param>
    /// <param name="data">The pixel data in RGBA format (4 bytes per pixel).</param>
    public void SetTextureData(object texture, IntRect bounds, byte[] data);

    /// <summary>
    /// Renders the accumulated draw calls to the screen or render target.
    /// </summary>
    /// <param name="canvas">The canvas containing vertices and indices.</param>
    /// <param name="drawCalls">The list of draw calls to render.</param>
    public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls);

    /// <summary>
    /// Whether this backend implements backdrop blur (capturing and blurring the framebuffer
    /// behind a shape). When false, backdrop-blur draw calls degrade to flat tinted fills.
    /// </summary>
    bool SupportsBackdropBlur => false;
    /// <summary>Whether ordered render-layer markers are understood. Unsupported backends reject insertion.</summary>
    bool SupportsRenderLayers => false;

    /// <summary>Whether font atlas disposal is ordered after previously submitted draws.</summary>
    bool SupportsFontAtlasRetirement => false;
    /// <summary>Retire a font-owned texture after submission, preserving queued GPU users.</summary>
    void RetireFontAtlas(object texture) => throw new NotSupportedException("Font atlas retirement is unsupported.");
}
