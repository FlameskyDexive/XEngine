# Changelog

## [0.1.0] - 2026-08-16

- Official optional package: `installByDefault: false`. Listed in Package Manager; Install copies into `Library/PackageCache`.
- Recast/Detour runtime extract: `NavMeshBuilder`, `NavMeshData`, `NavMesh` query, `NavMeshAgent` / `Obstacle` / `OffMeshLink`, crowd, tile cache. Namespace `XEngine.Runtime.Navigation` with Echo `$type` redirects from assembly `XEngine.Runtime`.
- Core Runtime keeps a Recast-free `INavMeshTriangleSink` / `INavMeshGeometryCollector` registry so Terrain can contribute geometry without DotRecast.
- Live MCP tools `runtime_nav_*` move to this package's Editor assembly (`NavRuntimeTools`).
- AI Host `ComponentRegistry` stamps NavMesh short-names into `XEngine.AI.Navigation.Runtime`.
- NavMeshSurface / Modifier / ModifierVolume / Link, Navigation bake window, scene-view visualization, OBJ/JSON/BIN export.
- GameObject menus for NavMesh Agent, Off Mesh Link, and NavMesh Obstacle.
