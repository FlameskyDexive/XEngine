---
name: play-spine-animation
description: Drive live Spine skeletons in a running editor — play/queue animations, set skins, inspect tracks and available animations via runtime_spine_* MCP tools. Offline, add SkeletonAnimation components to scenes via scene_component_add short-names.
---

# play-spine-animation

Use when an AI needs to verify or drive Spine skeletal animation (`com.xengine.spine` package):
select a skeleton in the Inspector and play an animation, switch clips mid-playmode, inspect
available animations/skins, or add a Spine component to a scene headlessly.

## Prerequisites
- The project must enable the optional package: `Packages/manifest.json` →
  `"dependencies": { "com.xengine.spine": "0.1.0" }`.
- Live tools require `XEngine.Editor --serve --project <root>` (in-process MCP server).

## Offline: add a Spine component to a scene
```bash
xengine-cli scene-component-add --project <root> --scene <path> \
  --object <GameObject-identifier> --type SkeletonAnimation \
  --fields '[{"path":"AnimationName","value":"walk"},
             {"path":"Loop","value":"true"},
             {"path":"SkeletonDataAsset/AssetID","value":"<skeleton-json-guid>"}]' \
  --idempotency-key <key> --intent "add spineboy"
```
- `SkeletonAnimation` / `SkeletonRenderer` are registered short-names (resolve to
  `XEngine.Spine.Runtime`).
- The skeleton guid is the `.meta` guid of the imported Spine skeleton JSON/`.skel.bytes`
  (`asset-search --query spineboy`). The importer auto-links a sibling `-pma` atlas.

## Live: play / inspect (runtime_spine_* tools)
```
runtime_spine_state  { identifier?: <GameObject guid> }        # tracks, times, animations, skins
runtime_spine_play   { animation, loop=true, track=0, timeScale?, identifier? }   # SetAnimation
runtime_spine_queue  { animation, loop=true, track=0, delay=0, identifier? }      # AddAnimation
runtime_spine_set_skin { skin, identifier? }
```
- `identifier` omitted → uses the current **Inspector selection** (select first with
  `runtime_selection {"action":"set"}` to test inspector-driven playback).
- Edit mode: `runtime_spine_play` switches the serialized `AnimationName` track instantly
  (pose applies on next collect). Playmode: `runtime_playmode {"action":"enter"}` first, then
  track `time` advances every frame — poll `runtime_spine_state` twice ~1 s apart to prove
  playback.
- Combine with `runtime_screenshot` / `runtime_stats` for visual + draw-call evidence.

## Notes
- Spineboy imports at 0.01 scale (~7.3 world units tall). An orthographic camera
  (`ProjectionMode 1`, `OrthographicSize ≈ 4.5`, `Y ≈ 3.8`) frames the full character.
- Animation `Start()` in user scripts must be `public override void Start()` — plain `Start()`
  hides the base virtual and never ticks (SceneComponentRegistry detects overrides).
