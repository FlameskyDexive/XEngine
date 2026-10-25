---
name: inspect-shader-graph
description: Read a Shader Graph (.shadergraph) offline via shadergraph_inspect. Echo text, journal-safe.
---

# inspect-shader-graph

Use when an AI needs to inspect a Shader Graph before compiling or patching it. Files are Echo text under `Assets/`. Do **not** read `Library/cache/`.

## Inspect
```bash
xengine-cli shadergraph-inspect --project <root> --graph Assets/Shaders/UnlitAlbedo.shadergraph
```

Returns `{ $type, fields[], contentHash }`. Pass `contentHash` to `shadergraph_patch`. `--asset` is accepted as an alias for `--graph`. The path must end in `.shadergraph`.

### Shader Graph paths
- `ShaderName` — Shader DSL name (e.g. `Graph/UnlitAlbedo`)
- `Target` — `0` Unlit / `1` Lit (LightBVH forward lighting) / `2` Decal / `3` Fullscreen
- `Properties/$values/<i>/Id|Name|DisplayName|Type|DefaultValue` — material properties (`Type`: 0 Float, 4 Color, 5 Texture2D)
- `Nodes/$values/<i>/Id|Type|PosX|PosY` — node catalog types (Color, SampleTexture2D, MasterUnlit, MasterLit, …)
- `Nodes/$values/<i>/Fields/$values/<j>/Key|Value` — per-node constants (Texture/Property ids, Color R/G/B/A)
- `Edges/$values/<i>/FromNode|FromPort|ToNode|ToPort` — wires into master stack ports (`Albedo`, `Position`, `Metallic`, `Smoothness`, …)

Lit graphs must have a `MasterLit` node; Unlit graphs a `MasterUnlit` node; Decal graphs a `MasterDecal` node; Fullscreen graphs a `MasterFullscreen` node. Compile with [edit-shader-graph](../edit-shader-graph/SKILL.md).

Or MCP: `shadergraph_inspect`.
