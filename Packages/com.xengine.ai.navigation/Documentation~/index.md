# AI Navigation

Official optional Recast/Detour navmesh package (`com.xengine.ai.navigation`).

The package ships with the Editor and is listed in Package Manager. It is **not** mounted until you click Install, which copies it into `Library/PackageCache/com.xengine.ai.navigation@<version>/` and pins `Packages/manifest.json`.

## Runtime

Query APIs, bake pipeline, and agents live in this package:

- `NavMesh.CalculatePath` / `SamplePosition` / `Raycast` / `FindClosestEdge`
- `NavMeshBuilder` / `NavMeshData` (`.navmesh` Echo + Detour payload)
- `NavMeshAgent` / `NavMeshObstacle` / `OffMeshLink` / crowd

Authorization components:

- `NavMeshSurface` — collect scene geometry, bake, visualize, export
- `NavMeshModifier` / `NavMeshModifierVolume` — ignore or override areas
- `NavMeshLink` — off-mesh connections (jump / teleport)

Core Runtime keeps `INavMeshGeometryCollector` / `INavMeshTriangleSink` so Terrain can rasterize heightmaps without referencing DotRecast.

## Editor

- GameObject / AI menus for Surface, Modifier, Link, Agent, Obstacle
- Window → AI → Navigation bake panel

## Samples

See `Samples~/README.md` after installing the package from Package Manager.
