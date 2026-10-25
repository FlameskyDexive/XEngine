# Voxel World

Official optional XEngine package (`com.xengine.voxelworld`). It ships with the Editor but is **not** added to a project until you install it from Package Manager (`Window / Package Management / Package Manager`).

Installing copies this folder into `Library/PackageCache/com.xengine.voxelworld@<version>/` and records the pin in `Packages/manifest.json`.

- Code: MIT (`LICENSE.md`)
- Art: owned 16×16 voxel faces plus `white64.png` (`THIRD_PARTY_ASSETS.md` has no remaining third-party rows)
- Docs: `Documentation~/index.md`
- npm: `@inxep/voxelworld` (preview). Takedown notes: `Documentation~/npm.md`

Runtime types live in `XEngine.VoxelWorld`. Identity: `VoxelWorldRuntime.PackageName` / `Version`.

Playable: WASD + gravity jump, LMB breaks voxels or hits a dummy mob, RMB places from the hotbar, break emits debris chips. Player body is an owned cube-rig (not Dungeon Mason).
