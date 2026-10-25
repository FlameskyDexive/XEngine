---
name: run-scene-live
description: Drive a LIVE editor instance — read the running scene, capture runtime logs, toggle playmode. Requires the editor running with --serve (in-process MCP server).
---

# run-scene-live

Use when you need to interact with a **running** editor (not offline files): see the live scene
hierarchy, capture runtime logs/errors, or enter/exit playmode. This is the Layer 2 path (in-process
MCP server) — it reaches live state the offline tools cannot.

## Prerequisite
The editor must be running with `--serve` (which starts the in-process MCP server). Connect your MCP
client to the editor's stdio (see `cli/samples/mcp-editor-serve.json`). The offline `xengine-cli`
tools (scene_list/asset_search/etc.) are NOT available on this connection — only the `runtime_*`
tools below.

## Tools (MCP only — these live in the editor process)
- `runtime_state` — current scene name/path, playmode (IsPlaying), object count.
- `runtime_logs` — recent `Debug.OnLog` entries (512-entry ring), newest-first, optional severity
  filter (Success/Normal/Warning/Error/Exception).
- `runtime_playmode` — enter or exit playmode. `action: "enter"` | `"exit"`. Preserves full
  scene save/restore semantics.
- `runtime_live_scene` — the live hierarchy (root objects + child counts + tag/layer).

## Typical workflow
1. `runtime_state` to confirm what scene is loaded and whether you're in playmode.
2. `runtime_live_scene` to see the running hierarchy (the offline `scene_hierarchy` may be stale if
   the scene was edited in-memory).
3. Make a change (via the editor or offline tools), then `runtime_playmode` enter to test it.
4. `runtime_logs` (minimumSeverity: "Warning") to check for runtime errors after your change.

## Notes
- All `runtime_*` tools execute on the editor main thread (marshalled via MainThreadDispatcher) —
  safe to touch live Scene/GameObject/Application state.
- `runtime_logs` reads the startup-subscribed EditorLog ring, preserving severity, message,
  stack context and the original event UTC timestamp. Minimum severity is case-insensitive;
  `Warning` includes warnings, errors and exceptions. A lazy subscription is only a fallback
  when EditorLog was not initialized (such as a test host).
- `--project` accepts a directory or `.xengine` file; both use the project root's
  `Logs/Editor.log`, with the preceding session rotated to `Editor-prev.log`.
