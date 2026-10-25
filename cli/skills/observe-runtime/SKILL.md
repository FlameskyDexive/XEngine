---
name: observe-runtime
description: Observe and drive a LIVE editor instance — screenshot the game view, read perf counters, query/set selection, invoke menu items, eval C# code. The "AI can see and act" layer.
---

# observe-runtime

Use when you need to **observe** the running editor (see what your changes did) or **act** on it beyond
offline file edits. This is the "AI can see" layer — closes the blind-edit gap. Requires the editor
running with `--serve`.

## Screenshot (see the game view)
- `runtime_screenshot` — captures the live GameView as a PNG. Returns `{path, width, height}`. Read
  the PNG path to observe what the game currently looks like. **The key tool for visual verification.**

## Performance counters
- `runtime_stats` — live rendering counters from RenderStats.Last: draw calls, triangles, pass
  timings (ms), lights, shadows, image effects, + 120-frame time history. Use for perf-regression
  gates after a change.
- `runtime_render_debug` — Universal Rendering Debugger snapshot: pass list, Features, RTHandles,
  lighting/cascades, CPU overdraw proxy, Volume stack. See [inspect-render-debug](../inspect-render-debug/SKILL.md).
- `runtime_frame_debug` — Frame Debugger: enable/refresh/select, per-draw event tree + selected
  RT/shader/texture summary. See [inspect-frame-debug](../inspect-frame-debug/SKILL.md).
- `runtime_panel_screenshot` — offscreen PNG of any `DockPanel` (use
  `XEngine.Editor.GUI.Panels.RenderingDebuggerPanel` for the debugger UI).
- Default GameView and panel PNGs go to the open project's `.xengine/screenshots/`;
  without a project they go to `LocalApplicationData/XEngine/Screenshots/` (temporary
  storage if user data is unavailable). They do not use the editor installation's
  working directory. `CapturePanelNow` / `--panel-screenshot-output` still accept an
  explicit path; relative overrides resolve from the caller's working directory.

## Selection
- `runtime_selection` (action `query`) — returns the current editor selection (identifier/name/type).
- `runtime_selection` (action `set`, `identifier` = Guid) — selects a GameObject by Identifier.

## Menu items
- `runtime_menu` (action `list`) — enumerate all editor menu paths.
- `runtime_menu` (action `invoke`, `path`) — execute a menu item by path (e.g. "File/New Scene").

## Code eval (REPL)
- `runtime_eval` (`code`) — evaluates C# in the live editor (Roslyn REPL). Globals in scope: `Scene`
  (the current scene), `Editor` (EditorApplication.Instance), `IsPlaying`. Full engine API access.
  **Credential-grade** — only on local --serve, never in shipping builds.

## Animation window
- `runtime_animation_bind` (`identifier`, optional `clipGuid`) — bind the Animation window to a scene
  GameObject (and optional clip). Opens the panel if needed. Use after selecting a rig to drive
  play/scrub preview. Full clip/controller edits land in M31 (`clip_*` / `animator_*`).
- `runtime_animation_record_key` (`path`, `channel`, `value`, optional `time`, optional `typeName`) —
  stamp a key at the playhead (or `time`) on a bone channel (`PosX`..`ScaleZ`) or a generic float
  property. Bind a writable clip first. Scene Rec mode snapshots pose and writes keys as you edit.
- `runtime_animator_state` (optional `identifier`) — per-layer current state, normalized time,
  in-transition, parameter snapshot.
- `runtime_animator_set_parameter` (`identifier`, `name`, `type`, `value`) — Float/Int/Bool/Trigger.
- `runtime_animator_play` (`identifier`, `stateName`, optional `layer`, optional `fadeDuration`).
- `runtime_animation_preview` (`identifier`, `time`, optional `clipGuid`) — bind + scrub so you can
  inspect a pose (time = frame / frameRate).

Copy [`cli/samples/workflows/author-locomotion.json`](../../cli/samples/workflows/author-locomotion.json)
into the project's `.xengine/workflows/` to chain parameter/state/transition/validate.

## Typical verify loop
1. Make a change (offline `scene_object_patch` or in-editor).
2. `runtime_screenshot` to see the visual result.
3. `runtime_stats` to check perf didn't regress (draw calls / frame time).
4. `runtime_logs` (from run-scene-live skill) to check for errors.
5. If unexpected, `runtime_eval` to inspect live state (`return Scene.Current.AllObjects.Count();`).

## Notes
- All `runtime_*` tools run on the editor main thread (safe for live state).
- `runtime_screenshot` requires a GameView panel open; returns a clear error if none.
- `runtime_eval` compilation takes a few seconds on first call (Roslyn JIT); subsequent calls are
  faster. Wrap in try/catch mentally — compilation errors return as `Error` in the result.
