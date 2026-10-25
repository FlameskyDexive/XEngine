# Changelog

## [0.1.18] - 2026-09-21

- Add the first dual-style character presentation profile and keep it separate from gameplay bounds.

## [0.1.17] - 2026-09-21

- Add presentation material switching without rebuilding voxel meshes, for game-owned dual-style worlds.

## [0.1.16] - 2026-09-20

- Fix outward triangle winding on X/Y voxel faces for backface culling and mesh collisions.
- Cache empty mesh results; invalidate neighboring faces on boundary edits, unload and saved-section load.
- Queue collider creation when the focus moves instead of cooking the entire ring at once.
- Add continuous voxel character sweeps with unloaded-section barriers and explicit vertical-speed reset.
- Consume placement inventory only after a successful voxel write.
- Preserve rectangular greedy-face UV scale when correcting winding.
- Limit streaming mesh work and cook one pending collider per tick.
- Keep character assembly independent of HUD child creation; correct hotbar anchors and throttle text updates.
- Allow save loading without synchronously preloading the full view distance.
- Use the supported clip-space transform in the texture-array preview shader.

## [0.1.15] - 2026-09-19

- Fix: chunk colliders starved while streaming — views built mid-frame never received
  colliders unless the focus chunk later changed (the visible symptom: physics/gizmo
  colliders clustered around the terrain center). Collider cooking is now incremental:
  built in-range views enqueue and cook ≤2 per frame, every frame, regardless of focus
  movement; leaving the range or unloading dequeues.

## [0.1.14] - 2026-09-18

- Perf: `VoxelSfx.PlayClick` caches its synthesized click `AudioClip` — per-click clip
  instances churned the finalizer + native handle retirement for identical data.

## [0.1.13] - 2026-09-18

- Fix: `VoxelSfx.PlayClick` skips one-shots while the voice is still playing — stop/
  restart of a voice the device callback is reading races miniaudio's mixer and
  fast-fails the process (0xC0000409) once clicks fire over long clips.

## [0.1.12] - 2026-09-18

- Fix: audio-thread access violation when clip finalizers freed shared PCM buffers while
  the native mixer/voice still read them (0xC0000005 ~60-90s into play once frequent
  one-shots mixed with long clips). `AudioContext.Remove` now retires handles to a
  main-thread drain queue with a 5s grace period instead of freeing on the finalizer
  thread; `Deinitialize` frees all retired buffers. `VoxelSfx` caches the click clip.

## [0.1.11] - 2026-09-17

- Fix: greedy mesher seam culling — the mesher treated out-of-chunk neighbors as air, so
  every chunk border emitted duplicate back-to-back quads (z-fighting, "single-sided"/
  disappearing faces at chunk seams, visible from above as holes). `Build` now takes an
  optional `borderSampler` (chunk-local coords → neighbor voxel); `VoxelChunkStreamer`
  supplies it from the grid, reading unloaded neighbors as air so the world edge keeps
  its wall of faces.

## [0.1.10] - 2026-09-17

- Fix: cloud deck blanketed the world from any elevated view (aerial/minimap read as
  "missing terrain") — coverage thinned (noise gates 0.38/0.12 → 0.55/0.30).

## [0.1.9] - 2026-09-17

- Perf: physics mesh cooking dominated chunk streaming (~10-30ms/chunk; 16/frame cost
  seconds per frame ≈ 0.5 FPS) and cooked meshes taxed runtime physics — colliders now
  cook only for chunks within `VoxelChunkStreamer.ColliderChunkRadius` (default 2) of the
  focus chunk; `Tick` gained an ~8ms frame budget; built chunks mark siblings dirty so
  cross-chunk border faces rebuild (seam fix).

## [0.1.8] - 2026-09-17

- Add: obelisk block (atlas layer 23) — `VoxelFaceTextures.Obelisk()` violet pillar with a
  glowing rune band for the N3.3 home-base respawn/teleport network.

## [0.1.7] - 2026-09-17

- Add: `VoxelStructures` — deterministic POI grid (192-block cells, one structure each):
  watch towers (5×5×16 hollow stone + top platform), shrines (7×7 broken walls + altar)
  and village ruins (broken walls + scattered roof tiles). `EarthTerrainGenerator.AddStructures`
  (default true) stamps them after terrain/caves; `NearbyPoi` powers gameplay proximity.

## [0.1.6] - 2026-09-17

- Add: station blocks — `VoxelFaceTextures.Forge/Bed/Campfire` pixel faces and demo-atlas
  layers 14/15/16 (forge: stone body + ember mouth; bed: red blanket + pillow; campfire:
  log ring + flame core) for the N2.3 forge/crafting-station and functional-block loop.

## [0.1.5] - 2026-09-17

- Add: Earth caves — `VoxelValueNoise.Smooth3`/`Hash3` 3D value noise plus an
  `EarthTerrainGenerator` cave carver (density threshold 0.5, XZ scale 0.075, Y scale 0.11,
  floor Y 4, 3-block surface cap). Carving runs after water fill so ocean cells stay water;
  deterministic per seed. 3 new `VoxelEarthTests` (carve coverage, per-seed determinism,
  noise bounds).

## [0.1.4] - 2026-09-17

- Fix: fully qualify `System.HashCode` in the rig tint cache — standalone (generated-csproj)
  package compiles have no implicit `using System`, so 0.1.3 failed to compile outside the
  engine repo test harness. 0.1.3 superseded immediately.

## [0.1.3] - 2026-09-17

- Fix: 0.1.2 shipped a compile error (`<<` on float color components in the tint key) —
  superseded immediately; use 0.1.3. Tint key now `HashCode.Combine`.

## [0.1.2] - 2026-09-17

- Fix: apply the owned per-part `VoxelBodyPart.Color` tints — rig cubes now share cached
  `Default/Standard` materials tinted via `_MainColor` (player skin/shirt/pants, mob stone/eye
  colors render as authored instead of untextured white).

## [0.1.1] - 2026-09-17

- Fix: `VoxelCharacterView.Attach` assigned no `Material` to rig cubes, and `MeshRenderer`
  skips draws with an empty material list — cube rigs (player body, dummy mobs) never
  rendered. Attach now falls back to a shared `DefaultShader.Standard` material.

## [0.1.0] - 2026-09-16

- Terrain store: 16³ `VoxelPalette` (MC padded longs) + Anvil-shell `region/*.vwr` (zlib); unedited columns omitted; legacy Echo edit lists migrate once.
- Catalog: owned faces for white/grass/dirt/stone/leaves/sand/snow/water/wood/cloud; Earth stamps water + clouds.
- Playable: gravity jump, owned cube-rig player, dummy mob combat (LMB prefers mob AABB), break debris chips.
- Split: `VoxelWorldGrid` holds voxels; `VoxelChunkStreamer` only remeshes/spawns views.
- Atlas: owned 16×16 grass/dirt/stone/leaf/sand/snow/water/wood/cloud faces (no more solid 8×8 colors). One Texture2DArray, one material, one draw per chunk.
- Demo: Unity-style Earth/Flat perspective screenshots (greedy mesh + biome terrain).
- N-art: replaced `white64.png` with an owned 64×64 white (GUID kept). THIRD_PARTY table empty.
- N-npm: dry-run packed `@inxep/voxelworld` 0.1.0 (36506 B). Live npmjs PUT waits for a token.
- N12: origin shift, see-through ceiling hide, Colorizer paint; remaining.md lists deferred items.
- N-npm: dry-run packs `@inxep/voxelworld` 0.1.0; live publish waits for npm token.
- N11: Paper inspectors for VoxelWorld / VoxelDefinition / WorldDefinition, GameObject create menus.
- N10: hotbar inventory, Canvas HUD, owned click WAV, Echo cell-edit save (not format 14).
- N9: water fill, forest plants/trees, VoxelDayCycle sun arc.
- N8: Earth terrain generator, forest/desert/snow biomes, EarthWorld scene.
- N7: sunlight/torch flood, vertex AO, MeshCollider on streamed chunks.
- N6: voxel DDA pick, first-person WASD/look, break/place, CharacterController.
- N5: view-distance chunk streaming, FlatWorld scene, XTask build budget.
- N4: TextureArrayPacker, Voxel/Opaque + Cutout + CutoutCross shaders, Flat grass/dirt/stone/leaves faces.
- N3: greedy mesher, chunk pool, UV2 slice, solid 16³ preview host.
- N2: voxel/world/biome/item/model definitions, Flat terrain generator, Demo3 Flat import (`white64` temporary).
- N1: preview shader `VoxelWorld/ArrayPreview` samples `Texture2DArray` (engine resource + material bind).
- Official optional package skeleton: listed in Package Manager, not mounted until Install.
- Runtime / Editor asmdefs, `VoxelWorldRuntime` / `VoxelWorldPackageEditor` identity markers.
- Documentation~, Samples~/Empty placeholder, LICENSE (code MIT), THIRD_PARTY_ASSETS (empty until N2).
- `VoxelWorldTestProject` embeds this package via `file:` for Play-mode verification.
