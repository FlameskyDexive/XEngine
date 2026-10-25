# Post Processing

Official optional XEngine package. It ships with the Editor but is **not** added to a project until you install it from Package Manager (`Window / Package Management / Package Manager`).

Installing copies this folder into the project's `Library/PackageCache/com.xengine.postprocessing@<version>/` and records the pin in `Packages/manifest.json`.

Full docs: `Documentation~/index.md`. Samples: Package Manager → Samples (Quick Start).

## Runtime

Unity Post-processing Stack v2 style authoring:

1. Add **Post-process Layer** to a camera (`Rendering / Post-process Layer`)
2. Create a **Post-process Profile** (`Assets / Create / Rendering / Post-process Profile`, `.postprofile`)
3. Add a **Post-process Volume** (`GameObject / Volume / Post-process Volume`) and assign the profile

The layer blends volumes at the camera (or a custom trigger) and injects the engine's existing ImageEffects (Bloom, tonemap, DoF, GTAO, SSR, cinematic uber pass, AA). It does not replace the built-in Volume framework — use one authoring path per effect to avoid stacking the same look twice.

## Authoring

| Effect | GPU path |
| --- | --- |
| Bloom | `BloomEffect` (dual-filter pyramid) |
| Color Grading + tonemap | `CinematicEffects` + `TonemapperEffect` |
| Vignette / Chromatic Aberration / Grain | `CinematicEffects` |
| Depth of Field | `BokehDepthOfFieldEffect` |
| Motion Blur | `MotionBlurEffect` |
| Ambient Occlusion | `GTAOEffect` (after opaques) |
| Screen-Space Reflections | `ScreenSpaceReflectionEffect` (after opaques) |
| Auto Exposure | `AutoExposureEffect` |
| Anti-aliasing | FXAA / SMAA / TAA on the layer |

Custom effects: subclass `PostProcessEffectSettings`, mark with `[PostProcess(typeof(YourRenderer), "Menu/Name")]`, implement `PostProcessEffectRenderer<T>`.
