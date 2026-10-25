# Terrain

Official optional XEngine package. It ships with the Editor but is **not** added to a project until you install it from Package Manager (`Window / Package Management / Package Manager`).

Installing copies this folder into the project's `Library/PackageCache/com.xengine.terrain@<version>/` and records the pin in `Packages/manifest.json`.

Full docs: `Documentation~/index.md`. Samples: Package Manager → Samples → **Starter Terrain** (sculpted hill scene).

## Runtime

`TerrainComponent` (`Terrain / Terrain`) renders a GPU-instanced heightmap with quadtree LOD, splat layers, holes, grass details, and trees. `TerrainCollider` samples the same `TerrainData` for physics. `TerrainData` (`.terraindata`) stores the heightmap, splatmap, holes, detail density, and tree instances. Splat layers are `AssetRef<TerrainLayer>` slots; shareable layers live as `.terrainlayer` assets. Gameplay: `ActiveTerrains`, `SampleHeight`, `GetInterpolatedNormal`, `AddTreeInstance`. `WindZone` drives grass/tree sway. Neighbor tiles share a grouping ID and can sync seams.

Runtime types live in this package (`XEngine.Terrain`). Old Echo `$type` names from `XEngine.Runtime` still resolve. Terrain/Grass shaders load from `Runtime/Shaders` via `TerrainShaderLibrary`.

## Authoring

Inspector tabs (Sculpt / Paint / Holes / Details / Trees / Settings) and Scene View brushes ship in the package Editor assembly. Create a terrain from **GameObject / 3D Object / Terrain** after the package is installed. Create a shared splat layer from **Assets / Create / Terrain / Terrain Layer**.

Sculpt brushes support a mask texture, rotation, spacing, scatter, and a strength curve. Height tools include Set Height and Stamp. Settings can import/export 16-bit RAW heightmaps. Trees/Details tabs include mass place and fill. Create a wind source from **GameObject / 3D Object / Wind Zone**. Trees beyond `TreeBillboardDistance` draw as camera-facing billboards. Tiles with the same **Grouping ID** auto-connect; **Sync Seams** copies edge heights; brushes continue onto overlapping neighbors.

## AI CLI

Terrain scene/asset inspect and patch stay on the existing scene/asset tools. Offline host short-names `Terrain` / `TerrainComponent` / `TerrainCollider` / `WindZone` stamp `$type` as `XEngine.Terrain.*` in `XEngine.Terrain.Runtime`.
