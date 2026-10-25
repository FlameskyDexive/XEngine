---
name: inspect-scene
description: Read an XEngine .scene's hierarchy, GameObjects, transforms and components offline (no engine running) via xengine-cli.
---

# inspect-scene

Use when you need to understand the contents of an XEngine scene file: its GameObjects,
their hierarchy, transforms (position/rotation/scale), and component fields — **without
loading the engine**.

## When to use
- "What's in `Scenes/Main.scene`?"
- "Where is the Main Camera positioned?"
- "Which GameObjects have a Light component?"
- Before editing a scene, to learn object Identifiers and field paths.

## How (xengine-cli)

```bash
# List all scenes in the project
xengine-cli scene-list --project <project-root>

# Read a scene's full hierarchy (root objects, transforms, components)
xengine-cli scene-hierarchy --project <project-root> --scene <relative-path-or-absolute>
```

Or via MCP: call tools `scene_list` / `scene_hierarchy`.

## Output shape
`scene_hierarchy` returns `rootObjects[]`, each with `identifier`, `name`, `transform`
(x/y/z, rotation, scale), `childIdentifiers`, and `components[]` (each `typeName` + its
serialized `fields` as strings). Nested fields show as `"...""`.

## Notes
- Works offline — the Host reads the raw Echo tag tree, no engine process needed.
- Object `identifier` (a GUID) is what `scene-object-patch`/`scene-object-delete` target.
- Field paths for patching are slash-separated tag names (e.g. `Transform/_localPosition/Y`).
