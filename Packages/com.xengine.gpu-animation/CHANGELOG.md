# Changelog

## 0.1.1 (2026-09-28)

- Half-precision (RGBA16F) VAT atlases: half the GPU memory per crowd, Linear-filterable on
  every backend (WebGPU included); baker-window toggle, default on. Float32 stays the exact
  option. The 10k-instance acceptance renders from a 16F atlas end-to-end.
- Baker now saves a ready-to-use crowd `.mat` (three-piece output: `.gpuanim` + `_VAT.mesh` +
  `.mat`); `GpuAnimatedCrowd.MaterialAsset` accepts it (`MaterialOverride` for runtime).
- Shipped sample: `Samples~/Crowd` carries a self-baking runtime crowd script (procedural
  rig → bake → 10,000 randomized-phase instances), compiled into the test suite so it can't
  rot silently.
- Shader hardening: a keyword-combination smoke sweep (16 combos × both shaders × all passes)
  caught and fixed the non-instanced Slang variants silently falling back to Invalid
  (`XENGINE_MATRIX_MVP` undefined without GPU_INSTANCING).
- Lit crowd shading moved to the vertex stage (per-instance shading; fragment identical to
  the proven unlit path); scene-sun uniform wiring documented as under investigation.
- Tarball no longer ships `Tests~/obj` NuGet intermediates.

## 0.1.0 (2026-09-28)

First complete release — Animation Baker and Instancing (M0–M7).

- Editor baker window: generic + humanoid clips → `.gpuanim` VAT atlas + UV2-encoded mesh,
  per-frame bounds, root pinning for in-place loops; `.gpuanim` Echo asset + importer.
- Headless bake core with shader-exact CPU skinning (verified against synthetic rigs and a
  real 22-SMR character).
- `GPU Animated Crowd` renderer component: engine instancing pipeline, per-instance
  clip/phase/speed/tint, multi-clip atlas, union-AABB culling.
- Unlit + lit crowd shaders (GLSL + Slang) with animated-pose ShadowCaster passes.
- 21-fact test suite incl. headless Vulkan render/animation/shadow acceptance; benchmarks:
  10k instances ≈ 5.4 ms/frame, 30k ≈ 12.2 ms/frame headless.
