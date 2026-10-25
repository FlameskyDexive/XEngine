# XEngine patches to Quill (vector graphics / canvas)

Baseline for diffing: Anthology tag `v2.8.6` (commit `b3ab895d`), path `Quill/` — see `UPSTREAM.txt`.
Retrieve any baseline file with `git show v2.8.6:Quill/<path>`.

## Status

- Was vendored at **v2.8.6**; **synced to v3.3.0 on 2026-08-31** (M1.6, atomic with M1.5 Paper).
- Upstream v2.8.6→v3.3.0 lib changes: `Canvas.cs` +153/−38 (GeometryBuffer backing store,
  `VertexCount`/`IndexCount`, `MatchesState` draw-state machinery with a hash fast path,
  per-vertex brush-gradient `brushInverse`), `GeometryBuffer.cs` (new), `Shaders/*.slang` (new),
  `Canvas.FastBorders.cs`, `TextRenderer.cs`, csproj shader-gen target. (The rest of upstream
  churn was Samples/ShaderGen, not vendored.)

## Local patches (preserved — they are the engine's GPU contract)

### Brush-record batching (Canvas.cs, the engine UI render path)

- `Vertex` carries `brushIndex`/`brushPage` (20→24 byte layout); per-frame `UiBrushRecord`
  table (144-byte GPU records) addressed per vertex, grouped in pages; `DrawCall.BrushPage`.
- Draw-call merge logic is brush-page + texture-compatibility aware (with an "adopt untextured
  batch" path). Consumed by `XEngine.Runtime`'s Paper renderer via `Vertices`/`Indices`/
  `BrushRecords`/`Copy*To` and rendered with `UI.shader`'s UiBrushes uniform block.
- **The vertex layout must not change without updating `UI.shader` and the renderer.**

## Sync resolution notes (2026-08-31)

- `Canvas.cs`: 7 conflict blocks, all resolved **keeping the local architecture**:
  view accessors (blocks 1–3), brush-stamped vertex emit loops (4–5), local `EnsureDrawState()`
  draw-state flow (6–7). Upstream's `VertexCount`/`IndexCount` were **re-added on the List-backed
  store** (public API parity for `PaperDevTools`).
- **Adopted from upstream**: `GeometryBuffer.cs` (upgraded XEngine-side to implement
  `IReadOnlyList<T>` + `CopyTo`, so the local view/copy surface works on the new store),
  `_vertices`/`_indices` as `GeometryBuffer<T>`, the `MatchesState`/`_drawStateDirty`/
  `_verifiedDrawCall` machinery fields and `Shaders/Canvas.slang` + `Blur.slang`.
- **Deferred (documented, revisit on next sync)**: (a) upstream's hash-based draw-call fast path
  in `AddTriangleCount` — assumes the upstream stateHash model, incompatible with the local
  brush-page merge logic; (b) upstream per-vertex brush-gradient (`brushInverse`, +16 B/vertex) —
  replaced here by the brush-record table; (c) ShaderGen/ generator project (csproj target kept,
  self-disabling via `Exists()` guard).
- **Rename artifact fixed**: bare identifier `ProwlCanvasState` → `XEngineCanvasState`
  (the `Prowl\.` dotted rename rule does not catch undotted prefixed identifiers — watch for this
  pattern on future syncs).

## Verification (2026-08-31 sync)

- Quill + Paper + full `XEngine.sln` build: **0 errors** (engine Paper renderer compiles against
  the merged Canvas unchanged). Paper.Tests 22/22 (UI-level coverage; Quill has no local tests).
