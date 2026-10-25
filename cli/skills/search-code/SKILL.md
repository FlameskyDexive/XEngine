---
name: search-code
description: Search code in an XEngine project — find symbol declarations (code_symbols via Roslyn) or find any text via regex (find_in_file). Two complementary search tools.
---

# search-code

Use when an AI needs to **find something in the codebase**. Two complementary tools:

## Find symbol declarations (Roslyn-powered)
```bash
xengine-cli code-symbols --project <root> --query "Camera" --limit 20
```
- Searches the XEngine.sln symbol index (types + members) by name substring, case-insensitive.
- Returns `{name, kind, containingType, project, filePath, line, column}`.
- Requires XEngine.sln at the project root + .NET SDK on PATH.
- Use to find where a class/method/field is DEFINED.

## Find any text via regex (file contents)
```bash
# Single file:
xengine-cli find-in-file --project <root> --pattern "Debug\.Log" --path Assets/Scripts/Player.cs

# Glob (all .cs under Assets/Scripts/):
xengine-cli find-in-file --project <root> --pattern "class.*Controller" --glob "Scripts/**/*.cs"

# By extension (all .shader files):
xengine-cli find-in-file --project <root> --pattern "_MainColor" --glob ".shader"
```
- Returns `{relativePath, lineNumber, excerpt}` per match.
- `--path` is relative to Assets/ (e.g. `Scripts/Player.cs`, not `Assets/Scripts/Player.cs`).
- `--glob` patterns: `**/*.cs`, `Scripts/**/*.cs`, `.cs` (bare extension = suffix filter).
- Capped at `--limit` matches (default 100, max 500) with `hasMore`.
- Skips `Library/` and `.xengine/` (engine caches).
- Works on ANY text file (.cs, .shader, .mat, .scene, .json, .asset).

## When to use which
- "Where is the Camera class defined?" → `code_symbols` (precise, structured).
- "Where is Debug.Log called?" → `find_in_file` (finds usages, not just declarations).
- "What files reference this GUID?" → `find_in_file` with the GUID as the pattern.
- "What's in this config file?" → `find_in_file` on a single file, or `asset_data_inspect`.
