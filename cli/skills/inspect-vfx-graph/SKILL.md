---
name: inspect-vfx-graph
description: Read a Visual Effect Graph (.vfx) offline via vfxgraph_inspect. Echo text, journal-safe.
---

# inspect-vfx-graph

Use when an AI needs to inspect a Visual Effect Graph before compiling or patching it. Files are Echo text under `Assets/`. Do **not** read `Library/cache/`.

## Inspect
```bash
xengine-cli vfxgraph-inspect --project <root> --graph Assets/VFX/Loop.vfx
```

Returns `{ $type, fields[], contentHash }`. Pass `contentHash` to `vfxgraph_patch`. `--asset` is accepted as an alias for `--graph`. The path must end in `.vfx`.

List node types:

```bash
xengine-cli vfxgraph-nodes --project <root>
```

### VFX Graph paths
- `GraphName` — compiled Shader DSL name (e.g. `VFX/Loop`)
- `Template` — `0` Empty / `1` Loop / `2` Burst / `3` Trail / `4` Mesh / `5` Subgraph / `6` Decal
- `Capacity` — particle budget
- `Contexts/$values/<i>/Id|Type|PosX|PosY` — Spawn, Initialize, Update, OutputParticleQuad / OutputParticleMesh / OutputParticleStrip / OutputParticleDecal
- `Blocks/$values/<i>/Id|ContextId|Type|Order` plus `Fields/$values/<j>/Key|Value`
- `Operators/$values/<i>/Id|Type|PosX|PosY` plus Fields (Color R/G/B/A, Float Value, Subgraph `Path`)
- `Edges/$values/<i>/FromNode|FromPort|ToNode|ToPort` — flow (`Flow`) and value wires (`Color`, `Rate`, …)

Loop/Burst/Trail blackboard includes `CollisionMode` (0 off / 1 kill / 2 bounce), `CollisionBounce`, and `CollisionPlane` (`nx,ny,nz,d`). Update block `CollideWithPlane` lowers those knobs. `SdfCollisionMode` / `SdfShape` (1 sphere / 2 box / 3 Texture3D volume / 4 cylinder / 5 cone) / `SdfCenter` / `SdfSize` feed `CollideWithSDF` (signed distance; sphere radius is `SdfSize.x`). Shape 3 samples blackboard `SdfVolume` inside the `SdfCenter ± SdfSize` box. Shape 4 is a Y-capped cylinder (radius `SdfSize.x`, half-height `SdfSize.y`). Shape 5 is a Y-capped cone (bottom `SdfSize.x`, top `SdfSize.z`, half-height `SdfSize.y`). `TurbulenceIntensity` / `TurbulenceFrequency` feed the `Turbulence` Update block (curl of value noise).

A `Subgraph` operator or block with `Path` (or `Graph`) inlines another packaged/project `.vfx` at compile time. Parent blackboard names win. Cycles and missing paths throw.

`LitMode` is 0 unlit / 1 Lambert / 2 Blinn-Phong. Companion blackboard: `LightDirection`, `LightColor` (RGB + intensity in A), `AmbientColor`, `Shininess`. Mesh template defaults to Lambert.

`SoftParticleFade` (0 = off) fades Output alpha against `_CameraDepthTexture` (t3): `alpha *= saturate((sceneEyeZ - particleEyeZ) / fade)`.

`EventKind` is 0 off / 1 OnDie / 2 OnCollide / 3 Always / 4 OnPlay. `EventCount` is the event units. OnDie / OnCollide write `_Counters[0]` (OnDie also ring-spawns up to 8 children). Always queues `EventCount` each `Simulate`; OnPlay queues once from `Play()`. Always / OnPlay are CPU-side and honor `GPUEventTarget`.

`DepthCollisionMode` is 0 off / 1 kill / 2 bounce. Update block `CollideWithDepth` samples `_VfxCameraDepth` at a fixed look-at camera and Kill/Bounce when the particle is behind the linearized scene depth. Mode packs into `SdfCenter.w`. Templates default to off.

`ConformAttraction` (0 = off) pulls particles onto the SDF isosurface: `position -= n * dist * saturate(attraction * dt)`. Update block `ConformToSDF` lowers the knob. Uses the same `SdfShape` / `SdfCenter` / `SdfSize` / `SdfVolume` as `CollideWithSDF`. `VfxSimParams` is 160 bytes.

`VectorFieldIntensity` (0 = off) samples blackboard `VectorField` (Texture3D) over `SdfCenter ± SdfSize` and adds `sample * intensity * dt` to velocity. GPU textures store UNORM RGB where 0.5 is zero wind. Update block `VectorField` lowers the intensity. `VfxSimParams` stays 160 bytes.

`SpawnOverDistance` (0 = off) emits `rate` particles per unit the `VisualEffect` transform travels. Spawn block `SpawnOverDistance` lowers the knob. The first sample records position without spawning so a start/teleport does not dump a burst. Fractional remainder carries across frames.

`AttributeMapMode` is 0 off / 1 position / 2 color / 3 size / 4 position+color. Init block `AttributeMap` samples blackboard `AttributeMap` (Texture2D) at sequential texel `slot % (w*h)`. Position is `emitter + (rgb * 2 - 1) * AttributeMapScale`. Mode `<= 0` is a no-op so a white 1×1 fallback is safe. `VfxSimParams` stays 160 bytes.

`DistortionIntensity` (0 = off) refracts `_VfxSceneColor` at offset screen UVs: `uv + (localUV * 2 - 1) * intensity`. Output block `Distort` lowers the knob. Bind a scene-color texture with `VisualEffect.SetTexture("SceneColor")`. Intensity `<= 0` is a no-op.

`ParticleLightIntensity` (0 = off) contributes point lights from live-particle proxies. `ParticleLightRange` is the light radius; `ParticleLightCount` is how many lights to emit (clamped to 16). Output block `ParticleLight` lowers those knobs. Positions use the Init `HashDir` ballistic so Host/CPU and the editor preview match.

`OutputParticleStrip` (`_OutputMode` 2) connects neighboring live particles into a camera-facing ribbon. Width is particle size. Trail uses this output.

`OutputParticleDecal` (`_OutputMode` 3) projects a particle quad onto blackboard `CollisionPlane` (`n·x + d = 0`) and extrudes in the plane tangent frame, biased 1 cm along the normal to avoid z-fight. Decal template uses this output. `VfxSimParams` stays 160 bytes.

`SortMode` (0 = off / 1 = camera distance) bitonic-sorts `_LiveList` far-to-near after Update so transparent quads/meshes/decals composite correctly. Output block `Sort` lowers the knob. `OutputParticleStrip` skips sort so ribbon connectivity stays sequential. The sort bit packs into `indexCount` bits 24–31; `VfxSimParams` stays 160 bytes.

`CameraFadeNear` / `CameraFadeFar` (0 = off) fade particle alpha by eye-Z distance. Near `> 0` fades in as `saturate(d / near)`; far `> 0` fades out as `saturate((far - d) / far)`. Output block `CameraFade` lowers those knobs. Both `<= 0` is a no-op. Near/far reuse output-cbuffer pads so the layout stays 112 bytes.

`FlipbookBlend` (0 = off) mixes the current flipbook cell with the next using `frac(age/life * frames)`. Output block `FlipbookBlend` lowers the knob. Blend `<= 0` is a no-op. The output cbuffer is 128 bytes (`_FlipbookBlend` after `_DecalPlane`). `VfxSimParams` stays 160 bytes.

`ColorOverLife` / `SizeOverLife` mix particle color and size by `age/lifetime` (`from + (to - from) * t`, no `lerp`). Size is `particle.size * mix` (From=1 / To=0 shrinks to nothing). Compiler-inserted into Update. `VfxSimParams` stays 160 bytes.

`MotionVectorMode` (0 = off / 1 = velocity) reconstructs previous world as `position - velocity * dt` and encodes the NDC delta, scaled by `MotionVectorScale` (default 1). Output block `MotionVector` lowers those knobs. Mode `<= 0` is a no-op. Mode/scale reuse output-cbuffer pads so the layout stays 128 bytes. `VfxSimParams` stays 160 bytes.

`OrientMode` (0 = face camera / 1 = along velocity / 2 = fixed axis) orients quad and mesh local Y along particle velocity or a compiler-inserted axis, with X facing the camera. Output block `OrientAlongVelocity` lowers mode 1. Output block `OrientFixedAxis` ports `Mode` / `X` / `Y` / `Z` (default world Y) set mode `>= 1.5`. Mode `<= 0` is a no-op. Strips and decals keep their own extrusion. `_OrientMode` reuses the last output-cbuffer pad so the layout stays 128 bytes. `VfxSimParams` stays 160 bytes.

`PositionShape` (Init) offsets new particles from the emitter: Mode 0 off / 1 sphere / 2 box / 3 cone / 4 circle. `Sx`/`Sy`/`Sz` are radius or half-extents. Mode `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`LinearDrag` (Update) scales velocity by `exp(-amount * dt)`. Amount `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`SizeOverSpeed` (Update) multiplies size by `1 + length(velocity) * Amount`. Amount `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`ColorOverSpeed` (Update) mixes color from `From` to `To` by `saturate(length(velocity) / Scale)`. Scale `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`ScreenSpaceSize` (Output) scales size by `cameraDistance / Amount` so authored world size holds at that reference distance. Amount `<= 0` is a no-op. Compiler-inserted; output cbuffer stays 128 bytes. `VfxSimParams` stays 160 bytes.

`Force` (Update) adds `force * dt` to velocity (`X` / `Y` / `Z`). Zero force is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes. This is extra acceleration besides blackboard Gravity.

`AngularVelocity` (Update) integrates `pad0 += rate * dt`. Output spins quad/mesh local XY by `pad0`. Rate 0 / angle 0 is a no-op. Strips and decals keep their own extrusion. Reuses unused particle `pad0`; `VfxSimParams` stays 160 bytes.

`CustomHLSL` (Update) inserts the `Code` field into the Update compute pass. Empty / whitespace code is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`Sequential` (Init) places new particles evenly by slot index: Mode 0 off / 1 XZ circle / 2 line X / 3 line Y. `Sx`/`Sy` are radius or half-length; `Sz` is the point count. Mode `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes. Attribute map position still wins when that mode is on.

`InheritVelocity` (Init) adds the `VisualEffect` transform velocity × `Amount` at spawn. Amount `<= 0` is a no-op. Compiler-inserted; the Init dispatch packs emitter vel into `p.gravity` then restores real gravity for Update. `VfxSimParams` stays 160 bytes.

`PeriodicBurst` (Spawn) emits `Count` particles every `Period` seconds. Count or period `<= 0` is a no-op. CPU-side like `SpawnOverDistance`; first burst fires after one period. `VfxSimParams` stays 160 bytes.

`GPUEventTarget` (Update) names another `VisualEffect` GameObject. `SendEvent` / `ForwardGpuEvents` enqueue `EventCount` on that target (or this system when the name is empty). `VfxSimParams` stays 160 bytes.

`TriggerEventRate` (Update) queues `Rate` GPU events per second onto that same CPU path. Rate `<= 0` is a no-op. Independent of `EventKind`. `VfxSimParams` stays 160 bytes.

`TriggerEventOverDistance` (Update) queues `Rate` GPU events per unit the emitter travels. Rate `<= 0` or the first sample is a no-op. Samples the same last pose as `SpawnOverDistance` / `InheritVelocity`. `VfxSimParams` stays 160 bytes.

`TriggerEventOnce` (Update) queues `Count` GPU events on the first Simulate, then stays armed until `Stop()`. Count `<= 0` is a no-op. Honors `GPUEventTarget`. `VfxSimParams` stays 160 bytes.

`CollisionFriction` (Update) scales the tangent of velocity after a plane / SDF / depth bounce by `saturate(1 - Amount)`. Amount `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`CollisionElasticity` (Update) scales the outgoing bounce normal after a plane / SDF / depth contact by `saturate(1 - Amount)`. Amount `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`CollisionLifetimeLoss` (Update) scales remaining life after a plane / SDF / depth contact by `saturate(1 - Amount)`. Amount `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`LimitVelocity` (Update) clamps particle speed to `Speed`. Speed `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`KillSphere` (Update) sets `alive = 0` when a particle is inside a sphere (`X`/`Y`/`Z`/`Radius`). Radius `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`KillAABox` (Update) sets `alive = 0` when a particle is inside an axis-aligned box (`X`/`Y`/`Z`/`Sx`/`Sy`/`Sz`). Any half-extent `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`Attract` (Update) adds velocity toward `float3(X,Y,Z)` by `Strength * dt`. Strength `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`ConformToSphere` (Update) adds velocity toward a sphere surface (`X`/`Y`/`Z`/`Radius`) by `Strength * dt`. Strength `<= 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`Vortex` (Update) adds tangential rotation around `float3(Ax,Ay,Az)` through `float3(X,Y,Z)`, plus optional pull toward that axis. Zero axis, or both Rotation and Attraction `== 0`, is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`ConformToCylinder` (Update) adds velocity toward a cylinder surface (`Radius`) around `float3(Ax,Ay,Az)` through `float3(X,Y,Z)`. Strength `<= 0` or a zero axis is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`MultiplySize` (Update) scales particle size by `Amount`. Amount `== 1` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`MultiplyColor` (Update) scales particle RGBA by `R`/`G`/`B`/`A`. Identity `(1,1,1,1)` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`AddSize` (Update) adds `Amount` to particle size. Amount `== 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`AddColor` (Update) adds `R`/`G`/`B`/`A` to particle color. Identity `(0,0,0,0)` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`AddVelocity` (Update) adds `X`/`Y`/`Z` to particle velocity (not scaled by dt). Zero vector is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`MultiplyVelocity` (Update) scales particle velocity by `Amount`. Amount `== 1` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`OverwriteSize` (Update) overwrites particle size with `Amount` (Init already owns `SetSize`). Amount `== 0` is a no-op (use MultiplySize 0 for a true zero). Compiler-inserted; `VfxSimParams` stays 160 bytes.

`OverwriteColor` (Update) overwrites particle RGBA (Init already owns `SetColor`). Identity `(0,0,0,0)` is a no-op (use MultiplyColor 0 for a true zero). Compiler-inserted; `VfxSimParams` stays 160 bytes.

`OverwriteVelocity` (Update) overwrites particle velocity (Init already owns `SetVelocity`). Zero vector is a no-op (use MultiplyVelocity 0 for a true zero). Compiler-inserted; `VfxSimParams` stays 160 bytes.

`OverwriteLifetime` (Update) overwrites particle lifetime (Init already owns `SetLifetime`). Amount `== 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`AddLifetime` (Update) adds `Amount` to particle lifetime. Amount `== 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`MultiplyLifetime` (Update) scales particle lifetime by `Amount`. Amount `== 1` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`AddAge` (Update) adds `Amount` to particle age. Amount `== 0` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`MultiplyAge` (Update) scales particle age by `Amount`. Amount `== 1` is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`SetAge` (Update) overwrites particle age with `Amount`. Amount `== 0` is a no-op (use MultiplyAge 0 for a true zero). Compiler-inserted; `VfxSimParams` stays 160 bytes.

`AddPosition` (Update) adds `X`/`Y`/`Z` to particle position (not scaled by dt). Distinct from Init `PositionShape`. Zero vector is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`SetPosition` (Update) overwrites particle position with `X`/`Y`/`Z`. Distinct from Init `PositionShape`. Zero vector is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`OrientLookAt` (Output) aims quad/mesh local Y at `float3(X,Y,Z)` when Mode `>= 1.5` (`vfxOrientAlong = target - position`). Mode `<= 0` is a no-op. Compiler-inserted; output cbuffer stays 128 bytes. Strips and decals keep their own extrusion.

`FlipbookRate` (Output) advances atlas cells by `age * Rate` (wrapped into the grid) when Rate `> 0`. Rate `<= 0` keeps the lifetime `age/life` mapping. Compiler-inserted; output cbuffer stays 128 bytes.

`FlipbookStart` (Output) offsets the atlas cell by `Frame`, then wraps into the grid. Frame `== 0` is a no-op. Compiler-inserted; output cbuffer stays 128 bytes.

`KillPlane` (Update) sets `alive = 0` when `dot(position, normalize(N)) - Distance < 0`. A zero normal is a no-op. Compiler-inserted; `VfxSimParams` stays 160 bytes.

`FlipbookMotion` (Output) warps adjacent flipbook UVs from `_FlipbookMotion` (t5, RG 0.5 = rest) before `FlipbookBlend`. Scale `<= 0` is a no-op. Compiler-inserted; output cbuffer stays 128 bytes. `VfxSimParams` stays 160 bytes.

A valid graph has all four contexts. Compile / patch with [edit-vfx-graph](../edit-vfx-graph/SKILL.md).

Or MCP: `vfxgraph_inspect` / `vfxgraph_nodes`.
