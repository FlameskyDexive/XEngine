# N1 editor UI comparison prototype

This is an isolated .NET 10 experiment, not the editor's default UI. The full N1
goal remains a Paper / ImGui.NET / retained implementation comparison with the
same tree, property list, text input, viewport texture, scrolling and RHI.

Implemented so far:

- `RowDocument` owns fixed-size row text and revision data, with selection changes.
- `RetainedRowViewport` retains a bounded ring of row descriptors. Unchanged frames
  do not inspect rows; edits inspect only the visible range; small scrolls reuse
  overlapping rows. Fractional offsets and hit testing use the same row geometry.
- Seventeen Release test cases compare visibility/hits to a full reference at
  0/1/1000/100000 rows, verify invalidation and bounds, and measure zero managed
  allocation during a warmed scrolling/selection loop.
- `TreeDocument` indexes immutable preorder topology with mutable expansion using
  range-add/min-count visibility. Nested toggles and visible ranks are checked
  against a full reference; insert/delete/reparent remains outstanding.
- `PrototypeWorkload` and `ReplayTimeline` apply deterministic data mutations,
  including catch-up across skipped presentation ticks. They do not prove widget
  input routing.
- `../XEngine.UiPrototype.Runner` now has three drawing adapters: actual Paper
  element construction/layout/text, retained Quill canvas, and ImGui.NET with a
  dedicated RHI renderer. All three use the same font file, model, logical layout,
  DPI and viewport texture. The runner uses Workstation GC.
- `--replay-model` changes expansion, selection, both scroll positions, property
  text, input text and split ratio, then restores them. Three Vulkan captures per
  adapter show the initial, changed and restored state. Each adapter's restored PNG
  is byte-identical to its initial PNG. This is model replay, not native input proof.
- `--replay-input` now sends pointer press/release, wheel, Ctrl+A, characters and
  Backspace through each adapter. Tree selection and text editing use Paper/ImGui
  widgets or retained hit/edit state; the custom splitter/scroll controller is shared.
  All three verify the expected selected node, property/input text, scrolls and split.
  Without capture/replay flags, the runner polls the real engine input source.

These descriptors are not shaped text, generated GPU geometry or uploaded buffers.
The allocation test does not establish zero-allocation rendering or an editor FPS
improvement. OS input delivery/IME and production lifecycle coverage remain
unverified requirements of the overall migration plan. Retained Canvas submission
still uploads all geometry; partial uploads remain N3 work.

N1 now includes Vulkan texture/ownership/size checks and 72,012-vertex ImGui
index/multiple-list raster validation. Five-round static, dynamic-input and
presented changing-viewport comparisons, plus five paired effects on/off rounds,
are recorded under `docs/benchmarks/editor-performance/2026-09-14-incremental-ui-n1`.
See `acceptance.md` there for exact scope and remaining production requirements.
Earlier README wording bundled native OS/IME acceptance into the prototype;
the authoritative plan assigns N1 the comparison/input-replay fixture, while
full text/input, platform and editor acceptance remain in N2–N7. Those requirements
have not been removed or counted as passed.

The runner pins ImGui.NET 1.91.6.1 and uses the native library distributed by that
NuGet package. No separate newer native build is mixed in. Its shader uses the
engine's `UIVS` projection-buffer convention; a different buffer name did not receive
the named projection uniform on Vulkan. The correction is local to the prototype.

Sources: [ImGui.NET releases](https://github.com/ImGuiNET/ImGui.NET/releases),
[matching native builds](https://github.com/ImGuiNET/ImGui.NET-nativebuild/releases).

```powershell
dotnet test Tools/XEngine.UiPrototype.Tests/XEngine.UiPrototype.Tests.csproj -c Release
```
