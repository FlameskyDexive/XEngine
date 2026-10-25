---
name: hot-path-performance
description: Enforces XEngine's CPU hot-path and zero-allocation practices. Use when changing update loops, simulation, scene callbacks, rendering CPU paths, command execution, pooling, caching, or code marked HotPath.
---

# Hot-Path Performance

Use `[HotPath]` for real per-frame or per-object critical paths. Do not annotate broad
call graphs merely to silence or trigger analyzers.

## Analyzer requirements

PR0002–PR0007 are errors in `[HotPath]`:

- No capturing lambdas.
- No class-enumerator `foreach`.
- No string concatenation or interpolation.
- No `await`.
- No `params` allocation.
- No new reference object or array allocation; `[Pool]` is the intentional exemption.

Apply the LINQ rules in `../linq-performance/SKILL.md` whenever collection operators are
involved.

## Implementation practices

- Prefer indexed loops for concrete arrays/lists when no benchmark allowlist applies.
- Cache a collection count when retrieving it is non-trivial; do not obscure trivial
  array length or concrete-list count access.
- Reuse frame-scoped lists, arrays, command buffers, render contexts, cull masks, and
  temporary data. Clear and refill retained buffers.
- Pre-size collections when the final size is known.
- Move exception construction and formatted error messages to
  `[MethodImpl(MethodImplOptions.NoInlining)]` cold helpers when the normal path must
  remain allocation-free.
- Avoid boxing, interface enumeration, repeated reflection, repeated property traversal,
  and repeated materialization. Cache stable results with an explicit invalidation path.
- Preserve deterministic ordering where rendering, serialization, or editor output relies
  on it.
- Pool only when ownership, clearing, maximum retention, and return paths are explicit.
  Do not trade a visible allocation for unbounded retained memory.

## Review checklist

1. Identify invocation frequency and expected collection sizes.
2. Check steady-state allocations, including delegates, enumerators, arrays, strings,
   exceptions, and hidden materialization.
3. Verify retained buffers cannot leak stale state across frames/scenes.
4. Confirm caches are invalidated on every relevant mutation.
5. Run the Runtime build and inspect PR0002–PR0007 diagnostics.
