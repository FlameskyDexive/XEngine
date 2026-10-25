# GPU Animation — Documentation

Vertex Animation Texture (VAT) baking + GPU-instanced crowd rendering.
Reference implementation: chenjd/Render-Crowd-Of-Animated-Characters
(position-only "AnimMap"); this package additionally bakes normals, per-frame
bounds, and supports multi-clip atlases and per-instance phase/speed/color —
the feature set of the commercial "Animation Baker and Instancing".

## How it works

Bake side (editor, per clip):

- `W = NextPowerOfTwo(vertexCount)` texture columns (x = vertex index),
  `H` rows (y = frame index); sample the clip headlessly through the engine's
  playable API, CPU-skin every vertex, and write `texel(j, i) = (pos.xyz, 1)`
  (and the skinned normal into a second texture).
- A UV2 channel encoding `(j + 0.5) / W` is written into a clone of the mesh so
  the vertex shader can find its texel without relying on vertex-ID intrinsics.

Render side (runtime):

- One crowd component submits the baked mesh through the engine's instancing
  pipeline (`GPU_INSTANCING`). The vertex shader samples
  `v = fract((time / animLen + phase) * speed)`; bilinear filtering blends
  frames and the Repeat wrap cross-fades the loop seamlessly.
- Playback is GPU-time driven: no per-instance CPU work per frame.

## Milestones

| M | Scope | Status |
|---|-------|--------|
| M0 | Package skeleton + layout tests | done — `7328f77e4` |
| M1 | `.gpuanim` asset + importer + round-trip tests | done — `5c859feba` |
| M2 | Headless bake core (sampling + CPU skinning + bounds) | done — `29d3584af` |
| M3 | Baker editor window (real-character bake verified) | done — `88013d6d1` |
| M4 | Shaders + crowd renderer + 10k headless acceptance | done — `ae2281008` |
| M5 | Multi-clip atlas + per-instance API tests | done — `75a540201` |
| M6 | Animated-pose shadow casting | done — `41d80bba8` |
| M7 | Benchmarks + docs + npm publish | done — benchmark `docs/benchmarks/rendering/2026-09-28-gpu-animation-crowd/` |
