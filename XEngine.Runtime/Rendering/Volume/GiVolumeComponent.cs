// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Blended realtime global illumination settings. Drives
/// <see cref="ScreenSpaceGlobalIlluminationEffect"/> (and the dynamic irradiance probe volume)
/// when overrides are set. Blendable per <see cref="Volume"/> so scenes can fade GI in/out
/// spatially, like the post-processing components.
/// </summary>
public sealed class GlobalIlluminationVolumeComponent : VolumeComponent
{
    /// <summary>Master toggle. The effect is only enqueued when an active volume enables it.</summary>
    public bool OverrideEnable;
    public bool Enable;

    public bool OverrideIntensity;
    public float Intensity = 1f;

    /// <summary>Maximum bounce distance in world units.</summary>
    public bool OverrideRadius;
    public float Radius = 2.5f;

    /// <summary>0 = Full, 1 = Half, 2 = Quarter (gather resolution).</summary>
    public bool OverrideResolution;
    public int Resolution = 1;

    public bool OverrideTemporal;
    public bool UseTemporal = true;

    public bool OverrideTemporalResponse;
    public float TemporalResponse = 0.9f;

    public bool OverrideBlur;
    public float BlurRadius = 1.0f;

    /// <summary>Debug overlay: 0 = off, 1 = irradiance, 2 = albedo capture, 3 = AO + hit rate.</summary>
    public bool OverrideDebugView;
    public int DebugView;

    public bool WantsEffect => Active && OverrideEnable && Enable;

    public override void Interp(VolumeComponent state, float interpFactor)
    {
        if (state is not GlobalIlluminationVolumeComponent other)
            return;
        InterpBool(ref Enable, ref OverrideEnable, other.Enable, other.OverrideEnable, interpFactor);
        InterpFloat(ref Intensity, ref OverrideIntensity, other.Intensity, other.OverrideIntensity, interpFactor);
        InterpFloat(ref Radius, ref OverrideRadius, other.Radius, other.OverrideRadius, interpFactor);
        InterpInt(ref Resolution, ref OverrideResolution, other.Resolution, other.OverrideResolution, interpFactor);
        InterpBool(ref UseTemporal, ref OverrideTemporal, other.UseTemporal, other.OverrideTemporal, interpFactor);
        InterpFloat(ref TemporalResponse, ref OverrideTemporalResponse, other.TemporalResponse, other.OverrideTemporalResponse, interpFactor);
        InterpFloat(ref BlurRadius, ref OverrideBlur, other.BlurRadius, other.OverrideBlur, interpFactor);
        InterpInt(ref DebugView, ref OverrideDebugView, other.DebugView, other.OverrideDebugView, interpFactor);
    }

    public override void ResetToDefault()
    {
        OverrideEnable = false;
        Enable = false;
        OverrideIntensity = false;
        Intensity = 1f;
        OverrideRadius = false;
        Radius = 2.5f;
        OverrideResolution = false;
        Resolution = 1;
        OverrideTemporal = false;
        UseTemporal = true;
        OverrideTemporalResponse = false;
        TemporalResponse = 0.9f;
        OverrideBlur = false;
        BlurRadius = 1.0f;
        OverrideDebugView = false;
        DebugView = 0;
    }
}
