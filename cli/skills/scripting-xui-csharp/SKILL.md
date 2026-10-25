---
name: scripting-xui-csharp
description: Write C# that drives a .xui screen — XuiController lifecycle, the generated view binding, Xui.Show/Hide/Close, and xui_scaffold. For the markup itself see authoring-xui-xml.
---

# scripting-xui-csharp

A `.xui` document is pure structure. Everything behavioural is C#, split across three files per screen:

| File | Who writes it | Contains |
|---|---|---|
| `Assets/UI/{feature}/{Name}.xui` | you / an agent | structure, anchors, colours, layer |
| `{Name}View.g.cs` | **`xui_scaffold`, always overwritten** | one typed property per `id` |
| `{Name}.cs` | you, **written once and never regenerated** | lifecycle, events, data |

The rule that makes this work: **the controller never touches a GameObject.** It reads
`View.BtnLogin`. If an id is renamed in the document, re-scaffolding turns every stale use into a
compile error instead of a runtime null.

## Generate the binding

```bash
xengine-cli xui-scaffold --project <root> --doc Assets/UI/login/Login.xui --screen Login --out Scripts/UI/Login --namespace Game.UI
```

Or MCP `xui_scaffold`. It refuses to run on a document with lint errors — run `xui_validate` first.

- `{Name}View.g.cs` is derived and rewritten every run. Never edit it.
- `{Name}.cs` is written only when absent. `--overwrite-controller` exists but discards your logic.
- Re-run it after **every** structural change to the document.

The result lists each binding's `property` and `type`, which is the fastest way to see a screen's API
without opening anything.

## What the binding looks like

```csharp
public sealed partial class LoginView : XuiView
{
    private Button? _btnLogin;
    public Button BtnLogin => _btnLogin ??= Require<Button>("XBtnLogin");
    // ...
}
```

`Require<T>` throws with the id and the fix ("re-run xui_scaffold") rather than returning null, so a
document/code mismatch names itself.

A `Btn` or `Toggle` with inline text also gets a `{Id}Label` binding for its generated label child.

## The controller

```csharp
[XuiScreen("Login")]
public partial class Login : XuiController<LoginView>
{
    private string _account = "";

    protected override void OnRegisterEvents()
    {
        View.BtnLogin.OnClick += Submit;
        View.InputAccount.OnValueChanged += text => _account = text;
    }

    protected override void OnShow(object? context)
    {
        View.TxtTitle.TextValue = "Sign in";
        View.TxtStatus.TextValue = "";
    }

    private void Submit() => View.TxtStatus.TextValue = $"Signing in as {_account}…";
}
```

Lifecycle, in order:

| Hook | When | Use it for |
|---|---|---|
| `OnInitCoreData` | before the hierarchy is built | anything the build depends on |
| `OnBuildView` | hierarchy and `View` now exist | one-time setup that needs controls |
| `OnRegisterEvents` | **exactly once per controller** | subscribing |
| `OnShow(context)` | every time it becomes visible | populating from data |
| `OnHide` | every time it is hidden | pausing, clearing transient state |
| `BeforeUnload` | before the hierarchy is destroyed | releasing what the screen owned |

**Subscribe in `OnRegisterEvents`, not `OnShow`.** `OnShow` runs on every re-show; subscribing there
stacks a duplicate handler each time, and the symptom (an action firing N times) shows up far from the
cause.

## Opening screens

```csharp
Xui.Documents = documentTable;          // XuiDocumentTable, filled as .xui assets load
Xui.Scene = Scene.Current;              // optional; defaults to the current scene

Login login = Xui.Show<Login>();        // builds if needed, then shows
Xui.Show<Login>(context: userId);       // re-show delivers new context without rebuilding
Xui.Hide<Login>();                      // keeps the hierarchy and the controller
Xui.Close<Login>();                     // destroys it; next Show rebuilds from the document
Login? open = Xui.Find<Login>();        // null when not open
```

Screens are opened **by controller type**, never by string, so a deleted screen is a compile error at
every call site.

Registration is automatic: the generated `.g.cs` calls `Xui.Register` from a module initializer. If
`Show` reports a type is not registered, the `.g.cs` is missing or stale — re-scaffold.

## Layers

`layer` in the document decides which root a screen is parented under and what can cover it:
`Normal` (exclusive content) · `Fixed` (always-on HUD) · `PopUp` (modals) · `Other`. Each layer owns a
canvas sort-order band, so a PopUp is above the HUD because it said so, not because of call order.

Put the layer in the document, not in `[XuiScreen]`. It is a property of the screen, and having it in
two places invites them to disagree.

## Deep links and the router

```csharp
XuiRouter.Navigate("xui://Settings?tab=audio");   // Opened | Reshown | UnknownScreen | MalformedLink
XuiRouter.Back();                                  // false at the start of history
XuiRouter.Scheme = "mygame";                       // for external links
```

The query arrives as the controller's `OnShow` context:

```csharp
protected override void OnShow(object? context)
{
    if (context is XuiRouteQuery query)
        SelectTab(query.GetString("tab", "general"));
}
```

`GetInt` / `GetFloat` / `GetBool` / `GetString` all take a fallback and never throw — a link comes from
outside the app, and a bad value must not break showing the screen. A bare flag (`?featured`) reads as
`true`. Malformed percent-encoding is left as written rather than throwing.

**Exclusivity is the router's job, not yours.** Navigating to a `Normal` screen closes the other
`Normal` screens; `Fixed`, `PopUp`, and `Other` stack. That is why two menus can never end up drawn on
top of each other depending on tap order. Only `Normal` navigations enter the back history — a HUD is
not a place you came from.

## Modals

```csharp
int choice = await Xui.MessageBox("Delete", "Are you sure?", "Cancel", new XuiMessageBoxButton("Delete", primary: true));
bool ok    = await Xui.Confirm("Quit", "Unsaved changes will be lost.");
```

Returns the index of the button pressed. Concurrent calls **queue** — two systems asking at once is
normal, and without a queue the second dialog would replace the first and its caller would wait
forever. `Xui.DismissModal(choice)` closes the one on screen from code (an escape key, a timeout).

## Toasts and loading

```csharp
Xui.Toast("Saved");                 // expires on its own; no handle to leak
Xui.Toast("Slow one", seconds: 8f);

Xui.ShowLoading("Downloading…");    // reference-counted
Xui.HideLoading();                  // closes only when the last holder releases
Xui.ResetLoading();                 // for a failure path that lost count
```

Toasts stack at the bottom, oldest dropped past five. Expiry is time-based, so an abandoned toast
cannot leak.

All three overlays are ordinary `.xui` documents built from the same twelve tags. Register your own
document under `XuiMessageBox`, `XuiToastStack`, or `XuiLoading` to restyle them without forking the
package.

## Transitions

`show` and `hide` fire by themselves, so an entrance needs no C# at all — it is authored in the
document (see **authoring-xui-xml**). Any other trigger name is yours to play:

```csharp
Xui.PlayTrigger("Settings", "pop");     // returns how many animators had that trigger
```

A screen that authors a `hide` transition stays on screen until the exit has finished; `Xui.Hide` still
returns immediately, so do not treat it as a point where the screen is already gone.

`Xui.TickAnimations("Settings", 0.016f)` steps a screen's transitions by hand. Screens tick themselves
every frame; this exists so a test or a tool can advance time deterministically instead of sleeping and
hoping.

Hover and press are **not** trigger names. `Selectable` owns those states and raises no event on
change, so a document cannot hook them; use the control's own colour tint for press feedback, or play a
named trigger from a click handler.

## Translation

```csharp
Xui.Locale = "zh-Hans";              // re-translates every open screen in place
Xui.Tr("Saved");                     // the source string is the key
Xui.Tr("Post", context: "verb");     // when one string needs two translations
Xui.LocaleChanged += _ => Refresh(); // for text you formatted yourself
```

`Xui.Tr("…")` calls are extracted alongside the documents, so a string built in C# reaches the same
catalog. The argument must be a **literal** — an interpolated or concatenated string cannot be
extracted at all, so format after translating: `string.Format(Xui.Tr("Level {0}"), n)`.

Switching locale re-translates in place instead of rebuilding, so scroll position, focus, and anything
a controller wrote after the build all survive. That last part is why it matters: a label your code set
is **not** clobbered by a language switch — but it is also not re-translated, which is what
`LocaleChanged` is for.

Catalogs are `.po` files under `Assets/`, registered when they import. A font named after the locale
next to its catalog (`Locales/zh-Hans.ttf` beside `Locales/zh-Hans.po`) becomes that locale's font;
from game code, `Xui.RegisterLocaleFont(locale, font)`. This is whole-asset substitution, not per-glyph
fallback — XGUI has no fallback chain by character range.

## Hot reload

`Xui.Reload("Login")` rebuilds an open screen from its re-imported document and calls `Rebind` on the
view. The controller instance, its fields, and its subscriptions all survive —`OnRegisterEvents` does
**not** run again. This is the editor loop: edit the `.xui`, save, see it.

If the reload reports missing ids, the document and the generated binding have diverged: re-scaffold.

## Checklist before reporting done

1. `xui_validate` — `valid: true`.
2. `xui_scaffold` re-run if any `id` or tag changed.
3. The project compiles.
4. State which screens you touched, their layers, and which controls you wired.
