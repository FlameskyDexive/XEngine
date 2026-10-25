---
name: write-code
description: Create, edit, and delete C# scripts in an XEngine project. The editor auto-detects changes and hot-reloads via Roslyn. Use structured text edits for safe modifications.
---

# write-code

Use when an AI needs to **write or modify C# source code**. Scripts live under `Assets/` (convention:
`Assets/Scripts/`). The engine's AssetWatcher auto-detects file changes → ScriptImporter calls
`ScriptAssemblyManager.RequestRecompile()` → Roslyn compiles → hot-reload. No manual compile step.

## Create a new script
```bash
xengine-cli script-create --project <root> --path Assets/Scripts/Player.cs \
  --content 'using XEngine.Runtime;
public class Player : MonoBehaviour
{
    public float Speed = 5f;
    void Update() { /* ... */ }
}' \
  --idempotency-key <key> --intent "add player controller"
```
- Mints a `.meta` sidecar (fresh GUID, ScriptImporter v1).
- Refuses to overwrite an existing script (use `script_apply_edits` to modify, or delete + create).

## Edit an existing script (safe, structured)
```bash
xengine-cli script-apply-edits --project <root> --path Assets/Scripts/Player.cs \
  --edits '[{"oldText":"public float Speed = 5f;","newText":"public float Speed = 10f;"}]' \
  --idempotency-key <key> --intent "double player speed"
```
- `oldText` is matched EXACTLY (case-sensitive, whitespace-significant).
- `occurrence="first"` (default): `oldText` must be UNIQUE — throws `edit_ambiguous` if it appears
  multiple times (provide more surrounding context to disambiguate).
- `occurrence="all"`: replaces every match.
- Use `find_in_file` first to locate the exact current text if unsure.
- Optional `--expected-before-sha` for drift protection (SHA from a prior read).

## Delete a script
```bash
xengine-cli script-delete --project <root> --path Assets/Scripts/Old.cs \
  --idempotency-key <key> --intent "remove unused script"
```
- Removes the `.cs` + `.meta`.

## Find symbols vs find text
- `code_symbols` — find type/member DECLARATIONS via Roslyn (e.g. "where is class Camera defined?").
- `find_in_file` — find any TEXT via regex (usages, comments, string literals).

## Dynamically loading assets outside Resources/

When the script loads an asset at runtime and the asset does NOT live under a `Resources/` folder:

1. **Cover it with an AssetBundle collector** in `ProjectSettings/AssetBundles.yaml`
   (`AssetBundleCollectorSetting`: Packages → Groups → Collectors; `CollectPath` relative to
   `Assets/`; `PackRule` = PackTogether / PackSeparately / PackByDirectory; `AddressRule` =
   ByPath / ByFileName / ByGuid / NoAddress). Without collector coverage the asset loads fine
   in the editor but is **missing from an AssetBundle-packaged player** — the player backend
   resolves only assets present in the package manifests.
2. **Load via the runtime APIs**, never loose file paths: an `AssetRef<T>` field (serialized GUID
   reference) or `Runtime.AssetDatabase.Get(guid)`; addressable packages also load by collector
   address.
3. **Verify in editor-simulated mode**: enter Play mode in the editor (`runtime_playmode`) —
   dynamic loads are served straight from the asset database (no bundles built), exercising the
   same GUID/address resolution the player will perform.

`Resources/` assets are exempt — `Resources.Load` works in editor and player without collectors.

## Idempotency
All three ops take `--idempotency-key`. A replay returns the original result without re-writing.
