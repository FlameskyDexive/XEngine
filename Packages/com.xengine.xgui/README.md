# XGUI

`com.xengine.xgui` is the first-party retained-mode UI package for XEngine.

The package builds on the Runtime Canvas host and keeps the existing `XEngine.Runtime.UI`
components available as compatibility types. New code can use the package-facing `Graphic`,
`MaskableGraphic`, `Image`, `Text`, and `Button` names while old scenes continue to deserialize.
`Graphic.RaycastPadding` (and the legacy `UIImage` field) expands or shrinks the pointer hit
rectangle without changing the drawn mesh. `GameCanvas.PixelPerfect` snaps overlay UI positions
and verts to whole framebuffer pixels at the current scale factor; world-space canvases ignore it.
Screen-space layout follows a camera RenderTexture or pipeline override, and 90°/270° rotation swaps
width and height so Scale With Screen Size relayouts for portrait. Scene switches unbind the old
Canvas and promote the new scene's EventSystem; prefab instances of Canvas/Image rebuild and pick.
Script and package hot reload drop static Canvas/EventSystem/Graphic registries so the collectible
ALC can unload, then `CanvasHotReload.Rebind` restores still-live hosts without waiting for OnEnable.
Window framebuffer resize and safe-area inset changes dirty every Canvas; GPU device lost/restore
drops baked UI meshes so the next rebuild uploads again. OpenGL, Vulkan, and D3D12 consume the same
`CanvasRenderer` fingerprint; screenshot diffs use a 3-channel GPU gate and must not silently fall
back to the Null device. `PlayerXguiAssemblyPolicy` requires `XEngine.XGUI.Runtime` in Desktop,
Android, and HarmonyOS Players and rejects `XEngine.XGUI.Editor` / `XEngine.Editor`.

The package is intentionally backend-neutral: mesh submission goes through `CanvasRenderer` and
the Runtime render-node contract. OpenGL, Vulkan, D3D12, and HarmonyOS mapping stays in the RHI. Desktop, Android, and HarmonyOS Players collect `XEngine.XGUI.Runtime` and reject `XEngine.XGUI.Editor` / `XEngine.Editor` in the runtime closure.

Text reports preferred width/height to the layout system, and `LayoutRebuilder` can mark or immediately rebuild a RectTransform so `ContentSizeFitter` sizes to that measurement. Text supports `HorizontalOverflow`, `VerticalOverflow` (including `Ellipsis`), and `BestFit` (min/max size) so a paragraph can wrap, overflow, truncate, ellipsize, or shrink to its rect. With `RichTextEnabled`, Scribe tags cover style/color/size, links (`<link>` / `<a href>`), `<mark>`, inline `<sprite>` (via `TextSpriteAsset`), and effects; use `FindIntersectingLink` / `OnLinkClicked` for TMP-style hyperlink handling on the same `Text` type. Optional Bold/Italic/Mono font slots fall back to `Font` when unset. `GameObject/UI/Raycast Receiver` adds an invisible hit target. Image, RawImage, and the legacy UIImage expose `SetNativeSize()`, which collapses stretched anchors and writes the sprite or texture pixel size (including the pixels-per-unit multiplier and RawImage UV rect) into `RectTransform.SizeDelta`. The Image inspectors surface this as a **Set Native Size** button for every Image type.

InputField content types filter committed text and select the matching touch-screen keyboard (including secure PIN/password entry) through `ITextInputService`. Android Players map that selection to `EditorInfo.InputType` and restart IME when the focused field changes. HarmonyOS Players expose the matching `@ohos.inputMethodEngine` InputType through `xengine_player_text_input_type` and restart the soft keyboard when the focused field changes. The package ships `Runtime/Resources/DefaultSprite.png` (256x256 sliced panel, 32px border) and `Runtime/Resources/DefaultFont.ttf` (Geist Regular) with stable GUIDs. New Images resolve the package sprite when an editor asset database is present and fall back to the embedded UI panel at runtime. Adjacent overlay and world-space Graphics that share material, texture, clip and stencil collapse into one persistent mesh page at draw time; a leaf color change rewrites only that page and does not rebuild layout or text. Host acceptance covers empty Canvas, one Image, Image+Text+Button, and 10-10k same-material Graphics: static frames skip mesh upload and the CanvasRenderer contract is shared by OpenGL, Vulkan, and D3D12. The first vertical slice also includes stable package asset metadata, de-duplicated dirty queues,
package-owned `RectMask2D`, and a two-pass layout convergence guard. Existing RectTransform and
Horizontal/Vertical/Grid algorithms remain the host compatibility implementation.

Hidden `Samples~` and `Documentation~` folders ship a controls-gallery template plus API and migration guides; PackageManager ignores those folders so they never compile. The Editor package supplies `Tools/XGUI/Scan Legacy UI` and `Window/Analysis/XGUI Diagnostics`.
Both tools are read-only: they scan active and disabled scene objects, distinguish XGUI package
components from Runtime compatibility components, and identify missing serialized XGUI UI types
without changing Echo data. Diagnostics also report overlay/world node counts, UIBatches/draws/uploads,
and rect/stencil clip-chain counters from the last host snapshot. Built-in, embed, and local sources keep the stable logical root `Packages/com.xengine.xgui`. `CanvasScaler` Constant Physical Size scales UI by `dpi / defaultSpriteDpi` (content scale × 96, with a fallback DPI when no window is present). `GameObject/UI/Safe Area` adds a `SafeAreaFitter` that stretches to the parent and insets to `Window.SafeAreaInsets` so HUD clears notches and system bars.

`Tools/XGUI/Upgrade Legacy UI in Scene` replaces safe Runtime compatibility components in one
Undo step. It preserves component identifiers, serialized fields, and references between upgraded
components, while recording expected support components such as `CanvasRenderer`. `Dropdown` is
intentionally reported for manual migration because a valid XGUI Dropdown requires the package
template's disabled item row and independent overlay root.
