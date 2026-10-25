# XEngine patches to Unwrapper (UV unwrap)

Baseline for diffing: Anthology tag `v2.8.6` (commit `b3ab895d`), path `Unwrapper/` — see `UPSTREAM.txt`.
Retrieve any baseline file with `git show v2.8.6:Unwrapper/<path>`.

## Status

- Was vendored at **v2.8.6**; **synced to v3.3.0 on 2026-08-31** (M1.7).
- Upstream v2.8.6→v3.3.0: 14 files +810/−208 — new `ProjectionUnwrapper` (fast box-projection
  default path, conformal Lloyd/LinABF/LSCM now the expensive alternative), pipeline/options/tests.
- Referenced by **XEngine.Runtime + XEngine.Editor** (lightmap UV generation).
- Local drift was csproj-only; all content taken from upstream with the mechanical rename;
  csproj Description/Tags taken from upstream (with `prowl`→`xengine` tag) — no local content
  patches exist for this library.
