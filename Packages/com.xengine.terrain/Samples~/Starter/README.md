# Starter Terrain

Sculpted 64 m hill (`Starter.terraindata`, 33×33 heightmap) with one shared `Grass.terrainlayer`, a filled grass detail layer, three tree instances, a directional Wind Zone, and a camera.

Import from Package Manager → Terrain → Samples after the package is installed. The copy lands under `Assets/Samples/`. Open `Starter.scene`.

| Asset | Role |
|-------|------|
| `Starter.scene` | Camera, directional light, `TerrainComponent` + `TerrainCollider`, Wind Zone |
| `Starter.terraindata` | Heights, splat weights, grass density, tree instances |
| `Grass.terrainlayer` | Shared splat layer (tiling 8, roughness 0.85) |

Sculpt further with the Terrain inspector (Raise / Set Height / Stamp, RAW import). Neighbor tiles and extra layers are authored the same way as a new terrain from **GameObject / 3D Object / Terrain**.
