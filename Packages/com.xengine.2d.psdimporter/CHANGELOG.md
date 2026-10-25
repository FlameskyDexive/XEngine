# Changelog

## [0.1.0] - Unreleased

### Added
- The PSD parser: the MIT-licensed Paint.NET PSD plugin (the same code Unity's 2D PSD Importer
  vendors), ported from that fork's NativeArray/Jobs form back to pure C# arrays. Pure BCL on
  purpose — the identical sources are linked into `Tools/XEngine.Ai.Host`, so a document is
  parseable offline without an editor. Covers PSD and PSB, RAW/RLE/ZIP/ZIP-prediction compression,
  RGB / CMYK / Lab / Grayscale / Indexed / Bitmap / Duotone / Multichannel at 8/16/32 bit, layer
  groups (`lsct`), unicode layer names (`luni`), stable layer ids (`lyid`), layer and user masks,
  and the flattened composite.
- Contract tests in `Tools/XEngine.Ai.Host.Tests` against fixtures built byte-by-byte by
  `PsdFixtureBuilder`, so the format offsets the reader consumes are visible in the tests rather
  than frozen in binary blobs: unicode (CJK) names, document-space rects, nested group trees,
  group bounds as the union of children, exact RGBA round-trip through RAW and RLE, alpha,
  hidden-layer and opacity flags, and the zero-layer flattened-export fallback.
- `Build~/` compile-verification project, because package sources are never built by `XEngine.sln`.

### Fixed (relative to the reference implementation)
- The single-threaded decode path indexed channel data with the upstream document-space formula
  while the fork's surfaces are layer-local; any layer not anchored at the document origin read
  out of bounds. (Unity never executes that path — it is dead code kept for reference there; here
  it is the only path.)
- `DecodeMaskAlphaRow32` multiplied an already-byte-based offset by four, reading the wrong floats
  for every 32-bit mask row.

### Changed
- Dropped the placeholder dependencies on `com.xengine.2d.sprite` / `com.xengine.2d.animation`:
  the importer emits core `Texture2D` + `Sprite` assets, same as the `.pxl` importer. A future
  character-rig mode can re-add what it actually needs.

- `PsdLayerImporter` (`[ImporterFor(".psd", ".psb", Priority = 200)]`): the flattened composite
  as the main `Texture2D`, plus one `Sprite` per pixel layer packed into a single atlas
  (deterministic shelf packing, so reimports are byte-stable). Sprites are named by group path
  (`header/title`) — stable across sibling reorders, collision-free across groups. Priority 200
  beats the built-in Magick.NET placeholder; `.psb` has no other owner. Hidden layers import with
  their visibility flag intact.
- Verified against a real Photoshop file (24 layers, 4 groups — a file Pillow 12.3 cannot even
  read the layer section of): full tree with rects/opacity/visibility, and the composite decode
  compared pixel-by-pixel against Pillow's independent implementation — 0 of 186,516 pixels
  differ. Evidence: `docs/superpowers/evidence/psd/m1-real-psd-import.md`.

- Offline MCP/CLI tools `psd_inspect` (the layer tree as JSON — names, document-space rects,
  nested groups, visibility, opacity, layer ids — plus dimensions, depth, color mode, and a
  contentHash) and `psd_render` (the flattened composite, or one layer's own pixels with its
  document position, as a PNG the agent reads back). Both run without an editor; verified against
  the real Photoshop file, whose composite renders as the five UI buttons it contains.

- `xui_from_psd` (Host MCP/CLI, `xui-from-psd`): one command from a layered PSD to an assembled
  `.xui` screen plus per-layer sprite PNGs (each with a sprite-mode-Single `.meta`, GUID-stable
  across re-runs). Mapping is driven by layer names — `btn_login=Login` → `<Btn>` with an inline
  label (the group's `txt_` child stays re-typable pixels-free), `txt_title=…` → `<Text>` with
  dominant-colour and layer-height font size, `input_user=…` → `<InputField placeholder>`,
  `img_*`/plain pixel layers → `<Image>`, plain groups → `<Frame>` with document-coordinate
  rects (Y negated). Nothing is silently dropped: `#skip`, hidden, and empty layers are all in
  the report. Lint gates the write (`XuiDocumentParser` + `XuiLinter`); `--dry-run` writes
  nothing. 11 fixture tests in `Tools/XEngine.Ai.Host.Tests` (`XuiFromPsdTests`), which also
  caught a rerun crash (re-attaching an already-parented Echo settings compound).

- End-to-end acceptance (`AnimationProject`): generated demo PSD → `xui-from-psd` → import
  (`XuiImporter`, sprite bindings resolving to sub-asset GUIDs) → live `runtime_xui_open` with
  **measured** rects pixel-exact against the PSD (panel (24,86,200x26), avatar (330,162,48x48),
  button (40,24,140x36) at reference 400x240) → `xui-scaffold` view/controller → demo login
  logic compiles in the project. Evidence: `docs/superpowers/evidence/psd/m4-xui-from-psd-e2e.md`.

### Not yet included
- Text-layer content (`TySh`) parsing — text layers arrive as rasterized pixels, exactly as the
  reference implementation treats them — and Unity's character-rig/prefab generation mode.

## [0.1.0-scaffold] - 2026-08-14

- Added the first-party package scaffold, documentation, and assembly definitions for 2D PSD Importer.
- Official optional package (`installByDefault: false`); no longer auto-mounted as a built-in.