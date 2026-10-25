# Changelog

All notable changes to this package are documented here. Newest first.

## [0.1.0] - 2026-09-21

- Package scaffold: `package.json`, runtime/editor asmdefs with identity
  markers, README/LICENSE/CHANGELOG/Documentation~ stubs, and the xUnit test
  project with layout-convention pins (spine38 pattern).
- Bundled the UnityHUD art set (60 PNGs: `b/c/h/l` digit styles, bar
  backgrounds/fills, bubble, guild signs, slider chrome) under
  `Runtime/Assets/Sprites` with texture metas.
- `HudmeshAssetLibrary`: package path search (repo / PackageCache / player),
  BuiltInAssets shader registration, cached sprite texture loading.
- `Hudmesh/SpriteTop` (ZTest Off) and `Hudmesh/SpriteOccluded` (ZTest LEqual)
  shaders — straight-alpha world-space HUD sprite passes.
- Core combine pipeline: `HudManager` (registry, distance sort, per-material
  `MeshRenderable` submission), `Hud` handle, `HudObject` (5-slot stack with
  reflow), `HudSpriteWidget`, `HudGeometry` (sprite / nine-slice / three-segment
  sliced fill writers), fixed-capacity `HudMeshData` with degenerate-triangle
  padding (steady-state zero GC, verified by a 500-HUD perf smoke test), and
  the pooled-widget `HudPool`.
- `HudTextWidget`: SDF text via the shared font system streamed directly into
  the slot-0 combined mesh (four-corner gradient, 4-pass outline, automatic
  atlas-growth refresh through Scribe's re-layout-on-draw).
- `HudBarWidget` + `Hud.SetBloodVisible/SetBloodPercent`: UnityHUD's default
  180×30 bar with 160×13 three-segment fill, alignment-corrected offset,
  horizontal/vertical, reverse fill.
- `HudNumberWidget` + `Hud.PopNumber`: floating damage numbers in two glyph
  modes (digit-atlas art or SDF text), curve-driven move/alpha/scale animation,
  camera-space spawn offset, expiry + pooled recycle.
- `HudTalkWidget` + `Hud.Talk`: auto-sized nine-sliced chat bubble with wrapped
  text; replacement bubbles fade out on an accelerated window.
- `HudmeshConfig` (Echo `.hudconfig` asset): stack gaps, bar/bubble layouts, and
  floating-number styles with engine-`AnimationCurve`s; round-trip tested.
  `HudmeshConfigImporter` claims `.hudconfig`; `HudManager.ChangeConfig`
  hot-swaps configs. Editor `Tools/Hudmesh/Pack Bundled Sprites` menu packs the
  sprite folder into an atlas + `.hudsprites` text index (parsed by
  `HudSpriteTable.RegisterAtlas`), plus a `Build~` compile-check project for
  the Editor assembly.
