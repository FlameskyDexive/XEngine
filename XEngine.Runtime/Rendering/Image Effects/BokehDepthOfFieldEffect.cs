// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Material = XEngine.Runtime.Resources.Material;
using Shader = XEngine.Runtime.Resources.Shader;

namespace XEngine.Runtime.Rendering;

public sealed class BokehDepthOfFieldEffect : ImageEffect
{
    public override RenderFeatureRequirements Requirements =>
        UseAutoFocus ? RenderFeatureRequirements.Depth : RenderFeatureRequirements.None;

    public enum ResolutionMode
    {
        Full = 1,
        Half = 2,
        Quarter = 4,
        Eighth = 8
    }

    public bool UseAutoFocus = true;
    public float ManualFocusPoint = 0.5f;
    public float FocusStrength = 200.0f;
    public float MaxBlurRadius = 4.0f;
    public ResolutionMode Resolution = ResolutionMode.Quarter;

    private Material _mat;

    public override void OnRenderEffect(RenderContext context)
    {
        _mat ??= new Material(Shader.LoadDefault(DefaultShader.BokehDoF));

        int fullWidth = context.Width;
        int fullHeight = context.Height;
        int divisor = (int)Resolution;
        int blurWidth = fullWidth / divisor;
        int blurHeight = fullHeight / divisor;

        // Create MRT render texture for horizontal pass (3 color attachments for R, G, B)
        // Use floating point format to store complex number values (can be negative)
        RenderTexture horizontalMRT = RenderTexture.GetTemporaryRT(blurWidth, blurHeight, false, [
            TextureImageFormat.Short4,
            TextureImageFormat.Short4,
            TextureImageFormat.Short4
        ]);

        // Create vertical result texture
        RenderTexture verticalResult = RenderTexture.GetTemporaryRT(blurWidth, blurHeight, false, [context.SceneColor.MainTexture.ImageFormat]);

        // Set common shader properties
        _mat.SetFloat("_FocusStrength", ResolvedFocusStrength());
        _mat.SetFloat("_ManualFocusPoint", ResolvedManualFocusPoint());
        _mat.SetFloat("_MaxBlurRadius", ResolvedMaxBlurRadius());
        _mat.SetKeyword("AUTOFOCUS", ResolvedUseAutoFocus());

        // Set resolution for blur passes
        _mat.SetVector("_Resolution", new Float2(blurWidth, blurHeight));

        using var cmd = Graphics.GetCommandBuffer("BokehDoF");

        // Pass 0: Horizontal MRT - outputs to 3 render targets (R, G, B channels)
        _mat.SetTexture("_MainTex", context.SceneColor.MainTexture);
        cmd.Blit(horizontalMRT, _mat, 0);

        // Pass 1: Vertical Composite - reads from 3 horizontal textures and combines
        _mat.SetTexture("_HorizR", horizontalMRT.InternalTextures[0]);
        _mat.SetTexture("_HorizG", horizontalMRT.InternalTextures[1]);
        _mat.SetTexture("_HorizB", horizontalMRT.InternalTextures[2]);
        cmd.Blit(verticalResult, _mat, 1);

        // Pass 2: Final Combine - blend with original image based on CoC (at full resolution)
        _mat.SetTexture("_MainTex", context.SceneColor.MainTexture);
        _mat.SetTexture("_BlurredTex", verticalResult.MainTexture);
        _mat.SetVector("_Resolution", new Float2(fullWidth, fullHeight));
        var temp = RenderTexture.GetTemporaryRT(fullWidth, fullHeight, false, [context.SceneColor.MainTexture.ImageFormat]);
        cmd.Blit(context.SceneColor, temp, _mat, 2);
        cmd.Blit(temp, context.SceneColor, null, 0);
        Graphics.Submit(cmd);
        RenderTexture.ReleaseTemporaryRT(temp);

        // Clean up MRT
        RenderTexture.ReleaseTemporaryRT(horizontalMRT);
        RenderTexture.ReleaseTemporaryRT(verticalResult);
    }

    internal bool ResolvedUseAutoFocus()
    {
        if (UseVolumeOverrides)
        {
            DepthOfFieldVolumeComponent vol = VolumeManager.Instance.Get<DepthOfFieldVolumeComponent>();
            if (vol.OverrideFocus)
                return vol.UseAutoFocus;
        }

        return UseAutoFocus;
    }

    internal float ResolvedManualFocusPoint()
    {
        if (UseVolumeOverrides)
        {
            DepthOfFieldVolumeComponent vol = VolumeManager.Instance.Get<DepthOfFieldVolumeComponent>();
            if (vol.OverrideFocus)
                return vol.ManualFocusPoint;
        }

        return ManualFocusPoint;
    }

    internal float ResolvedFocusStrength()
    {
        if (UseVolumeOverrides)
        {
            DepthOfFieldVolumeComponent vol = VolumeManager.Instance.Get<DepthOfFieldVolumeComponent>();
            if (vol.OverrideFocus)
                return vol.FocusStrength;
        }

        return FocusStrength;
    }

    internal float ResolvedMaxBlurRadius()
    {
        if (UseVolumeOverrides)
        {
            DepthOfFieldVolumeComponent vol = VolumeManager.Instance.Get<DepthOfFieldVolumeComponent>();
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
