# Editor UI kernel

Internal .NET 10 retained UI building block for the N2 editor migration. This project
depends only on the BCL. Production editor panels still use their existing implementation;
render-command/geometry/text caching and editor integration are subsequent plan nodes.

```csharp
using XEngine.Editor.UI;

using var ui = new UiNodeStore(initialCapacity: 256, maximumCapacity: 4096);
var panel = ui.Create();
ui.SetRootBounds(panel, new UiRect(0, 0, 400, 300));
ui.SetStyle(panel, new UiStyle { Padding = 12, Gap = 6 });

var apply = ui.CreateControl(UiControlKind.Button, panel);
ui.SetStyle(apply, ui.GetStyle(apply) with
{
    Width = UiLength.Stretch(), Height = UiLength.Fixed(28)
});
ui.SetContentSize(apply, new UiSize(80, 20)); // Supplied by the text/content layer.
UiLayoutMetrics work = ui.Layout();
UiRect rectangle = ui.GetBounds(apply);
UiRect clip = ui.GetClip(apply);

ui.PointerDown(rectangle.X + 5, rectangle.Y + 5);
UiAction action = ui.PointerUp(rectangle.X + 5, rectangle.Y + 5);
// Apply action to the editor model at the host boundary.
```

## Ownership and updates

- Use one store on one owning UI thread; it is not a concurrent collection. Editor async
  work uses XTask and explicitly switches back to the owning thread before mutation.
- Keep handles, not slot indices. Null, stale and foreign handles are rejected. Removing
  a node removes its subtree, invalidates old generations and clears its input ownership.
  Reparenting preserves identities and appends to the new sibling order; cycles are rejected
  before mutation. Null parent means an independently hosted layout root.
- Capacity grows only when needed, up to the explicit maximum. Slots are reused until their
  generation is exhausted, then retired. Dispose releases arrays; a retained handle keeps
  only an identity token alive, not the store and its nodes.
- SetStyle replaces the entire style. Use `GetStyle(node) with { ... }` to retain defaults
  supplied by CreateControl (especially focusability and label hit-test transparency).
- SetContentSize supplies intrinsic content size. Rendering/text services own text, fonts,
  shaping, DPI-dependent measurement and associated lifetime outside this N2 project.
- Layout processes pending work. With no changes, measure/arrange counts are zero. Paint-only
  changes do not run layout. Scroll invalidates arrangement. Child size changes stop causing
  ancestor recomputation after a fixed-width-and-height boundary; descendant traversal still
  reaches the changed subtree. Changing that boundary's own size invalidates its dependencies.
- Run Layout after mutations and before consuming rectangles or dispatching geometric hits.
  HitTest reads the last cached geometry; it does not hide an implicit layout pass.
- Acknowledge is a stage-completion API. External rendering bridges may clear Paint only
  after processing it; do not clear unprocessed layout invalidations to force a cache hit.

## Layout contract

- Logical units, row/column/overlay, content/fixed/weighted-stretch sizes, symmetric padding,
  main-axis gaps, min/max constraints, clipped scroll offsets and absolute left/top offsets.
- Normal flow offsets are ignored. Absolute children do not contribute parent intrinsic size
  or sibling gaps. Absolute stretch extends from its offset to the content edge. All children
  are clipped to their parent's padded content area and inherited clip.
- Weighted stretch redistributes space after constraints. Infeasible minima deliberately
  overflow and are clipped. Exhausted maxima leave slack. Fixed and intrinsic dimensions
  are not automatically shrunk. This is an editor layout contract, not CSS compatibility.
- Host bounds determine root geometry; root desired size does not override its host rectangle.
  Hidden nodes contribute no intrinsic size or hit area; disabled ancestry blocks interaction.
- Logical input magnitude is at most 1e9; stretch weight range is 1e-6 to 1e6. Invalid inputs
  throw before mutation. Maximum constraints can use the default float.MaxValue upper cap.
- Forced full mode bypasses dirty pruning but shares layout equations. Independent expected
  rectangles and a bisection constraint oracle complement incremental/full differential tests.

## Current cost boundaries

The store is not a virtualized data model: create only visible/prefetch rows for large
lists when panel integration is implemented. Layout prunes clean branches, but hit testing
visits candidate siblings, and Tab walks eligible nodes on a keyboard event. Mixed stretch
constraints use an active-set solver with a quadratic worst case in stretch siblings.
The N2 benchmark corpus characterizes these limits instead of claiming every arbitrarily
large retained tree has constant cost.

The isolated graphics fixture lives in `Tools/XEngine.UiPrototype.Runner` behind
`--validate-kernel`. It uses Quill/PaperRenderer to draw actual kernel rectangles and
compare incremental/full screenshots. It is not the N3 renderer or editor visual parity.
Tests live in `Tools/XEngine.Editor.UI.Tests`; timing tools and evidence are documented in
`docs/benchmarks/editor-performance/2026-09-14-incremental-ui-n2/`.
