# XGUI API

`com.xengine.xgui` is the first-party retained-mode UI package. Runtime types live in `XEngine.Runtime.UI`. Editor menus, templates, and migration tools live in `XEngine.XGUI.Editor`.

## Host vs package

The engine host keeps `RectTransform`, `GameCanvas`, `CanvasRenderer`, and platform input. The package owns Graphic, layout, events, and controls. Compatibility types such as `UIImage` and `TextComponent` still deserialize; new authoring uses the package names.

## Core

- `Graphic` / `MaskableGraphic` — dirty flags, mesh populate, material properties. `RaycastPadding` expands or shrinks the pointer hit rectangle (left, bottom, right, top). `PixelAdjustPoint` snaps a design-space point when the owning overlay Canvas has `PixelPerfect`.
- `GameCanvas.PixelPerfect` — overlay positions and verts snap to whole framebuffer pixels at `round(design * scale) / scale`. World-space canvases ignore the flag.
- `GameCanvas.RenderCamera` / `TargetRotationDegrees` — screen-space layout uses a camera RenderTexture when no pipeline override is set; 90° and 270° swap width/height.
- Scene switch promotes the remaining `EventSystem` immediately; prefab Canvas/Image instances register and rebuild after `scene.Add`.
- `CanvasHotReload.Rebind` restores Canvas, EventSystem, and Graphic registries after script/package assembly unload so a live scene keeps picking.
- `CanvasDisplayChange.NotifyResize` / `NotifyDeviceLost` / `NotifyDeviceRestored` relayout HUD after framebuffer resize and drop UI GPU meshes across a device loss.
- `CanvasBackendParity` fingerprints overlay `CanvasRenderNode` mesh/clip/sort so OpenGL, Vulkan, and D3D12 screenshot diffs share one contract (max channel delta 3; Null is not a fallback).
- `PlayerXguiAssemblyPolicy` requires `XEngine.XGUI.Runtime` in Player assembly lists and rejects `XEngine.XGUI.Editor` / `XEngine.Editor`.
- `CanvasUpdateRegistry` / `GraphicRegistry` — layout and graphic rebuild queues.
- `VertexHelper` — pooled mesh builder bridge.
- `XguiDefaultResources` — stable package sprite/font keys and GUIDs. Headless runtime falls back to the embedded UI panel and Geist font.

## Graphics

- `Image` — Simple, Sliced, Tiled, Filled. `SetNativeSize()` writes sprite pixels into `SizeDelta`.
- `RawImage` — direct texture + UV rect. Also supports `SetNativeSize()`.
- `Text` — Scribe-backed rich text (TMP-style tags on the existing `Text` / `TextComponent` types; no separate TextMeshPro type). `HorizontalOverflow`, `VerticalOverflow` (`Truncate` / `Overflow` / `Ellipsis`), `BestFit`, `GetPreferredValues()`. Optional `BoldFont` / `ItalicFont` / `BoldItalicFont` / `MonoFont` slots; unset slots fall back to `Font`.
- `RaycastReceiver` — invisible Graphic that participates in pointer hits with an empty mesh (`GameObject/UI/Raycast Receiver`).
- `RectMask2D` / `Mask` — package clip types.

### Text rich tags and link API

Enable with `RichTextEnabled`. Supported tags (Scribe):

- Style: `<b>`, `<i>`, `<u>`, `<s>`, `<mono>` / `<code>`, `<color=...>`, `<size=...>`, `<font=mono>`
- Links: `<link=id>`, `<link="id">`, `<a href="url">`
- Highlight: `<mark=#rrggbbaa>` (default yellow when color omitted)
- Inline sprites: `<sprite name="icon">`, `<sprite index=0>`, `<sprite=0>` (void; needs `SpriteAsset` — a `TextSpriteAsset` table of named/indexed `Sprite`s). Sprites draw on a second UI mesh with `SharedUIMaterial`.
- Effects: `<shake>`, `<wave>`, `<rainbow>`, `<pulse>`, `<fade>`, `<jitter>`, `<typewriter>` (attributes vary)

TMP-style query / click surface on `Text` / `TextComponent`:

- `linkInfo` / `textInfo` (`characterInfo` + `linkInfo` subset)
- `FindIntersectingLink(Float2 designPosition)` / `HitLink(Float2 designPosition)`
- `OnLinkClicked` — raised from `IPointerClickHandler` when a link glyph is clicked (`PointerEventData.DesignPosition`)

```csharp
text.RichTextEnabled = true;
text.SpriteAsset = coins; // TextSpriteAsset
text.Text = "Buy <link=\"shop\">here</link> <sprite name=\"coin\">";
text.OnLinkClicked += id => OpenShop(id);
int i = text.FindIntersectingLink(e.DesignPosition);
```

## Layout

- `LayoutRebuilder.MarkLayoutForRebuild` / `ForceRebuildLayoutImmediate`.
- `HorizontalLayoutGroup`, `VerticalLayoutGroup`, `GridLayoutGroup`.
- `ContentSizeFitter`, `AspectRatioFitter`, `LayoutElement`, `SafeAreaFitter`.
- `CanvasScaler` — Constant Pixel Size, Scale With Screen Size, and Constant Physical Size (`dpi / defaultSpriteDpi`).
- Text reports preferred width/height so a fitter can size to measured text.
- `SafeAreaFitter` insets a stretched rect to `Window.SafeAreaInsets` (notch / status bar / home indicator).

## Controls

- `Button`, `Toggle`, `ToggleGroup`, `Slider`, `Scrollbar`, `ScrollRect`.
- `InputField` — `ContentType` / `KeyboardType` filter committed text and select the soft keyboard.
- `Dropdown` — requires the package item template and overlay root. Create it from `GameObject/UI/Dropdown`.
- `EventTrigger` — pointer, drag, scroll, drop, selection, move, submit, cancel.

## Defaults

- Sprite: `Packages/com.xengine.xgui/Runtime/Resources/DefaultSprite.png`
- Font: `Packages/com.xengine.xgui/Runtime/Resources/DefaultFont.ttf`

New Images resolve the package sprite when an editor asset database is present.
