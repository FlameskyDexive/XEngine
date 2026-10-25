# Changelog

## [0.1.0] - 2026-08-16

- Official optional package: listed in Package Manager, copied to `Library/PackageCache` on Install (`installByDefault: false`).
- Runtime / Editor asmdefs, Documentation~, Samples~/QuickStart, CHANGELOG, LICENSE.
- Unity Post-processing Stack v2 style API: `PostProcessLayer`, `PostProcessVolume`, `PostProcessProfile` (`.postprofile`), `ParameterOverride`, `[PostProcess]`.
- Built-in effects drive existing engine ImageEffects: Bloom, Color Grading / tonemap, Vignette, Chromatic Aberration, Grain, Depth of Field, Motion Blur, Ambient Occlusion (GTAO), Screen-Space Reflections, Auto Exposure, plus per-layer FXAA / SMAA / TAA.
- Layer implements `ICameraImageEffectSource` so `DefaultRenderPipeline` gathers effects without a core compile-time dependency on this package.
