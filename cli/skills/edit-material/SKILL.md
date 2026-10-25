---
name: edit-material
description: Read and patch XEngine .mat files offline — shader GUID, colors, floats, texture bindings. Every change is journaled with provenance + drift-protected.
---

# edit-material

Use when an AI needs to inspect or modify a material's shader reference, colors, floats, or texture
bindings. Materials are `.mat` files holding a shader reference + a categorized property bag.

## Inspect a material
```bash
xengine-cli material-inspect --project <root> --material Assets/Materials/Player.mat
```
Returns `{ shaderGuid, properties[], contentHash }`. Each property has a `category` (colors / floats /
ints / textures / vectors) and a `value` (e.g. `R=1, G=0.5, B=0, A=1` for a color, a GUID for a
texture). Note the `contentHash` — pass it to `material-patch` for drift protection.

## Patch a material
```bash
xengine-cli material-patch --project <root> --material Assets/Materials/Player.mat \
  --patches '[{"path":"_properties/_colors/_MainColor/R","value":"0.5"},
              {"path":"_properties/_floats/_EmissionIntensity","value":"2.5"}]' \
  --expected-before-hash <hash-from-inspect> \
  --idempotency-key <key> --intent "tint player red + boost emission"
```

### Common paths
- Change shader: `_shader/AssetID` → new shader GUID.
- Color channel: `_properties/_colors/<Name>/R|G|B|A`.
- Float: `_properties/_floats/<Name>`.
- Int: `_properties/_ints/<Name>`.
- Vector: `_properties/_vectors2|3|4/<Name>/X|Y|Z|W`.
- Texture binding: `_properties/_textures/<Name>/AssetID` → texture GUID.

## Drift protection + idempotency
- `--expected-before-hash`: if the material changed since you read it, you get `stale_state` (re-read
  and retry with the fresh hash).
- `--idempotency-key`: a replay returns the original result without rewriting the file.

## Bind a freshly-imported texture
1. `asset_import` the texture → note its `guid`.
2. `material_inspect` the material → note `contentHash`.
3. `material_patch` with `_properties/_textures/_MainTex/AssetID` = the new GUID, passing the hash.
