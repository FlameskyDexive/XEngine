---
name: generate-asset
description: Generate art assets (3D models, motion, pixel sprites, skyboxes, sound effects) from prompts via gen_submit → gen_status → gen_fetch, then wire them into scenes. Providers — meshy, pixellab, blockade, elevenlabs, comfyui (local), openai (GPT Image 2), mock (offline).
---

# generate-asset

Turn a text prompt into an imported, scene-ready project asset. The generation gateway is the
submit → poll → fetch triple; every fetched asset is journaled, carries its **recipe** in the
`.meta` (re-rollable), and is deduplicated by recipe hash (no double billing).

## Providers and kinds

| Provider | Kinds | Notes (extra keys) |
|---|---|---|
| `mock` | all | Offline, keyless, instant — fabricates valid tiny GLB/PNG/WAV. Use to test pipelines. |
| `meshy` | `model3d`, `motion` | text-to-3d (default), `mode=refine` + `previewTaskId`, `mode=remesh` + `inputTaskId`, `imageUrl` → image-to-3d; motion: `inputTaskId`/`modelUrl` → rig, `rigTaskId` + `actionId` → library animation. Options: `topology`, `targetPolycount`, `symmetry`, `poseMode`, `artStyle`, `shouldRemesh`, `enablePbr`, `textureResolution`, `fetchThumbnail`. |
| `pixellab` | `sprite`, `texture` | Pixel art (synchronous). `width`/`height` (32–400, default 128), `noBackground` (default true for sprite), `outline`, `shading`, `detail`, `isometric`, `textGuidanceScale`, `negativeDescription`. |
| `blockade` | `skybox` | Equirectangular 360 skybox (async). `styleId`, `negativeText`, `enhancePrompt`; fetch-time `fetchDepthMap`, `fetchThumbnail`. |
| `elevenlabs` | `sfx` | Sound effects → MP3 (synchronous). `durationSeconds` (0.5–30), `promptInfluence` (0–1). |
| `comfyui` | `model3d`, `texture`, `sprite`, `skybox` | Local ComfyUI HTTP (`http://127.0.0.1:8188`). `preset=trellis2` or `hunyuan3d` (text→GLB), or `workflow` / `workflowJson` (API-format graph). Inject: `promptNode`/`promptField`, `negativeNode`, `loraNode`+`loraModelId`, `seedNode`, `baseUrl`, `inputImageNode`+`inputImagePath` (Host fills the path for Spine atlas crops). Optional `COMFYUI_API_KEY` Bearer. `kind=texture` + `atlasPath`/`region` pack-crops a Spine page on fetch (see [restyle-spine-atlas](../restyle-spine-atlas/SKILL.md)). |
| `openai` | `texture`, `sprite` | GPT Image 2 (`OPENAI_API_KEY` or secrets.json `openai`). Default model `gpt-image-2`. Extra: `size`, `quality`, `background` (not `transparent` on gpt-image-2), `outputFormat`, `inputImagePath`. Atlas restyle uses `/v1/images/edits` on the extracted crop. |

**API keys:** `{PROVIDER}_API_KEY` env var (e.g. `MESHY_API_KEY`) or `.xengine/secrets.json`
(`{"meshy": "msy_..."}`) — gitignored. Keys are never echoed into results or the journal.

## The three tools

```bash
# 1. Submit (use --dry-run first to see the inputHash + estimated routing without spending credits)
xengine-cli gen-submit --project <root> --provider meshy --kind model3d \
  --prompt "a rusty iron sword" --seed 7 --extra '{"topology":"quad","targetPolycount":"20000"}'
# → { taskId, inputHash, state, deduplicated }   (same recipe → same task, no re-bill)

# 2. Poll until succeeded (async providers; mock/pixellab/elevenlabs succeed immediately)
xengine-cli gen-status --project <root> --task <taskId>

# 3. Fetch: download → import under Assets/ → recipe into .meta → sub-asset GUIDs in the result
xengine-cli gen-fetch --project <root> --task <taskId> \
  --destination Assets/GenArt --base-name rusty-sword --idempotency-key gen-sword-1 \
  --intent "hero weapon prop"
```

MCP tools: `gen_submit` / `gen_status` / `gen_fetch` (same arguments, camelCase).

`gen_fetch` output for models inlines the derived sub-assets (same algorithm as `asset_subassets`):

```json
{ "assets": [ { "assetPath": "Assets/GenArt/rusty-sword.glb", "guid": "...", "role": "model",
    "subAssets": [ { "stableId": "Blade", "kind": "Mesh", "guid": "..." },
                   { "stableId": "Iron", "kind": "Material", "guid": "..." } ] } ] }
```

## Wire into a scene

Use the sub-asset GUIDs directly (no editor import needed — GUIDs are deterministic):

```bash
xengine-cli scene-object-create --project <root> --scene Assets/Scenes/Main.scene \
  --name Sword --idempotency-key sword-go-1          # → returns createdIdentifier
xengine-cli scene-component-add --project <root> --scene Assets/Scenes/Main.scene \
  --object <createdIdentifier> --type MeshRenderer --idempotency-key sword-mr-1 \
  --fields '[{"path":"Mesh/AssetID","value":"<meshGuid>"},
             {"path":"Materials/$values/0/AssetID","value":"<materialGuid>"}]'
```

For a **playable** prop (budget gate + BoxCollider + `.prefab`), copy
[`cli/samples/workflows/text-to-playable-prop.json`](../../samples/workflows/text-to-playable-prop.json)
instead. That DAG runs `gen_ensure_budget` **before** wiring mesh GUIDs (remesh can rename
sub-assets), then `scene_collider_add` (AABB box) and `prefab_create`.

```bash
xengine-cli asset-mesh-stats --project <root> --asset Assets/GenArt/rusty-sword.glb --max-triangles 5000
xengine-cli gen-ensure-budget --project <root> --asset Assets/GenArt/rusty-sword.glb \
  --max-triangles 5000 --task <taskId> --idempotency-key sword-budget-1
xengine-cli scene-collider-add --project <root> --scene Assets/Scenes/Main.scene \
  --object <createdIdentifier> --model Assets/GenArt/rusty-sword.glb --shape box \
  --idempotency-key sword-col-1
xengine-cli prefab-create --project <root> --scene Assets/Scenes/Main.scene \
  --object <createdIdentifier> --destination Assets/Prefabs/Sword.prefab \
  --idempotency-key sword-prefab-1
```

For a **playable character** (clips → `.controller` + Animator). Different skeletons: run
`clip_retarget` first (Mixamo/VRM aliases → Unity names, or `--like` a clip that already plays):

```bash
xengine-cli clip-retarget --project <root> --clip Assets/GenArt/walk.anim \
  --idempotency-key retarget-walk-1
# or --like Assets/Characters/HeroIdle.anim
```

Then wire:
copy [`cli/samples/workflows/text-to-character.json`](../../samples/workflows/text-to-character.json).
Mock `kind=motion` yields a GLB with **Idle** and **Walk** clips; Meshy is generate+rig then
library/BYOC animate. `character_wire` mints the controller, one looping state per clip, cyclic
exit-time transitions, a `Speed` float, and binds `Animator.Controller`.

```bash
xengine-cli character-wire --project <root> --scene Assets/Scenes/Main.scene \
  --object <createdIdentifier> --model Assets/GenArt/hero.glb \
  --controller Assets/Animators/Hero.controller --idempotency-key hero-wire-1 \
  --intent "hero locomotion"
```

For a **playable 2D sprite** (grid-slice a sheet → `SpriteRenderer` + `SpriteFlipbook`), copy
[`cli/samples/workflows/text-to-sprite.json`](../../samples/workflows/text-to-sprite.json).
Mock `kind=sprite` yields a 32×8 / 4-frame strip. PixelLab returns one PNG — prompt for a
walk-cycle strip and pass `columns` / `rows`. `sprite_slice` writes TextureImporter Multiple
into the `.meta` (names `{base}_0`…, GUIDs = `SHA256(parent+name)[0..16]`); `sprite_wire`
adds the renderer (first frame) and flipbook (all frames, fps).

An imported `.aseprite`/`.ase` already emits those sprites (`texture:sheet` + `sprite:0`…,
GUIDs = parent + stable id). Use `asset_subassets` and wire `sprite:{i}` — skip `sprite_slice`.

```bash
xengine-cli sprite-slice --project <root> --asset Assets/GenArt/hero.png \
  --columns 4 --rows 1 --idempotency-key hero-slice-1
xengine-cli sprite-wire --project <root> --scene Assets/Scenes/Main.scene \
  --object <createdIdentifier> --asset Assets/GenArt/hero.png \
  --columns 4 --fps 12 --idempotency-key hero-sprite-1
```

If you already have clip GUIDs (no generate step), use
[`cli/samples/workflows/author-locomotion.json`](../../samples/workflows/author-locomotion.json)
instead (`animator_state_create` / `animator_transition_create`).

Or run the whole generate-only chain as one DAG: copy
[`cli/samples/workflows/text-to-prop.json`](../../samples/workflows/text-to-prop.json) into the
project's `.xengine/workflows/`, then:

```bash
xengine-cli workflow-run --project <root> --name text-to-prop --inputs '{
  "provider":"mock", "prompt":"a rusty iron sword", "scenePath":"Assets/Scenes/Main.scene",
  "objectName":"Sword", "idempotencyKey":"t2p-sword-1"}'
```

The sample assumes a single-mesh single-material prop (`subAssets.0` = Mesh, `.1` = Material). For
richer models, read `asset_subassets` output and wire each mesh/material explicitly.

## Local ComfyUI (TRELLIS.2 / Hunyuan3D)

No cloud key required. Export the graph as **Save (API Format)** — the UI `nodes[]` export is rejected.

```bash
# Built-in text-to-GLB presets (custom nodes must be installed in ComfyUI)
xengine-cli gen-submit --project <root> --provider comfyui --kind model3d \
  --prompt "a low-poly crate" --extra '{"preset":"trellis2"}'
# or --extra '{"preset":"hunyuan3d"}'

# Your own graph (node 6 = CLIPTextEncode is the ComfyUI SD default)
xengine-cli gen-submit --project <root> --provider comfyui --kind texture \
  --prompt "brick wall" --extra '{"workflow":"Assets/GenArt/wall-api.json","promptNode":"6"}'
```

Poll / fetch are the same as other async providers. `style-anchor` LoRA/palette/negatives fold into the graph when `loraNode` is set (palette is appended to the positive prompt).

## Candidate gallery (pick a winner)

After several `gen_fetch` imports, browse them in the editor (**Window → AI → Candidate Gallery**)
or offline:

```bash
xengine-cli gen-candidates --project <root> --kind model3d --query sword
xengine-cli gen-candidate-choose --project <root> --task <taskId>
xengine-cli gen-candidate-discard --project <root> --task <otherTaskId>
```

Choose writes `.xengine/gen-gallery.json` (does not delete files). Discard only hides the card.

## Style Anchor (project look)

Store a reusable look in `.xengine/style-anchors.json`. `gen_submit --style-anchor <id>`
appends `Negative: …` to the prompt and fills extra `loraModelId` / `palette` /
`referenceImageGuids` (caller extra keys win).

```bash
xengine-cli style-anchor-upsert --project <root> --id scenario-default \
  --lora scenario-lora-abc --palette '#2b2b2b,#e8d5a3' --negative 'blurry, watermark'
xengine-cli gen-submit --project <root> --provider mock --kind model3d \
  --prompt "a crate" --style-anchor scenario-default
```

## Headless import (Library/cache)

Offline `gen_fetch` / `asset_import` copy the file and mint `.meta` only. To materialize
editor sub-assets into `Library/cache/{guid}.asset` (sprites, meshes) without a resident
editor:

```bash
XEngine.Editor --refresh-assets --project <root>
# → { "success": true, "assetCount": N, "cachePath": "<root>/Library/cache", ... }  exit 0
```

## Re-roll (regenerate without breaking references)

The recipe lives in the asset's `.meta` (`genRecipe` / `genInputHash`) and in
`.xengine/gen-tasks.json`. To re-roll: change any recipe field (prompt/seed/extra), `gen_submit`
(new hash → new task), then `gen_fetch --base-name <same> --overwrite` — the import preserves the
existing GUID, so every scene/prefab reference picks up the new art with zero rewiring.

## Async providers (meshy, blockade)

`gen_status` transitions pending → running → succeeded|failed. Poll with a sleep between calls;
tasks persist in `.xengine/gen-tasks.json`, so polling works across CLI invocations (and after a
crash). `gen_fetch` before success fails with the current state — safe to retry.

## Spine atlas restyle (same rig, new paint)

Do **not** generate a new `.skel`. Keep bones and `.atlas` text. `kind=texture` + extra `atlasPath` (and optional `region`) makes `gen_fetch` write a **full page** PNG:

- `mock` — tint the named region(s) on the source page (`mode=mock-tint`).
- PixelLab / ComfyUI / `openai` — blit the crop back (`pack-crop`; resize if the API size ≠ region). Submit fills `width`/`height`. ComfyUI / openai also get `inputImagePath` (unrotated crop).

See [restyle-spine-atlas](../restyle-spine-atlas/SKILL.md) and `Packages/com.xengine.spine/Documentation~/atlas-restyle.md`.

```bash
xengine-cli gen-submit --project <root> --provider mock --kind texture \
  --prompt "goblin head, moss green" \
  --extra '{"atlasPath":"Assets/Spine/Goblins/goblins-pma.atlas","region":"goblin/head"}'
```

## Gotchas

- **Kind must match the provider** (`gen_submit` validates and lists supported kinds on error).
- **Unknown `extra` keys fail fast** with the supported list — never silently ignored.
- **`gen_fetch` re-run needs `--overwrite`** once the asset exists (deliberate: re-roll is explicit).
- Meshy text-to-3d preview is untextured; chain `mode=refine` + `previewTaskId` for textures.
- Blockade skyboxes are equirect JPG (8192×4096) — wire via scene settings skybox material.
- Downloads are staged under `.xengine/gen-downloads/{taskId}/` before import (inspectable).
