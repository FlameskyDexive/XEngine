# Hudmesh (com.xengine.hudmesh)

World-space overhead HUD system for XEngine, ported from
[UnityHUD](https://github.com/FlameskyDexive/UnityHUD): health bars, nameplates,
floating damage numbers, and chat bubbles rendered as camera-facing meshes —
**one combined mesh per material**, not per-entity GameObjects or canvases.

## What it does

- **Stacked nameplates** — health bar, name, title, guild name, guild icon in a
  vertical stack above a target `Transform`; hiding a slot reflows the stack.
- **Health bars** — nine-sliced background + three-segment sliced fill
  (horizontal/vertical, reverse fill supported), live percent updates.
- **Floating damage numbers** — two modes per style: SDF text (any string,
  gradients, outline) or digit-atlas art (`b/c/h/l` digit sets); float-up /
  alpha / scale curves; pooled; auto-release.
- **Chat bubbles** — nine-sliced bubble auto-sized to wrapped text, show timer
  with accelerated fade-out when replaced.
- **Performance model** — every HUD quad lands in a per-material dynamic mesh
  (`Mesh.AssignStreams` reuse + degenerate-triangle padding → steady-state
  zero allocation); CPU painter's sort by camera distance; pooled widgets.

## Install

Optional official package — install from the editor's Package Manager, or add
to the project manifest. No runtime dependencies beyond `XEngine.Runtime`.

## Quick start

```csharp
using XEngine.Hudmesh;

HudManager.Init(null);                      // null → package default config
Hud hud = HudManager.Instance.CreateHud(1);
hud.Init(targetTransform, new Float3(0, 1, 0));

hud.SetBloodVisible(true, HudRelation.Self);
hud.SetBloodPercent(0.75f);
hud.SetText(HudSlot.Name, "Player", HudRelation.Self);
hud.SetText(HudSlot.Title, "Lord", HudRelation.Self);

hud.PopNumber(0, -500, Float3.Zero);        // floating damage number
hud.Talk(0, "hello world");                 // chat bubble

hud.Release();                              // when the entity dies
```

## Documentation

See [Documentation~/index.md](Documentation~/index.md). Development history and
acceptance evidence live in the engine repository under
`docs/superpowers/evidence/packages/hudmesh/`.
