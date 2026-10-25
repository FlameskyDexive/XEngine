---
name: trace-history
description: Read how an XEngine scene/project evolved — query the operation journal to see the provenance (author, intent, timestamp, replayable delta) of past changes.
---

# trace-history

Use when you need to understand **why** a scene/project is in its current state — not just
what it is, but how it got there. The operation journal (`.xengine/journal/*.jsonl`) records
every edit (from the editor's Undo path and from AI `scene-object-*` tools) as a structured,
provenance-bearing, replayable delta.

This is the core of XEngine's process-aware data layer: an AI can answer "the fog is orange
because it was set to dusk for cutscene X" — the gap Unity YAML-only storage leaves.

## Query the journal
```bash
# All recent changes
xengine-cli journal-query --project <root>

# Filter by asset
xengine-cli journal-query --project <root> --asset "Assets/Scenes/Main.scene"

# Filter by who/tool
xengine-cli journal-query --project <root> --by-author "xengine-cli.scene-object-patch"

# Filter by intent text (case-insensitive substring)
xengine-cli journal-query --project <root> --intent-contains "dusk"
```
Or via MCP: `journal_query` (same filters: `targetAsset` / `byAuthor` / `intentContains`).

## What each entry tells you
- `author` / `source`: who/what made the change (a tool name, or `editor`).
- `intent`: the natural-language reason recorded at change time.
- `timestampUtc` / `recordedAtUtc`: when.
- `deltaText`: the serialized field-level delta (e.g. a `SetValue` on a precise path) —
  replayable via Echo's `ApplyDelta`, so you can reconstruct any past state.
- `beforeHash` / `afterHash`: content hashes for drift detection.

## Workflow tip
Pair with `inspect-scene`: see current state, then `journal-query` to learn how it evolved.
Before making an edit, check the journal so your change builds on documented history.
