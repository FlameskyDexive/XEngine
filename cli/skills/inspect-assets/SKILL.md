---
name: inspect-assets
description: Discover XEngine project assets and read their stable GUID/importer from .meta sidecars. Use to find assets and the GUIDs scenes reference.
---

# inspect-assets

Use when you need to enumerate a project's assets or learn an asset's stable GUID / importer.

## List assets
```bash
xengine-cli asset-search --project <root> [--query <name>] [--extension <.png>] [--limit 200]
```
Returns each asset's relative path, GUID, importer, version, size, and whether a `.meta`
exists. Filters are optional.

## Inspect one asset
```bash
xengine-cli asset-inspect --project <root> --asset <relative-path>
```
Returns GUID, importer, importer version, and the importer `settings` keys/values.

Or MCP: `asset_search` / `asset_inspect`.

## Notes
- Reads the `.meta` sidecar (the source of truth) — engine-agnostic, no `metadata.db` needed.
- The GUID is what scene files and AssetRefs reference; useful for resolving "which asset is
  this GUID".

## ⚠️ CRITICAL: Never read `Library/cache/*.asset` — it's binary
XEngine source assets under `Assets/` are **Echo text** (human-readable, git-diffable, AI-editable).
But the engine also writes **import cache** files to `Library/cache/{guid}.asset` — these share the
`.asset` extension but are **Echo BINARY** (a compact serialized form for fast loading). They are NOT
source files.

- **DO** read/edit: `Assets/**/*.asset`, `Assets/**/*.scene`, `Assets/**/*.prefab`, `Assets/**/*.mat` — all text.
- **DO NOT** read/edit: `Library/cache/*.asset`, `Library/metadata.db` (engine-managed; binary or internal format).
- If `asset_data_inspect` or `find_in_file` reports "Invalid value found while reading" on an `.asset`
  file, you are almost certainly pointing at a `Library/cache/` binary cache, not a source asset. Re-path
  to the `Assets/` source.
- `asset_search` already excludes `Library/` — trust its results for source-asset discovery.
