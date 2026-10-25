# XTS TypeScript Scripting

Official optional package: author XEngine gameplay in **TypeScript**, build it
with the editor toolchain, and run it on **QuickJS** behind a versioned native
ABI — with **content hot-update** on Windows and HarmonyOS players.

The package is *not mounted until the project installs it*; an engine install
never carries the QuickJS native library or the Node toolchain by default.

## What you get

- `XtsRuntime` — create / run / pause / destroy isolated script environments
  (ESM modules, host callbacks, promise job pump, memory + execution limits).
- `XtsBehaviour` — attach TypeScript component classes to GameObjects
  (later node).
- Bindings generator — static `.d.ts` declarations + typed C# wrappers built
  at build time; NativeAOT-safe, no runtime reflection (later node).
- `XtsUpdateService` — signed JS content packs with preflight verification,
  atomic activation and baseline rollback (later node).

## Native layer

QuickJS (2025-04-26, MIT) is vendored under `Native/quickjs` and wrapped by
`Native/xengine_xts_abi` — the only contract between the engine and the JS
engine. Platform libraries and their SHA256 hashes are pinned in
`Native/native-manifest.json`; a player build refuses to package a platform
whose entry is missing, has the wrong ABI, or fails hash validation.

See `Native/source-manifest.json` for the upstream lock and the exact
vendored patches.
