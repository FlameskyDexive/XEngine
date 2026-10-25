# Compatibility matrix (first version, draft)

Status per node N3. "Rejected" constructs fail at load/scan/execution with a
located `ILError` (module, method, IL offset) — never a CLR fallback.

## IL / language shapes

| Shape | Status | Notes |
| --- | --- | --- |
| int/long/float/double arithmetic, comparisons, conversions | supported | differential-tested |
| checked overflow (`add.ovf`), div/rem by zero | supported | host exception mapping |
| branches, `switch` jump tables, recursion | supported | |
| static fields, static constructors, per-closed-generic statics | supported | |
| arrays (SZ), element refs (`ldelema`), bounds checks | supported | struct arrays included |
| structs: copy semantics, `ref`/`out`, by-ref `this` | supported | |
| interfaces, virtual/override, `base` calls, `is`/`as`, casts | supported | |
| generics: VM-side instantiation, constrained calls, generic statics | supported | interpreted types only |
| delegates, multicast `+`/`-`, closures (display classes), events | supported | |
| exceptions: try/catch/finally, rethrow, host + interpreted types | supported | |
| async methods over host-registered task types | supported (N2 carrier) | closed result types only |
| string concat/length/equality, primitive `ToString` | supported | via explicit bindings |
| box/unbox (interpreted + host primitives) | supported | |
| `Nullable<T>` patterns (`?.` value, `??` coalesce, HasValue, GetValueOrDefault) | **partial** | default bindings register HasValue/GetValueOrDefault/ctors; `initobj` local init and nullable boxing still rejected — avoid in hotfix style until retest |
| `calli`, `localloc`, `arglist`, `refany*`, `mkrefany` | rejected | plan §5.2 first-version contract |
| exception filters, `ldtoken`/`sizeof`/`initobj`/`cpobj`/`ldobj` | rejected | build-time scan reports located findings |
| `conv.ovf.*` family | rejected | |
| pointer/`Span`/ref-struct/SIMD shapes | rejected | |
| generic virtual methods, host-generic method calls | rejected | requires static AOT instantiations (N5) |

## Host bindings

- Only explicitly registered host members are callable; everything else fails
  with `missing host binding: Type::Member/params` (runtime) and appears in the
  scan report's `requiredBindings` (build time).
- Binding IDs: `ILBindingIds.IdFor(type, member, paramCount)` — SHA-256 over a
  schema-versioned normalized signature; stable across runs (never
  `GetHashCode`).
