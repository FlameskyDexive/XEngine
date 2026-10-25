// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Per-pipeline camera targets. <see cref="SetReferenceSize"/> is the scaled camera pixel size
/// (already includes <see cref="UniversalRenderPipelineAsset.RenderScale"/> × <see cref="Camera.RenderScale"/>).
/// </summary>
public sealed class RTHandleSystem : IDisposable
{
    public RTHandle CameraColor { get; } = new(RTHandleNames.CameraColor);
    public RTHandle CameraPrepass { get; } = new(RTHandleNames.CameraPrepass);
    public RTHandle CameraOpaque { get; } = new(RTHandleNames.CameraOpaque);
    public RTHandle Light2D { get; } = new(RTHandleNames.Light2D);
    public RTHandle Shadow2D { get; } = new(RTHandleNames.Shadow2D);
    public RTHandle CameraResolve { get; } = new(RTHandleNames.CameraResolve);
    private RenderTexture? _presentation;
    internal bool IsPresentation(RenderTexture? target) => target != null && ReferenceEquals(target, _presentation);

    internal RenderTexture AllocPresentation(int width, int height, TextureImageFormat[] formats)
    {
        if (_presentation is { IsDisposed: false } current && current.Width == width && current.Height == height)
            return current;
        _presentation?.Dispose();
        _presentation = new RenderTexture(width, height, true, formats, 1, TextureImageFormat.Depth24Stencil8);
        return _presentation;
    }

    public int ReferenceWidth { get; private set; }
    public int ReferenceHeight { get; private set; }

    public void SetReferenceSize(int width, int height)
    {
        ReferenceWidth = width < 1 ? 1 : width;
        ReferenceHeight = height < 1 ? 1 : height;
    }

    /// <param name="sampleCount">MSAA sample count for the camera color target (1 = single-sample).
    /// Above 1 the target is multisampled and must be resolved before post processing samples it.</param>
    public RenderTexture AllocCameraColor(bool hasDepth, TextureImageFormat[] formats, int sampleCount = 1)
        => CameraColor.Alloc(ReferenceWidth, ReferenceHeight, hasDepth, formats, sampleCount);

    public RenderTexture AllocCameraPrepass(TextureImageFormat[] formats)
        => CameraPrepass.Alloc(ReferenceWidth, ReferenceHeight, true, formats);

    public RenderTexture AllocCameraOpaque(TextureImageFormat[] formats)
        => CameraOpaque.Alloc(ReferenceWidth, ReferenceHeight, false, formats);

    public RenderTexture AllocLight2D(TextureImageFormat[] formats)
        => Light2D.Alloc(ReferenceWidth, ReferenceHeight, false, formats);

    public RenderTexture AllocShadow2D(TextureImageFormat[] formats)
        => Shadow2D.Alloc(ReferenceWidth, ReferenceHeight, false, formats);

    public RenderTexture AllocCameraResolve(int width, int height, TextureImageFormat[] formats)
        => CameraResolve.Alloc(width, height, false, formats);

    public void ReleasePrepass() => CameraPrepass.Release();
    public void ReleaseOpaque() => CameraOpaque.Release();
    public void ReleaseLight2D()
    {
        Light2D.Release();
        Shadow2D.Release();
    }

    public void ReleaseAll()
    {
        _presentation?.Dispose();
        _presentation = null;
        CameraColor.Release();
        CameraPrepass.Release();
        CameraOpaque.Release();
        Light2D.Release();
        Shadow2D.Release();
        CameraResolve.Release();
    }

    public void Dispose()
    {
        _presentation?.Dispose();
        _presentation = null;
        CameraColor.Dispose();
        CameraPrepass.Dispose();
        CameraOpaque.Dispose();
        Light2D.Dispose();
        Shadow2D.Dispose();
        CameraResolve.Dispose();
    }
}
