# XUI — documentation

`com.xengine.xui` is the AI-first UI application layer on top of `com.xengine.xgui`.

| Topic | Page |
|---|---|
| `.xui` document language | [xui-language.md](xui-language.md) |
| Logic / render separation | [architecture.md](architecture.md) |
| Translation | [i18n.md](i18n.md) |
| Build cost, and why there is no bake step | [../../../docs/superpowers/evidence/xui/m11-performance-and-build.md](../../../docs/superpowers/evidence/xui/m11-performance-and-build.md) |

Not written yet: the `.pxl` pixel sprite language and the full AI tool surface. Those ship with the
milestones that introduce them. Agent-facing guidance lives in the repo's `cli/skills/`
(`authoring-xui-xml`, `scripting-xui-csharp`, `authoring-xui-pxl`, `run-xui-live`).

## Install

Package Manager → **XUI** → Install. This copies the package into
`Library/PackageCache/com.xengine.xui@<version>/` and pins it in `Packages/manifest.json`.
`com.xengine.xgui` is a hard dependency and is installed with it.

## Samples

| Sample | Shows |
|---|---|
| Login Screen | Theme tokens, a stretched backdrop, a content-sized stack, one template used twice |
| Settings Flow | Tabs with bound panels, transitions, a controller, a modal, loading, a toast, translation |
| Pixel Button | A two-state 9-slice skin authored as a `.pxl` text grid |

Package Manager → **XUI** → Samples → Import. Each lands under `Assets/Samples/`.

## Layout

| Folder | Contents | Constraint |
|---|---|---|
| `Runtime/Core` | IR, parser, lint rules, `.pxl` parser, `.po` catalogs | **Pure BCL.** Linked into `Tools/XEngine.Ai.Host`; an `XEngine.Runtime` reference here breaks offline validation |
| `Runtime/Assembly` | IR → XGUI GameObject tree | Engine-side |
| `Runtime/App` | `Xui`, `XuiView`, `XuiController`, router, modals, toasts | Engine-side |
| `Runtime/Anim` | Relative-offset transitions, ticked per screen | Engine-side |
| `Runtime/Controls` | Composite controls built from XGUI primitives | Engine-side |
| `Editor/Importers` | `.xui` / `.pxl` / `.po` asset importers | Editor-only |
| `Editor/Tools` | In-process MCP tools (`runtime_xui_*`) | Editor-only |
| `Editor/Codegen` | View binding + controller scaffolding | Editor-only |
