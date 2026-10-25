# XEngine patches to Origami (editor UI widgets)

Baseline for diffing: Anthology tag `v2.8.6` (commit `b3ab895d`), path `Origami/` — see `UPSTREAM.txt`.
Retrieve any baseline file with `git show v2.8.6:Origami/<path>`.

## Status

- Was vendored at **v2.8.6**; **synced to v3.3.0 on 2026-08-31** (M1.7 of the prowl-sync plan).
- Upstream v2.8.6→v3.3.0: 43 files, +6.9k/−1.7k — new `Charts/` subsystem (18 files replacing the
  old `Widgets/Chart.cs`+`Widgets/FlameGraph.cs`), `Widgets/Legend.cs`, widget fixes
  (Menu/Modal/Tree/ScrollView/PropertyGrid/NodeGraph…), `DockSpace` and `ViewManipulatorGizmo` work.

## Local patches (preserved — the editor chrome is built on them)

- `Docking/DockSpace.cs` (+~130 local lines) and `Docking/DockWindowChrome.cs` (XEngine-only):
  XEngine's dock window chrome.
- `Widgets/PropertyGrid.cs` (+~370), `Widgets/Tree.cs` (+~246, pooled `RowBindings` subscription
  pattern — zero-alloc row events), `Widgets/NodeGraph.cs` (+~210), `Widgets/Chart.cs` (superseded
  by upstream `Charts/` in v3.3.0 — deleted with the sync), `Gizmo/ViewManipulatorGizmo.cs`
  (XEngine's Unity-style cube+Persp/Iso orientation gizmo — upstream's axis-ring redesign skipped),
  `MathParser.cs`, `OrigamiTheme.cs`, `FieldDrawers.cs`, `Widgets/{MenuBar,Modal,ContextMenu,
  RangeSlider,ScrollView,NumericField,ColorField,DatePicker,…}` local rework.

## Sync resolution notes (2026-08-31)

- Clean 3-way merges despite heavy local work: `DockSpace.cs`, `NodeGraph.cs`, `PropertyGrid.cs`,
  `ScrollView.cs`, `TextField.cs`, `Origami.cs`.
- `Modal.cs`: fused — upstream's `IModal.OnDismissed` + `ModalDismissReason` (CloseButton/Escape/
  Backdrop) added alongside XEngine's `CustomDrawModal.Closed` + `DismissAt(index)` (now takes a
  reason). All four `IModal` implementors updated (DialogModal, CustomDrawModal, ColorPickerModal,
  DatePickerModal) + editor-side CurveEditorModal/GradientEditorModal.
- `Tree.cs` / `ViewManipulatorGizmo.cs`: conflicts resolved **keeping the local side** (local
  pooled-binding architecture / local gizmo design). Deferred upstream features: Tree alt+click
  recursive expand + `OnExpandChanged` callback + per-event caret propagation; upstream axis-ring
  view-manipulator variant.
- Upstream `Charts/` adopted wholesale; editor `ProfilerPanel` flame view migrated from the old
  `FlameGraph`/`FlameNode` (start+duration axis model) to `Chart.FlameGraph<T>` + a local
  `FlameScope` node (depth-row model, Name/Value/Children selectors).
- Samples/docs from upstream not synced (not consumed by the engine build).

## Verification (2026-08-31 sync)

- Origami + full `XEngine.sln` build: **0 errors**.
