---
name: navigate
description: Inspect, query, export, and patch XEngine NavMesh assets offline, or bake/steer agents in a live editor.
---

# navigate

Use when you need to inspect a baked `.navmesh`, find a path without starting the engine, export geometry, patch bake settings, or drive live `NavMeshAgent`s.

`.navmesh` files under `Assets/` are **Echo text** (manifest + `TileData` ByteArray). Do **not** read `Library/cache/`.

## Offline (xengine-cli / Host MCP)

```bash
xengine-cli nav-inspect --project <root> --navmesh Assets/Scenes/Demo_navmesh/Demo.navmesh
xengine-cli nav-query-path --project <root> --navmesh <path> --from 2,0,2 --to 18,0,18
xengine-cli nav-export --project <root> --navmesh <path> --format obj --output Exports/nav.obj
xengine-cli nav-settings-patch --project <root> --navmesh <path> \
  --patches "[{\"path\":\"Settings/AgentRadius\",\"value\":\"0.6\"}]" \
  --idempotency-key nav-1 --intent "widen agent" --dry-run
```

`--asset` is accepted as an alias for `--navmesh`. Paths must end in `.navmesh`.

| Tool | What it returns |
|---|---|
| `nav_inspect` | Name, settings, tile summaries, bounds, payload sizes, `contentHash` |
| `nav_query_path` | Detour straight-path corners (`Complete` / `Partial` / `Failed`) |
| `nav_export` | `obj` (triangles), `json` (verts/indices), `bin` (Detour mesh-set) |
| `nav_settings_patch` | Journaled Echo patch. Pass `contentHash` as `expectedBeforeHash`. Supports `--dry-run`. |

`nav_query_path` loads `TileData` with DotRecast Detour only — the Host does **not** reference `XEngine.Runtime`.

Typical settings paths: `Settings/AgentRadius`, `Settings/AgentHeight`, `Settings/AgentMaxSlope`, `Settings/CellSize`, `Settings/TileSize`, `Settings/EnableCarving`.

## Live (editor `--serve`)

Requires the in-process MCP server **and** the optional `com.xengine.ai.navigation` package installed in the project. These hop onto the editor main thread.

- `runtime_nav_state` — loaded mesh count, agents / obstacles / off-mesh links, crowd size
- `runtime_nav_bake` — Recast-bake the current scene and load it for queries (does not write a file)
- `runtime_nav_agent_move` — `SetDestination` on a `NavMeshAgent` (`identifier` Guid, or the selection)
- `runtime_nav_agent_state` — pose, destination, remaining distance, velocity, path flags

## Workflow
1. `nav_inspect` the `.navmesh` and keep `contentHash`.
2. `nav_query_path` to verify start/end snap onto the mesh.
3. Dry-run `nav_settings_patch`, then apply with the hash + a stable `idempotencyKey`.
4. In a running editor, `runtime_nav_bake` then `runtime_nav_agent_move` / `runtime_nav_agent_state`.
