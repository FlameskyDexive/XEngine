# The `.xui` document language

A `.xui` file describes screens. It carries structure, layout, and colour — and nothing else. There are
no event handlers, no bindings, no expressions: everything behavioural lives in C#, reached through the
`id`s declared here.

```xml
<?xml version="1.0" encoding="utf-8"?>
<XUI version="1">
  <Theme name="dark">
    <Color name="panel" value="#1E2433"/>
    <Color name="accent" value="#4C8DFF"/>
  </Theme>

  <Screen name="Login" reference="1920x1080">
    <Image id="XImgMask" anchor="stretch" color="#000000AA"/>
    <VStack id="XVsPanel" anchor="center" size="480x360" color="panel" spacing="12" padding="24">
      <Text  id="XTxtTitle" size="_x48" font-size="32" align="center">Sign in</Text>
      <InputField id="XInputAccount" size="400x44" placeholder="Account"/>
      <Btn   id="XBtnLogin" size="400x44" color="accent">Start</Btn>
    </VStack>
  </Screen>
</XUI>
```

## Document shape

The root is always `<XUI version="1">`. Only four elements are allowed beneath it.

| Element | Purpose |
|---|---|
| `<Import src="common/Buttons" as="ui"/>` | Pull another document's templates in. With `as`, they are referenced as `ui.TemplateName` — a dot, not a colon, because a colon in an XML tag is a namespace prefix and would need an `xmlns` you never wrote. |
| `<Theme name="dark" base="light">` | A named colour palette. Children are `<Color name="panel" value="#1E2433"/>` only. `base` inherits, then this theme overrides. A token's value must be a literal — a token cannot point at another token. |
| `<Screen name="Login">` | One screen, one Canvas, opened by name. `name` is also the controller type name. |
| `<Template name="TitledPanel">` | A reusable subtree. Exactly one root element in the body. |

A document that declares neither a `<Screen>` nor a `<Template>` is rejected: nothing in it could ever
be opened or reused.

## Screen attributes

| Attribute | Values | Meaning |
|---|---|---|
| `name` | identifier | Required. Unique across loaded documents. |
| `reference` | `1920x1080` | Design resolution. Both axes required. Omit for an unscaled Canvas. |
| `scale-mode` | `auto` (default) / `pixel` | `pixel` snaps to whole framebuffer pixels — use it for pixel art. |
| `layer` | `Normal` (default) / `Fixed` / `PopUp` / `Other` | Which root the screen is parented to, and what the router may close over it. |
| `canvas` | `overlay` (default) / `camera` / `world` | `camera` builds a screen-space Canvas and expects the application to assign `RenderCamera`. |

## Elements

Twelve tags. Each maps onto components that already ship in `com.xengine.xgui`.

| Tag | Builds | `id` prefix | Bound as |
|---|---|---|---|
| `Frame` | RectTransform container | `XRect` | `RectTransform` |
| `Image` | Image | `XImg` | `Image` |
| `RawImage` | RawImage | `XRimg` | `RawImage` |
| `Text` | Text | `XTxt` | `Text` |
| `VStack` | vertical container | `XVs` | `XuiStack` |
| `HStack` | horizontal container | `XHs` | `XuiStack` |
| `Grid` | fixed-cell grid | `XGrid` | `XuiGrid` |
| `Btn` | Image + Button (+ label) | `XBtn` | `Button` |
| `Toggle` | Image + Toggle (+ label) | `XTog` | `Toggle` |
| `Slider` | background + fill + handle | `XSlider` | `Slider` |
| `InputField` | background + text area + placeholder + caret | `XInput` | `InputField` |
| `ScrollList` | ScrollRect + viewport + content | `XSv` | `ScrollRect` |
| `SafeArea` | device safe-area inset | `XSafe` | `SafeAreaFitter` |
| `Show` | toggleable subtree | `XShow` | `XuiShowGroup` |
| `Progress` | read-only fill bar | `XProg` | `XuiProgress` |
| `TabBar` / `Tab` | exclusive tab strip | `XTabs` / `XTab` | `XuiTabBar` / `XuiTab` |
| `Anim` | a transition on its parent | `XAnim` | *(none)* |

`Slider`, `InputField`, and `ScrollList` build the same sub-hierarchies and wiring as the XGUI package's
own control templates, so a document-built control behaves identically to a menu-created one.

Inline text on `Btn` and `Toggle` becomes a stretched label child, bound as `{id}Label`.
`ScrollList` children are rows: they are parented under Content, not under the ScrollRect.

## Naming: why the prefixes

An `id` is a C# property name in waiting. `XBtnLogin` becomes field `_btnLogin`, property `BtnLogin`,
and business code reads `View.BtnLogin`. The prefix encodes the component, so the generated binding's
type is derivable from the document alone — and a mismatch between the two is mechanically detectable
rather than a null nobody can trace.

Short ids are completed for you: `id="Confirm"` on a `<Btn>` is written back as `XBtnConfirm`.

Ids must be unique within a screen, and must be plain identifiers.

## Common attributes

| Attribute | Grammar | Notes |
|---|---|---|
| `anchor` | `center` `top` `bottom` `left` `right` `top-left` `top-right` `bottom-left` `bottom-right` `stretch` `top-stretch` `bottom-stretch` `left-stretch` `right-stretch` | Fixes both anchor corners and the pivot. |
| `size` | `480x360`, `400x_`, `_x44` | `_` leaves that axis to the layout. On a **pinned** axis this is width/height; on a **stretched** axis it is an inset delta, so `anchor="stretch" size="-16x-16"` insets 8px per edge. A bare number is not valid — a typo must not silently mean "square". |
| `pos` | `x,y` | Anchored position. +Y is up. |
| `pivot` / `scale` | `x,y` | |
| `color` | `#rgb` `#rrggbb` `#rrggbbaa`, a CSS colour name, or a theme token | Anything that is not a literal is treated as a token. |
| `padding` | `24` or `T,R,B,L` | Layout containers. |
| `spacing` | number (`Grid`: `x,y`) | |
| `mask` | `rect` | Clips children. |
| `hidden` | `true` / `false` | Starts disabled. |
| `sprite` | asset path | Resolved at import against `Assets/`, then the document's own folder. |
| `font` / `font-size` / `align` / `rich` | | `align` is `left` / `center` / `right`. |

Booleans are `true` / `false` only. `1` and `yes` are rejected so the lint rules stay unambiguous.

## Transitions

```xml
<Image id="XImgCard" anchor="center" size="400x200" color="accent">
  <Anim on="show" move="0,-120:0,0" fade="0:1" duration="0.25" ease="out-quad"/>
  <Anim on="hide" move=":0,-120" fade=":0" duration="0.15"/>
</Image>
```

`<Anim>` is the one tag that is not an element: it attaches a transition to the element it sits inside
and builds no node, so several may share one element without affecting its layout, and it produces no
view binding.

Each channel is a `from:to` pair — `move` (pixel offsets), `scale`, `fade` (subtree alpha) — plus
`duration`, `delay`, and `ease` (`linear` / `out-quad` / `in-quad` / `in-out-quad` / `out-back`).
An empty side means the element's rest state: no offset, no scaling, opaque. A lone point without the
colon is rejected, because "move to here" and "move from here" look identical in the source and produce
opposite motion on screen.

Movement is a **relative offset**, never a position. A layout container rewrites its children's rects
on every rebuild, so an animation that wrote a position would either be overwritten or fight the
container; an offset composes with any anchor and behaves identically inside a stack and outside one.

`show` and `hide` fire as the screen is shown and hidden — a screen with an exit stays up until the exit
has played out — and any other trigger name is played by a controller through `Xui.PlayTrigger`.

## Templates

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
  <Tile rootId="XVsLeft" label="Left">
    <Btn id="XBtnGo">Go</Btn>
  </Tile>
  <Tile rootId="XVsRight"/>
</Screen>
```

- `{name}` interpolates a parameter anywhere in an attribute value or inline text, **including `id`**.
  That is how one template is used twice on a screen: you rename the inner ids yourself instead of the
  expander inventing names behind your back. Two usages that both leave the inner ids literal will
  collide, and you will be told where.
- `{{` and `}}` are literal braces.
- `<Slot/>` is replaced by the usage site's children.
- Passing an attribute the template does not declare as a `<Param>` is an error, not a silent drop — a
  dropped attribute produces a screen that reads correctly in the source and is wrong on screen.

## Reuse and lists

- Shared chrome (a currency bar, a title row) → a `<Template>`, imported where needed.
- A dynamic list → a separate document per row type, referenced by `<ScrollList item="Item_Role"/>`.
  Do **not** inline N rows in the parent screen.

## Validation

Every write should be validated before it is called done. The parser reports one structural error with
a line and column; lint reports every semantic problem at once.

```bash
xengine-cli xui-validate --project <root> --doc Assets/UI/login/Login.xui
```
