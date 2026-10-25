# XUI

`com.xengine.xui` is the AI-first UI application layer that sits on top of [`com.xengine.xgui`](../com.xengine.xgui).

Official optional XEngine package. It ships with the Editor but is **not** added to a project until you
install it from Package Manager (`Window / Package Management / Package Manager`). Installing copies this
folder into the project's `Library/PackageCache/com.xengine.xui@<version>/` and records the pin in
`Packages/manifest.json`.

## Why

XGUI already owns the widgets — `Graphic` / `Image` / `Text` / `Button` / `InputField` / `ScrollRect` /
`LayoutGroup` / `RectMask2D` / `CanvasScaler` / `SafeAreaFitter`. What it does not own is a way to *author*
a whole screen in one pass. Scenes and prefabs are Echo JSON: a single button is dozens of lines of nested
`$id` / `$type` objects, and changing one thing means replaying a whole chain of scene-write calls.

XUI adds the missing layer:

- **`.xui`** — a compact XML screen language. One file describes one screen: structure, anchors, colors,
  layer. No logic, no binding expressions.
- **`.pxl`** — a plain-text pixel-grid sprite language for UI chrome (9-slice frames, button skins, icons),
  imported as point-filtered Sprites.
- **Runtime assembly** — the document is parsed into an IR and laid out as a real XGUI hierarchy. Saving a
  `.xui` in the editor rebuilds the open screen in place.
- **Logic / render separation** — the `id`s in the document become a generated strongly-typed view binding;
  business code subclasses `XuiController<TView>` and only ever touches `View.BtnLogin`, never a GameObject.
- **App framework** — four window layers (Normal / Fixed / PopUp / Other), a router with deep links, awaitable
  modal dialogs, toasts, and offset-based transitions.

## Offline validation

`Runtime/Core` (IR, parser, lint, pxl) is deliberately **pure BCL** — no `XEngine.Runtime` types. Those
sources are linked into `Tools/XEngine.Ai.Host`, so an agent can validate, inspect, and patch a document
with no editor running:

```bash
xengine-cli xui-validate --project <root> --doc Assets/UI/login/Login.xui
```

Do not introduce an engine dependency into `Runtime/Core` — it breaks offline validation on the spot.

## Docs

Full docs: `Documentation~/index.md`. Authoring guidance for agents lives in the repo's `cli/skills/`
(`authoring-xui-xml`, `authoring-xui-pxl`, `scripting-xui-csharp`, `run-xui-live`).
