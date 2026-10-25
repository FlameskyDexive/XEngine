// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Material = XEngine.Runtime.Resources.Material;
using Shader = XEngine.Runtime.Resources.Shader;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Per-pixel motion blur using the motion vectors buffer.
/// Samples the scene color along each pixel's motion vector to simulate camera
/// and object motion blur. Uses depth-aware weighting to reduce background
/// bleeding through foreground objects.
///
/// Motion vectors come from the unified prepass (always produced).
/// </summary>
public sealed class MotionBlurEffect : ImageEffect
{
    public override RenderFeatureRequirements Requirements =>
        RenderFeatureRequirements.Depth | RenderFeatureRequirements.MotionVectors;

    public override RenderStage Stage => RenderStage.PostProcess;

    /// <summary>Blur intensity multiplier. 1.0 = physically correct, higher = exaggerated.</summary>
    public float Intensity = 1.0f;

    /// <summary>Number of samples along the motion vector (4-16 typical). More = smoother but slower.</summary>
    public int Samples = 8;

    /// <summary>Maximum blur radius in pixels. Prevents extreme streaking.</summary>
    public float MaxBlurRadius = 40.0f;

    private Material _mat;

    public override void OnRenderEffect(RenderContext context)
    {
        _mat ??= new Material(Shader.LoadDefault(DefaultShader.MotionBlur));

        _mat.SetVector("_Resolution", new Float2(context.Width, context.Height));
        _mat.SetFloat("_Intensity", ResolvedIntensity());
        _mat.SetInt("_Samples", Maths.Clamp(ResolvedSamples(), 1, 32));
        _mat.SetFloat("_MaxBlurRadius", Math.Max(1.0f, ResolvedMaxBlurRadius()));

        if (context.MotionVectors != null)
            _mat.SetTexture("_MotionVectorsTex", context.MotionVectors);

        var temp = RenderTexture.GetTemporaryRT(context.Width, context.Height, false,
            [context.SceneColor.MainTexture.ImageFormat]);
        using var cmd = Graphics.GetCommandBuffer("MotionBlur");
        cmd.SetGlobalTexture("_CameraDepthTexture", context.DepthNormals.InternalDepth);
        cmd.Blit(context.SceneColor, temp, _mat, 0);
        cmd.ClearGlobalTexture("_CameraDepthTexture");
        cmd.Blit(temp, context.SceneColor, null, 0);
        Graphics.Submit(cmd);
        RenderTexture.ReleaseTemporaryRT(temp);
    }

    internal float ResolvedIntensity()
    {
        if (UseVolumeOverrides)
        {
            MotionBlurVolumeComponent vol = VolumeManager.Instance.Get<MotionBlurVolumeComponent>();
            if (vol.OverrideIntensity)
                return vol.Intensity;
        }

        return Intensity;
    }

    internal int ResolvedSamples()
    {
        if (UseVolumeOverrides)
        {
            MotionBlurVolumeComponent vol = VolumeManager.Instance.Get<MotionBlurVolumeComponent>();
            if (vol.OverrideSamples)
                return vol.Samples;
        }

        return Samples;
    }

    internal float ResolvedMaxBlurRadius()
    {
        if (UseVolumeOverrides)
        {
            MotionBlurVolumeComponent vol = VolumeManager.Instance.Get<MotionBlurVolumeComponent>();
            if (vol.OverrideMaxBlur)
                return vol.MaxBlurRadius;
        }

        return MaxBlurRadius;
    }

    public override void OnDisable()
    {
        _mat?.Dispose();
        _mat = null;
    }
}
