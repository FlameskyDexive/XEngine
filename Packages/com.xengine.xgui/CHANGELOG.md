# Changelog

## Unreleased

- Renamed the Runtime layout compatibility classes to the `UI` prefix every other compatibility type already used (`UILayoutGroup`, `UIHorizontalOrVerticalLayoutGroup`, `UIHorizontalLayoutGroup`, `UIVerticalLayoutGroup`, `UIGridLayoutGroup`, `UIContentSizeFitter`, `UILayoutElement`, `IUILayoutElement`, `UILayoutUtility`). `XEngine.Runtime` and `XEngine.XGUI.Runtime` previously declared all nine under the same names in `XEngine.Runtime.UI`, so any assembly referencing both — the normal shape for a package — hit `CS0433` on first use. Old serialized names redirect to the package components as before.

- Added rich-text link hit-testing on `Text` / `TextComponent`: `FindIntersectingLink`, `HitLink`, `linkInfo`, `textInfo`, and `OnLinkClicked` (Scribe `<link>` / `<a href>`).
- Added inline `<sprite name|index>` layout via `TextSpriteAsset` and a secondary UI mesh for the sprite atlas.
- Added `<mark>` highlight spans, `VerticalOverflow.Ellipsis`, and optional Bold/Italic/BoldItalic/Mono font slots on text.
- Added `RaycastReceiver` (invisible hit Graphic) and `GameObject/UI/Raycast Receiver` creation menu/template.

## [0.1.0] - 2026-08-10

- Added `PlayerXguiAssemblyPolicy` so Desktop/Android/HarmonyOS Player assembly lists must include XGUI Runtime and exclude XGUI Editor / XEngine.Editor.
- Added `CanvasBackendParity` so OpenGL/Vulkan/D3D12 screenshot diffs share one CanvasRenderer fingerprint and reject Null fallback.
- Added `CanvasDisplayChange` so window resize and GPU device lost/restore relayout Canvas and re-upload UI meshes.
- Added `CanvasHotReload.Rebind` so script and package ALC reload restore live Canvas, EventSystem, Graphic, and raycast registrations.
- Added scene-switch EventSystem promotion and prefab Canvas/Image instantiate coverage so UI survives load.
- Added screen-space Canvas layout against a camera RenderTexture or rotated framebuffer (90/270 swap width and height).
- Added GameCanvas.PixelPerfect so overlay UI positions and verts snap to whole framebuffer pixels.
- Added Graphic.RaycastPadding so pointer hit testing can expand or shrink past the visual rect.
- Added CanvasScaler Constant Physical Size (`dpi / defaultSpriteDpi`) and a Safe Area fitter that insets HUD to `Window.SafeAreaInsets`.
- Added adjacent UI batch merge and persistent vertex/index pages so same-material Graphics share one draw and static frames skip mesh upload.
- Added four-source logical-root coverage so built-in, embed, and local XGUI mounts keep `Packages/com.xengine.xgui`.
- Added Player dependency audit so Desktop/Android/HarmonyOS include XGUI Runtime and exclude XGUI Editor.
- Added XGUI host diagnostics snapshot so the Diagnostics window reports batches, clip chains, and last-frame RenderStats.
- Added the empty/one/controls/10-10k RenderStats acceptance matrix so static UI frames stay at zero mesh upload across the CanvasRenderer backends.
- Added HarmonyOS IME InputType mapping so InputField ContentType/KeyboardType selects email, number, PIN, and password keyboards.
- Added Android IME InputType mapping so InputField ContentType/KeyboardType selects email, number, PIN, and password keyboards.
- Added hidden Samples~ controls gallery and Documentation~ API/migration guides for the first package release.
- Added package default sprite and font assets under Runtime/Resources with stable GUIDs; Image resolves the package sprite in the editor and falls back to the embedded UI panel at runtime.
- Added InputField ContentType/KeyboardType filters and soft-keyboard options for Android and HarmonyOS.
- Added `LayoutRebuilder` and Text preferred-size reporting so ContentSizeFitter can size to measured text.
- Added Text horizontal/vertical overflow modes and Best Fit sizing, with Inspector controls and acceptance screenshots.
- Added `Graphic.SetNativeSize` for Image, RawImage, and legacy UIImage, including Inspector buttons that honor the pixels-per-unit multiplier.
- Added the package Runtime assembly and Canvas update/graphic registries.
- Added package-facing Graphic, Image, Text, Button, and RectMask2D compatibility types.
- Added the pooled VertexHelper bridge over the existing UI mesh algorithms.
- Added package-owned default control templates and a template factory for the GameObject/UI menu.
- Added a read-only migration scanner and diagnostics summary for package, compatibility, and missing XGUI components.
- Added an undoable legacy-to-XGUI batch upgrader with field-copy, component-reference redirection, GUID and component-count reporting; Dropdown remains template-driven manual migration.
