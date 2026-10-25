# Terrain

3D heightmap terrain for XEngine. This package is **optional**: it ships with the Editor but is not mounted until you install it from Package Manager (`Window / Package Management / Package Manager`).

Install copies the folder into `Library/PackageCache/com.xengine.terrain@0.1.0/` and pins it in `Packages/manifest.json`.

## Runtime

`TerrainComponent` (`Terrain / Terrain`, namespace `XEngine.Terrain`) on a GameObject:

1. Samples a `TerrainData` heightmap in the vertex shader
2. GPU-instances a base grid through a distance quadtree
3. Blends splat layers (up to eight, each an `AssetRef<TerrainLayer>`) with optional holes
4. Draws grass details and tree instances by distance

`TerrainCollider` registers the same heightmap with the physics world. Gameplay helpers: `ActiveTerrains` / `ActiveTerrain`, `SampleHeight`, `GetInterpolatedNormal`, `AddTreeInstance`.

`WindZone` (`Terrain / Wind Zone`) is directional (local +Z) or spherical. It writes grass `_WindStrength` / `_WindSpeed` and a small tree lean. Trees between `TreeBillboardDistance` and `TreeDistance` draw as camera-facing billboards.

Neighbor tiles share a `GroupingId`. Matching XZ edges auto-fill `Left` / `Right` / `Top` / `Bottom`; `PushHeightSeams` copies painted edges.

Scenes saved before the extract keep loading: Echo `$type` values such as `XEngine.Runtime.Terrain.TerrainComponent` redirect to the package types.

Shaders (`Terrain.shader` / `Grass.shader`) and default materials ship in `Runtime/Shaders` and load through `TerrainShaderLibrary`. The original `$Default:Shader/Terrain` GUIDs are re-registered when the package is installed.

## Authoring

Inspector tabs: Sculpt, Paint, Holes, Details, Trees, Settings. Scene View brushes consume `TerrainComponent.Raycast`. Create from **GameObject / 3D Object / Terrain**, **Assets / Create / Terrain Data**, or **Assets / Create / Terrain / Terrain Layer** (`.terrainlayer`, shareable across `TerrainData`). Pre-package embedded layer blobs still load.

Sculpt brushes take a luminance **mask**, **rotation**, **spacing**, **scatter**, and a **strength curve**. Height tools: Raise, Lower, Flatten, Smooth, **Set Height**, **Stamp**. Settings import/export little-endian 16-bit **RAW** heightmaps. Trees **Mass Place** / **Clear Trees**; Details **Fill Layer** / **Clear Layer**. **GameObject / 3D Object / Wind Zone** adds a directional or spherical wind source. Tiles with the same **Grouping ID** auto-connect; **Sync Seams** copies edge heights; brushes continue onto overlapping neighbors.

Offline AI Host `scene_component_add --type Terrain` (also `TerrainComponent` / `TerrainCollider` / `WindZone`) writes `$type` as `XEngine.Terrain.*` in assembly `XEngine.Terrain.Runtime`.

## Samples

Package Manager → Terrain → Samples → **Starter Terrain**. Import copies `Samples~/Starter` into `Assets/Samples/`:

- `Starter.scene` — camera, directional light, terrain + collider, Wind Zone
- `Starter.terraindata` — 33×33 sculpted hill, splat weights, grass density, three trees
- `Grass.terrainlayer` — shared splat layer

`Samples~` is skipped by package asset scan until you import.

## Performance

CPU helper cost (Release probe) lives in `docs/superpowers/evidence/terrain/m5-wrapup.md`. GPU Terrain/Grass shader extract parity is in `m2-shader-move.md`. Keep heightmap resolution modest on mobile; quadtree LOD and grass/tree distances are the main runtime knobs.
