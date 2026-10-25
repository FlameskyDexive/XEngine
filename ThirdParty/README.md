# ThirdParty

Vendored third-party libraries, consumed as source via `ProjectReference`.

## Anthology (XEngine.* libraries)

- **Upstream**: https://github.com/XEngine/Anthology
- **Vendored at**: v2.8.6 (MIT)
- **Copyright**: Copyright (c) 2026 Wulferis

These 15 libraries were originally consumed as `XEngine.*` NuGet packages. They are now
built from source inside this repository so the engine is fully self-contained.

| Package | Path |
|---|---|
| XEngine.Clay | `Clay/` |
| XEngine.Crumb | `Crumb/` |
| XEngine.Drift | `Drift/` |
| XEngine.Echo | `Echo/` |
| XEngine.Graphite (+ ShaderDef, ShaderDef.Compiler) | `Graphite/` |
| XEngine.Origami | `Origami/` |
| XEngine.Paper | `Paper/` |
| XEngine.Photonic | `Photonic/` |
| XEngine.Quill | `Quill/` |
| XEngine.Rosetta | `Rosetta/` |
| XEngine.Scribe | `Scribe/` |
| XEngine.Slang | `Slang/` |
| XEngine.Unwrapper | `Unwrapper/` |
| XEngine.Vector | `Vector/` |
| XEngine.Wicked | `Wicked/` |

### Build configuration

`Directory.Build.props` in this folder inherits the engine-repo-root settings and
re-declares the Anthology-specific `$(XEngineTargetFrameworks)` /
`$(XEngineTargetFrameworksCompat)` framework sets that each library csproj opts into.

NuGet publishing metadata from upstream is intentionally not carried over — these
projects are consumed as source and never packed.

### Slang native binaries

`Slang/Native/lib/<RID>/` contains pre-built native libraries for the Slang shader
compiler (v2026.12, MIT), fetched from https://github.com/shader-slang/slang releases
via `Slang/Native/UpdateSources.cs`. Six RIDs are committed: windows-x64,
windows-arm64, linux-x64, linux-arm64, macos-x64, macos-arm64.

See `../THIRD_PARTY_NOTICES.md` for full license text.

## wgpu-native (WebGPU desktop host)

- **Upstream**: https://github.com/gfx-rs/wgpu-native (`v29.0.1.1`, MIT OR Apache-2.0; `webgpu.h` BSD-3-Clause)
- **Location**: `wgpu-native/`
- **Used for**: desktop `WebGPUGraphicsDevice` (`DllImport("wgpu_native")`)

See `wgpu-native/README.md` for the refresh procedure. Only `include/webgpu/webgpu.h` and
per-RID `runtimes/` shared libraries are committed.

## DotRecast (Recast / Detour)

- **Upstream**: https://github.com/ikpil/DotRecast (zlib; Recast Navigation lineage)
- **Location**: `DotRecast/`
- **Used for**: NavMesh bake and pathfinding (`Packages/com.xengine.ai.navigation/`)

Refresh from a local checkout: `powershell -File DotRecast/UpdateSources.ps1 <DotRecast-src>`

## Jitter Physics 2

- **Upstream**: https://github.com/notgiven688/jitterphysics2 (MIT; commit `8bf70b32`)
- **Location**: `Jitter2/`
- **Used for**: per-scene 3D physics (`PhysicsWorld`, `Rigidbody3D`, colliders, joints)
- **Modified**: yes — see `Jitter2/PATCHES.md`. Refresh: `powershell -File Jitter2/UpdateSources.ps1 <jitterphysics2-checkout>`

