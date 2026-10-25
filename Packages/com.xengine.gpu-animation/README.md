# GPU Animation (com.xengine.gpu-animation)

Animation Baker and Instancing for XEngine, ported in spirit from chenjd's
[Render-Crowd-Of-Animated-Characters](https://github.com/chenjd/Render-Crowd-Of-Animated-Characters)
and its commercial sibling "Animation Baker and Instancing for Animated Characters".

## What it does

- **Bake** (`Window/Animation/GPU Animation Baker`): samples a skinned character's clips
  (generic and humanoid) at a fixed frame rate through the engine's animation playables and
  bakes every skinned vertex position — and optionally normals — into vertex animation
  textures (`.gpuanim`), plus a UV2-encoded static mesh (`.mesh`), with per-frame bounds.
- **Render** (`GPU Animated Crowd` component): draws thousands of instances through the
  engine's GPU instancing pipeline. The vertex shader samples the VAT in the vertex stage;
  playback is driven by the engine's global `_Time` — steady-state CPU cost is one float per
  crowd per frame. 10,000 instances ≈ 5.4 ms/frame headless (see
  `docs/benchmarks/rendering/2026-09-28-gpu-animation-crowd/`).
- **Per-instance control**: animation phase, playback speed, clip selection (multi-clip
  texture atlas), per-instance tint, and shadows cast from the animated pose.
- **Shaders**: unlit and lit (lambert + per-crowd light/ambient) variants, GLSL + Slang
  dual-source, with a `ShadowCaster` pass each.

## Status

v0.1.0 — milestones M0–M7 complete on main (plan:
`docs/superpowers/plans/2026-09-28-gpu-animation-baker-instancing-plan.md`; evidence:
`docs/superpowers/evidence/gpu-animation/`).

## Install

Optional Official package — install through the editor's Package Manager
(npm: `@inxep/gpu-animation`). Add the `GPU Animated Crowd` component to a GameObject,
assign the baked `.gpuanim` asset + VAT mesh, and populate `Instances`.

## Tests

```
dotnet test Packages/com.xengine.gpu-animation/Tests~/XEngine.GpuAnimation.Tests.csproj -c Debug
```

21 facts: layout, Echo round-trip + GPU texture round-trip, exact bake math on synthetic
rigs, instance-data API, 10k crowd render + phase/clip-swap animation acceptance, and
animated shadow casting. GPU tests share a serial collection and skip without Vulkan.
