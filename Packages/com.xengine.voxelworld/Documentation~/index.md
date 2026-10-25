# Voxel World

Optional voxel world runtime for XEngine. Ships with the Editor; install from **Window / Package Management / Package Manager**. Install copies the folder to `Library/PackageCache/com.xengine.voxelworld@0.1.0/` and pins `Packages/manifest.json`.

## Status

N0–N12, the npm dry-run, and N-art are in. Playable layer: break/place with debris chips, gravity jump, owned cube-rig player, dummy mob (LMB prefers the mob). Demo shots: `docs/superpowers/evidence/voxelworld/demo-earth-overview.png`, `demo-earth-water-clouds.png`, `demo-full-catalog.png`, `demo-build-break.png`, `demo-voxel-character.png`. See [save.md](save.md) and [remaining.md](remaining.md).

## Runtime

`VoxelWorld` packs a `Texture2DArray` at startup (`TextureArrayPacker`) and binds it on materials from `VoxelShaderLibrary`. Slice index lives in `Mesh.UV2.x` (top / side / bottom can differ). `VoxelWorldGrid` is the logic map (paletted sections); `VoxelChunkStreamer` remeshes and spawns chunk views. Saves use Anvil-aligned `region/*.vwr` — see [save.md](save.md).

## Samples

Package Manager → Voxel World → Samples → **Empty World**. Playable Flat/Earth scenes land after N5/N8.

## npm

Preview publish and takedown steps: [npm.md](npm.md).
