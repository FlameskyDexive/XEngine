# Visual Effect Graph

Node-based GPU particles for XEngine. This package is **optional**: it ships with the Editor but is not mounted until you install it from Package Manager (`Window / Package Management / Package Manager`).

Install copies the folder into `Library/PackageCache/com.xengine.vfx@0.1.0/` and pins it in `Packages/manifest.json`.

## Runtime

`VisualEffect` (`Effects / Visual Effect`) on a GameObject:

1. CPU spawn count from `SpawnRate`, `SpawnOverDistance` (particles per unit the emitter travels), `PeriodicBurst` (`Count` every `Period` seconds), or a pending `BurstCount` on Play
2. **Init** compute — ring-write new particles; optional `PositionShape` offsets from the emitter (sphere / box / cone / circle); optional Texture2D `AttributeMap` overwrites position / color / size when `AttributeMapMode` is greater than 0
3. **Update** compute — integrate + compact live indices; `ColorOverLife` / `SizeOverLife` mix by age
4. **WriteArgs** compute — `DrawIndexedIndirect` args
5. Billboard quads, mesh instances (`OutputParticleMesh`), strip ribbons (`OutputParticleStrip`), **or** plane-projected decals (`OutputParticleDecal`) on the transparent queue, sampling `_MainTex` (white default; `SetTexture` / blackboard `MainTexture` override). Flipbook cells from `FlipbookColumns` × `FlipbookRows` over particle age; `FlipbookBlend` mixes adjacent cells when greater than 0. Update collides with `CollisionPlane` (`CollisionMode` 0 off / 1 kill / 2 bounce) and an SDF (`SdfCollisionMode` / `SdfShape` sphere, box, Texture3D `SdfVolume`, cylinder, or cone), then adds curl-noise turbulence (`TurbulenceIntensity` / `TurbulenceFrequency`). GPU events (`EventKind` 0 off / 1 OnDie / 2 OnCollide) increment `_Counters[0]` by `EventCount`; OnDie also ring-spawns children. Output lighting: `LitMode` 0 unlit / 1 Lambert / 2 Blinn-Phong. Soft particles fade alpha against `_CameraDepthTexture` when `SoftParticleFade` is greater than 0. Distortion / heat haze refracts `_VfxSceneColor` when `DistortionIntensity` is greater than 0. Output `ParticleLight` adds point lights when `ParticleLightIntensity` and `ParticleLightCount` are greater than 0. Output `Sort` reorders live particles far-to-near when `SortMode` is greater than 0 (strips skip sort). Output `CameraFade` fades alpha by camera eye-Z when `CameraFadeNear` or `CameraFadeFar` is greater than 0. Output `MotionVector` encodes screen motion from `position - velocity * dt` when `MotionVectorMode` is greater than 0. Output `OrientAlongVelocity` aligns quad/mesh local Y with velocity when `OrientMode` is greater than 0 and less than 1.5. Output `OrientFixedAxis` uses a compiler-inserted axis when `OrientMode` is 1.5 or greater (default world Y).

Requires `SupportsCompute` (Vulkan first). OpenGL / WebGL2 / Null keep Play/Stop but do not simulate or draw.

Assign a `.vfx` to `VisualEffect.Graph` and call `ApplyGraph()` (or rely on OnValidate) to copy compiled spawn/gravity/color onto the component. Loop/Burst/Trail templates expose blackboard properties (`SpawnRate`, `BurstCount`, `Gravity`, `StartColor`, `MainTexture`, `FlipbookColumns`, `FlipbookRows`, `CollisionMode`, `CollisionBounce`, `CollisionPlane`, `SdfCollisionMode`, `SdfShape`, `SdfCenter`, `SdfSize`, `SdfVolume`, `TurbulenceIntensity`, `TurbulenceFrequency`, `LitMode`, `LightDirection`, `LightColor`, `AmbientColor`, `Shininess`, `SoftParticleFade`, `EventKind`, `EventCount`, `AttributeMapMode`, `AttributeMapScale`, `AttributeMap`, `CameraFadeNear`, `CameraFadeFar`, `FlipbookBlend`, `MotionVectorMode`, `MotionVectorScale`, `OrientMode`). Override them per instance with `SetFloat` / `SetVector3` / `SetVector4` / `SetTexture`; `ResetExposedOverrides()` restores graph defaults. A scene directional light fills `LightDirection` / `LightColor` unless those names are overridden.

## Authoring

`.vfx` files are Echo text: flat `Contexts` / `Blocks` / `Operators` / `Edges`. Create one from **Assets / Create / Effects / Visual Effect Graph**, start from a template, or import a sample.

| Template | Spawn | Notes |
|----------|-------|--------|
| Empty | none | four contexts, no blocks |
| Loop | `ConstantSpawnRate` | gravity + plane bounce + sphere SDF + billboard output |
| Burst | `Burst` | one-shot count on Play; kill on plane |
| Trail | high rate, short life | `ColorOverLife` fade + `OutputParticleStrip` ribbon |
| Mesh | `ConstantSpawnRate` | `OutputParticleMesh` cubes (or `SetMesh`) |
| Subgraph | Loop host | `Subgraph` operator `Path=Trail` inlines the child's Update fade |
| Decal | `ConstantSpawnRate` | `OutputParticleDecal` quads projected onto `CollisionPlane` |

`Window / Effects / Visual Effect Graph` opens `VfxGraphWindow`: Origami node canvas, blackboard, compile, GPU preview RT. Double-click a `.vfx` asset.

## AI CLI

Offline (`xengine-cli`, no engine runtime):

```bash
xengine-cli vfxgraph-create --project <root> --graph Assets/VFX/Hero.vfx --template Loop
xengine-cli vfxgraph-inspect --project <root> --graph Assets/VFX/Hero.vfx
xengine-cli vfxgraph-compile --project <root> --graph Assets/VFX/Hero.vfx
```

Live editor (`XEngine.Editor --serve`) with the package installed: `runtime_vfx_state`, `runtime_vfx_preview`. Skills: `cli/skills/inspect-vfx-graph`, `cli/skills/edit-vfx-graph`.

## Player

The Runtime asmdef has empty `includePlatforms` (ships in players). Editor asmdef is `includePlatforms: ["Editor"]`. Compute shaders come from `Runtime/Shaders/VfxParticles.shader` (and compiled graph source). Devices without compute skip GPU work at runtime.
