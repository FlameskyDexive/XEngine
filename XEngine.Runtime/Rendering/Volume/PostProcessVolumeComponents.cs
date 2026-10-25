// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>Blended Bloom settings (M10). Drives <see cref="BloomEffect"/> when overrides are set.</summary>
public sealed class BloomVolumeComponent : VolumeComponent
{
    public bool OverrideIntensity;
    public float Intensity = 1f;
    public bool OverrideThreshold;
    public float Threshold = 0.6f;
    public bool OverrideIterations;
    public int Iterations = 4;

    public bool WantsEffect => Active && (OverrideIntensity || OverrideThreshold || OverrideIterations);

    public override void Interp(VolumeComponent state, float interpFactor)
    {
        if (state is not BloomVolumeComponent other)
            return;
        InterpFloat(ref Intensity, ref OverrideIntensity, other.Intensity, other.OverrideIntensity, interpFactor);
        InterpFloat(ref Threshold, ref OverrideThreshold, other.Threshold, other.OverrideThreshold, interpFactor);
        InterpInt(ref Iterations, ref OverrideIterations, other.Iterations, other.OverrideIterations, interpFactor);
    }

    public override void ResetToDefault()
    {
        OverrideIntensity = false;
        Intensity = 1f;
        OverrideThreshold = false;
        Threshold = 0.6f;
        OverrideIterations = false;
        Iterations = 4;
    }
}

/// <summary>Blended tonemapper settings (M10).</summary>
public sealed class TonemapVolumeComponent : VolumeComponent
{
    public bool OverrideType;
    public int Type = (int)TonemapperType.AgX;
    public bool OverrideContrast;
    public float Contrast = 1.1f;
    public bool OverrideSaturation;
    public float Saturation = 1.1f;

    public bool WantsEffect => Active && (OverrideType || OverrideContrast || OverrideSaturation);

    public override void Interp(VolumeComponent state, float interpFactor)
    {
        if (state is not TonemapVolumeComponent other)
            return;
        InterpInt(ref Type, ref OverrideType, other.Type, other.OverrideType, interpFactor);
        InterpFloat(ref Contrast, ref OverrideContrast, other.Contrast, other.OverrideContrast, interpFactor);
        InterpFloat(ref Saturation, ref OverrideSaturation, other.Saturation, other.OverrideSaturation, interpFactor);
    }

    public override void ResetToDefault()
    {
        OverrideType = false;
        Type = (int)TonemapperType.AgX;
        OverrideContrast = false;
        Contrast = 1.1f;
        OverrideSaturation = false;
        Saturation = 1.1f;
    }
}

/// <summary>Blended bokeh DoF settings (M10).</summary>
public sealed class DepthOfFieldVolumeComponent : VolumeComponent
{
    public bool OverrideFocus;
    public bool UseAutoFocus = true;
    public float ManualFocusPoint = 0.5f;
    public float FocusStrength = 200f;
    public bool OverrideMaxBlur;
    public float MaxBlurRadius = 4f;

    public bool WantsEffect => Active && (OverrideFocus || OverrideMaxBlur);

    public override void Interp(VolumeComponent state, float interpFactor)
    {
        if (state is not DepthOfFieldVolumeComponent other)
            return;
        InterpBool(ref UseAutoFocus, ref OverrideFocus, other.UseAutoFocus, other.OverrideFocus, interpFactor);
        InterpFloat(ref ManualFocusPoint, ref OverrideFocus, other.ManualFocusPoint, other.OverrideFocus, interpFactor);
        InterpFloat(ref FocusStrength, ref OverrideFocus, other.FocusStrength, other.OverrideFocus, interpFactor);
        InterpFloat(ref MaxBlurRadius, ref OverrideMaxBlur, other.MaxBlurRadius, other.OverrideMaxBlur, interpFactor);
    }

    public override void ResetToDefault()
    {
        OverrideFocus = false;
        UseAutoFocus = true;
        ManualFocusPoint = 0.5f;
        FocusStrength = 200f;
        OverrideMaxBlur = false;
        MaxBlurRadius = 4f;
    }
}

/// <summary>Blended motion-blur settings (M10).</summary>
public sealed class MotionBlurVolumeComponent : VolumeComponent
{
    public bool OverrideIntensity;
    public float Intensity = 1f;
    public bool OverrideSamples;
    public int Samples = 8;
    public bool OverrideMaxBlur;
    public float MaxBlurRadius = 40f;

    public bool WantsEffect => Active && (OverrideIntensity || OverrideSamples || OverrideMaxBlur);

    public override void Interp(VolumeComponent state, float interpFactor)
    {
        if (state is not MotionBlurVolumeComponent other)
            return;
        InterpFloat(ref Intensity, ref OverrideIntensity, other.Intensity, other.OverrideIntensity, interpFactor);
        InterpInt(ref Samples, ref OverrideSamples, other.Samples, other.OverrideSamples, interpFactor);
        InterpFloat(ref MaxBlurRadius, ref OverrideMaxBlur, other.MaxBlurRadius, other.OverrideMaxBlur, interpFactor);
    }

    public override void ResetToDefault()
    {
        OverrideIntensity = false;
        Intensity = 1f;
        OverrideSamples = false;
        Samples = 8;
        OverrideMaxBlur = false;
        MaxBlurRadius = 40f;
    }
}

/// <summary>Blended color grading (M10). Drives <see cref="CinematicEffects"/> color-grading uniforms.</summary>
public sealed class ColorAdjustmentsVolumeComponent : VolumeComponent
{
    public bool OverrideExposure;
    public float PostExposure;
    public bool OverrideContrast;
    public float Contrast;
    public bool OverrideSaturation;
    public float Saturation;
    public bool OverrideTemperature;
    public float Temperature;

    public bool WantsEffect => Active && (OverrideExposure || OverrideContrast || OverrideSaturation || OverrideTemperature);

    public override void Interp(VolumeComponent state, float interpFactor)
    {
        if (state is not ColorAdjustmentsVolumeComponent other)
            return;
        InterpFloat(ref PostExposure, ref OverrideExposure, other.PostExposure, other.OverrideExposure, interpFactor);
        InterpFloat(ref Contrast, ref OverrideContrast, other.Contrast, other.OverrideContrast, interpFactor);
        InterpFloat(ref Saturation, ref OverrideSaturation, other.Saturation, other.OverrideSaturation, interpFactor);
        InterpFloat(ref Temperature, ref OverrideTemperature, other.Temperature, other.OverrideTemperature, interpFactor);
    }

    public override void ResetToDefault()
    {
        OverrideExposure = false;
        PostExposure = 0f;
        OverrideContrast = false;
        Contrast = 0f;
        OverrideSaturation = false;
        Saturation = 0f;
        OverrideTemperature = false;
        Temperature = 0f;
    }
}

/// <summary>Blended vignette (M10). Drives <see cref="CinematicEffects"/> vignette uniforms.</summary>
public sealed class VignetteVolumeComponent : VolumeComponent
{
    public bool OverrideIntensity;
    public float Intensity = 0.4f;
    public bool OverrideSmoothness;
    public float Smoothness = 0.2f;
    public bool OverrideRoundness;
    public float Roundness = 1f;

    public bool WantsEffect => Active && (OverrideIntensity || OverrideSmoothness || OverrideRoundness);

    public override void Interp(VolumeComponent state, float interpFactor)
    {
        if (state is not VignetteVolumeComponent other)
            return;
        InterpFloat(ref Intensity, ref OverrideIntensity, other.Intensity, other.OverrideIntensity, interpFactor);
        InterpFloat(ref Smoothness, ref OverrideSmoothness, other.Smoothness, other.OverrideSmoothness, interpFactor);
        InterpFloat(ref Roundness, ref OverrideRoundness, other.Roundness, other.OverrideRoundness, interpFactor);
    }

    public override void ResetToDefault()
    {
        OverrideIntensity = false;
        Intensity = 0.4f;
        OverrideSmoothness = false;
        Smoothness = 0.2f;
        OverrideRoundness = false;
        Roundness = 1f;
    }
}

/// <summary>Screen-space lens flare (U17-M6). Settings only until a dedicated flare shader lands.</summary>
public sealed class ScreenSpaceLensFlareVolumeComponent : VolumeComponent
{
    public bool OverrideIntensity;
    public float Intensity = 1f;
    public bool OverrideTint;
    public Color Tint = Color.White;
    public bool OverrideGhostCount;
    public int GhostCount = 3;

    public bool WantsEffect => Active && (OverrideIntensity || OverrideTint || OverrideGhostCount);

    public override void Interp(VolumeComponent state, float interpFactor)
    {
        if (state is not ScreenSpaceLensFlareVolumeComponent other)
            return;
        InterpFloat(ref Intensity, ref OverrideIntensity, other.Intensity, other.OverrideIntensity, interpFactor);
        InterpColor(ref Tint, ref OverrideTint, other.Tint, other.OverrideTint, interpFactor);
        InterpInt(ref GhostCount, ref OverrideGhostCount, other.GhostCount, other.OverrideGhostCount, interpFactor);
    }

    public override void ResetToDefault()
    {
        OverrideIntensity = false;
        Intensity = 1f;
        OverrideTint = false;
        Tint = Color.White;
        OverrideGhostCount = false;
        GhostCount = 3;
    }
}
