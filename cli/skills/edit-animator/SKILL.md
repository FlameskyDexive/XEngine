---
name: edit-animator
description: Inspect and patch AnimatorController .controller assets offline — states, transitions, parameters, layers, BlendTrees, plus AnimatorOverrideController and AvatarMask. Journaled with drift protection.
---

# edit-animator

Use when you need to **read or edit** a `.controller` without opening the Animator window.
Requires no running editor. For a visual check after edits, see [inspect-animator](../inspect-animator/SKILL.md).

## Inspect first
```bash
xengine-cli animator-inspect --project <root> --controller Assets/Hero.controller
```
Returns layers, states (motion GUID + BlendTree parameter + `Behaviours` type names), transitions + conditions, parameters,
BlendTree summaries, and `contentHash`. Pass that hash to writes as `--expected-before-hash`.

## Override controller / Avatar Mask
```bash
xengine-cli overridecontroller-inspect --project <root> --asset Assets/Hero.overridecontroller
xengine-cli overridecontroller-patch --asset Assets/Hero.overridecontroller \
  --patches '[{"path":"Overrides/$values/0/To/AssetID","value":"<clip-guid>"}]' --idempotency-key <key>
xengine-cli avatarmask-inspect --project <root> --asset Assets/Upper.mask
xengine-cli avatarmask-patch --asset Assets/Upper.mask \
  --patches '[{"path":"Entries/$values/0/Weight","value":"0"},{"path":"HumanoidParts/$values/2","value":"false"}]' \
  --idempotency-key <key>
```
`HumanoidParts` index 2 is Head (`Root, Body, Head, LeftArm, RightArm, LeftLeg, RightLeg, LeftFingers, RightFingers`).

## Validate
```bash
xengine-cli animator-validate --controller Assets/Hero.controller
```
Structured diagnostics (`unreachable-state`, `missing-motion`, `missing-parameter`, `dead-transition`,
`muted-transition`, `solo-shadowed-transition`, `layer-weight-zero`). Read-only; no idempotency key.
Use this after patches and before asking for a screenshot.

## States
```bash
xengine-cli animator-state-create --controller Assets/Hero.controller --name Jump \
  --idempotency-key <key> --intent "add jump state"
xengine-cli animator-state-patch --controller Assets/Hero.controller --state Jump \
  --patches '[{"path":"Speed","value":"1.2"},{"path":"WriteDefaults","value":"true"},{"path":"Behaviours/$values/0","value":"MyGame.JumpBehaviour"}]' --idempotency-key <key>
xengine-cli animator-state-delete --controller Assets/Hero.controller --state Jump \
  --idempotency-key <key>
```

## Transitions / parameters / layers
```bash
xengine-cli animator-transition-create --controller Assets/Hero.controller --from Idle --to Jump --name IdleToJump --idempotency-key <key>
xengine-cli animator-transition-patch --controller Assets/Hero.controller --transition IdleToJump \
  --patches '[{"path":"Solo","value":"true"},{"path":"HasFixedDuration","value":"true"},{"path":"InterruptionSource","value":"1"}]' --idempotency-key <key>
xengine-cli animator-transition-reorder --controller Assets/Hero.controller --state Idle \
  --names IdleToJump,IdleToWalk --idempotency-key <key>
xengine-cli animator-parameter-add --controller Assets/Hero.controller --name Jump --type Trigger --idempotency-key <key>
xengine-cli animator-layer-add --controller Assets/Hero.controller --name Upper --idempotency-key <key>
```

`--type` is `Float` | `Int` | `Bool` | `Trigger`. Add `--dry-run` to any write. Optional `--layer` scopes state/transition ops to a mixer layer.

## Nested state machines
```bash
xengine-cli animator-statemachine-create --controller Assets/Hero.controller --name Combat \
  --idempotency-key <key> --intent "add combat SM"
xengine-cli animator-statemachine-move --controller Assets/Hero.controller --state Punch \
  --target Combat --idempotency-key <key>
xengine-cli animator-statemachine-delete --controller Assets/Hero.controller --name Combat \
  --idempotency-key <key>
```

`--parent` (create/delete) and `--path` (move source) are slash-separated names from the layer root.
`animator_inspect` returns `stateMachines` (the tree per layer) plus a flattened `states` list.

## BlendTree on a state
```bash
xengine-cli blendtree-inspect --asset Assets/Hero.controller --state Walk
xengine-cli blendtree-patch --asset Assets/Hero.controller --state Walk \
  --patches '[{"path":"ParameterName","value":"Speed"}]' --idempotency-key <key>
```
2D trees also expose `ParameterNameY`, `UseAutomaticThresholds`, child `Position` (`X`/`Y`), nested `Tree`, and `DirectParameterName`. Patch those paths the same way (`ParameterNameY`, `Children/0/Position/X`, …).
Standalone `.motion` files omit `--state`.

MCP names match the CLI with underscores (`animator_inspect`, `overridecontroller_inspect`, `avatarmask_inspect`, …).
