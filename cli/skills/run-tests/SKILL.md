---
name: run-tests
description: Run xUnit tests in an XEngine project via 'dotnet test'. Get structured pass/fail/skip counts. Verify code changes didn't break anything.
---

# run-tests

Use when an AI needs to **verify code changes by running tests**. Wraps `dotnet test` and parses the
xUnit summary into structured counts.

## Run a specific test project
```bash
xengine-cli run-tests --project <root> \
  --target Tools/XEngine.Ai.Host.Tests/XEngine.Ai.Host.Tests.csproj \
  --timeout 120
```
Returns `{succeeded, exitCode, durationMs, passed, failed, skipped, total, outputTail}`.

## Run the whole solution
```bash
xengine-cli run-tests --project <root> --timeout 600
```
- Omit `--target` to auto-discover XEngine.sln at the project root.

## Filter tests
```bash
xengine-cli run-tests --project <root> \
  --target Tools/XEngine.Ai.Host.Tests/XEngine.Ai.Host.Tests.csproj \
  --filter "FullyQualifiedName~ScriptWriterTests"
```
- `--filter` is an xUnit filter expression (e.g. `FullyQualifiedName~Camera`, `Category=Smoke`).

## Parameters
- `--target`: path to a `.csproj` (one project) or `.sln` (whole solution). Omit for auto-discover.
- `--filter`: xUnit filter expression.
- `--timeout`: seconds (default 300). On timeout, kills the process and returns a clear message.

## Typical workflow
1. Make a code change (`script_apply_edits`).
2. `run-tests` to verify nothing broke.
3. If failed, read `outputTail` for the failure details.
4. Fix + re-run.

## Test discovery
XEngine uses xUnit. Test projects are `*.Tests.csproj` (e.g. `XEngine.Ai.Host.Tests`,
`XEngine.Data.Provenance.Tests`). There's no in-editor test runner — tests run via the standard
`dotnet test` CLI.
