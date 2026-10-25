# Jitter Physics 2 (vendored, modified)

XEngine's 3D physics kernel. Source: https://github.com/notgiven688/jitterphysics2
License: MIT (`LICENSE`). See repo-root `THIRD_PARTY_NOTICES.md`.

- **Vendored at**: commit `8bf70b32` (`2.8.9-1-g8bf70b32`, one commit past the `2.8.9` tag)
- **Consumed as**: `ProjectReference` from `XEngine.Runtime` (previously the `Jitter2` NuGet package)
- **Modified**: yes — see [`PATCHES.md`](PATCHES.md)

## Layout

- `Jitter2/` — the kernel (96 files, zero external dependencies). This is the tree XEngine modifies.
- `Tests/` — upstream's `JitterTests` suite (NUnit), vendored as the regression net for our
  kernel modifications. Must be green before and after every patch.
- `.upstream-baseline/` — pristine upstream snapshot, never edited and never referenced by a
  project. Exists so a version bump can be resolved as a three-way merge instead of a guess.
- `UpdateSources.ps1` — refreshes the baseline from a local checkout; `-Force` also overwrites
  the live trees.

## Why source instead of the NuGet package

XEngine welds Unity semantics that the upstream API cannot express from the outside:

- Trigger volumes need overlap detection that creates no contact constraint. Upstream has no
  sensor concept, and emulating one through `INarrowPhaseFilter` abuses a filter for its side
  effects while still paying for arbiter allocation and solver iteration.
- Per-collider physics materials need friction/restitution resolved per shape pair. Upstream
  resolves them once per arbiter from per-*body* values with a hardcoded `Max` combine.
- Narrow/broad phase filters run concurrently but receive no worker index, forcing consumers to
  build their own thread-safe side tables for what should be per-worker scratch space.

## Upgrading to a newer upstream version

1. `powershell -File UpdateSources.ps1 <jitterphysics2-checkout>` — refreshes
   `.upstream-baseline/` only.
2. `git diff --stat -- .upstream-baseline` — what upstream changed.
3. `git diff --no-index .upstream-baseline/Jitter2 Jitter2` — our patches, for reapplying.
4. Merge, then run both `Tests/` (upstream suite) and `XEngine.Runtime.Test` before committing
   the baseline together with the live source.

Do not use `-Force` while unrecorded local patches exist.
