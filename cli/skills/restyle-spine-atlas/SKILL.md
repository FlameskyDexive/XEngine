---
name: restyle-spine-atlas
description: Replace a Spine atlas page (character paint) via gen_submit kind=texture extras. Bones, slots, and .atlas text stay. Mock tint offline; PixelLab/ComfyUI/openai pack-crop the unrotated region.
---

# restyle-spine-atlas

Turn a prompt into a **new Spine look** without generating bones. Read [atlas-restyle.md](../../../Packages/com.xengine.spine/Documentation~/atlas-restyle.md) and the plan [2026-08-16-spine-atlas-restyle.md](../../../docs/superpowers/plans/2026-08-16-spine-atlas-restyle.md) before calling tools.

**Do not** generate a new `.skel` from a hero portrait. Keep skeleton JSON + `.atlas` text. Only the page PNG changes.

## Extra keys (`kind=texture`)

| Key | Required | Notes |
|-----|----------|-------|
| `atlasPath` | yes | Project-relative `.atlas` / `.atlas.txt` |
| `region` | no | e.g. `goblin/head`. `*` or omit = all regions (mock-tint) |
| `sourcePagePath` | no | Override page PNG |
| `mode` | no | `auto` / `mock-tint` / `pack-crop` |
| `tintR` `tintG` `tintB` | no | Mock only, default 80/180/255 |
| `inputImageNode` | ComfyUI | LoadImage node id. Host sets `inputImagePath`. |
| `size` `quality` | openai | Optional. Default quality `medium`. gpt-image-2 size is 1024-class. |

Submit fills `width`/`height` from the unrotated region (PixelLab crop size). For `comfyui` / `openai`, submit also writes the unrotated crop to `.xengine/gen-crops/` and sets `inputImagePath`.

## Offline mock (tests / dry pipeline)

```bash
xengine-cli gen-submit --project <root> --provider mock --kind texture \
  --prompt "goblin head, moss green" --seed 7 \
  --extra '{"atlasPath":"Assets/Spine/Goblins/goblins-pma.atlas","region":"goblin/head"}'
xengine-cli gen-status --project <root> --task <taskId>
xengine-cli gen-fetch --project <root> --task <taskId> \
  --destination Assets/Spine/Goblins --base-name goblins-pma --overwrite \
  --idempotency-key goblin-head-1 --intent "atlas restyle goblin/head"
```

Fetch writes a **full page** PNG (same WxH as the source page). Assign it on `SpineAtlasAsset.PageTextures` or reimport. Then `SpineAtlasSwap.Apply` / `ClearCache` + `InvalidateSkeleton`.

## PixelLab / ComfyUI / OpenAI crop

Same atlas extra. Fetch `pack-crop`s the provider PNG back (resizes if the API cannot emit the exact region size).

- **PixelLab** — uses submit `width`/`height`. Rejects sizes outside 32–400; skip tiny regions or use mock.
- **ComfyUI** — API-format graph with a `LoadImage` node. Pass `inputImageNode` (Host already sets `inputImagePath`). Keep the graph at the crop’s pixel size when you can.
- **openai** (GPT Image 2) — `OPENAI_API_KEY` or `.xengine/secrets.json` `openai`. Default model `gpt-image-2`. Edits the injected crop (`/v1/images/edits`). Does **not** support `background=transparent`. Optional extra: `size`, `quality` (`low`/`medium`/`high`).

```bash
xengine-cli gen-submit --project <root> --provider openai --kind texture \
  --prompt "goblin head, moss green, same silhouette" \
  --extra '{"atlasPath":"Assets/Spine/Goblins/goblins-pma.atlas","region":"goblin/head"}'

xengine-cli gen-submit --project <root> --provider comfyui --kind texture \
  --prompt "goblin head, moss green" \
  --extra '{"atlasPath":"Assets/Spine/Goblins/goblins-pma.atlas","region":"goblin/head","workflow":"Assets/GenArt/head-img2img-api.json","promptNode":"6","inputImageNode":"10"}'
```

## Re-roll

Recipe is in the page `.meta`. Change prompt/seed/extra → new hash → `gen_fetch --overwrite` to the same `--base-name` (GUID preserved).

## Workflow

Copy [`cli/samples/workflows/spine-atlas-restyle.json`](../../samples/workflows/spine-atlas-restyle.json) into `.xengine/workflows/`.
