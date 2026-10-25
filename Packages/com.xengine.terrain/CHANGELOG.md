# Changelog

## [0.1.0] - 2026-08-15

- Official optional package skeleton: listed in Package Manager, copied to `Library/PackageCache` on Install.
- Runtime / Editor asmdefs, Documentation~, Samples~ placeholder, CHANGELOG, LICENSE.
- Runtime extract: `TerrainComponent`, `TerrainData`, `TerrainCollider`, quadtree LOD, grass/trees, NavMesh collector. Namespace `XEngine.Terrain` with Echo `$type` redirects from `XEngine.Runtime.*`.
- Editor extract: inspector, Scene View brushes, `.terraindata` importer, GameObject / 3D Object / Terrain, shader-variant scene collector.
- Shader extract: `Terrain.shader` / `Grass.shader` / default materials live in `Runtime/Shaders`. Core `DefaultShader` / `DefaultMaterial` no longer list Terrain or Grass.
- AI Host `ComponentRegistry` stamps `Terrain` / `TerrainComponent` / `TerrainCollider` as `XEngine.Terrain.*` in `XEngine.Terrain.Runtime`.
- Independent `TerrainLayer` assets (`.terrainlayer`). `TerrainData.Layers` is `List<AssetRef<TerrainLayer>>`; old embedded layer compounds migrate to runtime resources.
- Brush upgrades: luminance mask, rotation, spacing, scatter, strength curve; Set Height / Stamp tools; 16-bit RAW import/export; mass place trees and fill detail layers.
- `WindZone` (directional / spherical) drives grass `_WindStrength` / `_WindSpeed` and a small tree lean. Trees between `TreeBillboardDistance` and `TreeDistance` draw as camera-facing billboards.
- Neighbor tiles: `GroupingId` auto-connects shared XZ edges; `PushHeightSeams` copies painted edges; Scene View brushes stamp overlapping grouped tiles.
- Gameplay API: `ActiveTerrains` / `ActiveTerrain`, `SampleHeight`, `GetInterpolatedNormal`, `AddTreeInstance`.
- Samples~/Starter: sculpted 33×33 hill `.terraindata` + `Grass.terrainlayer` + `.scene` (grass, trees, Wind Zone). Documentation~ covers install, authoring, samples, and CPU helper evidence.
