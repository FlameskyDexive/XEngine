---
name: inspect-animator
description: Read a .controller's structure offline, then optionally screenshot the Animator window in a live editor.
---

# inspect-animator

Use when you need the **graph structure** of an Animator Controller (states, transitions, parameters,
BlendTrees), an **AnimatorOverrideController** mapping, an **AvatarMask**, or a **screenshot** of the Animator window.

## Read structure first (offline, no editor)

```bash
xengine-cli animator-inspect --project <root> --controller Assets/BananaManLocomotion.controller
```

See [edit-animator](../edit-animator/SKILL.md) for writes and `animator_validate`. Clip curves: [edit-animation-clip](../edit-animation-clip/SKILL.md).

## Validate (offline)

```bash
xengine-cli animator-validate --project <root> --controller Assets/Hero.controller
```

MCP: `animator_validate`. Codes include `unreachable-state`, `missing-motion`, `missing-parameter`,
`dead-transition`, `muted-transition`, `solo-shadowed-transition`, `layer-weight-zero`.

## Screenshot (live editor `--serve`)

```csharp
// runtime_eval — first call can take 60–90s (Roslyn)
var db = EditorAssetBackend.Instance;
var entry = db.GetEntry("BananaManLocomotion.controller")
          ?? db.GetEntry("Assets/BananaManLocomotion.controller");
Selection.Select(new XEngine.Editor.GUI.Panels.ContentItem {
    Name = "BananaManLocomotion.controller",
    RelativePath = entry.Path,
    Guid = entry.Guid
});
```

Then `runtime_panel_screenshot` with
`panelType = "XEngine.Editor.GUI.Panels.AnimatorWindow"` (900×600).

Sample: [`Samples/AnimationDemo/BananaManLocomotion.controller`](../../../Samples/AnimationDemo/BananaManLocomotion.controller)
(Locomotion BlendTree + Jump).

## Notes
- Layers | Parameters tabs and in-canvas 1D BlendTree editing are v0.5 (M19–M20).
- v0.6 M32: nested state machines. `animator_inspect` returns `stateMachines` (tree). Writes:
  `animator_statemachine_create` / `delete` / `move`. Animator window: breadcrumb, double-click SM to
  drill in, Up node, drag a state onto an SM node.
- v0.6 M27: box-select / Ctrl-add / group drag, Ctrl+C V D Echo clipboard, F frames the
  selection, Alt+drag pans, wheel zooms, wires have direction arrows and fan-out.
- v0.6 M37: background/node menus (Make Transition, From Selected Clip, confirm-delete),
  Auto Live Link progress bars, drag `.anim` onto the canvas, `animator_validate`.
- GPU readback may flip/rotate glyphs; layout + visible nodes is the acceptance gate.
- Driver: `python Tools/capture_panels.py` (throwaway `AnimationProject/`, gitignored).
- v0.6 M45: live `runtime_animator_state` / `set_parameter` / `play` / `runtime_animation_preview`.
  Workflow sample: `cli/samples/workflows/author-locomotion.json`. Prompts: `author_animator_controller`,
  `debug_animation_state`.
