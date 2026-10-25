# XTS v1 limitations and budgets

Authoritative list of what XTS v1 does **not** do, and the measured budgets
for what it does. Every limitation here is deliberate (plan
`2026-09-11-official-xts-package-plan.md` §7) and needs a new ADR plus an
acceptance node to change.

## Script surface

- **No `Atomics` / `SharedArrayBuffer`.** Disabled on every platform
  (`XTS_NO_ATOMICS`) so Windows/Editor and HarmonyOS expose identical JS
  surfaces. Script domains are single-threaded.
- **No Node.js APIs.** Bundles build with `--platform=browser`; a
  `node:*` import fails the build with a located diagnostic.
- **No runtime TypeScript compilation.** Scripts are checked (`tsc
  --noEmit`) and bundled (esbuild, ES2020) in the editor only.
- **No `eval`-style dynamic code from content at runtime** beyond what the
  engine itself evaluates; content ships as plain JS text (QuickJS bytecode
  is never downloaded — it is version-locked and unsigned).

## Host boundary

- Only primitives cross the native ABI: bool, int32, float64, bigint (i64),
  string, null/undefined. Objects/arrays/functions surface as `undefined`
  until explicitly bound.
- Structured values cross **by value as JSON documents** through the
  generated glue — never as live CLR references.
- Script functions handed to the host cross as **handles** into the
  environment's `__xts_handles` array. Invoke goes through an expression;
  released handles are invalid.
- **No CLR reflection at runtime.** Bindings are static, build-time
  declarations; open generics, `ref/out`, and pointer types are rejected
  with diagnostics at build time.
- Missing bindings fail at activation. There is no reflection fallback.

## Resource limits

| Limit | Default | Behavior on breach |
| --- | --- | --- |
| JS heap | per environment (`XtsRuntimeOptions.MemoryLimitBytes`) | eval/pump returns `Limit`; environment must be rebuilt (allocations pin until runtime death) |
| JS stack | QuickJS default | stack-overflow exception inside the script |
| Execution deadline | per eval / job batch (`SetExecutionDeadlineMs`) | interrupt; script fails with "interrupted"; env stays usable |
| Promise jobs per frame | `XtsFramePump.MaxJobsPerFrame` (64) | remainder waits for later frames |

## Memory / lifecycle

- A behaviour's allocations pin until its environment dies. A heap-limit
  failure is recovered by **rebuilding the environment** (safe point), not
  by patching the old one.
- The safe-point rebuild exports state via the script's own
  `exportState()`; a throwing `importState` aborts the rebuild and keeps the
  old domain.

## Measured budgets (Windows x64, Release, desktop VM)

See `docs/superpowers/evidence/xts/N10/n10-perf.json` for the full
percentile table. Highlights (P50):

- environment cold start ≈ 0.3 ms
- simple expression eval ≈ 3 µs
- frame pump (1000 behaviour instances) ≈ 15 µs/frame
- create+eval+destroy cycle ≈ 0.34 ms
- safe-point rebuild with 100 behaviours ≈ 1.5 ms

HarmonyOS numbers require the platform toolchain node; they are recorded as
environment-blocked until then.
