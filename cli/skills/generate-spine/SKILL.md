---
name: generate-spine
description: Generate a rigged, animated 2D Spine character from one prompt/image — slice a character illustration into layered parts, inpaint occlusion, one-click biped rig + mesh skinning, and AI text-to-animation. Output is standard Spine 4.3 playable by com.xengine.spine and the official Spine editor.
---

# generate-spine

Use when an AI (or human) needs a complete 2D skeletal character from a single illustration:
auto-slicing, occlusion fill, rigging/skinning, and animation — with human-reviewable artifacts
at every step. The editor wizard (`Window / AI / Spine Generator`) drives the same commands.

## One-shot pipeline (workflow)

```bash
cp cli/samples/workflows/spinegen-pipeline.json <root>/.xengine/workflows/
xengine-cli workflow-run --project <root> --name spinegen-pipeline \
  --inputs '{"image":"Assets/Art/bear.png","fillProvider":"mock","motion":"idle_breath"}'
```

Steps: `spinegen_slice → spinegen_fill → spinegen_rig → spinegen_skin → spinegen_animate`
(DAG with `${steps.slice.outputDirectory}` templating; every step is also a standalone MCP/CLI tool).

## Stage tools

| Tool | Key args | Produces (under `.xengine/spinegen/slice/<stem>-<hash8>/`) |
|---|---|---|
| `spinegen_slice` | `--image`, `--alpha-threshold`, `--min-area-pixels`, `--split watershed` | `parts/*.png` + `*.seed.png`, packed `<stem>.png`, `<stem>.atlas.txt`, `slice_manifest.json` (content-hash idempotent; `dryRun` hashes only). `--split watershed` additionally cuts solid-touching islands at distance-transform saddles (mech/armor art where every part touches); knobs: `--marker-min-depth`, `--marker-spacing` (0 = auto) |
| `spinegen_fill` | `--slice`, `--provider mock\|openai\|…` | seed continuation per part; candidate gallery (`fill/`, 3 deterministic strategies + generative via `/v1/images/edits`), seam-scored winner composited into `parts/<name>.png`, `fill_manifest.json` |
| `spinegen_rig` | `--slice` | `rig/skeleton.json` (Spine 4.x, biped template: root/hips/spine/head/arms/legs) + `rig/rig_manifest.json` |
| `spinegen_skin` | `--slice`, `--simplify-epsilon-x100`, `--max-hull-vertices` | contour → Douglas-Peucker → ear-clip mesh attachments (concave-safe), `rig/skin_manifest.json` |
| `spinegen_animate` | `--slice`, `--motion idle_breath\|sway\|bob\|wave_arm` **or** `--prompt "…"` (+`--provider mock\|openai`) | appends animation into `rig/skeleton.json` (LLM plans schema-validated; up to 3 repair rounds) + `rig/motion_manifest.json` |

## Play the result

The skeleton JSON + `.atlas.txt` + page PNG import through `com.xengine.spine` like any official
export (see [play-spine-animation](../play-spine-animation/SKILL.md)):
`scene-component-add --type SkeletonAnimation --fields '[{"path":"AnimationName","value":"idle_breath"},…]'`,
then `runtime_spine_play` in a live editor.

## Review loop (human-in-the-loop, like the reference video)

- Slice/fill galleries keep every candidate PNG on disk with seam scores (`fill_manifest.json`) —
  swap a part PNG and re-run `spinegen_rig` onward.
- All stages are deterministic for identical inputs+options; parameters live in the manifests, so
  the wizard's sliders (or an agent) can re-run any stage in isolation.
- `--provider mock` keeps the entire chain offline; real fill/LLM routes need
  `OPENAI_API_KEY` (or `.xengine/secrets.json`).
## Explicit exploded-sheet assembly (recipe v1)

Use `spinegen-slice --project <project> --image Assets/Reference/parts-original.png --recipe Recipes/sword-assembly.json`, then `spinegen-rig --slice <outputDirectory>`. MCP `spinegen_slice` accepts the equivalent optional `recipe` argument. The manifest embeds the recipe and its SHA-256; editing the recipe changes the output directory. Without a recipe, the existing silhouette inference path remains available.

`version: 1`, `bones` (Spine local transforms) and `parts` form the recipe. Each enabled part specifies a unique safe `name`, source `crop` (x/y/width/height), `bone`, crop-local `pivotX/pivotY`, bone-local `x/y/rotation/scaleX/scaleY`, `layer` and `rigid`. Source coordinates use Y down; assembly coordinates use Y up. Optional `maskPolygon` is a flat crop-local XY polygon; `keepLargestIsland` rejects disconnected neighbouring artwork while retaining original alpha and a one-pixel fringe. Disabled parts are excluded. Unknown parents, cycles, duplicate names, non-finite/zero-scale transforms and out-of-bounds crops fail before files are emitted. The `b_` bone prefix is reserved for generated part bones.

Atlas padding is separate from content bounds. Editable part PNGs retain straight RGBA; published page PNGs use premultiplied alpha and declare `pma: true`, matching the engine Spine shader. Reproduce the sword example with `python SpineGenProject/Recipes/generate.py` after building the AI Host.

Recipe crops are tightly trimmed only at fully transparent borders after mask/island selection. The recipe retains the original crop and pivot; manifest part X/Y/width/height describe actual trimmed content. Rig and explicit mesh paths account for this offset. Mesh weights keep their original source-space transition, and UVs cover only the trimmed region.

The packer searches independent power-of-two widths/heights up to 4096, by area then longest side, using deterministic unrotated MaxRects. It chooses the smallest page found by its fixed heuristics; this is not a general proof of optimal rectangle packing. Padding includes every edge, with one-pixel edge extrusion when padding is nonzero. The fill stage republishes the PMA atlas after modifying parts. Legacy manifests with padding inside content bounds are rejected before any fill writes: run slice again.

Optional `joints` entries (`bone`, `parentPart`, `sourceX`, `sourceY`) resolve a child bone's
setup location from a landmark in the parent's **original crop-local** Y-down coordinates.
They are setup anchors, not runtime IK constraints. For soft parent parts, keep connecting
regions pinned to the corresponding influence. `weightStart` / `weightEnd` (default 0/1)
control the top-to-bottom smooth weight transition in original crop space.

Parts may name an external `image` and explicit `imageWidth` / `imageHeight` (1..4096).
The source is area-resampled in premultiplied space and kept as editable straight RGBA;
its byte hash joins the recipe's cache identity and is recorded in `sourceHashes`.
The main sheet may be larger than the atlas limit when selected crops fit. `algorithmVersion`
identifies the slicing semantics. `shearX` / `shearY` follow Spine local transform conventions.

`excludePolygons` remove crop-local regions. Optional `maskFeather` / `exclusionFeather`
(0..64 source pixels) soften only authored mask boundaries; use them only where another
part covers the seam. `alphaScale` defaults to 1 and is an explicit per-part opacity gain
(0..4, exclusive zero), not binary thresholding. Leave original artwork at 1; record any
repair-source normalization. All original input PNGs remain unchanged.

`spinegen-animate --plan Recipes/idle.json` (MCP `plan`) loads a local MotionPlan. Supply
exactly one of `plan`, `motion`, or `prompt`. Plans retain semantic validation and export
through the same animation pipeline; they are not hand-patched skeleton JSON.
