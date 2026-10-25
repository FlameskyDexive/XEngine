# Post Processing

Unity Post-processing Stack v2 style layer, volume, and profile for XEngine. This package is **optional**: it ships with the Editor but is not mounted until you install it from Package Manager (`Window / Package Management / Package Manager`).

Install copies the folder into `Library/PackageCache/com.xengine.postprocessing@0.1.0/` and pins it in `Packages/manifest.json`. Uninstall removes the cache copy and the lockfile pin.

## Quick start

1. Install **Post Processing** from Package Manager.
2. Select a camera and add **Post-process Layer** (`Rendering / Post-process Layer`), or use **GameObject / Volume / Post-process Layer**.
3. Create a profile: **Assets / Create / Rendering / Post-process Profile** (`.postprofile`).
4. Add effects on the profile (Bloom, Color Grading, Vignette, …).
5. Create **GameObject / Volume / Post-process Volume**, mark it **Global**, and assign the profile.

The layer blends volumes at the camera transform (or `volumeTrigger`) and injects the engine ImageEffects into `DefaultRenderPipeline`. Per-camera **Anti-aliasing** on the layer adds FXAA, SMAA, or TAA.

## How it relates to the built-in Volume framework

XEngine already has a URP-style `Volume` + `.volumeprofile` path (Bloom / Tonemap / DoF / cinematic) inside the core pipeline. This package is the **PPv2 authoring stack**:

| | Built-in Volume | This package |
| --- | --- | --- |
| Camera | `UniversalAdditionalCameraData.RenderPostProcessing` | `PostProcessLayer` |
| Scene blend | `Volume` | `PostProcessVolume` |
| Asset | `.volumeprofile` | `.postprofile` |
| Settings | `VolumeComponent` | `PostProcessEffectSettings` + `ParameterOverride` |

Use one path per look. The layer sets `ImageEffect.UseVolumeOverrides = false` on the effects it owns so a scene `Volume` does not overwrite PPv2 values.

## Built-in effects

| Effect | Menu | GPU |
| --- | --- | --- |
| Bloom | XEngine/Bloom | `BloomEffect` |
| Color Grading | XEngine/Color Grading | `CinematicEffects` + `TonemapperEffect` |
| Vignette | XEngine/Vignette | `CinematicEffects` |
| Chromatic Aberration | XEngine/Chromatic Aberration | `CinematicEffects` |
| Grain | XEngine/Grain | `CinematicEffects` |
| Depth of Field | XEngine/Depth Of Field | `BokehDepthOfFieldEffect` |
| Motion Blur | XEngine/Motion Blur | `MotionBlurEffect` |
| Ambient Occlusion | XEngine/Ambient Occlusion | `GTAOEffect` (after opaques) |
| Screen-space Reflections | XEngine/Screen-space Reflections | `ScreenSpaceReflectionEffect` (after opaques) |
| Auto Exposure | XEngine/Auto Exposure | `AutoExposureEffect` |

`breakBeforeColorGrading` on the layer skips tonemap for HDR export.

## Custom effects

```csharp
[PostProcess(typeof(ExampleRenderer), "Custom/Example")]
public sealed class ExampleEffect : PostProcessEffectSettings
{
    public FloatParameter intensity = new FloatParameter { value = 0f };
    public override bool IsEnabledAndSupported() => enabled.value && intensity.value > 0f;
}

public sealed class ExampleRenderer : PostProcessEffectRenderer<ExampleEffect>
{
    public override void Render(PostProcessRenderContext context) { }
}
```

Built-in effects apply through ImageEffects. A custom renderer can blit from `context.RenderContext` when you hook it from your own `ImageEffect`.

## Samples

Package Manager → Post Processing → Samples → **Quick Start**.
