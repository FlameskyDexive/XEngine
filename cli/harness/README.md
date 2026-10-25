# XEngine AI Harness (agent 配套层)

The shippable agent-harness layer: what makes an XEngine **game project** immediately
agent-operable. Same library (`Tools/XEngine.Ai.Harness`) powers three entry points:

- **Editor** — `Project.Create` seeds the harness into every new game project (best-effort).
- **CLI** — `xengine-cli harness-install / harness-status / harness-upgrade` seeds or refreshes an
  existing project (e.g. created before the harness existed, or after an engine upgrade).
- Templates (AGENTS.md guide, skills manifest) are **embedded resources** of
  `XEngine.Ai.Harness.dll` — no on-disk template layout to break.

## What gets installed (三件套 + 技能包)

| Piece | Path in the project | Content |
|---|---|---|
| MCP wiring | `.mcp.json` | `xengine` (offline CLI host) + `xengine-live` (`XEngine.Editor --serve`, when the editor binary is found) |
| Agent guide | `AGENTS.md` | Project-specific agent instructions; placeholders resolved at install time |
| Skills | `.xengine/harness/skills/<skill>/SKILL.md` | The manifest-curated game-dev skills (engine-repo-only skills excluded) |
| Stamp | `.xengine/harness.json` | Harness version, edition, engine root, file/skill inventory — provenance + upgrade detection |

## Editions (引擎开发版 vs 用户版)

The same `xengine-cli` binary serves two audiences; the edition decides the tool surface:

- **engine-dev** — the engine repository itself (root has `XEngine.sln` + `Tools/`). Full surface,
  including engine-source tools (`code_symbols`, `run_tests`) and all future engine-internal tools
  (they carry `[AiToolEdition(AiEdition.EngineDev)]` and live in `EngineDevTools.cs` / siblings).
- **user** — a game project. Game-development tools only; engine-source tools never register
  server-side and never appear in `xengine_capabilities`.

Resolution: `--edition engine-dev|user|auto` (default `auto` = layout detection). The repo-root
[`.mcp.json`](../../.mcp.json) pins `--edition engine-dev` explicitly; the harness installs into
game projects always carry the user wiring.

## Engine layouts recognized (EngineLocator)

| Layout | Marker | Skills source | CLI invocation |
|---|---|---|---|
| Engine repo (dev) | up-walk finds `XEngine.sln` + `Tools/` | `cli/skills/` | `dotnet Tools/XEngine.Ai.Host/bin/Debug/net10.0/xengine-cli.dll` |
| Shipped install | up-walk finds `harness/skills/` | `harness/skills/` | `tools/xengine-cli(.exe)` |

Up-walk runs from the project root and then the process base directory, so both a project inside
the engine repo and a temp project driven by a dev/shipped binary resolve.

## Release packaging

`.github/workflows/release.yml` stages `tools/` (published `xengine-cli`) and `harness/skills/`
(pruned to the manifest) into the editor install zip — that IS the shipped layout above. macOS keeps
`tools/` + `harness/` at the extraction root next to `XEngine.app`; the up-walk reaches them from
inside the bundle.

## Maintaining the skill set

Edit `Tools/XEngine.Ai.Harness/Templates/skills.manifest` (one skill dir name per line). Skills
live in [`cli/skills/`](../skills/); `HarnessInstallTests.ManifestSkills_AllExistInTheEngineRepo`
fails when the manifest drifts from what exists.
