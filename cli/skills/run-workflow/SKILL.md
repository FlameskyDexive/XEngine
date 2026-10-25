---
name: run-workflow
description: Define and run declarative tool DAGs (.xengine/workflows/*.json) — chain scene edits, builds, diagnostics with dependencies and failure policies.
---

# run-workflow

Use when you want to chain multiple tool calls into one reproducible pipeline — e.g. "patch the
scene, then build, then report diagnostics". Workflows are JSON DAGs of tool calls with declared
dependencies and failure policies.

## List available workflows
```bash
xengine-cli workflow-list --project <root>
```
Or MCP: `workflow_list`.

## Run a workflow
```bash
xengine-cli workflow-run --project <root> --name <workflow> \
  --inputs '<json>' [--dry-run]
```
Or MCP: `workflow_run` (`name`, `inputs` JSON string, `dryRun`).

## Authoring a workflow
Put a `.json` file under `.xengine/workflows/`. Shape:
```json
{
  "name": "patch-and-build",
  "description": "Patch a value then build the project",
  "inputs": { "type": "object", "properties": { "editorExe": {"type":"string"}, "outputDir": {"type":"string"} }, "required": ["editorExe","outputDir"] },
  "steps": [
    { "name": "build", "tool": "build_project",
      "arguments": { "editorExe": "${inputs.editorExe}", "outputDir": "${inputs.outputDir}" },
      "dependsOn": [], "failurePolicy": "Stop" }
  ]
}
```
- `tool` is the MCP/CLI tool name (`build_project`, `scene_object_patch`, `code_symbols`,
  `scene_hierarchy`, `asset_search`, `asset_inspect`, `journal_query`, `scene_object_create/delete`,
  `run_headless`, `scene_list`).
- `dependsOn`: step names that must complete first (DAG; cycles rejected).
- `failurePolicy`: `Stop` (halt the DAG on failure) | `Continue` (proceed).
- Templating: `${inputs.x}` (string/number/bool injection) and `${steps.<name>.<field>}` (reference
  a prior step's result JSON, dotted paths supported, e.g. `${steps.build.afterHash}`).

## Notes
- Execution is synchronous (XEngine has no Domain Reload — no ticket/polling needed).
- `--dry-run` validates the DAG + resolves templates without executing any tool.
- Locomotion starter: copy [`cli/samples/workflows/author-locomotion.json`](../../samples/workflows/author-locomotion.json)
  to the project's `.xengine/workflows/` then `workflow_run --name author-locomotion`.
