// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime.Resources;

using Material = XEngine.Runtime.Resources.Material;
using Shader = XEngine.Runtime.Resources.Shader;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Bloom effect using dual filtering (downsample + upsample) for fast, high-quality blur.
/// Much faster than separable Gaussian or Kawase ping-pong at equivalent quality.
/// </summary>
public sealed class BloomEffect : ImageEffect
{
    /// <summary>Bloom intensity multiplier.</summary>
    public float Intensity = 1f;

    /// <summary>Luminance threshold for bright pixel extraction.</summary>
    public float Threshold = 0.6f;

    /// <summary>Number of downsample iterations. More = wider bloom but more GPU cost. 4-8 is typical.</summary>
    public int Iterations = 4;

    private Material _mat;
    // Reused across frames: RenderTextureKey stores the format array by reference (content-
    // compared), so the array is recreated when the format changes and never mutated in place.
    private readonly List<RenderTexture> _mipChain = new(8);
    private TextureImageFormat[] _formatArray = [];

    public override void OnRenderEffect(RenderContext context)
    {
        _mat ??= new Material(Shader.LoadDefault(DefaultShader.Bloom));

        int w = context.Width / 2;
        int h = context.Height / 2;
        var format = context.SceneColor.MainTexture.ImageFormat;
        if (_formatArray.Length == 0 || _formatArray[0] != format)
            _formatArray = [format];

        using var cmd = Graphics.GetCommandBuffer("Bloom");

        // Pass 0: Threshold extract bright pixels into half-res
        RenderTexture thresholdRT = RenderTexture.GetTemporaryRT(w, h, false, _formatArray);
        _mat.SetFloat("_Threshold", ResolvedThreshold());
        cmd.Blit(context.SceneColor, thresholdRT, _mat, 0);

        // Downsample chain each iteration halves resolution
        List<RenderTexture> mipChain = _mipChain;
        mipChain.Clear();
        mipChain.Add(thresholdRT);

        RenderTexture current = thresholdRT;
        int iterations = ResolvedIterations();
        for (int i = 0; i < iterations; i++)
        {
            w = System.Math.Max(1, w / 2);
            h = System.Math.Max(1, h / 2);

            RenderTexture downRT = RenderTexture.GetTemporaryRT(w, h, false, _formatArray);
            cmd.Blit(current, downRT, _mat, 1); // Pass 1: Downsample
            mipChain.Add(downRT);
            current = downRT;
        }

        // Upsample chain walk back up, each iteration doubles resolution
        for (int i = mipChain.Count - 1; i > 0; i--)
        {
            RenderTexture src = mipChain[i];
            RenderTexture dst = mipChain[i - 1];

            cmd.Blit(src, dst, _mat, 2); // Pass 2: Upsample (overwrites dst)
        }

        // Pass 3: composite bloom over the scene into a fresh buffer and hand THAT forward
        // (Tonemapper's ReplaceSceneColor idiom). The previous tail blitted the composite back
        // into SceneColor — a redundant full-resolution round trip every frame.
        _mat.SetTexture("_BloomTex", mipChain[0].MainTexture);
        _mat.SetFloat("_Intensity", ResolvedIntensity());
        bool hasDepth = context.SceneColor.InternalDepth != null;
        var composited = RenderTexture.GetTemporaryRT(context.Width, context.Height, hasDepth, _formatArray);
        if (hasDepth)
        {
            // Preserve depth so later passes drawn into the replacement still occlude correctly.
            cmd.SetRenderTargets(composited.frameBuffer, context.SceneColor.frameBuffer);
            cmd.BlitFramebuffer(
                0, 0, context.Width, context.Height,
                0, 0, context.Width, context.Height,
                ClearFlags.Depth, BlitFilter.Nearest);
        }
        cmd.Blit(context.SceneColor, composited, _mat, 3);
        Graphics.Submit(cmd);

        // Release all mip chain RTs
        foreach (var rt in mipChain)
            RenderTexture.ReleaseTemporaryRT(rt);
        mipChain.Clear();

        context.ReplaceSceneColor(composited);
    }

    internal float ResolvedIntensity()
    {
        if (UseVolumeOverrides)
        {
            BloomVolumeComponent vol = VolumeManager.Instance.Get<BloomVolumeComponent>();
            if (vol.OverrideIntensity)
                return vol.Intensity;
        }

        return Intensity;
    }

    internal float ResolvedThreshold()
    {
        if (UseVolumeOverrides)
        {
            BloomVolumeComponent vol = VolumeManager.Instance.Get<BloomVolumeComponent>();
            if (vol.OverrideThreshold)
                return vol.Threshold;
        }

        return Threshold;
    }

    internal int ResolvedIterations()
    {
        if (UseVolumeOverrides)
        {
            BloomVolumeComponent vol = VolumeManager.Instance.Get<BloomVolumeComponent>();
            if (vol.OverrideIterations)
                return vol.Iterations;
        }

        return Iterations;
    }

    public override void OnDisable()
    {
        _mat?.Dispose();
        _mat = null;
    }
}
