# XEngine patches to Scribe (fonts / text layout)

Baseline for diffing: Anthology tag `v2.8.6` (commit `b3ab895d`), path `Scribe/` — see `UPSTREAM.txt`.
Retrieve any baseline file with `git show v2.8.6:Scribe/<path>`.

## Status

- Vendored at **v2.8.6** + mechanical rename (`Prowl.Scribe` → `XEngine.Scribe`, license header).
- Upstream Anthology touched Scribe in **0 commits** between v2.8.6 and v3.3.0 → nothing to sync this round.
- Referenced **transitively** via Quill (Quill → Scribe); the editor text stack renders through it.

## Patch log

Verified 2026-08-31 by per-file CRLF-normalized diff against `v2.8.6`. Three real patches,
all **additive** (upstream call sites keep compiling; defaults preserved):

### 1. SDF raster-quality bucket selection (`PickBucket`)

- **Files**: `Scribe/FontSystem.cs` (+~40 lines, `FontQualityExtensions`)
- **Motive**: Scribe bakes the distance field at the quality's em height regardless of display
  size, so text drawn far below its raster size (scaled-down canvas, small Game view / device
  preview) bilinearly downsamples the field ~3× and turns to mush.
- **Shape of change**: additive extension method `PickBucket(this FontQuality authored, float effectivePixelSize)`
  — picks the smallest quality bucket that still covers the effective pixel size, **capped by the
  authored quality** (adaptation only ever lowers; an explicit High/Ultra choice for upscaled text
  is respected).

### 2. Visible missing-glyph (tofu) fallback

- **Files**: `Scribe/FontSystem.cs` (GetOrCreateGlyph variant), new `Tests/MissingGlyphTests.cs`
- **Motive**: a codepoint no font maps used to return null and silently disappear; a visible
  `.notdef` box makes the coverage gap debuggable.
- **Shape of change**: additive resolver that returns the requested font's `.notdef` glyph
  (index 0) instead of null; **default-ignorable codepoints (ZWSP, ZWJ, variation selectors)
  still return null** — invisible by design.

### 3. Rich-text inline sprite provider

- **Files**: new `Scribe/IRichSpriteProvider.cs`; `Scribe/RichTextLayout.cs` (+~320),
  `Scribe/RichTextParser.cs` (+~140), `Scribe/RichTextLayoutSettings.cs` (+2),
  `Tests/RichTextTests.cs` (+~100), new `Samples/RaylibTest/XEngineLogo.png`
- **Motive**: rich text markup could only style glyphs; inline images/sprites (emoji, icons)
  were impossible.
- **Shape of change**: additive provider interface hooked into the rich-text parse/layout
  pipeline so markup can embed provider-supplied sprites laid out inline with text.
- **Tests**: `Tests/RichTextTests.cs` extensions + `Tests/MissingGlyphTests.cs`.

Non-patch tracked extras: `Build/Editor/Verify/*` verify artifacts, `XEngine.Scribe.sln` (rename).
