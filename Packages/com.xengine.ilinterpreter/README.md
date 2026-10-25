# IL Interpreter

Official optional package: executes supported hot-update C# assemblies
("interpreted modules") on NativeAOT players by reading them as **data** — no
`Assembly.Load`, no runtime code generation. Target scenario: HarmonyOS ARM64
and desktop players that download new gameplay DLLs after install.

## Status

`0.1.0` — skeleton milestone (N1). The interpreted-assembly classification is
active with this package installed; the module loader and VM land in N2+.

## What N1 gives you

- An **Interpreted Assembly (IL VM)** toggle on every Assembly Definition.
- With the package installed, interpreted asmdefs compile to
  `Library/ILInterpreter/Modules/<name>/<name>.dll` instead of the ordinary
  script-assembly folder.
- Interpreted assemblies are never CLR-loaded by the editor, never
  auto-referenced, and never enter a player build's managed assembly closure
  (desktop or HarmonyOS AOT collection).
- Dependency-direction enforcement: a host (non-interpreted) assembly that
  references an interpreted one fails the compile plan with a specific error.
- Without this package installed, the asmdef flag is inert and nothing in the
  engine changes.

## Usage

1. Install the package (Package Manager → Official category).
2. Create an Assembly Definition, enable **Interpreted Assembly (IL VM)**.
3. Reference approved host contract assemblies from it (never the other way).
4. Later nodes: create hot-update modules, generate AOT bindings, check
   compatibility, build hot-update content, and run in interpreter mode.

See `Documentation~/index.md` for the full roadmap and compatibility model.
