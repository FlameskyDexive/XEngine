# XEngine patches to Photonic (CPU lightmap baker)

Baseline for diffing: Anthology tag `v2.8.6` (commit `b3ab895d`), path `Photonic/` — see `UPSTREAM.txt`.
Retrieve any baseline file with `git show v2.8.6:Photonic/<path>`.

## Status

- Was vendored at **v2.8.6**; **synced to v3.3.0 on 2026-08-31** (M1.7 of the prowl-sync plan).
- Upstream v2.8.6→v3.3.0: sparse texel integration (`SparseStride`/`SparseIncludesDirect`,
  `Integration/SparseNeighbours`/`SparseTexels`, `Rasterization/SamplePositionRefiner`/
  `UvSeamFinder`, `Imaging/SeamFixer`, `FixSeams` option), `RayMath.OffsetOrigin` robust bias
  offsets — and **removal of the lightmap denoiser** (see below).
- Referenced by **XEngine.Editor** (lightmap baking service + environment panel).

## Local patches (preserved through the sync)

### 1. Lightmap denoiser kept (upstream deleted it in v3.3.0)

- **Files**: `Imaging/LightmapDenoiser.cs` (restored), `BakeOptions` `Denoise`/`DenoiseIterations`/
  `DenoiseNormalPhi`/`DenoisePositionScale` (re-added), `Job.Denoise()` (re-added onto the v3.3.0
  Job; adapted to v3.3.0's `Dilate.Run` scratch-buffer signature).
- **Motive**: `XEngine.Editor/AssetsDatabase/Lightmapping/LightmapBakeService.cs` drives it
  (`Options.Denoise`, `Job?.Denoise()`) and the environment panel exposes the toggle.

### 2. Bake path options (local, re-added)

- `BakeOptions.RussianRoulette`, `BakeOptions.IgnoreAlbedo`, `DoBackfaceCull` (XEngine wording),
  `RayBias`/`EffectiveRayBias` configurable bias.
- `PathIntegrator`: all bias paths unified on the job's `_bias` (= `EffectiveRayBias`, 0 →
  scene-size derived — upstream's improved semantic; the configurable option still drives it);
  local `XEngineKeepPositiveDet` const kept; adopted upstream `RayMath.OffsetOrigin` origins.

## Verification (2026-08-31 sync)

- Photonic + full `XEngine.sln` build: **0 errors** (editor lightmap service compiles unchanged).
