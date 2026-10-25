---
name: edit-animation-clip
description: Inspect and patch AnimationClip .anim curves offline — bindings, keyframe add/set/remove. Journaled with drift protection.
---

# edit-animation-clip

Use when you need to **read or edit** a `.anim` without the Animation window. No running editor required.
To bind the live window to a scene object, use `runtime_animation_bind` ([observe-runtime](../observe-runtime/SKILL.md)). To stamp a key from a live pose, use `runtime_animation_record_key`.

## Inspect
```bash
xengine-cli clip-inspect --project <root> --clip Assets/Walk.anim
```
Returns duration, `ticksPerSecond`, wrap, and bindings (`path` = bone or GameObject path, `channel` = PosX/RotY/… or a generic property name, key count, value range). Generic tracks also report `typeName` and `property`. Plus `contentHash`.

Optional scene root (M30): `--scene Assets/Scenes/Main.scene --object <GameObject-Guid>` lists clip GUIDs on that object's Animator / AnimationComponent as `availableClipGuids`.

Sample rate is `ticksPerSecond`. Patch it (M39):
```bash
xengine-cli clip-patch --clip Assets/Walk.anim \
  --patches '[{"path":"TicksPerSecond","value":"60"}]' \
  --idempotency-key <key> --intent "24→60 fps"
```
`SampleRate` is an alias for `TicksPerSecond`. Also writable: `Duration`, `Wrap`, `Name`, `RootBonePath`.

Clip events (M40):
```bash
xengine-cli clip-event-add --clip Assets/Walk.anim --time 0.5 --function Footstep \
  --float 1 --int 0 --string heel --idempotency-key <key> --intent "foot plant"
xengine-cli clip-event-patch --clip Assets/Walk.anim --index 0 \
  --patches '[{"path":"FunctionName","value":"OnLand"}]' --idempotency-key <key>
xengine-cli clip-event-remove --clip Assets/Walk.anim --index 0 --idempotency-key <key>
```
`clip_inspect` lists events as `functionName@time`.

## Patch a curve
```bash
xengine-cli clip-curve-patch --clip Assets/Walk.anim \
  --bone Hips --channel PosX --op add --time 0.5 --value 1.2 \
  --expected-before-hash <hash> --idempotency-key <key> --intent "mid-stride hip X"
```
Generic (component float):
```bash
xengine-cli clip-curve-patch --clip Assets/Lit.anim \
  --type XEngine.Runtime.DirectionalLight --channel Intensity --op add --time 0 --value 1 \
  --idempotency-key <key> --intent "light intensity"
```
- `--op` `add` | `set` | `remove`. `set`/`add` on an existing time overwrites the value.
- `--channel` is `PosX|PosY|PosZ|RotX|RotY|RotZ|RotW|ScaleX|ScaleY|ScaleZ` for bone TRS.
- Generic float bindings (M38): pass `--type <TypeName>` (e.g. `XEngine.Runtime.DirectionalLight`); `--bone` is then the GameObject path (omit or empty for the animation root) and `--channel` is the property (`Intensity`).
- Missing bones / generic tracks are created on `add`/`set`.
- Curve keys store `inTangentMode` / `outTangentMode` / `weightedMode` / weights (M41). Missing fields on old `.anim` files default to Free / None.

MCP: `clip_inspect`, `clip_patch`, `clip_curve_patch`, `clip_event_add`, `clip_event_patch`, `clip_event_remove`.
