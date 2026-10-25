---
name: authoring-xui-xml
description: Write or edit a .xui screen document (XML UI for XEngine, assembled into XGUI at runtime). Validate offline with xui_validate — no editor needed.
---

# authoring-xui-xml

Use when a screen needs building or changing in `com.xengine.xui`. A `.xui` file is **pure structure**:
tags, ids, anchors, colours. No logic, no bindings, no expressions — those live in C#, reached through
the `id`s declared here.

Requires the optional package: `package_add --name com.xengine.xui --specifier 0.1.0`.

## Validate after every write — this is not optional

```bash
xengine-cli xui-validate --project <root> --doc Assets/UI/login/Login.xui
```

Or MCP `xui_validate`. It needs **no running editor**, returns every finding at once with line, column,
and a stable rule code, and takes milliseconds. `valid: false` means the screen will not do what the
document says. Fix and re-run before reporting the work done.

`--errors-only` (MCP: `includeWarnings: false`) hides warnings but still counts them.

## Document shape

Root is always `<XUI version="1">`. Only four elements are allowed under it:

| Element | Notes |
|---|---|
| `<Import src="common/Buttons" as="ui"/>` | Imported templates are used as `ui.TemplateName` — a **dot**, not a colon (a colon is an XML namespace prefix and would need an `xmlns`). |
| `<Theme name="dark" base="light">` | Contains `<Color name="panel" value="#1E2433"/>` only. A token's value must be a literal; a token cannot point at another token. |
| `<Screen name="Login">` | One screen, one Canvas. `name` is also the controller type name. |
| `<Template name="Tile">` | Exactly one root element in the body. |

```xml
<?xml version="1.0" encoding="utf-8"?>
<XUI version="1">
  <Theme name="dark">
    <Color name="panel" value="#1E2433"/>
    <Color name="accent" value="#4C8DFF"/>
  </Theme>

  <Screen name="Login" reference="1920x1080" layer="Normal">
    <Image id="XImgScrim" anchor="stretch" color="#000000AA"/>
    <VStack id="XVsPanel" anchor="center" size="480x_" color="panel" spacing="12" padding="24">
      <Text id="XTxtTitle" size="_x48" font-size="32" align="center">Sign in</Text>
      <InputField id="XInputAccount" size="_x44" placeholder="Account"/>
      <Btn id="XBtnLogin" size="_x44" color="accent">Start</Btn>
    </VStack>
  </Screen>
</XUI>
```

Screen attributes: `reference="1920x1080"` (both axes required), `layer` = `Normal`|`Fixed`|`PopUp`|`Other`,
`scale-mode` = `auto`|`pixel` (pixel art), `canvas` = `overlay`|`camera`|`world`.

## The tags, and the id prefix each one requires

| Tag | id prefix | Bound as | Takes children |
|---|---|---|---|
| `Frame` | `XRect` | `RectTransform` | yes |
| `Image` | `XImg` | `Image` | yes |
| `RawImage` | `XRimg` | `RawImage` | yes |
| `Text` | `XTxt` | `Text` | no (inline text) |
| `VStack` | `XVs` | `XuiStack` | yes |
| `HStack` | `XHs` | `XuiStack` | yes |
| `Grid` | `XGrid` | `XuiGrid` | yes |
| `Btn` | `XBtn` | `Button` | yes (+ inline label) |
| `Toggle` | `XTog` | `Toggle` | yes (+ inline label) |
| `Slider` | `XSlider` | `Slider` | **no** |
| `InputField` | `XInput` | `InputField` | **no** |
| `ScrollList` | `XSv` | `ScrollRect` | yes (children are rows) |
| `SafeArea` | `XSafe` | `SafeAreaFitter` | yes |
| `Show` | `XShow` | `XuiShowGroup` | yes |
| `Progress` | `XProg` | `XuiProgress` | **no** |
| `TabBar` | `XTabs` | `XuiTabBar` | yes (`Tab` only) |
| `Tab` | `XTab` | `XuiTab` | yes (+ inline label) |
| `Anim` | `XAnim` | *(no binding)* | **no** |

Every tag also accepts `translate="false"`, which keeps its text — and its whole subtree's — out of
the translation catalog. See **Translation** below.

**The prefix is enforced.** `id="XImgGo"` on a `<Btn>` is an error (`XUI-ID-PREFIX`), because the id is
what makes the generated binding's type derivable: `XBtnLogin` → field `_btnLogin` → property
`BtnLogin` → business code writes `View.BtnLogin`. Ids must be unique within a screen.

`Slider` and `InputField` build their own sub-hierarchy; children under them are discarded, so putting
any there is an error.

## Attributes

Every tag accepts: `id` `anchor` `size` `pos` `pivot` `scale` `hidden`.

| Attribute | Grammar |
|---|---|
| `anchor` | `center` `top` `bottom` `left` `right` `top-left` `top-right` `bottom-left` `bottom-right` `stretch` `top-stretch` `bottom-stretch` `left-stretch` `right-stretch` |
| `size` | `WxH`, either axis may be `_`: `480x360`, `400x_`, `_x44`. A **bare number is invalid** — it must not silently mean "square". |
| `pos` `pivot` `scale` | `x,y` (+Y is up) |
| `padding` | `24` or `T,R,B,L` |
| `color` `text-color` `fill-color` | `#rgb` `#rrggbb` `#rrggbbaa`, a CSS colour name, or a theme token |
| booleans | `true` / `false` only — not `1`, not `yes` |

Per-tag extras:

- `Image` — `sprite` `type` (`simple`/`sliced`/`tiled`/`filled`/`contain`) `raycast` `mask="rect"`
- `Text` — `font` `font-size` `align` (`left`/`center`/`right`) `rich`
- `VStack` / `HStack` — `spacing` `align` `stretch-children` `expand`
- `Grid` — `cell` `columns` `spacing`
- `Btn` / `Toggle` — `text-color` `interactable`, plus `on` for Toggle
- `Slider` — `min` `max` `value` `whole-numbers` `fill-color`
- `InputField` — `placeholder` `text` `character-limit` `password` `readonly`
- `ScrollList` — `spacing` `horizontal` `vertical`
- `SafeArea` — `conform-x` `conform-y`. Always fills its parent, then insets to the device safe area
- `Show` — `visible`. The sanctioned way to toggle a subtree: `View.ShowEmpty.Visible = true`, so a
  controller still never touches a GameObject
- `Progress` — `value` (0..1, clamped) `fill-color` `direction`
  (`left-to-right`/`right-to-left`/`bottom-to-top`/`top-to-bottom`). Read-only, and deliberately not a
  disabled `Slider` — that would put focus navigation on something the player can never operate
- `TabBar` — `spacing` `padding` `align` `selected` `expand`
- `Tab` — `bind` (the id of the subtree it reveals) `text-color` `interactable`
- `Anim` — `on` `move` `scale` `fade` `duration` `delay` `ease`. See **Transitions** below

An attribute a tag does not accept is an **error**, not a silent drop — a dropped attribute produces a
screen that reads right in the source and is wrong on screen.

## Tabs

```xml
<TabBar id="XTabsMain" size="_x44" spacing="8" selected="0">
  <Tab id="XTabAudio" size="_x44" bind="XShowAudio">Audio</Tab>
  <Tab id="XTabVideo" size="_x44" bind="XShowVideo">Video</Tab>
</TabBar>

<Show id="XShowAudio">…</Show>
<Show id="XShowVideo">…</Show>
```

A tab names the subtree it reveals with `bind`, in the document — a tab and its panel are one
authoring decision, and splitting them across two files is how a tab ends up showing the wrong panel
after someone reorders the bar. The panel may be declared **after** the bar.

Exclusivity is the bar's job: selecting one tab hides the others' panels. `bind` naming no element is
an error (`XUI-TAB-BIND`), and a `Tab` outside a `TabBar` is an error (`XUI-TAB-ORPHAN`).

## Transitions

```xml
<Image id="XImgCard" anchor="center" size="400x200" color="accent">
  <Anim on="show" move="0,-120:0,0" fade="0:1" duration="0.25" ease="out-quad"/>
  <Anim on="hide" move=":0,-120" fade=":0" duration="0.15"/>
  <Anim on="pop"  scale="0.5:1" duration="0.3" ease="out-back"/>
</Image>
```

An `<Anim>` animates **the element it sits inside**. It builds no node of its own, so several may share
one element and the element's layout is unaffected by how many transitions it has. It produces no
binding — the animator lives on the parent, and binding the `Anim`'s own id would name nothing.

**Every channel is a `from:to` pair.** Either side may be left empty to mean the element's rest state:
no offset, no scaling, fully opaque. So `move="0,-120:"` enters from below and `move=":0,-120"` leaves
downward, with no second attribute and no rule to remember about which direction a trigger implies.

| Attribute | Grammar | Rest |
|---|---|---|
| `move` | `x,y:x,y` — offsets in canvas pixels, relative to wherever layout put the element | `0,0` |
| `scale` | `sx,sy:sx,sy`, or one number per side for both axes (`0.9:1`) | `1,1` |
| `fade` | `a:a`, 0..1 — alpha of the whole subtree, via a `CanvasGroup` | `1` |
| `duration` / `delay` | seconds | `0.15` / `0` |
| `ease` | `linear` `out-quad` `in-quad` `in-out-quad` `out-back` | `out-quad` |

**A single point with no colon is an error.** `move="0,-120"` reads as "move to here", which would
animate the wrong way and still look plausible in a still frame — the one class of mistake a screenshot
cannot catch.

`show` and `hide` fire by themselves as a screen is shown and hidden; a screen with a `hide` transition
stays on screen until its exit has finished playing. Any other trigger name is a controller's to play:

```csharp
Xui.PlayTrigger("Settings", "pop");
```

Movement is always a **relative offset**, never a position, so a transition composes with any anchor and
works the same inside a `VStack` as on a free element. `<Anim>` at the top of a screen is an error
(`XUI-ANIM-ORPHAN`) — there is no element there to animate — and one that sets neither `move`, `scale`,
nor `fade` is a warning (`XUI-ANIM-EMPTY`), because it plays and changes nothing.

## Translation

Authored text is translatable by default and **the source string is the key**:

```xml
<Text id="XTxtTitle">Sign in</Text>
<InputField id="XInputAccount" placeholder="Account"/>
<VStack id="XVsStats" translate="false">
  <Text id="XTxtFps">FPS</Text>          <!-- diagnostics, not prose -->
</VStack>
```

That means a screen reads correctly with no catalog at all — an untranslated build ships, and a missing
entry is a cosmetic problem rather than a screen of `MENU_TITLE_1`.

What is extracted: **inline text** on the tags that display it, and `placeholder` / `text` on
`InputField`. Nothing else — ids, colours, sprite paths, and font sizes are not prose and must never
reach a translator. `translate="false"` is inherited by the subtree, and a child may set
`translate="true"` for the one label inside a debug panel that really is prose.

Template text is extracted **after expansion**, so `placeholder="{hint}"` in a template body is not a
string but the `hint="Account"` at the usage site is.

```bash
xengine-cli xui-i18n-extract --project <root> --locales zh-Hans,ja --out UI/Locales
```

Extraction **merges**: existing translations are kept, strings no source mentions any more are marked
obsolete rather than deleted, and a string that comes back is revived with its translation. Safe to run
on every build.

## `size` on a stretched axis means an inset

On a pinned axis `size` is the width or height. On a **stretched** axis it is an inset delta:
`anchor="stretch" size="-16x-16"` insets 8px on every edge. `_` leaves whatever the anchor implies.

## Do not put `anchor` or `pos` on a layout container's child

`VStack` / `HStack` / `Grid` write their children's rects every rebuild, so `anchor` and `pos` on a
direct child do nothing (`XUI-LAYOUT-CHILD-RECT`). Use `size`, `spacing`, `padding`, `align` instead.

## Templates: parameterise the ids

```xml
<Template name="Tile">
  <Param name="rootId"/>
  <Param name="label" default="Untitled"/>
  <VStack id="{rootId}" spacing="4">
    <Text id="{rootId}Label">{label}</Text>
    <Slot/>
  </VStack>
</Template>

<Screen name="Home">
  <Tile rootId="XVsLeft" label="Left"><Btn id="XBtnGo">Go</Btn></Tile>
  <Tile rootId="XVsRight"/>
</Screen>
```

- `{name}` interpolates anywhere, **including in `id`**. Using a template twice with literal inner ids
  collides (`XUI-ID-DUPLICATE`); parameterising them is how one template serves two usages.
- `{{` / `}}` are literal braces. `<Slot/>` receives the usage site's children.
- Passing an attribute the template does not declare as a `<Param>` is an error.

## Reuse and lists

- Shared chrome (currency bar, title row) → a `<Template>`, imported where needed.
- A dynamic list → a **separate document per row type**, referenced by `<ScrollList item="Item_Role"/>`.
  Never inline N rows in the parent screen.

## Editing an existing document

```bash
xengine-cli xui-inspect --project <root> --doc Assets/UI/login/Login.xui
```

Note the `contentHash`, then:

```bash
xengine-cli xui-patch --project <root> --doc Assets/UI/login/Login.xui --patches '[{"path":"XBtnLogin/@color","value":"accent"}]' --expected-before-hash <hash> --idempotency-key <key> --intent "use the accent token for the primary action"
```

Patch paths: `<id>/@<attribute>`, `<id>/#text`, `screen:<Name>/@<attribute>`. `--remove` takes the same
paths (comma-separated) for clearing a dead attribute. Patches splice into the source text, so
indentation and comments survive and the diff shows only what changed. Ids produced by **template
expansion** are not patchable — edit the template. A patch that would make the document invalid is
rejected rather than written.

`xui_inspect` also reports, per element, the `bindingType` and `bindingProperty` the generated view
binding will use — read it before writing C# against a screen.

## Rule codes

`XUI-PARSE` · `XUI-TAG-UNKNOWN` · `XUI-ID-PREFIX` · `XUI-ID-DUPLICATE` · `XUI-ID-MISSING` ·
`XUI-ATTR-UNKNOWN` · `XUI-ATTR-VALUE` · `XUI-LAYOUT-CHILD-RECT` · `XUI-COLOR-TOKEN` · `XUI-THEME-BASE` ·
`XUI-CHILDREN-NOT-ALLOWED` · `XUI-TEXT-NOT-ALLOWED` · `XUI-SCREEN-EMPTY` · `XUI-TEMPLATE-UNUSED` ·
`XUI-TAB-BIND` · `XUI-TAB-ORPHAN` · `XUI-ANIM-ORPHAN` · `XUI-ANIM-EMPTY`

## Output requirements

State the document path, whether `xui_validate` returned `valid: true`, and the counts of errors and
warnings. If warnings remain, say which and why they are acceptable.
