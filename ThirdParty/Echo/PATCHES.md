# XEngine patches to Echo (serialization)

Baseline for diffing: Anthology tag `v2.8.6` (commit `b3ab895d`), path `Echo/` — see `UPSTREAM.txt`.
Retrieve any baseline file with `git show v2.8.6:Echo/<path>`.

## Status

- Was vendored at **v2.8.6**; **synced to v3.3.0 on 2026-08-31** (M1.2 of the prowl-sync plan).
- Upstream v2.8.6→v3.3.0 brought the **Cloning subsystem** (new `Echo/Cloning/`, 6 files: graph
  clone with `ICloneFormat` hooks wired into Dictionary/HashSet formatters), `[DataMember]` opt-in
  support in `ReflectionUtils`, `CloneTypeInfo.ClearCache()` in `Serializer.ClearCache`, and
  `Cloning_Tests`/`DataMemberOptIn_Tests`.
- `XEngine.Echo.sln` still references the pre-rename `Tests/Tests.csproj` path (stale; build the
  csprojs directly). Not fixed to keep vendored diffs minimal.

## Local patches (all preserved through the v3.3.0 sync)

### 1. Source generator rewritten (`SerializerGenerator.cs`)

- **Files**: `Echo.SourceGenerator/SerializerGenerator.cs` (~984 lines, locally rewritten from the
  upstream original)
- **Motive**: emit structurally-identical-to-reflection serializer bodies, `[UnsafeAccessor]`
  field access, and diagnostics (`ECHO001`/`ECHO002`) — the reflection path stays the semantic
  source of truth; the generator is wired into `XEngine.Runtime` as an analyzer.
- **Upstream activity**: upstream has **not touched** `Echo.SourceGenerator/` through v3.3.0 —
  kept as-is during the sync. Re-verify on any future sync that touches this directory.

### 2. Reflection path: compiled getters/setters

- **Files**: `Echo/ReflectionUtils.cs`
- **Motive**: per-call `FieldInfo.GetValue/SetValue` is an order of magnitude slower per access.
- **Shape of change**: cached field metadata additionally carries compiled expression-tree
  getter/setter delegates (`using System.Linq.Expressions;`); the reflection serialization path
  invokes delegates instead of `FieldInfo` accessors.
- **Merged with upstream 2026-08-31**: upstream's `[DataMember]` opt-in in `shouldSerialize`
  landed on top; both changes coexist.

### 3. `Serializer.ClearCache` also clears the generated dispatch

- **Files**: `Echo/Serializer.cs`
- **Shape of change**: one added line `GeneratedSerializerDispatch.ClearCache();` next to the
  upstream caches (now alongside upstream's own `Cloning.CloneTypeInfo.ClearCache();` added in
  v3.3.0 — both kept).

## Verification (2026-08-31 sync)

- `Echo.Tests` (net9.0 target run with `DOTNET_ROLL_FORWARD=LatestMajor` on .NET 10): **762/762 pass**,
  including the upstream `Cloning_Tests` + `DataMemberOptIn_Tests` brought by the sync.
- Engine-side serialization regression `XEngine.Runtime.Test --filter FullyQualifiedName~Echo`: **35/35 pass**.
- Full `XEngine.sln` build: 0 errors.
