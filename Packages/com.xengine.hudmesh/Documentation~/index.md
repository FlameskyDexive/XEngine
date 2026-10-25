# Hudmesh — world-space overhead UI

Hudmesh renders overhead UI (health bars, names, damage numbers, chat bubbles)
as camera-facing meshes combined into **one dynamic mesh per material**. It is a
port of the Unity 2021 project [UnityHUD](https://github.com/FlameskyDexive/UnityHUD)
to XEngine's mesh pipeline — no per-entity GameObjects, no canvases, one draw
call per texture.

## Architecture

- `HudManager` — scene component owning every `Hud`; ticks them in `LateUpdate`
  and combines + submits meshes in `OnRenderCollect`. HUDs are painter's-sorted
  far→near by camera distance before filling, so overlapping quads layer
  correctly inside each combined mesh.
- `Hud` — the public per-entity handle: attach to a `Transform`, drive slots,
  release. All heavy state lives in the internal `HudObject`.
- `HudObject` — five fixed slots (`Blood`, `Name`, `Title`, `GuildName`,
  `GuildIcon`) stacked bottom-up plus dynamic widgets (damage numbers, bubbles).
  Hiding a slot recomputes the stack so everything above drops down.
- Widgets — `HudSpriteWidget` (sprite / nine-slice), `HudBarWidget` (three
  segment sliced fill), `HudTextWidget` (SDF text, gradient, outline),
  `HudNumberWidget` (digit-art or SDF damage numbers with curve animation),
  `HudTalkWidget` (auto-sized chat bubble).
- Geometry — `HudGeometry` writes pixel-space quads (`HudQuad`, Y-up, BL/BR/TR/TL
  corners). At fill time the manager expands them against the camera basis
  (`1/PixelsPerUnit` world units per pixel) into fixed-capacity arrays padded
  with the last vertex + degenerate triangles — steady-state zero allocation;
  `Mesh.AssignStreams` + `Upload` reuse the GPU buffers every frame.

## Materials and shaders

Slot 0 of the combined mesh set uses the shared SDF font material
(`Default/DefaultTextMesh`, depth-tested — text is occluded by geometry, UnityHUD
parity). Sprite slots use `Hudmesh/SpriteTop` (`ZTest Off`, the UnityHUD
always-on-top default) or `Hudmesh/SpriteOccluded` (`ZTest LEqual`) selected by
`HudManager.OccludeByScene`. Set `HudManager.PixelsPerUnit` (default 100) to
scale every HUD.

## API quick start

```csharp
HudManager.Init(HudSpriteTable.CreateBundled());   // bundle the UnityHUD art set
Hud hud = HudManager.Instance.CreateHud(entityId);
hud.Init(entityTransform, new Float3(0, 1.2f, 0)); // anchor above the head

hud.SetBloodVisible(true, HudRelation.Enemy);      // bar from the bundled art
hud.SetBloodPercent(0.6f);
hud.SetText(HudSlot.Name, "Corvin", HudRelation.Enemy);
hud.SetText(HudSlot.Title, "Knight", HudRelation.Enemy);
hud.SetIcon(HudSlot.GuildIcon, "UI_headtop_sign_001_6", HudRelation.Enemy);

hud.PopNumber(0, 512, new Float3(0, 0.4f, 0));     // floating damage number
hud.Talk(0, "Hello!");                             // chat bubble

hud.SetSlotVisible(HudSlot.Title, false);          // stack reflows automatically
hud.Release();                                     // entity died
```

## Configuration

`HudmeshConfig` is an Echo asset (`.hudconfig`) holding stack gaps, the bar /
bubble layouts and `HudNumberStyle` entries (glyph mode, digit-set prefix, and
the move / alpha / scale `AnimationCurve`s whose last keys define the number
lifetime). Import one, then hot-swap at runtime:

```csharp
HudManager.Instance.ChangeConfig(config);
```

The editor menu `Tools/Hudmesh/Pack Bundled Sprites` packs the package's sprite
folder into `hud_atlas1.png` + `hud_atlas1.hudsprites` (a text index: one
`sprite <name> <x> <y> <w> <h> <bl> <bt> <br> <bb>` line per sprite, y measured
from the atlas bottom). Load a packed atlas with
`HudSpriteTable.RegisterAtlas(atlasTexture, indexText)` to cut 60 textures down
to one draw call.

## Demo

The `Hudmesh Demo` sample scene hosts the `HudmeshDemo` component: five
nameplates, looping damage numbers in both glyph modes, and a chat bubble.

## Evidence

Acceptance screenshots and performance measurements are archived in the engine
repository at `docs/superpowers/evidence/packages/hudmesh/`.
