---
name: inspect-render-debug
description: Read the live Universal Rendering Debugger via runtime_render_debug (in-process editor --serve). Pass list, Features, RTHandles, lighting, Volume stack.
---

# inspect-render-debug

Use when an AI needs to inspect the **running** Universal pipeline: which passes executed, which RendererFeatures are on, RTHandle sizes, lighting/cascades, and the Volume stack. This is live state, not an Echo asset.

Requires the editor with `--serve`. Offline Host does **not** implement this tool.

## Snapshot
```bash
# MCP (in-process only)
runtime_render_debug
```

Returns structured fields:

- **Lighting** — `Lights`, `DirectionalLights`, `PointLights`, `SpotLights`, `ShadowCasters`, `ShadowCascades`
- **OverdrawProxy** — extra color `DrawCalls` beyond `RenderablesDrawn` (CPU proxy, not a GPU overdraw heatmap)
- **Passes** — `PassEvents[]` (execute-path queue), `EnqueuedPasses`, `ExecutedPasses`, `CulledGraphPasses`, `CopyColorPasses`, `CopyDepthPasses`
- **RTHandle** — `RtWidth`/`RtHeight`, `OpaqueAllocated`, `PrepassAllocated`
- **Features** — type name, or `Name disabled`
- **Volume stack** — blended component type names; **Volumes** — registered `Volume` components (name, global/local, weight, priority, enabled)
- **Physics 3D** — body / shape / trigger / overlap / contact counts from `PhysicsDebugger.Capture`. Toggles on this panel (`Draw colliders` / `Draw contacts` / `Draw sleeping`) record an overlay into the scene-view gizmo buffer.

The physics overlay is **panel-only** (not a `runtime_render_debug` field). Screenshot the same panel to include it.

## Panel screenshot
```bash
runtime_panel_screenshot \
  --panelType XEngine.Editor.GUI.Panels.RenderingDebuggerPanel \
  --width 900 --height 600
```

Open the panel in the editor via **Window / Analysis / Rendering Debugger**.

## Related
- [observe-runtime](../observe-runtime/SKILL.md) — `runtime_stats` for frame times / draw totals
- [inspect-pipeline](../inspect-pipeline/SKILL.md) — Echo `.pipeline` defaults
- [inspect-volume](../inspect-volume/SKILL.md) — Echo `.volumeprofile` assets
