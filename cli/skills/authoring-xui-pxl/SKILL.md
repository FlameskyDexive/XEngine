---
name: authoring-xui-pxl
description: Author .pxl pixel-grid sprites for XUI — text in, Sprite out. Render to PNG with pxl_render and look at your own output before calling it done.
---

# authoring-xui-pxl

`.pxl` is a plain-text pixel grid: a palette plus one character per pixel. The `com.xengine.xui`
importer turns it into a point-filtered texture and one `Sprite` per section, with the 9-slice border
and pixels-per-unit declared in the file. Reference the result from `.xui` via `sprite=`.

**The text is the image.** You can re-read your own output row by row and fix a single pixel — which is
not true of a generated image. That is the whole reason the format exists.

**Sweet spot: UI chrome at 48×48 or smaller** — 9-slice frames, button skins, icons, badges. Not for
illustrations: a large grid is unmanageable as text, and pixel art gets worse with size anyway.

## The loop: write → render → look → revise

```bash
xengine-cli pxl-validate --project <root> --doc Assets/UI/art/button.pxl
xengine-cli pxl-render   --project <root> --doc Assets/UI/art/button.pxl --scale 12
```

Then **read the returned PNG path back as an image and actually look at it.** Validating proves the
file parses. Only looking catches "the bevel points the wrong way", and that is the mistake you will
actually make. Upscaling is nearest-neighbour, so a single wrong pixel stays visible.

Renders land in `.xengine/pxl/` unless you pass `--out`.

## Format

```
# 12x8 button skin, two states sharing one palette
ppu: 16
chars:
  K: #1b1b26
  H: #6f7ea8
  M: #3b4a72
  S: #2a3557
  D: #202a45

[normal]
border: 3,3,3,3
grid:
  .KKKKKKKKKK.
  KHHHHHHHHHHK
  KHMMMMMMMMSK
  KHMMMMMMMMSK
  KHMMMMMMMMSK
  KHMMMMMMMMSK
  KSSSSSSSSSSK
  .KKKKKKKKKK.

[pressed]
border: 3,3,3,3
grid:
  .KKKKKKKKKK.
  KSSSSSSSSSSK
  KSDDDDDDDDHK
  KSDDDDDDDDHK
  KSDDDDDDDDHK
  KSDDDDDDDDHK
  KHHHHHHHHHHK
  .KKKKKKKKKK.
```

### Directives

| Directive | Meaning |
|---|---|
| `ppu: 16` | Pixels-per-unit for the sprites. Default 100; pixel art usually wants a small value. |
| `palette: @ui` | Optional reference to a shared palette by name. |
| `chars:` | Starts the character table. Entries are **indented**, one per line: `K: #1b1b26`. |
| `border: T,R,B,L` | 9-slice border for the section, in pixels. |
| `grid:` | Starts the pixel rows. Rows are **indented**. |

### Characters

- One character per pixel. Values are `#rgb`, `#rrggbb`, `#rrggbbaa`, a CSS colour name, or
  `transparent`.
- **`.` is always transparent and reserved.** Declaring it is an error.
- Do not use `#`, `[`, `]`, or whitespace as a grid character — each would collide with the file's own
  syntax.
- A character the grid uses but the table never declares renders **transparent**, and `pxl_validate`
  reports it (`PXL-CHAR-UNDECLARED`). That is usually a typo eating part of your art.

### Sections

- `[name]` starts a section; each becomes its own `Sprite`. A file with no header has one section
  called `default`.
- All sections of a file share one texture, so a control's states stay together.
- Every row of a grid must be the same width. A ragged row reports both widths and its line number.
- A `border` must leave a stretchable middle — `border: 0,3,0,3` on a 5-wide grid is rejected, because
  it would import fine and then render as garbage at any size but its native one.

### Comments

A line whose first non-space character is `#`. **No trailing comments** — `K: night  # outline` would
make the colour name literally `night  # outline`.

## Drawing advice that actually matters

- **Bevel direction carries the state.** Light on top-left + shade on bottom-right reads as raised;
  swap them for pressed. That inversion is the whole button.
- **Outline first.** A 1px dark outline (`K`) around the shape does more for readability than any
  interior detail at this size.
- **Three or four colours is plenty** for a control: outline, highlight, fill, shade.
- **Keep the border ring uniform.** A 9-slice stretches the middle only; anything interesting in the
  border corners will be duplicated at every size.
- `.` the corners of a rounded frame — that is what makes it read as rounded.

## Using the result

From `.xui`, reference the imported sprite by asset path:

```xml
<Btn id="XBtnOk" sprite="UI/art/button.pxl" type="sliced">OK</Btn>
```

`type="sliced"` uses the declared border. Omit `sprite` to get XGUI's default panel sprite; write
`sprite="none"` for a rect that should draw nothing.

## Checklist before reporting done

1. `pxl_validate` — `valid: true`, no undeclared characters, no unresolved colour names.
2. `pxl_render` — **and read the PNG back.** Say what you saw.
3. If it is a 9-slice, confirm the border ring is uniform.
