---
name: edit-vfx-graph
description: Create, patch, or compile a Visual Effect Graph (.vfx). Journaled Echo text; live preview via runtime_vfx_*.
---

# edit-vfx-graph

Use when an AI needs a new `.vfx`, to change contexts/blocks/operators/edges, or to emit compute + output Shader DSL. Graphs are Echo assets. Live playback needs the editor `--serve` with `com.xengine.vfx` installed.

## Create
```bash
xengine-cli vfxgraph-create --project <root> --graph Assets/VFX/Hero.vfx --template Loop
```

`--template` is `Empty`, `Loop`, `Burst`, `Trail`, `Mesh`, `Subgraph`, or `Decal` (copies `Packages/com.xengine.vfx/Templates/`). Supports `--dry-run`. Not journaled; patch afterwards for edits. `Subgraph` is a Loop host with a `Subgraph` operator `Path=Trail`. `Decal` uses `OutputParticleDecal` and projects onto `CollisionPlane`.

## Patch
```bash
xengine-cli vfxgraph-inspect --project <root> --graph Assets/VFX/Hero.vfx
# note contentHash

xengine-cli vfxgraph-patch --project <root> --graph Assets/VFX/Hero.vfx \
  --patches '[{"path":"GraphName","value":"VFX/Hero"}]' \
  --expected-before-hash <hash> \
  --idempotency-key <key> --intent "rename compiled effect"
```

Paths match [inspect-vfx-graph](../inspect-vfx-graph/SKILL.md). Same drift protection / idempotency as `asset_data_patch`. `--asset` aliases `--graph`.

After patching, `runtime_refresh` if the editor is live.

## Compile
```bash
xengine-cli vfxgraph-compile --project <root> --graph Assets/VFX/Hero.vfx
# returns { graphName, template, capacity, spawnRate, burstCount, source, written }

xengine-cli vfxgraph-compile --project <root> --graph Assets/VFX/Hero.vfx \
  --out Assets/VFX/Hero.shader
```

`source` contains `SLANGPROGRAM` compute passes (`Init` / `Update` / `WriteArgs`) plus `Output`, and the `// VFXGRAPH_INIT_BLOCKS` / `// VFXGRAPH_UPDATE_BLOCKS` markers. Missing Spawn/Initialize/Update/OutputParticleQuad throws.

`--out` writes a `.shader` text file (derived output; not journaled). Prefer compiling after a successful patch.

## Live (`--serve`)

Requires `com.xengine.vfx` Editor loaded (package assembly scan registers the tools).

- `runtime_vfx_state` — VisualEffect count, playing, capacity, spawn/burst
- `runtime_vfx_preview` — Play the effect and write CPU preview PNGs (optional GameView capture). Pass `identifier` (GameObject Guid) or select one. `frameCount` 1–8.
- `runtime_panel_screenshot` of `XEngine.VFX.Editor.VfxGraphWindow` for the graph canvas

Or MCP: `vfxgraph_create` / `vfxgraph_patch` / `vfxgraph_compile` / `runtime_vfx_state` / `runtime_vfx_preview`.
