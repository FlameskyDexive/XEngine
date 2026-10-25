---
name: run-xui-live
description: Open a .xui screen in a live XEngine editor and check what it actually looks like — measured rects via runtime_xui_tree, pixels via runtime_screenshot, edit-save-look via runtime_xui_reload.
---

# run-xui-live

`xui_validate` proves a document is legal. It cannot tell you whether an anchor put the panel where
you meant. That needs a built screen, which needs a running editor:

```bash
Build/Editor/Debug/net10.0/XEngine.Editor.exe --project <root> --serve
```

The `runtime_xui_*` tools come from `com.xengine.xui`'s Editor assembly and register themselves via the
editor's MCP assembly scan. If they are missing from `tools/list`, the package is not installed in that
project.

## The loop

```
runtime_xui_open   → build the screen from the document
runtime_xui_tree   → check the geometry that actually resulted
runtime_screenshot → look at it
(edit the .xui)
runtime_xui_reload → rebuild from the edited document
```

### `runtime_xui_open`

```json
{"documentPath": "UI/login/Login.xui", "screenName": "Login"}
```

Paths are relative to `Assets/` (a leading `Assets/` is accepted too). `screenName` is optional — omit
it for the document's first screen.

The screen opens **with no controller**: nothing is wired, so nothing responds. That is the point.
You are checking the layout, before any C# exists for it.

### `runtime_xui_tree` — the tool that finds layout bugs

Returns **measured** rects, not authored attributes, forcing a layout pass at a resolution you choose:

```json
{"screenName": "Login", "width": 1920, "height": 1080}
```

A screenshot tells you something is wrong. This tells you which element and by how much:

```
Login        1920x1080     ← canvas root
XImgScrim    1920x1080     ← anchor="stretch" resolved
XVsPanel      480x304      ← size="480x_" grew to its content
XTxtTitle     424x48       ← size="_x48" honoured inside the stack
```

Pass different `width`/`height` to check a responsive layout at several resolutions without resizing
anything. No GameView panel is needed.

**Read this before reaching for a screenshot.** Numbers you can compare against the document beat
eyeballing pixels, and they work headless.

### `runtime_screenshot`

The stock editor tool captures the GameView. Two things have to be true before it shows any UI at all:

1. A **GameView panel is open** — invoke `runtime_menu` with
   `{"action": "invoke", "path": "Window/General/Game"}` and allow a couple of seconds. If it reports
   "No active GameView panel", the panel has not become active yet; retry.
2. The editor is **in play mode** — `runtime_playmode` with `{"action": "enter"}`. Outside play mode
   the GameView renders the scene but not the canvas, so the screenshot comes back as the skybox with
   no UI on it. That empty frame is easy to misread as "the screen failed to build"; check
   `runtime_xui_tree` before believing it.

Enter play mode *before* opening the screen — entering it reloads the scene, and screens built
beforehand are left behind.

### `runtime_xui_reload`

Re-imports the document and rebuilds every open screen it declares, keeping any controller and its
state. `OnRegisterEvents` does **not** run again, so subscriptions are not doubled.

If the reload reports missing ids, the document and a generated view binding have diverged — re-run
`xui_scaffold`.

### `runtime_xui_locale`

Loads every `.po` in the project, switches the locale, and re-translates the open screens. It reports
`translations` (entries in the active catalog) and `retranslated` (strings actually written back into
screens) — if the second is zero, nothing reached the UI. Pass `""` to go back to the source strings.

Catalogs are loaded on demand here rather than assumed to have been imported this session, so it works
from a cold start. Run `xui_i18n_extract` first if there are no `.po` files yet.

### `runtime_xui_state` / `runtime_xui_close`

`state` lists what is built (name, layer, visibility, element count, controller). `close` with no
`screenName` closes everything — do that before ending a session so the next one starts clean.

### Checking a transition

A transition is over in a fraction of a second, so a screenshot taken "after" it shows nothing but the
resting state. Two ways to catch it:

- **Step time.** `Xui.TickAnimations("<screen>", 0.2f)` over `runtime_eval` advances a screen's
  transitions by exactly that much, then `runtime_xui_tree` reports where everything actually is.
  Deterministic, and it needs neither play mode nor a GameView.
- **Slow it down.** A copy of the document with `duration="12"` stays mid-flight long enough for two
  or three screenshots to show the element in different places. Note the editor does not run at
  wall-clock speed headless, so a 12-second transition takes considerably longer than 12 seconds.

## When something looks wrong

1. `runtime_xui_tree` — is the geometry right? If yes, it is a colour, sprite, or draw-order problem.
2. `runtime_eval` — read the live component state. Note that the REPL only references core
   assemblies: reach package types (`Image`, `Text`) through `GetComponents<MonoBehaviour>()` and
   reflection, not by name.
3. `runtime_logs` — the importer reports a document that failed to parse, and unresolved sprite/font
   keys, as warnings.

## Caveats worth knowing

- **A project caches packages on install.** `Library/PackageCache/com.xengine.xui@<version>/` is
  reused if it already exists, so an edited package does not reach a project that already installed
  it. Bump the package version, or delete `Library/`, when the package itself changed.
- **Tint transitions do not animate outside play mode**, so a control shows its resting colour.
- **CJK text renders blank.** Assigning a font that contains the glyphs is not enough — the string
  and the font are both correct in memory and nothing draws. Engine-side; verify a translation by
  reading the text back with `runtime_eval`, not by looking at a screenshot. Evidence in
  `docs/superpowers/evidence/xui/m10-i18n-*.png`.
- **An `Image` next to a `Text` can render solid white.** A known engine-side rendering defect, not a
  document problem: exactly one Image per Text draws with the wrong texture, and which one depends on
  draw order. `runtime_eval` shows the correct `Color` on the component. Repro and screenshot are in
  `docs/superpowers/evidence/xui/repro-xgui-text-neighbour-white.*`; do not chase it in the `.xui`.
- Screens are built into the current scene. `runtime_xui_close` removes them; they are not saved
  unless you save the scene.

## Output requirements

Say which screen you opened, the measured size of the elements you cared about, and whether you
confirmed with a screenshot or with `runtime_xui_tree` alone.
