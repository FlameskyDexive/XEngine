---
name: edit-shader-graph
description: Patch a Shader Graph (.shadergraph) via shadergraph_patch, or compile it to dual GLSL+Slang via shadergraph_compile. Journaled Echo text.
---

# edit-shader-graph

Use when an AI needs to change graph nodes/edges/properties or emit a Shader DSL file. Graphs are `.shadergraph` Echo assets. Compile does **not** replace LightBVH; Lit target still emits `CalculateForwardLighting` / `StandardCalculateLighting`.

## Patch
```bash
xengine-cli shadergraph-inspect --project <root> --graph Assets/Shaders/UnlitAlbedo.shadergraph
# note contentHash

xengine-cli shadergraph-patch --project <root> --graph Assets/Shaders/UnlitAlbedo.shadergraph \
  --patches '[{"path":"ShaderName","value":"Graph/HeroUnlit"}]' \
  --expected-before-hash <hash> \
  --idempotency-key <key> --intent "rename compiled shader"
```

Paths match [inspect-shader-graph](../inspect-shader-graph/SKILL.md). Same drift protection / idempotency as `asset_data_patch`. `--asset` aliases `--graph`.

After patching, the running editor must reload the asset (or `runtime_refresh`) before Shader Graph window / Play mode pick up values.

## Compile
```bash
xengine-cli shadergraph-compile --project <root> --graph Assets/Shaders/UnlitAlbedo.shadergraph
# returns { shaderName, target, source, written }

xengine-cli shadergraph-compile --project <root> --graph Assets/Shaders/UnlitAlbedo.shadergraph \
  --out Assets/Shaders/UnlitAlbedo.shader
```

`source` always contains both `GLSLPROGRAM` and `SLANGPROGRAM`, plus `#include "ShaderLibrary/Core"` then Lighting (GLSL) / StandardLighting (Slang) for Lit. Decal (`Target` 2 / `MasterDecal`) and Fullscreen (`Target` 3 / `MasterFullscreen`) emit unlit tagged passes with `"LightMode" = "Decal"` or `"Fullscreen"` (comma-separated Tags). Missing master or a cycle throws.

`--out` writes a `.shader` text file (derived output; not journaled). Prefer compiling after a successful patch.

Or MCP: `shadergraph_patch` / `shadergraph_compile`.
