# AI Navigation

Official optional XEngine package. It ships with the Editor but is **not** added to a project until you install it from Package Manager (`Window / Package Management / Package Manager`).

Installing copies this folder into the project's `Library/PackageCache/com.xengine.ai.navigation@<version>/` and records the pin in `Packages/manifest.json`.

Full docs: `Documentation~/index.md`.

## Runtime

Recast bake (`NavMeshBuilder`), Detour query (`NavMesh.CalculatePath`), `NavMeshAgent` / `NavMeshObstacle` / `OffMeshLink`, crowd, and `.navmesh` assets live in this package. Namespaces stay `XEngine.Runtime.Navigation` for the query/agent types and `XEngine.AI.Navigation` for Surface / Modifier / Link. Old Echo `$type` strings that pointed at `XEngine.Runtime` still resolve.

Core Runtime keeps only a Recast-free geometry collector registry so Terrain (and other packages) can contribute triangles without a compile-time Recast dependency.

## Authoring

`NavMeshSurface`, modifiers, links, and **Window / AI / Navigation** ship in the package Editor assembly. Create agents from **GameObject / AI / NavMesh Agent** after the package is installed.

## AI CLI

Offline Host `nav_*` tools stay Echo + Detour (no engine). Live `runtime_nav_*` tools register when this package's Editor assembly is loaded in a running editor.
