# 2D PSD Importer

Imports Adobe Photoshop documents (`.psd`, `.psb`) with their layers intact:

- **Main asset** — the flattened composite as a `Texture2D`, exactly what Photoshop shows.
- **Sub-assets** — one `Sprite` per pixel layer, packed into a single atlas texture. Sprites are
  named by their group path (`header/title`), so a reused layer name in two groups never collides.
- Hidden layers import too, with their visibility flag intact — whether "hidden" means cut content
  or an alternate state is the consumer's decision, not the importer's.

This is an official optional package (`installByDefault: false`). Package Manager lists it as
Available until Install copies it into `Library/PackageCache`. Installing it upgrades `.psd`
handling from the built-in placeholder (dimensions and layer names only) to full pixels, and adds
`.psb` support.

## The parser

`Editor/PsdPlugin/` vendors the MIT-licensed Photoshop PSD FileType Plugin for Paint.NET (the same
parser Unity's 2D PSD Importer uses), ported to pure C# — no engine types, no native code. The
identical sources are linked into `Tools/XEngine.Ai.Host`, so a PSD is parseable **offline**,
without an editor: layer names, rects, groups, pixels.

Covers: PSD and PSB, RAW / RLE / ZIP / ZIP-prediction compression, RGB / CMYK / Lab / Grayscale /
Indexed / Bitmap / Duotone / Multichannel color modes at 8/16/32-bit depth, layer groups, unicode
layer names, layer and user masks, and the flattened composite.

## Deliberate limits

- **Text layers arrive as pixels.** The text-engine data (`TySh`) is not parsed — the same stance
  as the reference implementation. A pipeline that needs the string should carry it in the layer
  name.
- **No character-rig / prefab generation.** Unity's PSD importer can build a rigged character from
  a document; that mode depends on a 2D animation stack this package does not assume. Sprites and
  the layer tree are the contract.
- Layer **blend modes** are read and preserved in the parse, but the importer does not composite
  across layers — the flattened composite comes from the file's own composite image, exactly as
  Photoshop saved it.

## PSD → XUI: `xui_from_psd`

The offline Host tool `xui_from_psd` turns a layered document into a working `.xui` screen:

```
xengine-cli xui-from-psd --project <root> --doc Assets/Art/login.psd --screen Login --out UI/login
```

- **Naming contract** (the same one every PSD-to-UI pipeline ends up with): `btn_x=Label` →
  `<Btn>` (a group's `txt_` child becomes the inline label instead of baked pixels), `txt_x=内容` →
  `<Text>` (dominant pixel colour + layer-height font size), `input_x=placeholder` →
  `<InputField>`, `img_*` or unprefixed pixel layers → `<Image>`, unprefixed groups → `<Frame>`.
- **Positions are absolute document coordinates** (`pos="x,-y"`, top-left anchor). Repeated
  rows/columns can be promoted to VStack/HStack afterwards with `xui_patch`.
- **Nothing is silently dropped**: `#skip` layers, hidden layers, and empty layers are listed in
  the report.
- Output: `UI/<screen>/<screen>.xui` + `sprites/*.png` with sprite-mode-Single metas (GUIDs are
  stable across re-runs, so references survive a re-export).
- The generated document passes lint before anything touches disk; `--dry-run` previews the
  report without writing. Follow with `xui-scaffold` to get the typed view + controller skeleton.
