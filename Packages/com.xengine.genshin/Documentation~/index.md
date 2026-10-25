# com.xengine.genshin — Documentation

Genshin-style anime (NPR) character rendering and a basic combat demo for XEngine.
Ported from the private reference project `GenshinGamePlay` (Unity 2022.3, TaoTie
framework, FernNPR shader family). Full plan:
`docs/superpowers/specs/2026-08-20-genshin-toon-combat-demo.md` (repository root).

## Package layout

| Path | Contents |
|---|---|
| `Runtime/Shaders` | `GenshinToon{Body,Hair,Face,Eye}.shader`, `GenshinUnlit.shader`, shared includes `GenshinToon.glsl`/`.hlsl` |
| `Runtime/Combat` | Reusable gameplay pieces: hit-stop, damage numbers, camera shake |
| `Editor/GenshinUnityAssetCopier.cs` | Copies the source Unity project's assets in and writes the `unity-guid-map.json` sidecar (importers live in the shared `com.xengine.unityimporter` package) |
| `Editor/GenshinMenu.cs` | Editor menu entry *Genshin → Convert Unit Assets…* |
| `Samples~/CombatDemo` | The runnable demo: scene, converted KeQing + dummy assets, `KeQing.controller`, player/combat scripts |

## Install & run

1. **Window → Package Manager** → *Available* → **Genshin Toon Combat Demo** → **Install**
   (the package is official but optional; it is not mounted until installed).
2. Import the **Combat Demo** sample from the package detail view; it lands in
   `Assets/Samples/Genshin Toon Combat Demo/Combat Demo/`.
3. Open `Scenes/GenshinCombat.scene` and press **Play**:
   WASD move (camera-relative), Shift sprint, Space jump, left mouse / J for the
   5-hit sword combo against the training dummies.

## Rendering architecture (summary)

All toon shaders share one `ToonMaterial` cbuffer (union of body/hair/face fields) so a
single material-uniform packer serves the whole family on Vulkan/D3D12 (OpenGL binds
generically by name). Each shader carries three passes:

1. **Forward** (`RenderOrder=Opaque`) — ramp diffuse (`_RampTex` 256×20, U/V offsets),
   `_LightMap` channel packing, stylized/GGX specular, fresnel or screen-space rim,
   directional shadow sampling.
2. **ShadowCaster** — writes into the engine shadow atlas.
3. **Outline** — inverted hull (`Cull Front`, vertex extrusion along normal, skinned via
   the standard `TransformClip` helpers).

The face shader computes the SDF shadow lookup coordinate in the vertex shader (face
forward vs light direction, `_SDFFaceArea` arc, direction reversal) and thresholds
`_SDFFaceTex` in the fragment shader, matching the FernNPR reference values carried over
from the source materials (e.g. Face: area 45°, reversal on, threshold 0.416).

## Asset conversion

`Genshin → Convert Unit Assets…` reads from the reference project's
`Assets/AssetsPackage/Unit` (path configurable) and writes converted assets into the
package sample folder. Meshes + skeletons + animations are emitted as one glTF 2.0 `.glb`
per unit; textures are copied as PNG; FernNPR materials are mapped onto the GenshinToon
shaders with their original parameter values. Converted outputs are committed so the demo
runs without the reference project present.

## Milestones & evidence

Progress is tracked node-by-node (test + screenshot acceptance) in
`docs/superpowers/evidence/genshin/`:

- m0 — package scaffold (this milestone)
- m1 — mesh/skeleton converter, KeQing renders (placeholder material)
- m2 — animation converter + generated `KeQing.controller`
- m3 — `GenshinToonBody` + ToonMaterial uniform packers (GL + Vulkan)
- m4 — hair/face(SDF)/eye/unlit shaders — full character look
- m5 — movement gameplay (input/camera/locomotion FSM)
- m6 — combat gameplay (combo, hit detection, hit-stop, camera shake)
- m7 — headless acceptance run + performance sanity + docs

## License

MIT for package source. Sample assets converted from a private reference project —
internal testing only, no redistribution (see `LICENSE.md`).
