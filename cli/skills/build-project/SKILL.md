---
name: build-project
description: Build an XEngine project headlessly (no editor UI) via XEngine.Editor --buildmode, and run a built player headlessly. Returns structured exit code/duration/output.
---

# build-project

Use when an AI needs to produce a player build or run a simulation without the editor UI.

## Build
```bash
xengine-cli build --project <root> \
  --editor <absolute-path-to-XEngine.Editor.exe> \
  --output <absolute-output-dir> \
  [--build-profile "Android Store"] [--platform android]
```
Or MCP: `build_project` (`editorExe`, `outputDir`, `buildProfile`, `platform`).

### Choosing what to build
A project can hold several `.buildprofile` assets — including several for the same platform, e.g. a
"Windows Debug" and a "Windows Store" profile. Each carries its own platform settings, Player
Settings overrides, and asset import downgrades.

- `--build-profile` takes a profile name, a project relative path, or the asset GUID. Names are
  matched case-insensitively and must be unambiguous; on a miss the error lists the profiles that do
  exist. The platform is derived from the profile, so `--platform` is unnecessary.
- `--platform` (`desktop`, `android`, `ios`, `harmonyos`, `web` / `browser` / `wechat` / `douyin`) selects a pipeline without naming a
  profile. If the project's active profile targets a different platform it is dropped for that build,
  so its overrides do not leak into the wrong player.
- Web (`--platform web`) generates a relocatable `net10.0` + `browser-wasm` player, then
  `dotnet publish -p:IsBrowserTarget=true` (requires the `wasm-tools` workload). Browser uses stock
  `dotnet.js`; WeChat/Douyin adapters use `WXWebAssembly` / `TTWebAssembly` path instantiate and
  WebGL2 canvas. That JS is an ABI stub — it is not a proven Mono-WASM boot in the mini-game
  containers. Douyin publish forces `WasmEnableSIMD=false`.
- Passing both is only allowed when they agree.
- Passing neither builds whichever profile the project has marked active.

Selection is per-invocation: a headless build never rewrites the project's persisted active profile,
so building three profiles in a row on CI leaves the project untouched.

Use `asset_search` for `.buildprofile` to discover the available profile names.

## Run a built player headlessly (no window/audio/GPU)
```bash
xengine-cli run-headless --project <root> \
  --player <built-player.exe> [--scene <path>] \
  [--max-frames 60] [--max-seconds 0]
```
Or MCP: `run_headless` (`playerExe`, `scene`, `maxFrames`, `maxSeconds`).

## Result shape
Both return `ProcessRunResult`: `succeeded`, `exitCode`, `durationMs`, `outputPath`, and a
truncated `outputTail` (last ~4 KiB of stdout+stderr) for diagnostics.

## Notes
- Build/run are synchronous in B2 (the tool blocks until the process exits). Long builds block
  the call; a ticketed operation store is a follow-up.
- The editor/player exe path must be supplied (`--editor`/`--player`). If missing, the tool
  returns an `io_error` with `retryable: true`.
- Player shader collect strips ScreenSpace Decal / screen-space shadow variants unless those
  `RendererFeature`s are enabled (M19). Forward / ForwardPlus also strip `Default/DeferredLighting`
  unless `RendererData.RenderingPath` is Deferred or DeferredPlus (M21 / U17-M1). Universal (default) strips
  `Default/SpriteLit` / `Default/Light2D` / `Default/Shadow2D` unless `RendererData.Kind` is
  Renderer2D (M22). Editor import still compiles the full declared set.
