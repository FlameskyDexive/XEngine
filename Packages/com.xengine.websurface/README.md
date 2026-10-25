# com.xengine.websurface

Hosted web-content surfaces for XEngine: a `.websurface` asset drives a platform backend
(Windows WebView2 SharedBuffer, Browser canvas copy, Android ArrayBuffer bridge, ArkWeb native
channel) that produces tightly-packed RGBA8 frames into a triple-buffered pool, presented by
`WebSurfaceGraphic` (a `MaskableGraphic` — never the sealed `RawImage`).

- **Runtime core** (`XEngine.WebSurface.Runtime`): session state machine, frame pool
  (drop-newest-never-queue), capabilities, input mapping, diagnostics counters, a deterministic
  four-color-corner fake producer for tests.
- Platform backends, GPU fast paths, IME sessions and world-depth click-through land node by
  node (B2–B9 of `docs/superpowers/plans/2026-09-05-threejs-websurface-implementation-plan.md`).
