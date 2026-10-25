---
name: edit-prefab
description: Read and patch a .prefab file's GameObject tree offline (by Identifier, recursive through children). Structured edits avoid corrupting serialized data.
---

# edit-prefab

Use when you need to modify a `.prefab` file (the prefab template, not scene instances). Prefabs are
single-root GameObject trees on disk — this tool patches fields by Identifier, with recursive search
through `Children` (unlike scenes which use a flat array). Every change is journaled with provenance.

## Read a prefab's hierarchy
```bash
xengine-cli prefab-hierarchy --project <root> --prefab <path>
```
Or MCP: `prefab_hierarchy`. Returns the root GO + its children, transforms, components.

## Patch a prefab's GameObject
```bash
xengine-cli prefab-object-patch --project <root> --prefab <path> \
  --object <GameObject-identifier> \
  --patches '[{"path":"Transform/_localPosition/Y","value":"5"}]' \
  --idempotency-key <key> --intent "<why>"
```
Or MCP: `prefab_object_patch` (same params). `--object` searches recursively through Children, so
nested GOs are reachable. `--dry-run` previews without writing.

## Notes
- A `.prefab` file is a **single root GO** (not an array like `.scene`). The patch target may be the
  root or any descendant — both are found by Identifier.
- Patching the prefab template changes **all instances** that haven't overridden that field.
- To patch a specific **instance's override** (not the template), use the editor's override system
  (PropertyOverride.Reason). Instance overrides live on the scene's GameObject, not the .prefab file.
- Same delta + provenance + journal path as `scene_object_patch`.
