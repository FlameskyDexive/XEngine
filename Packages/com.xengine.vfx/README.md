# Visual Effect Graph

Official optional XEngine package. It ships with the Editor but is **not** added to a project until you install it from Package Manager (`Window / Package Management / Package Manager`).

Installing copies this folder into the project's `Library/PackageCache/com.xengine.vfx@<version>/` and records the pin in `Packages/manifest.json`.

Full docs: `Documentation~/index.md`. Samples: Package Manager → Samples (Simple Loop / Simple Burst).

## Runtime

`VisualEffect` is a GPU particle component (`Effects / Visual Effect`). Each playing instance:

1. Accumulates a CPU spawn count from `SpawnRate` plus `SpawnOverDistance` (particles per unit the emitter travels) and `PeriodicBurst` (`Count` every `Period` seconds)
2. Dispatches **Init** compute (writes new particles into a ring)
3. Dispatches **Update** compute (integrate + compact live indices)
4. Dispatches **WriteArgs** (fills `DrawIndexedIndirect` args)
5. Renders a billboarded quad per live particle on the transparent queue

OpenGL / WebGL2 / Null have no compute: the component still Play/Stops, but does not simulate or draw. Requires a device with `SupportsCompute` (Vulkan first).

## Authoring

`.vfx` files are Echo text (same idea as `.shadergraph`): flat `Contexts` / `Blocks` / `Operators` / `Edges`. Create one from **Assets / Create / Effects / Visual Effect Graph**, or start from a template:

| Template | Spawn | Notes |
|----------|-------|--------|
| Empty | none | four contexts, no blocks |
| Loop | `ConstantSpawnRate` | gravity + plane bounce + billboard output |
| Burst | `Burst` | one-shot count on Play; kill on plane |
| Trail | high rate, short life | `ColorOverLife` / `SizeOverLife` + `OutputParticleStrip` ribbon |
| Mesh | `ConstantSpawnRate` | `OutputParticleMesh` cubes (or `SetMesh`) |
| Subgraph | Loop host | `Subgraph` operator `Path=Trail` inlines `ColorOverLife` |
| Decal | `ConstantSpawnRate` | `OutputParticleDecal` quads projected onto `CollisionPlane` |

`VfxGraphCompiler` emits Init / Update / WriteArgs / Output compute+graphics shaders from `VfxParticles.shader`. Update `ColorOverLife` / `SizeOverLife` mix color and size by age (`from + (to - from) * t`). Init `PositionShape` offsets spawn inside a sphere / box / cone / circle (`Mode` 0 = off). Init can sample blackboard `AttributeMap` (Texture2D, `_AttributeMap` t0) to set position / color / size from `AttributeMapMode` (0 = off). The Output pass samples `_MainTex` (register t2, after particle SRVs) and remaps UVs across a flipbook grid from particle age. `FlipbookBlend` (0 = off) mixes the current cell with the next. Soft particles fade alpha against `_CameraDepthTexture` (t3) from blackboard `SoftParticleFade` (0 = off). Distortion / heat haze refracts `_VfxSceneColor` (t4) from blackboard `DistortionIntensity` (0 = off). Output `ParticleLight` contributes point lights (`ParticleLightIntensity` / `Range` / `Count`, 0 = off). Output `Sort` (`SortMode` 0 off / 1 camera distance) reorders live particles far-to-near for transparent blending; strips skip sort. Output `CameraFade` (`CameraFadeNear` / `CameraFadeFar`, 0 = off) fades alpha by camera eye-Z. Output `MotionVector` (`MotionVectorMode` 0 = off) encodes screen motion from `position - velocity * dt`. Output `OrientAlongVelocity` (`OrientMode` 0 = camera-facing / 1 = along velocity) aligns quad/mesh local Y with velocity. Output `OrientFixedAxis` (`OrientMode` >= 1.5) uses a compiler-inserted axis (default world Y). Update collides with blackboard `CollisionPlane` (`CollisionMode` 0 off / 1 kill / 2 bounce) and an SDF (`SdfCollisionMode` / `SdfShape` 1 sphere / 2 box / 3 Texture3D `SdfVolume` / 4 cylinder / 5 cone), then adds curl-noise turbulence (`TurbulenceIntensity` / `TurbulenceFrequency`). GPU events (`EventKind` 0 off / 1 OnDie / 2 OnCollide, `EventCount`) increment a counter and OnDie spawns children at the death site. `OutputParticleMesh` instances a cube (or `VisualEffect.SetMesh`) instead of a camera-facing quad. `OutputParticleStrip` (`_OutputMode` 2) extrudes a camera-facing ribbon between neighboring live particles (Trail default). `OutputParticleDecal` (`_OutputMode` 3) projects a quad onto `CollisionPlane` with a 1 cm normal bias. A `Subgraph` operator/block with `Path` inlines another `.vfx` (parent blackboard names win). Output lighting is `LitMode` 0 unlit / 1 Lambert / 2 Blinn, with blackboard light knobs and a scene directional fallback. Assign the asset to `VisualEffect.Graph` to copy compiled spawn/gravity/color onto the component. Templates expose blackboard properties; `VisualEffect.SetFloat` / `SetVector3` / `SetVector4` / `SetTexture` / `SetMesh` override them per instance.

`Window / Effects / Visual Effect Graph` opens `VfxGraphWindow`: Origami node canvas, blackboard, compile, and an embedded GPU preview (CPU stand-in without compute).

## AI CLI

`vfxgraph_inspect` / `vfxgraph_patch` / `vfxgraph_compile` / `vfxgraph_nodes` / `vfxgraph_create` on the offline Host (Echo-only). Live `--serve`: `runtime_vfx_state` / `runtime_vfx_preview` after the package Editor assembly is loaded.
