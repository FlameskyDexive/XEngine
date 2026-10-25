---
name: linq-performance
description: Applies XEngine's benchmark-backed LINQ policy and PR0001 allowlist. Use when writing, changing, reviewing, or diagnosing LINQ, collection queries, predicates, or materialization in engine code.
---

# LINQ Performance

Do not assume LINQ is slow or loops are fast. Operator, statically declared source type,
delegate capture, chain shape, materialization, input size, TieredPGO, and GC behavior
all affect the result.

## PR0001 allowlist

Permit only these measured shapes:

1. `array.ToList()` or `list.ToList()` outside `[HotPath]`, where the source is
   statically an array or `List<T>`.
2. `Any(static ...)` and `FirstOrDefault(static ...)` on arrays or `List<T>`.
3. `All(static ...)` and predicate `Count(static ...)` on `List<T>` only.

The explicit `static` lambda is mandatory:

```csharp
var copy = concreteList.ToList(); // outside [HotPath]
bool found = items.Any(static item => item.IsReady);
var first = array.FirstOrDefault(static item => item.IsReady);
int active = concreteList.Count(static item => item.IsActive);
bool valid = concreteList.All(static item => item.IsValid);
```

Use the source's static type. A runtime object that happens to be a list does not qualify
when declared as `IEnumerable<T>`, an iterator, `IReadOnlyCollection<T>`,
`Dictionary<TKey,TValue>.ValueCollection`, or another abstraction.

## Keep warning or use an explicit loop

- Capturing lambdas, method groups, and delegate variables.
- Parameterless `Any()`, `Count()`, and `FirstOrDefault()`; these were not benchmarked.
- Query expressions and `Where`, `Select`, `SelectMany`, `Cast`, `Concat`, `Distinct`,
  `OrderBy`, `ToArray`, or multi-operator materialization chains.
- `All(static ...)` or predicate `Count(static ...)` on arrays.
- Direct `ToList()` in `[HotPath]`.

When replacing LINQ, preserve deferred execution, enumeration count, mutation behavior,
ordering, exception behavior, and result type. Pre-size result collections when possible.

## Changing the policy

Never broaden this allowlist by intuition. Add or rerun representative benchmarks, then
update the analyzer, analyzer tests, this skill, and the benchmark report together.

Evidence and reproduction commands:
`docs/benchmarks/2026-07-23-linq-migration/README.md`.
