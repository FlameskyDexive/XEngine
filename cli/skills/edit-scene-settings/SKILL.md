---
name: edit-scene-settings
description: Query and patch a scene's top-level lighting/skybox settings (Fog, Ambient, Skybox) offline. Every change is journaled with provenance + drift-protected.
---

# edit-scene-settings

Use when an AI needs to read or change a scene's **scene-level** lighting/skybox — the `Fog`,
`Ambient`, and `Skybox` fields that live on the Scene root (not on GameObjects). These control the
overall mood: fog density/color, ambient light color/strength, skybox mode/colors.

## Query scene settings
```bash
xengine-cli scene-settings-query --project <root> --scene Assets/Scenes/Main.scene
```
Returns `{ categories: { fog: [...], ambient: [...], skybox: [...] }, contentHash }`. Each entry is a
slash-path + value (e.g. `{ path: "Fog/Density", value: "0.01" }`). Note the `contentHash` for drift
protection.

## Patch scene settings
```bash
xengine-cli scene-settings-patch --project <root> --scene Assets/Scenes/Main.scene \
  --patches '[{"path":"Fog/Density","value":"0.05"},
              {"path":"Skybox/Mode","value":"2"}]' \
  --expected-before-hash <hash-from-query> \
  --idempotency-key <key> --intent "thicker fog + gradient sky"
```

### Common paths
**Fog** (`Fog` compound):
- `Fog/Mode` — 0=Off, 1=Linear, 2=Exponential, 3=ExponentialSquared.
- `Fog/Color/R|G|B|A` — fog color.
- `Fog/Start`, `Fog/End` — linear fog range.
- `Fog/Density` — exp/exp2 fog density.

**Ambient** (`Ambient` compound):
- `Ambient/Mode` — 0=Uniform, 1=Hemisphere.
- `Ambient/Strength` — ambient intensity multiplier.
- `Ambient/Color/X|Y|Z|W` — uniform ambient color (Float4).
- `Ambient/SkyColor/...`, `Ambient/GroundColor/...` — hemisphere colors.

**Skybox** (`Skybox` compound):
- `Skybox/Mode` — 0=Procedural, 1=SolidColor, 2=Gradient, 3=Material.
- `Skybox/SolidColor/R|G|B|A` — solid sky color.
- `Skybox/GradientTop/...`, `Skybox/GradientBottom/...` — gradient sky colors.
- `Skybox/GradientExponent` — gradient blend.

## Drift protection + idempotency
- `--expected-before-hash`: stale hash → `stale_state` (re-read and retry).
- `--idempotency-key`: replay-safe.

## Difference from scene_object_patch
`scene_object_patch` edits **GameObjects** (inside `serializeObj.array`). `scene_settings_patch`
edits the **Scene root's own fields** (Fog/Ambient/Skybox) — they're siblings of `serializeObj`, not
children. Use the right tool for the target.
