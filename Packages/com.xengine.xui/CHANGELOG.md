# Changelog

All notable changes to `com.xengine.xui` are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses SemVer.

## [0.1.0] - Unreleased

### Added
- Optional-package manifest (`installByDefault: false`, depends on `com.xengine.xgui`), Runtime /
  Editor / Tests assembly definitions, documentation and samples folders.
- `.xui` document language v1: `<XUI version="1">` with `<Import>` / `<Theme>` / `<Screen>` /
  `<Template>` at the top level, and twelve element tags — `Frame`, `Image`, `RawImage`, `Text`,
  `VStack`, `HStack`, `Grid`, `Btn`, `Toggle`, `Slider`, `InputField`, `ScrollList`.
- Parser and IR under `Runtime/Core` (pure BCL, linked into `Tools/XEngine.Ai.Host`): every error
  carries a line and column, ids are validated as C# identifiers and must be unique, and the tag
  catalog fixes each tag's `id` prefix and generated binding type.
- Template expansion with `<Param>` interpolation (`{name}`, `{{` escapes), `<Slot/>` for the usage
  site's children, import aliases (`alias.TemplateName`), and a recursion guard.
- Runtime assembly: `XuiScreenBuilder` lays a screen out as a live XGUI hierarchy — Canvas and
  `CanvasScaler`, anchors and size deltas, theme-token colours, and the same sub-hierarchies and
  wiring the XGUI package's own control templates produce for Slider, InputField, and ScrollList.
- `[ImporterFor(".xui")]` producing `XuiDocumentAsset`, which keeps the document text and the art
  keys the importer resolved to GUIDs (registered as build dependencies).
- `Build~/` compile-verification projects, because package sources are otherwise never built by
  `XEngine.sln`.
- Lint rules under `Runtime/Core/Lint` with stable codes (`XUI-TAG-UNKNOWN`, `XUI-ID-PREFIX`,
  `XUI-ID-DUPLICATE`, `XUI-ID-MISSING`, `XUI-ATTR-UNKNOWN`, `XUI-ATTR-VALUE`, `XUI-LAYOUT-CHILD-RECT`,
  `XUI-COLOR-TOKEN`, `XUI-THEME-BASE`, `XUI-CHILDREN-NOT-ALLOWED`, `XUI-TEXT-NOT-ALLOWED`,
  `XUI-SCREEN-EMPTY`, `XUI-TEMPLATE-UNUSED`), each carrying a line and column.
- Offline MCP/CLI tools `xui_validate`, `xui_inspect`, `xui_patch`, plus the `authoring-xui-xml`
  agent skill.
- Logic / render separation: `XuiView` (generated typed binding), `XuiController<TView>` with the
  `OnInitCoreData` → `OnBuildView` → `OnRegisterEvents` → `OnShow` / `OnHide` → `BeforeUnload`
  lifecycle, `XuiRoot` with the four layer roots and their sort-order bands, and `Xui.Show` / `Hide` /
  `Close` / `Find` / `Reload` keyed by controller type rather than by string.
- `xui_scaffold` generating `{Name}View.g.cs` (derived, always rewritten) and `{Name}.cs` (written
  once, never overwritten), plus the `scripting-xui-csharp` agent skill.
- In-process editor tools `runtime_xui_open` / `state` / `tree` / `reload` / `close`, registered
  through the editor's MCP assembly scan with no editor-side change, plus the `run-xui-live` skill.
  `runtime_xui_tree` reports measured rects at a caller-chosen resolution, forcing a layout pass, so
  geometry is checkable headless.
- `Xui.ShowDocument` opens a screen with no controller — the editor and agent path for looking at a
  document before any C# exists for it.
- Composite tags `SafeArea`, `Show`, `Progress`, `TabBar`, `Tab`, with lint rules for the new
  attributes plus `XUI-TAB-BIND` (a tab binding nothing) and `XUI-TAB-ORPHAN` (a tab outside a bar).
  `Show` gives a controller a sanctioned way to toggle a subtree without touching a GameObject;
  `Progress` is its own component rather than a disabled `Slider`, which would leave focus navigation
  landing on something the player can never operate.
- Application framework: `XuiRouter` with deep links (`xui://Screen?a=1`), `Normal`-layer exclusivity,
  and a back history that records content screens only; awaitable queued modals (`Xui.MessageBox` /
  `Xui.Confirm`); self-expiring toasts; and a reference-counted loading overlay. The overlays are
  ordinary `.xui` documents, so a project restyles them by registering its own under the same screen
  name, and there is no second UI-construction path to maintain.
- `XuiLink` / `XuiRouteQuery` in `Runtime/Core` — a link is text, so its grammar is testable without an
  engine and the runtime cannot disagree with the offline tools about what a valid link is.
- `.pxl` pixel-grid sprite language: parser, rasterizer, and `[ImporterFor(".pxl")]` producing a
  point-filtered texture plus one `Sprite` per section carrying its 9-slice border and pixels-per-unit.
  Offline `pxl_validate` / `pxl_render` (nearest-neighbour upscale, so a single wrong pixel stays
  visible) and the `authoring-xui-pxl` skill.

### Fixed
- An authored `size` was lost inside a layout container whenever the element reported its own
  preferred size. A `<Text size="_x48">` in a `VStack` came out 48px tall in the document and 0px on
  screen. The assembler now publishes the authored size through `XuiSizeOverride`.
- `_` on an axis left a top-level container at the RectTransform's arbitrary 100px default, so
  `size="480x_"` on a panel was 480x100 regardless of its contents. It now sizes to content when
  nothing above it decides that axis.
- Every interactive control rendered white. `Selectable` tints its target graphic and its normal
  state defaults to white, repainting over the authored colour as soon as the control is added. The
  interaction states are now derived from the document's colour, and the graphic is painted after the
  control exists.
- An `Image` with no sprite has no texture to sample and draws nothing, so a panel authored with only
  a colour was invisible. The assembler assigns XGUI's default sprite, as XGUI's own creation menu
  does; `sprite="none"` opts out.

- `<Anim>`: offset, scale, and alpha transitions authored on any element. Every channel is a
  `from:to` pair (`move="0,-120:0,0"`, `fade="0:1"`, `scale="0.9:1"`) with an empty side meaning the
  element's rest state, five easing curves, `duration` / `delay`, and named triggers. `show` and `hide`
  fire with the screen; anything else is played by a controller through `Xui.PlayTrigger`. Movement is
  a relative offset written on top of whatever layout decided, so a transition behaves the same on a
  free element and inside a `VStack`, and one ticker per screen drives every animator under it.
  Verified live: the authored start state is applied the instant a trigger fires (no one-frame flash at
  the destination), an entrance settles on exactly `(0,0)` and alpha `1`, `out-back` overshoots to 1.04
  mid-flight and lands on 1, a `delay` holds a stack child at its start offset, and measured rects move
  by exactly the authored amount on both code paths — `AnchoredPosition` for a free element, the
  container-written `ComputedRect` for a stack child.

- A third sample, **Settings Flow**: tabs with bound panels, entry and exit transitions, a controller
  that never touches a GameObject, a modal, a loading overlay, a toast, and translation including one
  label deliberately left out of the catalog. Verified by scaffolding it and compiling the result.
- Translation, keyed by the source string. `.po` catalogs (`Runtime/Core/I18n`, pure BCL, so the
  offline Host and the runtime share one parser), `Xui.Tr` / `Xui.Locale` / `Xui.LocaleChanged`, and a
  `[ImporterFor(".po")]` that registers a catalog as it imports. A screen reads correctly with no
  catalog at all, so an untranslated build ships and a missing entry is cosmetic rather than a screen
  of `MENU_TITLE_1`.
- `xui_i18n_extract` (`xengine-cli xui-i18n-extract`): every translatable string in the documents plus
  every `Xui.Tr("…")` in C#, merged into one `.po` per locale. The merge is never destructive —
  existing translations are kept, strings no source mentions any more are marked obsolete rather than
  deleted, and a string that comes back is revived with its translation intact — so it is safe on every
  build. Verified: a second run over unchanged sources produces a byte-identical file.
- `translate="false"`, inherited by the subtree, keeps a debug panel or a product name out of the
  catalog; a child may set `translate="true"` for the one label that is prose after all.
- Locale switching re-translates open screens **in place** rather than rebuilding them, so scroll
  position, focus, and anything a controller wrote after the build survive. Verified live: a label
  written by code was still there after the switch, while all five authored strings changed.
- Per-locale fonts: a font named after the locale beside its catalog (`Locales/zh-Hans.ttf` next to
  `Locales/zh-Hans.po`) is bound automatically, or `Xui.RegisterLocaleFont` from code.
- `runtime_xui_locale`: loads every catalog in the project and switches the live locale, reporting how
  many strings were actually written back into open screens.

### Changed
- A screen with a `hide` transition now stays on screen until the exit has finished playing. Disabling
  the root the moment the trigger fired meant a correct exit that never rendered a single frame.
- `<Anim>` children do not make their parent a container: `<Btn><Anim …/>Go</Btn>` keeps its inline
  text. A behaviour builds no node, so treating it as a child element would have banned the natural way
  to give a button an entrance.
- Duplicate ids are a lint rule rather than a parse error. Stopping the parser at the first duplicate
  hid every other problem in the file, which is exactly the guess-compile loop lint exists to prevent.
- Removed `type="cover"` from the language: XGUI has no crop-to-fill path, and a silently-wrong stretch
  is worse than a lint error.

### Not yet included
- **A bake step.** `xui_bake` was implemented, measured, and removed: instantiating a baked `.prefab`
  costs **48,228 µs** against **367 µs** to assemble the same screen from its document — a 132×
  pessimization, because prefab instantiation deserializes an Echo graph while the assembler walks an
  in-memory IR. Parsing was never the cost either: 48 µs of a 435 µs cold build, 11%, which was the
  most the feature could ever have saved. Numbers and method in
  `docs/superpowers/evidence/xui/m11-performance-and-build.md`. Keeping code the documentation tells
  you never to use would be worse than not having it.
- Plural forms in `.po`. `msgid_plural` / `msgstr[n]` are rejected with a clear error rather than
  half-read: silently keeping the singular would be a wrong translation in every language with plural
  rules, and nothing downstream would report it.
- Glyph-level font fallback. A locale swaps its font wholesale, because XGUI's text has Bold / Italic /
  Mono slots but no fallback chain by character range.
- `hover` and `press` triggers. `Selectable` owns those states and raises no event when they change, so
  a document cannot hook them without reaching into XGUI; the control's own colour tint already covers
  press feedback. `show` / `hide` plus controller-played named triggers ship instead.
- `Carousel`, `Markdown`, and `Icon`. `Markdown` needs a Markdown parser XEngine does not have, and
  `Icon` needs a sprite-set layer that does not exist yet — shipping either as a stub would be worse
  than not shipping it. `Carousel` needs drag/snap interaction that cannot be verified headless.

### Verified
- A real desktop Player build of a project with the package installed ships `XEngine.XUI.Runtime.dll`
  and **no** `*Editor*` assembly at all — the importers, the `runtime_xui_*` tools, and the scaffolding
  cost a shipped game nothing. `XEngine.XUI.Editor` declares `includePlatforms: ["Editor"]`, and the
  build's assembly collection drops any assembly not included for the target platform.
- A screen of 22 GameObjects and 74 components costs 0.44 ms to parse and assemble from cold, on a
  Debug build. Opening a screen is not a frame-budget problem; the number that scales is document size,
  which is why a long list belongs in a separate document per row rather than inlined.

### Notes
- XUI ships its own layout containers (`XuiStack`, `XuiGrid`, `XuiContentFitter`) rather than reusing
  XGUI's `LayoutGroup` family. Seven of those type names are declared identically in both
  `XEngine.Runtime` and `XEngine.XGUI.Runtime`, so an assembly referencing both — which every UI
  package does — cannot name them without `CS0433`. Owning them also pins the layout semantics a
  document describes, so the same `.xui` lays out the same way across XGUI revisions.
- `canvas="camera"` builds a screen-space Canvas; `GameCanvas` has no camera render mode, so the
  application assigns `RenderCamera` itself.
- **Known engine-side defect, not a package one:** an `Image` neighbouring a `Text` can render solid
  white. Exactly one Image per Text draws with the wrong texture and which one depends on draw order;
  the component's `Color` is correct when read back. Reproduces with no animation and no XUI-specific
  feature involved — repro document and screenshot in
  `docs/superpowers/evidence/xui/repro-xgui-text-neighbour-white.*`.
- **Known engine-side defect:** CJK text renders blank even with a font that contains the glyphs.
  Verified live that the string (`登录`) and the font (`FamilyName=simhei`) are both correct in memory
  and nothing draws, while the same font with English strings renders normally — so font substitution
  works and glyph rasterization is the gap. Evidence in
  `docs/superpowers/evidence/xui/m10-i18n-*.png`. Check a translation by reading the text back, not by
  looking at a screenshot.
- The GameView renders a screen only in play mode. Outside it, a screenshot shows the scene with no UI
  at all, which is easy to misread as a screen that failed to build — check `runtime_xui_tree` first.
