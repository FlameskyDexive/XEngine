# com.xengine.genshin

Genshin-style anime (NPR) character rendering for XEngine plus a basic third-person
combat demo, ported from the private reference project `GenshinGamePlay`
(Unity 2022.3 / TaoTie framework / FernNPR shaders).

## What's inside

- **Runtime/Shaders** — the `GenshinToon` shader family for XEngine's ShaderLab-style DSL
  (dual-source GLSL + Slang):
  - `GenshinToonBody.shader` — ramp (two-band) diffuse, lightmap channels, stylized specular,
    fresnel rim light, inverted-hull outline, shadow caster.
  - `GenshinToonHair.shader` — ramp diffuse with hair ramp row, outline.
  - `GenshinToonFace.shader` — SDF face shadow, light-direction oblique blend, screen-space rim.
  - `GenshinToonEye.shader` / `GenshinUnlit.shader` — eye and effect/weapon fallbacks.
- **Runtime/Combat** — reusable gameplay pieces (hit-stop, damage, camera shake helpers).
- **Editor/UnityImport** — one-shot converters from the reference project's Unity YAML assets
  (`.mesh`/`.prefab`/`.anim`/FernNPR `.mat`) into XEngine assets (glTF `.glb` + Echo `.mat`).
- **Samples~/CombatDemo** — the runnable demo scene, converted KeQing assets, animator
  controller, and player/combat scripts.

## Install

The package is official but **optional** (`installByDefault: false`): open
**Window → Package Manager**, find *Genshin Toon Combat Demo* under *Available*, and click
**Install**. Installing copies it to `Library/PackageCache/com.xengine.genshin@0.1.0/`.
Import the *Combat Demo* sample from the package detail panel into your project's
`Assets/Samples/`.

## Asset license note

The sample character (KeQing) and training-dummy assets are converted from a private
reference project for **internal testing only**. Do not redistribute the sample assets.

See `Documentation~/index.md` for architecture details and the milestone/evidence trail in
`docs/superpowers/evidence/genshin/`.
