// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;

using Material = XEngine.Runtime.Resources.Material;
using Shader = XEngine.Runtime.Resources.Shader;

namespace XEngine.Runtime.Rendering;

public enum TonemapperType
{
    Melon,
    ACES,
    ACESSimple,
    AgX,
    ReinhardSimple,
    ReinhardLuma,
    ReinhardWhitePreserving,
    RomBinDaHouse,
    Uncharted2,
    /// <summary>Clamp linear scene color and encode it for display without a tone curve.</summary>
    None,
}

public sealed class TonemapperEffect : ImageEffect
{
    public override bool TransformsToLDR => true;

    public TonemapperType Type = TonemapperType.AgX;
    public float Contrast = 1.1f;
    public float Saturation = 1.1f;

    Material _mat;

    public override void OnRenderEffect(RenderContext context)
    {
            _mat ??= new Material(Shader.LoadDefault(DefaultShader.Tonemapper));

        TonemapperType type = ResolvedType();
        _mat.SetKeyword("TONEMAP_NONE", type == TonemapperType.None);
        _mat.SetKeyword("TONEMAP_MELON", type == TonemapperType.Melon);
        _mat.SetKeyword("TONEMAP_ACES", type == TonemapperType.ACES);
        _mat.SetKeyword("TONEMAP_ACES_SIMPLE", type == TonemapperType.ACESSimple);
        _mat.SetKeyword("TONEMAP_AGX", type == TonemapperType.AgX);
        _mat.SetKeyword("TONEMAP_REINHARD_SIMPLE", type == TonemapperType.ReinhardSimple);
        _mat.SetKeyword("TONEMAP_REINHARD_LUMA", type == TonemapperType.ReinhardLuma);
        _mat.SetKeyword("TONEMAP_REINHARD_WHITE", type == TonemapperType.ReinhardWhitePreserving);
        _mat.SetKeyword("TONEMAP_ROMBINDAHOUSE", type == TonemapperType.RomBinDaHouse);
        _mat.SetKeyword("TONEMAP_UNCHARTED2", type == TonemapperType.Uncharted2);

        _mat.SetFloat("Contrast", ResolvedContrast());
        _mat.SetFloat("Saturation", ResolvedSaturation());
        // This pass copies between scene render targets, whose storage coordinates match.
        // The shader uses fragment coordinates so it preserves rows on every viewport convention.
        _mat.SetFloat("_FlipY", 0f);

        // Create new LDR buffer to replace HDR
        var ldrBuffer = RenderTexture.GetTemporaryRT(
            context.Width,
            context.Height,
            true, // Keep depth for transparents
            [TextureImageFormat.Color4b] // LDR format
        );

        using var cmd = Graphics.GetCommandBuffer("Tonemapper");

        // Preserve depth so transparents drawn into the LDR buffer still occlude correctly.
        if (context.SceneColor.InternalDepth != null)
        {
            cmd.SetRenderTargets(ldrBuffer.frameBuffer, context.SceneColor.frameBuffer);
            cmd.BlitFramebuffer(
                0, 0, context.Width, context.Height,
                0, 0, context.Width, context.Height,
                ClearFlags.Depth, BlitFilter.Nearest);
        }

        cmd.Blit(context.SceneColor, ldrBuffer, _mat, 0);
        Graphics.Submit(cmd);

        context.ReplaceSceneColor(ldrBuffer);
    }

    internal TonemapperType ResolvedType()
    {
        if (UseVolumeOverrides)
        {
            TonemapVolumeComponent vol = VolumeManager.Instance.Get<TonemapVolumeComponent>();
            if (vol.OverrideType)
                return (TonemapperType)vol.Type;
        }

        return Type;
    }

    internal float ResolvedContrast()
    {
        if (UseVolumeOverrides)
        {
            TonemapVolumeComponent vol = VolumeManager.Instance.Get<TonemapVolumeComponent>();
            if (vol.OverrideContrast)
                return vol.Contrast;
        }

        return Contrast;
    }

    internal float ResolvedSaturation()
    {
        if (UseVolumeOverrides)
        {
            TonemapVolumeComponent vol = VolumeManager.Instance.Get<TonemapVolumeComponent>();
            if (vol.OverrideSaturation)
                return vol.Saturation;
        }

        return Saturation;
    }

    public override void OnDisable()
    {
        _mat?.Dispose();
        _mat = null;
    }
}
