---
name: inspect-frame-debug
description: Control the live Frame Debugger via runtime_frame_debug (in-process editor --serve). Capture one frame of game-rendering GPU events, select draws, inspect RT/shader/textures.
---

# inspect-frame-debug

Use when an AI needs a **Unity Frame Debugger-style** view of the running GPU command stream:
per-draw events nested under Camera / Pass / CommandBuffer, with sticky render-target / shader /
texture state for the selected event.

Requires the editor with `--serve`. Offline Host does **not** implement this tool.

**Capture is scoped to game rendering by default** (Unity parity): Game view cameras, pipeline
passes, and game UI. The editor's own Paper UI never enters the tree and is never truncated.
**A Game view panel must be open** or the capture will be empty — open it first with
`runtime_menu --action invoke --path "Window/General/Game"`.

This is complementary to [inspect-render-debug](../inspect-render-debug/SKILL.md) (CPU pass list /
Features / Volumes). Frame Debugger is the per-draw GPU event log.

**Enabling pauses play mode** (Unity behavior): gameplay Update stops so the captured frame
stays frozen while stepping events; rendering keeps running. Disable does not resume — unpause
via the editor toolbar (or `Application.IsPaused = false`).

## Control
```bash
# Arm capture of the next full frame (pauses play mode)
runtime_frame_debug --action enable

# After a frame has rendered, re-read status / tree
runtime_frame_debug --action status

# Grab another frame (also clears a stale capture)
runtime_frame_debug --action refresh

# Select leaf event N (0-based) — the PANEL preview shows the frame state at that event;
# the Game view keeps showing the capture frame's FINAL image (Unity behavior)
runtime_frame_debug --action select --eventIndex 12

# Debug mode: capture the whole frame including the editor's own UI
runtime_frame_debug --action set_scope --scope full   # back with --scope game

# Turn off (restore full rendering)
runtime_frame_debug --action disable
```

## Result fields

- **Enabled / Capturing** — debugger armed; currently decoding this frame
- **CaptureScope** — `GameRendering` (default) or `FullFrame`
- **CaptureStale** — replay frames diverged from the capture (scene changed); Refresh to re-capture
- **IndexMappingReliable** — false when the backend's GPU op count didn't match the tree
- **LeafEventCount / DrawCount** — tree leaves and draw subset
- **RenderTargets[]** — distinct named RTs bound during the capture (e.g. `_CameraColorTexture 1021x462 Color4b`)
- **Events[]** — `{Index, Kind, Label, CommandBufferName, Scope}` (Scope: GameView/SceneView/EditorFrame)
- **Selected** — RT name/size/format, shader (real names, e.g. `Default/ProceduralSkybox/Skybox`),
  mesh/material, topology, texture summary

## Panel screenshot
```bash
runtime_panel_screenshot \
  --panelType XEngine.Editor.GUI.Panels.FrameDebuggerPanel \
  --width 1100 --height 750
```

Open via **Window / Analysis / Frame Debugger**.

## Typical loop
1. `runtime_menu --action invoke --path "Window/General/Game"` — ensure a Game view exists
2. `runtime_frame_debug --action enable`
3. Wait one frame, then `runtime_frame_debug --action status` — inspect Events
4. `runtime_frame_debug --action select --eventIndex N` — truncate + inspect Selected
5. `runtime_screenshot` — the game view now shows the frame truncated at event N
6. `runtime_frame_debug --action disable` when done

## Related
- [inspect-render-debug](../inspect-render-debug/SKILL.md) — pipeline Features / Volumes
- [observe-runtime](../observe-runtime/SKILL.md) — screenshots and `runtime_stats`
