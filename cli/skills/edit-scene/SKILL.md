---
name: edit-scene
description: Create, patch (move/rename/field edits), or delete GameObjects in an XEngine scene via xengine-cli. Every change is recorded to the operation journal with provenance (author/intent).
---

# edit-scene

Use when an AI needs to **modify** a scene: move objects, rename them, change component
fields, add or remove GameObjects. Every edit is written back to the `.scene` file AND
appended to the on-disk operation journal (`.xengine/journal/`) with full provenance — so
the change is traceable and replayable, identical in shape to an editor edit.

## Patch a GameObject's fields (most common)
```bash
xengine-cli scene-object-patch --project <root> --scene <path> \
  --object <GameObject-identifier> \
  --patches '[{"path":"Transform/_localPosition/Y","value":"4.5"}]' \
  --idempotency-key <unique-key> --intent "<why you changed it>"
```
- `path` is a slash-separated tag path into the GO's Echo tree.
  - Move: `Transform/_localPosition/X|Y|Z`
  - Scale: `Transform/_localScale/X|Y|Z`
  - Rename: `Name`
  - Component field: ask the user / inspect the component's fields first.
- `value` is a literal: numbers (`4.5`, `-1`), booleans (`true`), or strings (`"MainCamera"`).
- `--idempotency-key`: any stable unique string (1–128 chars). Reusing it never re-applies.
- `--intent`: a short natural-language reason — it is persisted to the journal.

## Create a GameObject
```bash
xengine-cli scene-object-create --project <root> --scene <path> \
  --name "Directional Light" --idempotency-key <key> --intent "add key light"
```

## Delete a GameObject
```bash
xengine-cli scene-object-delete --project <root> --scene <path> \
  --object <GameObject-identifier> --idempotency-key <key> --intent "remove camera"
```

## Dry run (validate without writing)
Add `--dry-run` to any of the above: it reports the change id + hashes but writes neither
the file nor the journal.

## Add a component to a GameObject
```bash
xengine-cli scene-component-add --project <root> --scene <path> \
  --object <GameObject-identifier> --type Camera \
  --fields '[{"path":"FieldOfView","value":"75"}]' \
  --idempotency-key <key> --intent "add camera with custom FOV"
```
- `--type`: a known short-name (`Camera`, `MeshRenderer`, `DirectionalLight`, `Light2D`, `ShadowCaster2D`, `PixelPerfectCamera`, `AudioSource`,
  `Rigidbody3D`, `Terrain` / `TerrainComponent`, `TerrainCollider`, `WindZone`, ...) or a full `$type` for user MonoBehaviour scripts. Terrain types resolve to `XEngine.Terrain.Runtime` when `com.xengine.terrain` is installed.
- `--fields`: optional JSON array of path/value patches applied to the new component node.
- The new component's `_identifier` is returned in the summary (use it for precise removal).
- Unknown short-names are rejected with the list of known names.

## Remove a component
```bash
# By identifier (preferred — unambiguous):
xengine-cli scene-component-remove --project <root> --scene <path> \
  --object <GameObject-identifier> --component-id <component-GUID> \
  --idempotency-key <key> --intent "remove camera"

# By type (removes the first match):
xengine-cli scene-component-remove --project <root> --scene <path> \
  --object <GameObject-identifier> --type AudioSource \
  --idempotency-key <key> --intent "remove audio source"
```

## Patch an existing component's fields (★ edit Camera.FieldOfView, Light.Intensity, etc.)
```bash
# By component identifier (preferred — from scene_hierarchy's component.identifier):
xengine-cli scene-component-patch --project <root> --scene <path> \
  --object <GameObject-identifier> --component-id <component-GUID> \
  --patches '[{"path":"FieldOfView","value":"75"},{"path":"NearClipPlane","value":"0.5"}]' \
  --idempotency-key <key> --intent "widen camera FOV"

# By component type (first match):
xengine-cli scene-component-patch --project <root> --scene <path> \
  --object <GameObject-identifier> --type Camera \
  --patches '[{"path":"ClearColor/R","value":"0.1"}]' \
  --idempotency-key <key> --intent "tint clear color red"
```
- Patch paths are RELATIVE to the component root (e.g. `FieldOfView`, `ClearColor/R`, `NearClipPlane`) —
  no `Components/0/` prefix needed. This is the stable way to edit component fields: it locates the
  component by GUID/type, not array index (which concurrent edits can shift).
- Camera split-screen viewport is a normalized `Rect` (Min/Max in 0–1). Default `(0,0)–(1,1)` is full-frame.
  Left half: `Viewrect/Min/X=0`, `Viewrect/Min/Y=0`, `Viewrect/Max/X=0.5`, `Viewrect/Max/Y=1`.
  Right half: `Viewrect/Min/X=0.5`, `Viewrect/Max/X=1`.
- Camera stack: Base cameras keep `RenderType` default (`Base`). Overlay cameras set `RenderType` to `Overlay` (value `1`) and must be listed on a Base's `CameraStack`. Unstacked overlays do not render. Overlay composites into the Base color; the last camera in the stack blits.
```bash
xengine-cli scene-component-patch --project <root> --scene <path> \
  --object <OverlayCamera-identifier> --type Camera \
  --patches '[{"path":"RenderType","value":"1"},{"path":"ClearFlags","value":"2"}]' \
  --idempotency-key <key> --intent "mark UI camera as depth-only overlay"
```
`ClearFlags` `2` is Depth (keep Base color). Inspect `CameraStack` on the Base via `scene_hierarchy` (list of Camera refs).
```bash
xengine-cli scene-component-patch --project <root> --scene <path> \
  --object <GameObject-identifier> --type Camera \
  --patches '[{"path":"Viewrect/Min/X","value":"0"},{"path":"Viewrect/Max/X","value":"0.5"}]' \
  --idempotency-key <key> --intent "left-half split-screen camera"
```
- Reflection probe: `--type ReflectionProbe`. Influence AABB is axis-aligned around `Transform` position; full size is `Size` × lossy scale. Weight is 1 inside the box, falls off over `BlendDistance`, and is 0 with no cubemap (Standard keeps the ambient specular approx).
```bash
xengine-cli scene-component-add --project <root> --scene <path> \
  --object <GameObject-identifier> --type ReflectionProbe \
  --fields '[{"path":"Size/X","value":"10"},{"path":"Size/Y","value":"10"},{"path":"Size/Z","value":"10"},{"path":"Intensity","value":"1"},{"path":"BlendDistance","value":"1"},{"path":"Importance","value":"1"},{"path":"BoxProjection","value":"true"},{"path":"Mode","value":"0"},{"path":"Resolution","value":"128"}]' \
  --idempotency-key <key> --intent "add indoor reflection probe"
```
`Mode` `0` is Baked, `1` is Realtime. `Texture` is an `AssetRef` cubemap (leave empty until baked/captured). Patch the same paths with `scene-component-patch`.
- Decal projector: `--type DecalProjector`. Oriented box; full size is `Size` × lossy scale. Projection axis is local Y; albedo UV is local XZ. `RenderingLayers/mask` selects which renderers receive the decal (same 8 bits as lights). Empty `Texture` uses white. Requires a runtime `ScreenSpaceDecalRendererFeature` (not Echo on `.renderer` yet).
```bash
xengine-cli scene-component-add --project <root> --scene <path> \
  --object <GameObject-identifier> --type DecalProjector \
  --fields '[{"path":"Size/X","value":"2"},{"path":"Size/Y","value":"2"},{"path":"Size/Z","value":"2"},{"path":"Opacity","value":"1"},{"path":"RenderingLayers/mask","value":"255"}]' \
  --idempotency-key <key> --intent "add ground decal projector"
```
- Light cookies: `Cookie` (`AssetRef` Texture2D), `CookieSize` (directional ortho width, default 10), `CookieIntensity` (0 = ignore, 1 = full). Point/spot cookies pack into a 1024 color atlas (same Guillotine idea as ShadowAtlas). No cookie → lighting unchanged (multiply by 1). Terrain HLSL does not sample cookies (root-signature cap); Terrain GLSL does.
```bash
xengine-cli scene-component-patch --project <root> --scene <path> \
  --object <Light-identifier> --type DirectionalLight \
  --patches '[{"path":"CookieSize","value":"20"},{"path":"CookieIntensity","value":"1"}]' \
  --idempotency-key <key> --intent "widen directional light cookie projection"
```
- Rendering layers: 8 named bits (`Default`, `Layer1`…`Layer7`) on lights, renderers, and `DecalProjector`. Independent of `GameObject.LayerIndex` / camera `CullingMask`. Default `RenderingLayers/mask` is `255` (Everything) so lighting is unchanged. A light or decal only affects renderers whose mask overlaps (`(light & object) != 0`).
```bash
xengine-cli scene-component-patch --project <root> --scene <path> \
  --object <Light-identifier> --type DirectionalLight \
  --patches '[{"path":"RenderingLayers/mask","value":"2"}]' \
  --idempotency-key <key> --intent "light only Layer1"
```
```bash
xengine-cli scene-component-patch --project <root> --scene <path> \
  --object <Mesh-identifier> --type MeshRenderer \
  --patches '[{"path":"RenderingLayers/mask","value":"2"}]' \
  --idempotency-key <key> --intent "renderer on Layer1"
```
`mask` is a uint bitfield: bit 0 = Default, bit 1 = Layer1, … bit 7 = Layer7. `1` = Default only, `2` = Layer1 only, `255` = Everything.
- Light2D (Renderer2D only): `--type Light2D`. `LightType` `0` Global / `1` Point / `2` Sprite. Point uses `InnerRadius` / `OuterRadius` / `Falloff`. Sprite cookies are `Cookie` (`AssetRef` Sprite). `CastShadows` + `ShadowIntensity` sample `ShadowCaster2D` boxes. Does **not** go into LightBVH — requires `.renderer` `Kind` = `1`.
```bash
xengine-cli scene-component-add --project <root> --scene <path> \
  --object <GameObject-identifier> --type Light2D \
  --fields '[{"path":"LightType","value":"1"},{"path":"Intensity","value":"1"},{"path":"OuterRadius","value":"4"},{"path":"CastShadows","value":"true"}]' \
  --idempotency-key <key> --intent "add point Light2D"
```
- Shadow caster 2D: `--type ShadowCaster2D`. XY AABB; full size is `Size` × lossy scale.
```bash
xengine-cli scene-component-add --project <root> --scene <path> \
  --object <GameObject-identifier> --type ShadowCaster2D \
  --fields '[{"path":"Size/X","value":"1"},{"path":"Size/Y","value":"2"},{"path":"CastsShadows","value":"true"}]' \
  --idempotency-key <key> --intent "add 2D shadow caster"
```
- Pixel-perfect camera: `--type PixelPerfectCamera` on the same GameObject as `Camera`. Sets orthographic size from `AssetsPPU` × integer zoom of `RefResolutionX/Y`; `Snap` locks XY to the pixel grid. `Upscale` `true` renders at `RefResolutionX/Y` and the pipeline blits to the window (combine with `.pipeline` `Antialiasing` MSAA/TAA).
```bash
xengine-cli scene-component-add --project <root> --scene <path> \
  --object <Camera-identifier> --type PixelPerfectCamera \
  --fields '[{"path":"AssetsPPU","value":"100"},{"path":"RefResolutionX","value":"320"},{"path":"RefResolutionY","value":"180"},{"path":"Snap","value":"true"}]' \
  --idempotency-key <key> --intent "pixel-perfect 320x180 camera"
```
- `--enable` / `--disable`: toggle the component's `_enabled` flag without a manual patch.
- For prefabs, use `prefab-component-patch` (same params, `--prefab` instead of `--scene`).

## Or via MCP
`scene_object_patch` / `scene_object_create` / `scene_object_delete` /
`scene_component_add` / `scene_component_remove` (same parameters).

## Workflow tip
1. `scene-hierarchy` to find the object's `identifier` (and component `identifier`s for removal).
2. `scene-object-patch --dry-run` to preview.
3. Apply with a meaningful `--intent`.
4. `journal-query` to confirm the change was recorded.
