# IL Interpreter

Executes supported hot-update C# assemblies on NativeAOT players by
interpreting ECMA-335 IL read as pure data.

## Model

- A NativeAOT player image fixes the VM, engine, host bridges and required
  generic instantiations. Hot-update DLLs never load through the CLR.
- Interpreted assemblies are marked with the **Interpreted Assembly (IL VM)**
  toggle on their Assembly Definition. They compile to
  `Library/ILInterpreter/Modules/<name>/` and are excluded from CLR loading,
  auto-reference and the player managed-assembly closure.
- Dependency direction: interpreted → interpreted/host contracts only. A host
  assembly referencing an interpreted assembly fails the compile plan.

## Nodes

| Node | Scope |
| --- | --- |
| N1 (this release) | package skeleton, interpreted classification, exclusions, validation |
| N2 | PE/metadata loader, basic stack-machine VM, error reporting |
| N3 | full first-version semantics, static host bindings, support matrix |
| N4 | ILBehaviour lifecycle, serialization schema, XTask bridging, domain unload |
| N5 | editor workflow, build hooks, hashing and receipts |
| N6 | signed content updates, A/B activation, rollback |
| N7 | performance, compatibility and stability convergence |
| N8/N9 | npm publish rehearsal and release |

## Compatibility contract (draft)

Supported and unsupported shapes are listed per node in
`Documentation~/compatibility.md` once the VM lands (N2+). Unsupported
constructs fail at module validation or build time with module hash, method
token and IL offset — never a silent CLR fallback.
