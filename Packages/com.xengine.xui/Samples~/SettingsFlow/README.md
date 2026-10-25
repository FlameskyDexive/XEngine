# Settings Flow

The whole package on one screen: tabs with bound panels, a transition on entry and exit, a controller
that never touches a GameObject, a modal, a loading overlay, a toast, and translation — including the
one label that must **not** be translated.

## Try it

```bash
xengine-cli xui-validate --project <root> --doc Assets/UI/settings/Settings.xui
xengine-cli xui-scaffold --project <root> --doc Assets/UI/settings/Settings.xui --screen Settings --out Scripts/UI/Settings
xengine-cli xui-i18n-extract --project <root> --locales zh-Hans --out UI/Locales
```

Then in a live editor (`XEngine.Editor.exe --project <root> --serve`):

```
runtime_xui_open   { "documentPath": "UI/settings/Settings.xui" }
runtime_xui_tree   { "screenName": "Settings" }
runtime_xui_locale { "locale": "zh-Hans" }
```

## What each part is showing

**Tabs own exclusivity.** `<Tab bind="XShowAudio">` names the panel it reveals, in the document.
Selecting one hides the others' panels; the controller never sees it. A tab and its panel are one
authoring decision, and splitting them across two files is how a bar ends up showing the wrong panel
after someone reorders it.

**Transitions are `from:to` pairs.** `move="0,-32:0,0"` rises into place, `move=":0,-24"` leaves upward.
An empty side means rest, so one attribute expresses both directions and neither depends on the trigger
name. `show` and `hide` fire by themselves — a screen with an exit stays on screen until it has played
out.

**The controller only ever touches `View`.** `View.SliderVolume`, `View.BtnSave`, `View.TxtStatus` —
all generated from the ids. Rename an id in the XML and the C# stops compiling, which is the entire
point of the naming rules.

**`translate="false"` on `XTxtVramValue`.** `3.4 / 8.0 GB` is a number, not prose. Everything else in
the screen is extracted; that one is not, and the decision lives next to the text rather than in a
separate ignore list.

**Text the controller writes is translated by the controller.** `Xui.Tr("Signed in as {0}")` then
`string.Format` — translate first, format after, because an interpolated string cannot be extracted at
all. And because the catalog only owns what the document authored, a locale switch does not re-render
that label: the sample subscribes to `Xui.LocaleChanged` to do it itself.

**`Xui.HideLoading()` in a `finally`.** The loading overlay is reference-counted, so a save that throws
must still release its count — otherwise the screen sits behind an overlay nothing will ever remove.
