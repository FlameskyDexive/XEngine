# Logic / render separation

A screen is three files. The split is not stylistic — each one has a different author, a different
lifetime, and a different reason to change.

| File | Written by | Rewritten? | Holds |
|---|---|---|---|
| `Assets/UI/{feature}/{Name}.xui` | agent or human | by hand | structure, anchors, colours, layer |
| `{Name}View.g.cs` | `xui_scaffold` | **every run** | one typed property per `id` |
| `{Name}.cs` | human or agent | **never** | lifecycle, events, data |

```
 Login.xui ──parse──▶ IR ──assemble──▶ XGUI hierarchy
     │                                      │
     │                                 id → GameObject
     │                                      │
     └──scaffold──▶ LoginView.g.cs ◀────────┘
                          │
                          ▼
                      Login.cs   ← the only file with business logic
```

## Why the binding is generated code and not reflection

`View.BtnLogin` is a property backed by `Require<Button>("XBtnLogin")`. That means:

- Renaming an id in the document and re-scaffolding turns every stale use into a **compile error**,
  not a runtime null nobody traces back to the rename.
- The generated file is greppable, diffable, and indexed by `code_symbols`, so "what is this screen's
  API" is answerable without running anything.
- No reflection, so the screen still works under NativeAOT.

XEngine's script pipeline shells out to `dotnet build` and wires no analyzers, so an incremental source
generator was not available. Writing real files turned out to be the better answer regardless.

## Lifecycle

```
OnInitCoreData        before anything is built
      ↓
  (assemble)          IR → XGUI hierarchy, view binding created
      ↓
OnBuildView           hierarchy and View now exist
      ↓
OnRegisterEvents      exactly once per controller
      ↓
OnShow(context)  ⇄  OnHide          per visibility change
      ↓
BeforeUnload          before the hierarchy is destroyed
```

Subscribe in `OnRegisterEvents`. It runs once; `OnShow` runs on every re-show, and subscribing there
stacks a duplicate handler each time — a bug whose symptom (an action firing N times) appears far from
its cause.

This is XPlay XUI's `IXUIEventHandler` contract collapsed onto a base class. XEngine screens are
GameObject trees rather than ET entities, so a separate handler object would be a file with no state
of its own.

## Layers

`XuiRoot` owns four containers, and `layer` in the document picks one:

| Layer | For | Sort-order band |
|---|---|---|
| `Normal` | exclusive content | 0– |
| `Fixed` | always-on HUD | 1000– |
| `PopUp` | modals | 2000– |
| `Other` | escape hatch | 3000– |

Screens step up within their band as they open. A PopUp covers the HUD because it declared a layer,
not because of call order — which is what makes "what can cover what" a reviewable property of the
screen rather than a race.

One `XuiRoot` per scene: a screen belongs to the scene that opened it, so a scene unload takes its UI
with it.

## Hot reload

`Xui.Reload(screenName)` destroys the hierarchy, rebuilds it from the re-imported document, and calls
`XuiView.Rebind` — which drops every cached reference so none can outlive the objects it pointed at.

The controller instance, its fields, and its subscriptions all survive. `OnRegisterEvents` does **not**
run again. That is what lets an edit-save-look loop keep the state you were testing with.

If ids changed, the binding is stale and `Require<T>` says so by name. Re-scaffold.

## What a controller may not do

Touch a GameObject, look up a component, or use an element id as a string. Everything goes through
`View`. A controller that follows this reads as business logic, and can be reviewed without the
document open beside it.
