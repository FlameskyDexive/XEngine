---
name: performance-validation
description: Defines XEngine's performance measurement and regression workflow. Use when benchmarking, changing analyzers or runtime performance policy, validating optimizations, upgrading .NET, or documenting performance evidence.
---

# Performance Validation

Performance policy must be reproducible. Preserve benchmark source, environment metadata,
raw results, and conclusions for changes that affect project-wide guidance.

## Benchmark workflow

1. Establish a behaviorally equivalent baseline and candidate.
2. Run Release builds after warm-up with TieredPGO and intended host GC settings.
3. Test empty, one-item, small, medium, and large inputs and every relevant static source
   type.
4. Compare throughput, allocated bytes/op, Gen0/Gen1 collections, and confidence/noise.
5. For disputed results, inspect generated code and run a deterministic allocation probe.
6. Confirm behavior: ordering, deferred execution, enumeration count, exceptions, and
   mutation semantics.
7. Commit benchmark artifacts and update the decision record.

Do not use a single run or Debug timing as evidence. Re-run the LINQ suite after .NET
runtime upgrades before changing its allowlist.

## Runtime and analyzer configuration

- Keep Tiered Compilation, TieredPGO, Server GC, Concurrent GC, and RetainVM defaults in
  `Directory.Build.props` unless host-specific benchmark evidence justifies an exception.
- PR0001 remains a warning with a benchmark-backed allowlist. Do not promote it globally
  without validating targeted directories and preserving allowed forms.
- When changing an analyzer, update its tests and any related project skills/docs.

## Required verification

Analyzer changes:

```powershell
dotnet test Tools/XEngine.Analyzers.Tests/XEngine.Analyzers.Tests.csproj
```

Runtime changes:

```powershell
dotnet build XEngine.Runtime/XEngine.Runtime.csproj
```

Inspect PR0001–PR0007 diagnostics and run focused tests/benchmarks proportional to risk.
Treat unrelated package vulnerability warnings separately; do not hide them while
validating analyzer output.

Primary LINQ evidence:
`docs/benchmarks/2026-07-23-linq-migration/README.md`.
