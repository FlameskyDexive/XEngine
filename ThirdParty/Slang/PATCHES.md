# XEngine patches to Slang (shader compiler wrapper)

Baseline for diffing: Anthology tag `v2.8.6` (commit `b3ab895d`), path `Slang/` — see `UPSTREAM.txt`.
Retrieve any baseline file with `git show v2.8.6:Slang/<path>`.

## Status

- Vendored at **v2.8.6** + mechanical rename (`Prowl.Slang.*` → `XEngine.Slang.*`, license header;
  csproj renamed to `XEngine.Slang*.csproj`).
- Upstream Anthology touched Slang in **0 commits** between v2.8.6 and v3.3.0 → nothing to sync this round.
- Referenced by **XEngine.Runtime** (desktop shader pipeline: Slang → SPIR-V/DXIL, DXC fallback).

## Patch log

Verified 2026-08-31 by per-file CRLF-normalized diff against `v2.8.6`. One real patch family —
**native COM proxy lifetime management** (leak fixes), all preserving upstream defaults:

### Native proxy/blob disposal

- **Files**: `Slang/Native/MicroCom/NativeComProxy.cs` (+Dispose support),
  `Slang/Native/Interfaces/ISlangBlob.cs` (+33), `Slang/Session.cs` (+22),
  `Slang/ComponentType.cs` (+22), `Slang/Native/MicroCom/ManagedComProxy.cs` (+4)
- **Motive**: every reflection/compile call materialized blobs and sessions that backed native
  memory via `NativeComProxy`, and nothing ever released them — a per-shader-reload native leak.
- **Shape of change**: `ISlangBlob` string/byte materializers now `Dispose()` the underlying
  `NativeComProxy` in a `finally` after copying out; `Session` and `ComponentType` implement
  `IDisposable` (`Unsafe.As<NativeComProxy>(…).Dispose()` + `_disposed` guard).
  `COMApi/ComProxyGenerator.cs` diff is rename-only (generated strings embed the namespace).
- **Note**: disposal is opt-in (Dispose methods) — objects without explicit Dispose behave
  exactly as upstream.

### Native binary layout

- The vendored tree tracks `Native/lib/<rid>/lib/*.so|dylib` and `Native/lib/<rid>/bin/*.dll`
  including **linux-arm64**, which upstream does not carry at those paths (upstream relies on
  package build output). These binaries are XEngine-tracked assets, not code patches — keep when
  syncing (respect the `[Bb]in/` gitignore exceptions in the root `.gitignore`).

Non-patch tracked extras: `Build/Editor/Verify/*` verify artifacts, `Tests/XEngine.Slang.Tests.csproj` (rename).
