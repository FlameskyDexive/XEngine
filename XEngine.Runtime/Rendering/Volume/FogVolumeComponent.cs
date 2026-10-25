// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Blended volumetric-fog globals (M9). <see cref="VolumetricFogEffect"/> uses these when a
/// contributing volume sets the matching override; otherwise the ImageEffect fields win.
/// Spatial density regions remain on <see cref="FogVolume"/> and are uploaded separately.
/// </summary>
public sealed class FogVolumeComponent : VolumeComponent
{
    public bool OverrideDensity;
    public float GlobalDensity;

    public bool OverrideColor;
    public Color GlobalColorTint = new(1f, 1f, 1f, 1f);

    public bool OverrideScattering;
    public float Scattering = 0.5f;

    public bool OverrideExtinction;
    public float Extinction = 1f;

    public bool OverrideAmbient;
    public Color AmbientColor = new(0.4f, 0.5f, 0.6f, 1f);
    public float AmbientIntensity = 0.3f;

    public override void Interp(VolumeComponent state, float interpFactor)
    {
        if (state is not FogVolumeComponent other)
            return;

        InterpFloat(ref GlobalDensity, ref OverrideDensity, other.GlobalDensity, other.OverrideDensity, interpFactor);
        InterpColor(ref GlobalColorTint, ref OverrideColor, other.GlobalColorTint, other.OverrideColor, interpFactor);
        InterpFloat(ref Scattering, ref OverrideScattering, other.Scattering, other.OverrideScattering, interpFactor);
        InterpFloat(ref Extinction, ref OverrideExtinction, other.Extinction, other.OverrideExtinction, interpFactor);
        InterpColor(ref AmbientColor, ref OverrideAmbient, other.AmbientColor, other.OverrideAmbient, interpFactor);
        InterpFloat(ref AmbientIntensity, ref OverrideAmbient, other.AmbientIntensity, other.OverrideAmbient, interpFactor);
    }

    public override void ResetToDefault()
    {
        OverrideDensity = false;
        GlobalDensity = 0f;
        OverrideColor = false;
        GlobalColorTint = new Color(1f, 1f, 1f, 1f);
        OverrideScattering = false;
        Scattering = 0.5f;
        OverrideExtinction = false;
        Extinction = 1f;
        OverrideAmbient = false;
        AmbientColor = new Color(0.4f, 0.5f, 0.6f, 1f);
        AmbientIntensity = 0.3f;
    }
}
