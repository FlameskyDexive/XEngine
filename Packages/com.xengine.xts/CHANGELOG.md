# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0] - 2026-09-11

### Added

- N0: vendored QuickJS 2025-04-26 (MIT) behind the versioned `xengine_xts_abi`
  C ABI (v1): environment create/destroy, ESM module registry + eval, host
  callbacks, promise job pump, memory/execution limits, exception reporting.
  Windows x64 `xengine_xts.dll` built with MSVC; NativeAOT host executes the
  full suite identically to CoreCLR.
- N1: optional package skeleton (`installByDefault:false`), runtime/editor
  asmdefs, `XtsRuntime` managed surface, and the native dependency manifest
  (`Native/native-manifest.json`) with validation that rejects missing
  platforms, wrong ABI versions, and hash mismatches before any player build.

## [0.1.1] - 2026-09-12

### Fixed

- Explicit `using` directives across all runtime sources: game-project
  csprojs are generated without `ImplicitUsings`, and 0.1.0 failed to
  compile in freshly created player projects (found by the N12 real-install
  gate on a brand-new project installed from registry.npmjs.org).

## [0.1.2] - 2026-09-12

### Added

- HarmonyOS native libraries: `libxengine_xts.so` cross-compiled for
  `x86_64-linux-ohos` (emulator) and `aarch64-linux-ohos` (device) with the
  ohos ndk clang; GNU build ids pinned in the native manifest. Vendored
  musl patch: `__tm_gmtoff` under `__OHOS__`.

### Fixed

- Remaining implicit-using dependencies across runtime sources; the package
  now compiles under `ImplicitUsings=disable` game-project csprojs
  (verified by `Tools/Xts.Feasibility/Xts.GameProjectShape`).

### Fixed (0.1.3 hotfix, same day)

- Editor sources under `ImplicitUsings=disable` (game projects compile the
  package's Editor folder as well); publish checklist extended with
  `Tools/Xts.Feasibility/Xts.GameProjectShape` as the pre-publish gate.
