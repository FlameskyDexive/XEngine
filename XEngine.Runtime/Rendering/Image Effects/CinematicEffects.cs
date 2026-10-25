// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Material = XEngine.Runtime.Resources.Material;
using Shader = XEngine.Runtime.Resources.Shader;

namespace XEngine.Runtime.Rendering;

[Flags]
internal enum CinematicEffectFlags
{
    None = 0,
    Vignette = 1 << 0,
    ChromaticAberration = 1 << 1,
    FilmGrain = 1 << 2,
    ColorGrading = 1 << 3,
    Lut = 1 << 4,
    Sharpen = 1 << 5,
    EdgeDetection = 1 << 6,
    Pixelation = 1 << 7,
    GodRays = 1 << 8,
    All = (1 << 9) - 1,
}

/// <summary>
/// Uber post-processing effect combining multiple cinematic effects into a single pass.
/// Each effect can be toggled independently via a uniform bitmask.
/// </summary>
public sealed class CinematicEffects : ImageEffect
{
    // ── Vignette ──────────────────────────────────────────────
    public bool EnableVignette = false;
    public float VignetteIntensity = 0.4f;
    public float VignetteSmoothness = 0.2f;
    public float VignetteRoundness = 1.0f;

    // ── Chromatic Aberration ─────────────────────────────────
    public bool EnableChromaticAberration = false;
    /// <summary>Maximum channel offset in pixels.</summary>
    public float ChromaticIntensity = 3.0f;
    /// <summary>Barrel distortion amount for chromatic aberration (0 = linear, higher = more curved).</summary>
    public float ChromaticDistortion = 0.5f;

    // ── Film Grain ───────────────────────────────────────────
    public bool EnableFilmGrain = false;
    public float GrainIntensity = 0.15f;
    /// <summary>Grain luminance sensitivity (0 = uniform across all tones, 1 = only in highlights).</summary>
    public float GrainResponse = 0.5f;

    // ── Color Grading ────────────────────────────────────────
    public bool EnableColorGrading = false;
    /// <summary>Post-exposure adjustment in EV stops.</summary>
    public float PostExposure = 0.0f;
    /// <summary>Contrast adjustment (-1 to 1).</summary>
    public float Contrast = 0.0f;
    /// <summary>Saturation adjustment (-1 to 1). -1 = grayscale.</summary>
    public float Saturation = 0.0f;
    /// <summary>Color temperature shift (-1 = cool/blue, 1 = warm/orange).</summary>
    public float Temperature = 0.0f;
    /// <summary>Lift (shadows) color offset. Default black = no change.</summary>
    public Color Lift = new Color(0, 0, 0, 0);
    /// <summary>Gamma (midtones) color offset. Default black = no change.</summary>
    public Color Gamma = new Color(0, 0, 0, 0);
    /// <summary>Gain (highlights) color offset. Default black = no change.</summary>
    public Color Gain = new Color(0, 0, 0, 0);

    // ── LUT Color Grading ────────────────────────────────────
    public bool EnableLUT = false;
    /// <summary>LUT texture (strip format, e.g. 256x16 for a 16x16x16 LUT).</summary>
    public AssetRef<Texture2D> LUTTexture;
    /// <summary>How much of the LUT to apply (0 = original, 1 = full LUT).</summary>
    public float LUTContribution = 1.0f;

    // ── Sharpen (CAS) ────────────────────────────────────────
    public bool EnableSharpen = false;
    /// <summary>CAS sharpening amount (0 = minimum, 1 = maximum).</summary>
    public float SharpenAmount = 0.5f;
    /// <summary>CAS sample radius (1 = standard, higher = wider sharpening kernel).</summary>
    public float SharpenRadius = 1.0f;

    // ── Sobel Edge Detection ─────────────────────────────────
    public bool EnableEdgeDetection = false;
    public float EdgeIntensity = 1.0f;
    public Color EdgeColor = new Color(0, 0, 0, 1);
    /// <summary>How much of the original image to keep (0 = edges only, 1 = full blend).</summary>
    public float EdgeBackgroundFade = 1.0f;

    // ── Pixelation ───────────────────────────────────────────
    public bool EnablePixelation = false;
    public float PixelSize = 4.0f;

    // ── God Rays / Light Shafts ──────────────────────────────
    public bool EnableGodRays = false;
    public float GodRayIntensity = 0.5f;
    /// <summary>How quickly rays fade with distance from the sun.</summary>
    public float GodRayDecay = 0.96f;
    /// <summary>Spacing between samples along the ray.</summary>
    public float GodRayDensity = 0.5f;
    /// <summary>Per-sample brightness multiplier.</summary>
    public float GodRayWeight = 0.6f;
    /// <summary>Number of ray marching samples (8-128).</summary>
    public int GodRaySamples = 64;
    /// <summary>Luminance threshold only pixels brighter than this contribute to rays.</summary>
    public float GodRayThreshold = 0.8f;

    private Material _mat;

    public override void OnRenderEffect(RenderContext context)
    {
        _mat ??= new Material(Shader.LoadDefault(DefaultShader.CinematicEffects));
        Texture2D? lutTexture = LUTTexture.Res;
        bool useLut = EnableLUT && lutTexture != null;

        VignetteVolumeComponent? vig = UseVolumeOverrides
            ? VolumeManager.Instance.Get<VignetteVolumeComponent>()
            : null;
        ColorAdjustmentsVolumeComponent? grade = UseVolumeOverrides
            ? VolumeManager.Instance.Get<ColorAdjustmentsVolumeComponent>()
            : null;
        bool vignette = EnableVignette || (vig?.WantsEffect ?? false);
        bool grading = EnableColorGrading || (grade?.WantsEffect ?? false);

        _mat.SetInt("_EffectFlags", (int)BuildEffectFlags(useLut, vignette, grading));

        // Common uniforms (_Time is already set globally via ShaderVariables)
        _mat.SetVector("_Resolution", new Float2(context.Width, context.Height));

        // Vignette
        if (vignette)
        {
            _mat.SetFloat("_VignetteIntensity", vig is { OverrideIntensity: true } ? vig.Intensity : VignetteIntensity);
            _mat.SetFloat("_VignetteSmoothness", vig is { OverrideSmoothness: true } ? vig.Smoothness : VignetteSmoothness);
            _mat.SetFloat("_VignetteRoundness", vig is { OverrideRoundness: true } ? vig.Roundness : VignetteRoundness);
        }

        // Chromatic Aberration
        if (EnableChromaticAberration)
        {
            _mat.SetFloat("_ChromaticIntensity", ChromaticIntensity);
            _mat.SetFloat("_ChromaticDistortion", ChromaticDistortion);
        }

        // Film Grain
        if (EnableFilmGrain)
        {
            _mat.SetFloat("_GrainIntensity", GrainIntensity);
            _mat.SetFloat("_GrainResponse", GrainResponse);
        }

        // Color Grading
        if (grading)
        {
            _mat.SetFloat("_PostExposure", grade is { OverrideExposure: true } ? grade.PostExposure : PostExposure);
            _mat.SetFloat("_Contrast", grade is { OverrideContrast: true } ? grade.Contrast : Contrast);
            _mat.SetFloat("_Saturation", grade is { OverrideSaturation: true } ? grade.Saturation : Saturation);
            _mat.SetFloat("_Temperature", grade is { OverrideTemperature: true } ? grade.Temperature : Temperature);
            _mat.SetColor("_Lift", Lift);
            _mat.SetColor("_Gamma", Gamma);
            _mat.SetColor("_Gain", Gain);
        }

        // LUT
        if (useLut)
        {
            _mat.SetTexture("_LUTTex", lutTexture!);
            _mat.SetFloat("_LUTContribution", LUTContribution);
            _mat.SetFloat("_LUTSize", lutTexture!.Height);
        }
        else
            _mat.SetTexture("_LUTTex", context.SceneColor.MainTexture);

        // Sharpen (CAS)
        if (EnableSharpen)
        {
            _mat.SetFloat("_SharpenAmount", SharpenAmount);
            _mat.SetFloat("_SharpenRadius", MathF.Max(1.0f, SharpenRadius));
        }

        // Edge Detection
        if (EnableEdgeDetection)
        {
            _mat.SetFloat("_EdgeIntensity", EdgeIntensity);
            _mat.SetColor("_EdgeColor", EdgeColor);
            _mat.SetFloat("_EdgeBackgroundFade", EdgeBackgroundFade);
        }

        // Pixelation
        if (EnablePixelation)
        {
            _mat.SetFloat("_PixelSize", MathF.Max(1.0f, PixelSize));
        }

        // God Rays
        if (EnableGodRays)
        {
            _mat.SetFloat("_GodRayIntensity", GodRayIntensity);
            _mat.SetFloat("_GodRayDecay", GodRayDecay);
            _mat.SetFloat("_GodRayDensity", GodRayDensity);
            _mat.SetFloat("_GodRayWeight", GodRayWeight);
            _mat.SetInt("_GodRaySamples", Math.Clamp(GodRaySamples, 8, 128));
            _mat.SetFloat("_GodRayThreshold", GodRayThreshold);

            // Project the sun direction into screen space
            var camera = context.Camera;
            Float3 sunDir = GetSunDirection(camera);
            Float4x4 vp = camera.ViewMatrix * camera.ProjectionMatrix;
            Float3 sunWorld = camera.Transform.Position - sunDir * 10000f;
            Float4 clip = Float4x4.TransformPoint(new Float4(sunWorld.X, sunWorld.Y, sunWorld.Z, 1.0f), vp);
            Float2 sunScreen = new Float2(clip.X / clip.W * 0.5f + 0.5f, clip.Y / clip.W * 0.5f + 0.5f);
            _mat.SetVector("_SunScreenPos", sunScreen);

            if (context.DepthNormals != null && context.DepthNormals.IsValid())
                _mat.SetTexture("_CameraDepthTexture", context.DepthNormals.InternalDepth);
            else
                _mat.SetTexture("_CameraDepthTexture", context.SceneColor.MainTexture);
        }
        else
            _mat.SetTexture("_CameraDepthTexture", context.SceneColor.MainTexture);

        // Blit through temp RT to avoid reading and writing the same texture
        var temp = RenderTexture.GetTemporaryRT(context.Width, context.Height, false, [context.SceneColor.MainTexture.ImageFormat]);
        using var cmd = Graphics.GetCommandBuffer("Cinematic");
        cmd.Blit(context.SceneColor, temp, _mat, 0);
        cmd.Blit(temp, context.SceneColor, null, 0);
        Graphics.Submit(cmd);
        RenderTexture.ReleaseTemporaryRT(temp);
    }

    internal CinematicEffectFlags BuildEffectFlags(bool useLut)
        => BuildEffectFlags(useLut, EnableVignette, EnableColorGrading);

    internal CinematicEffectFlags BuildEffectFlags(bool useLut, bool vignette, bool grading)
    {
        CinematicEffectFlags flags = CinematicEffectFlags.None;
        if (vignette) flags |= CinematicEffectFlags.Vignette;
        if (EnableChromaticAberration) flags |= CinematicEffectFlags.ChromaticAberration;
        if (EnableFilmGrain) flags |= CinematicEffectFlags.FilmGrain;
        if (grading) flags |= CinematicEffectFlags.ColorGrading;
        if (useLut) flags |= CinematicEffectFlags.Lut;
        if (EnableSharpen) flags |= CinematicEffectFlags.Sharpen;
        if (EnableEdgeDetection) flags |= CinematicEffectFlags.EdgeDetection;
        if (EnablePixelation) flags |= CinematicEffectFlags.Pixelation;
        if (EnableGodRays) flags |= CinematicEffectFlags.GodRays;
        return flags;
    }

    public override void OnDisable()
    {
        _mat?.Dispose();
        _mat = null;
    }

    private static Float3 GetSunDirection(Camera camera)
    {
        var scene = Resources.Scene.Current;
        if (scene != null)
        {
            foreach (var go in scene.ActiveObjects)
            {
                var light = go.GetComponent<DirectionalLight>();
                if (light != null && light.EnabledInHierarchy)
                    return light.GetLightDirection();
            }
        }
        return Float3.Normalize(new Float3(0.5f, -0.7f, 0.5f));
    }
}
