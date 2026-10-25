---
name: rendering-gpu-performance
description: Guides XEngine rendering and GPU performance work. Use when changing render pipelines, passes, command buffers, render targets, shadows, viewports, batching, RenderStats, graphics backends, or RHI boundaries.
---

# Rendering and GPU Performance

Optimize observable GPU and render-thread work, not only CPU syntax.

## Resource and pass discipline

- Create and clear heavyweight GPU resources only when needed. Keep shadow atlases and
  similar targets lazy and size them through quality settings.
- Skip passes, clears, submissions, and full viewport rendering when no visible work
  exists. Hidden or inactive views should perform no full pipeline run.
- Reuse render-frame collections and temporary objects. Avoid per-camera, per-light,
  per-pass, per-command, and per-draw allocations.
- Batch compatible work while preserving ordering, state transitions, and visibility.
- Cache stable pass/material/shader lookups with explicit invalidation.
- Do not retain frame-owned resources beyond their valid lifetime.

## RHI and backends

- Route GPU work through `IGraphicsDevice` (`XEngine.Runtime.RHI`). Do not add new direct
  Silk OpenGL calls outside `Graphics/Backends/OpenGL/`.
- Prefer backend-neutral `CommandBuffer` opcodes and engine enums (`Primitives`,
  `VertexFormat`). Map formats inside `Backends/*/Formats.cs`.
- Backend selection: `--graphics=opengl|vulkan|d3d12|null|auto` or
  `XENGINE_GRAPHICS_BACKEND`. Windowed Auto stays on OpenGL until modern backends reach
  DefaultRenderPipeline parity; Factory Auto can still probe D3D12→Vulkan→OpenGL.
- Shader authoring: keep `GLSLPROGRAM` for OpenGL; use `SLANGPROGRAM` for Vulkan/D3D12.
  Legacy `HLSLPROGRAM` remains accepted while explicit `XENGINE_SHADER_COMPILER=dxc` is the
  compatibility rollback. Players load versioned offline SPIR-V/DXIL artifacts.
- Reject or emulate non-portable topologies (`Quads`, `LineLoop`, `TriangleFan`) via
  `TopologyUtilities.IsPortable`.
- Apply `../hot-path-performance/SKILL.md` to render-thread and per-frame CPU code.

## Architecture

- Keep command recording backend-agnostic. Separate resource lifetime, recording, and
  execution. OpenGL owns the FIFO render thread; Vulkan/D3D12 use explicit fences and
  frames-in-flight.
- Preserve OpenGL fallback behavior while bringing modern backends to parity.
- Do not leak OpenGL-only assumptions (bottom-left scissor, VAO identity, GLSL-only
  programs) into the pipeline layer.

## Observability and verification

- Add or update `RenderStats` counters for new optimization gates.
- Verify empty scenes, no-caster scenes, hidden viewports, Preview/Full tiers, and
  active content separately.
- CPU gates: `dotnet test XEngine.Runtime.Test --filter "FullyQualifiedName~Rhi|ShaderCompiler|HeadlessGraphics"`.
- Optional GPU smoke creates Vulkan/D3D12 devices and expects success or a clear
  unavailable exception — never a hang.
- Measure GPU passes/clears/submissions and CPU frame allocations before and after.
- Check resize, disposal, device-loss, scene changes, and editor/game-view transitions
  for stale caches or leaked resources.
