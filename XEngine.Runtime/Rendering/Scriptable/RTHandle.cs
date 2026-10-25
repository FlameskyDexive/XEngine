// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using RenderTex = XEngine.Runtime.Resources.RenderTexture;
using Texture2D = XEngine.Runtime.Resources.Texture2D;

namespace XEngine.Runtime.Rendering;

/// <summary>Stable names for the forward camera targets (M4). Prepass is the existing MRT.</summary>
public static class RTHandleNames
{
    public const string CameraColor = "_CameraColorTexture";
    /// <summary>Unified prepass: depth + view-space normals + motion/roughness/metallic.</summary>
    public const string CameraPrepass = "_CameraDepthTexture";
    /// <summary>Opaque color copy for grab / refraction (M7).</summary>
    public const string CameraOpaque = "_CameraOpaqueTexture";
    /// <summary>Deferred GBuffer albedo (linear RGB) + AO (A). Attachment 2 when Deferred.</summary>
    public const string GBufferAlbedo = "_GBufferAlbedo";
    /// <summary>Deferred GBuffer emission RGB. Attachment 3 when Deferred.</summary>
    public const string GBufferEmission = "_GBufferEmission";
    /// <summary>2D additive light accumulation (M22).</summary>
    public const string Light2D = "_LightTexture2D";
    /// <summary>2D shadow mask; white is unshadowed (M22).</summary>
    public const string Shadow2D = "_ShadowMask2D";
    /// <summary>MSAA / TAA resolve target at viewrect size (M23).</summary>
    public const string CameraResolve = "_CameraResolve";
}

/// <summary>
/// Scale-aware camera render target. Reuses the same <see cref="XEngine.Runtime.Resources.RenderTexture"/> across
/// frames while width, height, depth, and color formats match — no per-frame
/// <c>new RenderTexture</c> on a stable camera.
/// </summary>
public sealed class RTHandle : IDisposable
{
    private static readonly List<RTHandle> s_tracked = new();

    private RenderTex? _rt;
    private TextureImageFormat[]? _formats;
    private bool _pooled;

    public RTHandle(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        lock (s_tracked)
            s_tracked.Add(this);
    }

    public string Name { get; }
    public RenderTex? RenderTexture => _rt;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasDepth { get; private set; }
    public float Scale { get; private set; } = 1f;

    /// <summary>Multisample count of the current target (1 = single-sample).</summary>
    public int SampleCount { get; private set; } = 1;

    /// <summary>Depth attachment when this handle is the unified prepass (or any depth RT).</summary>
    public Texture2D? DepthTexture => _rt is { IsDisposed: false } ? _rt.InternalDepth : null;

    /// <summary>Prepass attachment 0: view-space normals.</summary>
    public Texture2D? NormalTexture
        => _rt is { IsDisposed: false } && _rt.InternalTextures.Length > 0 ? _rt.InternalTextures[0] : null;

    /// <summary>Prepass attachment 1: motion (.rg) + roughness (.b) + metallic (.a).</summary>
    public Texture2D? MotionTexture
        => _rt is { IsDisposed: false } && _rt.InternalTextures.Length > 1 ? _rt.InternalTextures[1] : null;

    public bool Owns(RenderTex? rt) => rt != null && _rt == rt;

    /// <summary>Pixel size from a camera reference size and a relative scale (round-to-nearest, min 1).</summary>
    public static int ScaledSize(int referencePixels, float scale)
    {
        if (referencePixels < 1)
            referencePixels = 1;
        if (!float.IsFinite(scale) || scale <= 0f)
            scale = 1f;
        int size = (int)(referencePixels * scale + 0.5f);
        return size < 1 ? 1 : size;
    }

    /// <summary>
    /// Ensure a target of <paramref name="width"/>×<paramref name="height"/>. Returns the existing
    /// texture when the descriptor matches.
    /// </summary>
    public RenderTex Alloc(int width, int height, bool hasDepth, TextureImageFormat[] formats, int sampleCount = 1)
    {
        if (formats == null)
            throw new ArgumentNullException(nameof(formats));
        if (width < 1) width = 1;
        if (height < 1) height = 1;
        if (sampleCount < 1) sampleCount = 1;

        if (CanReuse(width, height, hasDepth, formats, sampleCount))
        {
            RenderStats.RecordRTHandleReuse();
            return _rt!;
        }

        Release();
        _rt = new RenderTex(width, height, hasDepth, formats, sampleCount);
        _rt.SetDebugName(Name);
        _pooled = false;
        Width = width;
        Height = height;
        HasDepth = hasDepth;
        SampleCount = sampleCount;
        _formats = formats;
        RenderStats.RecordRTHandleAlloc();
        return _rt;
    }

    /// <summary>Allocate from a camera reference size using <see cref="Scale"/>.</summary>
    public RenderTex AllocScaled(
        int referenceWidth,
        int referenceHeight,
        float scale,
        bool hasDepth,
        TextureImageFormat[] formats)
    {
        Scale = scale;
        return Alloc(ScaledSize(referenceWidth, scale), ScaledSize(referenceHeight, scale), hasDepth, formats);
    }

    /// <summary>
    /// Take ownership of an existing target (for example a post-process replacement).
    /// Pooled textures are claimed so the 3-frame leak detector will not dispose them.
    /// </summary>
    public void Adopt(RenderTex rt, bool pooled)
    {
        if (rt == null)
            throw new ArgumentNullException(nameof(rt));
        if (_rt == rt)
        {
            _pooled = pooled;
            if (pooled)
                RenderTex.ClaimTemporaryRT(rt);
            return;
        }

        Release();
        _rt = rt;
        _rt.SetDebugName(Name);
        _pooled = pooled;
        Width = rt.Width;
        Height = rt.Height;
        HasDepth = rt.HasDepthAttachment;
        SampleCount = rt.SampleCount;
        _formats = rt.ColorFormats;
        if (pooled)
            RenderTex.ClaimTemporaryRT(rt);
        RenderStats.RecordRTHandleAlloc();
    }

    public void Release()
    {
        if (_rt == null)
            return;

        RenderTex rt = _rt;
        bool pooled = _pooled;
        _rt = null;
        Width = 0;
        Height = 0;
        HasDepth = false;
        SampleCount = 1;
        _formats = null;
        _pooled = false;

        if (!rt.IsDisposed)
        {
            if (pooled)
                RenderTex.ReleaseTemporaryRT(rt);
            else
                rt.Dispose();
        }

        RenderStats.RecordRTHandleRelease();
    }

    public void Dispose()
    {
        Release();
        lock (s_tracked)
            s_tracked.Remove(this);
    }

    /// <summary>Return every live handle to the pool or dispose it (device shutdown).</summary>
    internal static void ReleaseAllTracked()
    {
        lock (s_tracked)
        {
            for (int i = 0; i < s_tracked.Count; i++)
                s_tracked[i].Release();
        }
    }

    private bool CanReuse(int width, int height, bool hasDepth, TextureImageFormat[] formats, int sampleCount)
    {
        if (_rt == null || _rt.IsDisposed)
            return false;
        if (Width != width || Height != height || HasDepth != hasDepth || SampleCount != sampleCount)
            return false;
        return FormatsMatch(_formats, formats);
    }

    private static bool FormatsMatch(TextureImageFormat[]? stored, TextureImageFormat[] formats)
    {
        if (stored == null)
            return false;
        if (ReferenceEquals(stored, formats))
            return true;
        if (stored.Length != formats.Length)
            return false;
        for (int i = 0; i < stored.Length; i++)
        {
            if (stored[i] != formats[i])
                return false;
        }
        return true;
    }
}
