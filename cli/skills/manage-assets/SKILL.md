---
name: manage-assets
description: Full asset lifecycle for an XEngine project — import (copy in + mint .meta), move, delete. Every op is journaled with provenance and idempotent.
---

# manage-assets

Use when an AI needs to bring a file into the project, reorganize, or remove assets. The Host mints
`.meta` sidecars (fresh GUID + importer inferred from extension) so the asset is referenceable
immediately.

## Import a file into Assets/
```bash
xengine-cli asset-import --project <root> \
  --source <absolute-path-to-source-file> \
  --destination Assets/Textures/Grass.png \
  --idempotency-key <key> --intent "import grass texture"
```
- The importer is inferred from the extension: `.png/.jpg/...` → TextureImporter, `.wav/.mp3` →
  AudioImporter, `.fbx/.gltf` → EditorModelImporter, `.mat` → MaterialImporter, etc.
- Returns `{ guid, importerType, importerVersion }` — the GUID is what scene/material references use.
- `--overwrite`: replace an existing file but **preserve its GUID** (references survive).
- Models (`.fbx` / `.gltf` / `.glb` / `.obj`): pass `--animation-type` `0` Generic (default, `Animator`),
  `1` Humanoid, or `2` Legacy (`AnimationComponent`). The value is written into the `.meta` settings
  so the next editor import attaches the matching component.
- **Will game code load this asset dynamically (not via a scene/prefab reference) and does it live
  outside a `Resources/` folder?** Then also cover it with an AssetBundle collector in
  `ProjectSettings/AssetBundles.yaml` (a `CollectPath` that includes the asset's folder) — an
  uncollected asset is loadable in the editor but absent from an AssetBundle-packaged player.
  See the write-code skill for the full rule.

## Move (rename/relocate) an asset
```bash
xengine-cli asset-move --project <root> \
  --source Assets/Old.png --destination Assets/Textures/New.png \
  --idempotency-key <key> --intent "reorganize textures"
```
- Moves the file AND its `.meta` sidecar (GUID travels with it).

## Delete an asset
```bash
xengine-cli asset-delete --project <root> --asset Assets/Textures/Old.png \
  --idempotency-key <key> --intent "remove unused texture"
```
- Removes the file + `.meta`. **Breaks references** — check `journal_query` first to see if anything
  points at this asset's GUID.

## Idempotency
Every op takes `--idempotency-key`. A replay with the same key returns the original result WITHOUT
re-copying/moving/deleting — safe to retry on network blips. The file is never clobbered by a replay
even with `--overwrite`.

## Discover existing assets
`asset_search` lists assets (+ GUIDs from `.meta`); `asset_inspect` reads one asset's full `.meta`.

## ⚠️ Source assets vs binary caches
All `asset_*` tools operate on **source assets under `Assets/`** (Echo text format). The engine also
writes **binary import caches** to `Library/cache/{guid}.asset` — same `.asset` extension, but Echo
binary, NOT editable. Never point a tool at `Library/`. `asset_search` already excludes `Library/`,
so trust its results. If you see "Invalid value found while reading", you're on a cache file.
For reading/editing ScriptableObject DATA fields on a source `.asset`, use `asset_data_inspect` /
`asset_data_patch` (see the edit-scene-settings skill for the slash-path patch pattern).
