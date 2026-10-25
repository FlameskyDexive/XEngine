# Voxel World save format

Self-owned terrain store aligned with **Minecraft Java Anvil semantics**. **Not** Kronnect format 14 and **not** loadable as Mojang `.mca` (payload is `VW01`, not NBT).

## Layout (F5 / F9)

Default path: `Application.PersistentDataPath` / `voxelworld/` (directory name from `VoxelWorld.SaveFileName`).

```
voxelworld/
  world.echo          # VoxelWorldMeta (version, seed, min/max chunk Y, generator kind)
  region/
    r.{rx}.{rz}.vwr   # Anvil shell: 8 KiB BE header, 4 KiB sectors, zlib column payloads
```

- Region coords: `rx = floor(chunkX/32)`, `rz = floor(chunkZ/32)` (same as Minecraft).
- Column slot: `i = (chunkX&31) + (chunkZ&31)*32`.
- Only **edited** columns are written. Unedited terrain is regenerated from the seed/generator on load.
- Compression type byte `2` = zlib (same as Notchian default).

## In-memory

Each 16³ section uses `VoxelPalette` (bits 0 / 4–8 / 16, padded longs like Java 1.16+). Meshing and lighting expand to a temporary `Voxel[]` scratch; the palette remains the source of truth.

## Legacy Echo migration

Old single-file `VoxelWorldSave` Echo documents (`Version` 1 edit lists) still load via `ReadSave` on a `.echo` path: edits apply to the live grid, then flush into a sibling world directory + `region/*.vwr`. Prefer the directory format going forward.
