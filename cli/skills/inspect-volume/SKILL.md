---
name: inspect-volume
description: Read a Universal VolumeProfile (.volumeprofile) offline via volume_inspect. Echo text, journal-safe.
---

# inspect-volume

Use when an AI needs to inspect a volume profile (the shared settings asset Volumes sample). Scene Volume components (IsGlobal, Weight, BlendDistance, Priority, Profile GUID) live on GameObjects — use [inspect-scene](../inspect-scene/SKILL.md) / `scene_hierarchy` for those.

`.volumeprofile` files are Echo text. Do **not** read `Library/cache/`.

## Inspect
```bash
xengine-cli volume-inspect --project <root> --volume Assets/Settings/Default.volumeprofile
```

Returns `{ $type, fields[], contentHash }`. Pass `contentHash` to `volume_patch`.

### VolumeProfile paths
- `Components/$values/<i>/Active` — whether that component participates in blending
- `Components/$values/<i>/$type` — concrete component (e.g. `XEngine.Runtime.Rendering.FogVolumeComponent`)
- Fog globals: `OverrideDensity`, `GlobalDensity`, `OverrideColor`, `GlobalColorTint/R|G|B|A`,
  `OverrideScattering`, `Scattering`, `OverrideExtinction`, `Extinction`,
  `OverrideAmbient`, `AmbientColor/R|G|B|A`, `AmbientIntensity`
- Bloom: `OverrideIntensity`, `Intensity`, `OverrideThreshold`, `Threshold`, `OverrideIterations`, `Iterations`
- Tonemap: `OverrideType`, `Type`, `OverrideContrast`, `Contrast`, `OverrideSaturation`, `Saturation`
- DoF: `OverrideFocus`, `UseAutoFocus`, `ManualFocusPoint`, `FocusStrength`, `OverrideMaxBlur`, `MaxBlurRadius`
- Motion blur: `OverrideIntensity`, `Intensity`, `OverrideSamples`, `Samples`, `OverrideMaxBlur`, `MaxBlurRadius`
- Color adjustments: `OverrideExposure`, `PostExposure`, `OverrideContrast`, `Contrast`, `OverrideSaturation`, `Saturation`, `OverrideTemperature`, `Temperature`
- Vignette: `OverrideIntensity`, `Intensity`, `OverrideSmoothness`, `Smoothness`, `OverrideRoundness`, `Roundness`
- Screen-space lens flare: `OverrideIntensity`, `Intensity`, `OverrideTint`, `Tint/R|G|B|A`, `OverrideGhostCount`, `GhostCount`

Scene `FogVolume` components (Shape, DensityMultiplier, ColorTint) are spatial ray-march regions,
not the blended globals. Inspect them with [inspect-scene](../inspect-scene/SKILL.md).

`--asset` is accepted as an alias for `--volume`. The path must end in `.volumeprofile`.

Or MCP: `volume_inspect`.
