# XTS TypeScript Scripting

XTS lets a project write gameplay in TypeScript, packages it with the editor
toolchain, and executes it on QuickJS through a versioned native ABI. Content
updates ship signed JavaScript plus assets — the native library and the AOT
player binary are never replaced by content.

## Installing

The package ships with the editor but is **not installed by default**. Add
`"com.xengine.xts": "0.1.0"` to the project's `Packages/manifest.json` (or
install it from the Package Manager). Removing the entry unmounts the
package; no QuickJS code, native library or toolchain remains referenced.

## Concepts

- **Script environment** (`XtsRuntime`) — one isolated QuickJS runtime with
  its own heap limit and execution deadline. All calls run on the creating
  thread (the engine main thread).
- **Modules** — ES modules registered by name; imports resolve inside the
  environment only. There is no default `CS.*` global and no CLR reflection.
- **Host callbacks** — static, build-time-registered functions callable from
  TS. Missing bindings fail at activation, never via reflection fallback.
- **Native manifest** (`Native/native-manifest.json`) — per-platform library,
  ABI, SHA256, source and build id. Player builds refuse to package a
  platform that fails validation.

## Quick start (editor, later node)

`Window > XTS` menu: create a TS project, generate declarations and bindings,
run `tsc --noEmit` + esbuild, and mount a `XtsBehaviour` on a GameObject.

## Support

- Platforms: Windows x64, HarmonyOS ARM64 (device support lands with the
  platform nodes).
- QuickJS is pinned to 2025-04-26; upgrades are a deliberate package release
  with a re-locked tarball hash, never a content update.
