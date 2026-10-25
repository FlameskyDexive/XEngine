# Atlas restyle (AI character swap)

Spine animation is **bones + slots + timelines**. A new look does not need a new skeleton. Keep the JSON and `.atlas` text; replace only the page pixels.

Plan: [`docs/superpowers/plans/2026-08-16-spine-atlas-restyle.md`](../../../docs/superpowers/plans/2026-08-16-spine-atlas-restyle.md). Agent skill: [`cli/skills/restyle-spine-atlas/SKILL.md`](../../../cli/skills/restyle-spine-atlas/SKILL.md).

```
authored skeleton ──► same walk / shoot / physics
authored slots     ──► same attachment names
.ai page PNG       ──► new character paint
```

Two ways to get a “new Spine character” without generating bones:

| Path | When | API |
|------|------|-----|
| Authored skins | Goblins, Mix-and-Match | `SpineSkinMixer.Apply` |
| Atlas page swap | AI / restyle / seasonal paint | `SpineAtlasRestyle` + `gen_submit` `kind=texture` |

## Do not whole-page img2img blindly

One GPT Image 2 call on the full atlas is cheap and usually **breaks UVs**: regions drift, rotate, or bleed. Only use a whole-page restyle if the model is locked to the same width/height **and** a region mask keeps every rect in place.

## Reliable path: per-region img2img

1. Parse `.atlas` with `SpineAtlasLayout.Parse` (y is from the **top** of the page).
2. `ExtractUnrotated` each region (90° packed parts are rotated back).
3. Send that crop to GPT Image 2 / ComfyUI / PixelLab img2img at the **exact** unrotated size. Prompt with the region name (`goblin/head`, `left-arm`, …).
4. `BlitUnrotated` into a new page of the same width/height at the same rects. Leave `.atlas` and skeleton JSON unchanged.
5. Official PMA pages are premultiplied. Model output is usually straight alpha — `Premultiply` before assign, or use a straight-alpha shader. Current `Spine/Skeleton` is PMA (`Src One` / `Dst OneMinusSrcAlpha`).
6. `SpineAtlasSwap.Apply(atlasAsset, newPage, skeleton)` then keep playing. `ClearCache()` + `InvalidateSkeleton()` if you reload data instead of rebinding live pages.

CPU buffers are tightly packed RGBA. Pass `flipY: true` only when the buffer came from a GL `Texture2D` (bottom-up). File/PNG buffers stay `flipY: false`.

## Gateway (`gen_submit` / `gen_status` / `gen_fetch`)

No new CLI command. `kind=texture` plus extra `atlasPath` turns fetch into a layout-preserving page write.

```bash
xengine-cli gen-submit --project <root> --provider mock --kind texture \
  --prompt "goblin head, moss green" --seed 7 \
  --extra '{"atlasPath":"Assets/Samples/Goblins/goblins-pma.atlas","region":"goblin/head"}'
xengine-cli gen-status --project <root> --task <taskId>
xengine-cli gen-fetch --project <root> --task <taskId> \
  --destination Assets/Spine/Goblins --base-name goblins-pma --overwrite \
  --idempotency-key goblin-head-1 --intent "atlas restyle goblin/head"
```

| Extra | Required | Notes |
|-------|----------|-------|
| `atlasPath` | yes | Project-relative `.atlas` / `.atlas.txt` |
| `region` | no | Region name; `*` or omit = all (mock-tint). Comma-separated list allowed |
| `sourcePagePath` | no | Override page PNG (default: sibling named in the atlas) |
| `pageName` | no | Pick a page in a multi-page atlas |
| `mode` | no | `auto` (default), `mock-tint`, `pack-crop` |
| `tintR` `tintG` `tintB` | no | Mock tint, default 80/180/255 |
| `pma` | no | Premultiply crop before blit; default follows atlas `pma:` |

`auto`: if the fetched PNG is a real crop (not the source page size) → **pack-crop** (resize to the unrotated region if needed). Otherwise **mock-tint** (mock’s 1×1 placeholder). Submit fills `width`/`height` from the region so PixelLab can emit the crop at the right size. ComfyUI / `openai` also get `inputImagePath` (unrotated crop PNG under `.xengine/gen-crops/`).

| Provider | Role |
|----------|------|
| `mock` | Offline: tint the source region(s), write a full page |
| `pixellab` `kind=texture` | Text/img crop at `width`×`height`, then pack-crop |
| `comfyui` `kind=texture` | Submit uploads the crop to `extra.inputImageNode` (LoadImage). Fetch pack-crops the graph output |
| `openai` `kind=texture` | GPT Image 2 (`OPENAI_API_KEY`). With a crop → `POST /v1/images/edits`; otherwise `/v1/images/generations`. API size is 1024-class; fetch resizes then pack-crops |

Recipe is written into the page PNG `.meta` (`genRecipe` / `genInputHash`) so re-rolls with `--overwrite` keep the GUID.

Do not generate a new `.skel` from a single hero portrait. That throws away weights, meshes, and timelines. Atlas replace is the product: **same rig, new paint**.
