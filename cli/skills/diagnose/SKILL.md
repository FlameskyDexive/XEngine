---
name: diagnose
description: Diagnose environment, build, and code issues in an XEngine project via xengine_doctor (env check) + code_symbols (symbol lookup). Use when something is broken or you need to locate a type/member in the engine source.
---

# diagnose

Use when an AI needs to **figure out why something is broken** or **locate code**. Two complementary
offline tools: `xengine_doctor` checks the environment (SDK, solution, paths), `code_symbols` searches
the engine's Roslyn symbol index.

## 1. Environment + project health check
```bash
xengine-cli doctor --project <root>
```
Returns a structured report: each check is `Pass` / `Warning` / `Failed` with a fix hint. This is the
first thing to run when a build fails or a tool misbehaves — it catches missing SDK, stale solution,
wrong editor-exe path, etc.

## 2. Locate a symbol in the engine source
```bash
xengine-cli code-symbols --project <root> --query "Camera" --limit 20
```
- Searches the XEngine.sln symbol index (types + members) by name substring, case-insensitive.
- Requires the solution file (`XEngine.sln`) at the project root and the .NET SDK on PATH.
- Use this to find where a component/field/method lives before editing, or to resolve a type name
  from a `$type` string in a scene file.

## 3. Reusable prompt
Fetch the `diagnose_build_failure` prompt for the full build-failure playbook:
```
prompts/get → diagnose_build_failure
```

## Typical flow
1. `xengine_doctor` — is the environment healthy?
2. If a build failed, read the `build_project` result's `tail` for the error.
3. `code_symbols` — locate the symbol mentioned in the error.
4. Fix + rebuild.
