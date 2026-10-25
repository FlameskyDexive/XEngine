# Remaining / deferred Voxel World features

N-art replaced the last third-party texture (`white64.png`) with an owned 64×64 white. GUID unchanged.
Demo atlas now packs owned 16×16 grass/dirt/stone/leaf/sand/snow/water/wood/cloud faces (not solid 8×8 colors).
Playable tests cover break debris, gravity jump, WASD motor, owned cube-rig player, dummy-mob combat, and a full type catalog (1–10) with water/cloud terrain shots.
Logic (`VoxelWorldGrid`) is separate from presentation (`VoxelChunkStreamer` views).
Terrain storage is Anvil-aligned `VoxelPalette` + `region/*.vwr` (see [save.md](save.md)). Networking is still deferred.

Shipped in N12:

- Origin shift (`VoxelOriginShift`) — recenter focus onto the chunk grid; region keys via `VoxelRegionStore.ShiftWorld`.
- See-through (`VoxelSeeThrough`) — hide a ceiling column of one type around the camera cell.
- Colorizer (`VoxelColorizer`) — paint a streamed cell to a type.

Explicitly **not** in this package:

- Dungeon Mason / third-party character packs
- Unity TerrainData generators, XR, networking
- Kronnect binary save format 14 / Mojang NBT `.mca` interchange
- Connected textures, NavMesh, MagicaCloth import, HQForest extra scene (later nodes if needed)
