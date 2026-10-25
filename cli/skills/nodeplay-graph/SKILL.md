# NodePlay CLI/MCP — Graph Tools

Offline graph tools (M20) and live editor tools (M21) over `.nodeplay` Echo-text assets.
The three modules share the format: Behaviour Trees (`Module: "bt"`), hierarchical state
machines (`"fsm"`), and multi-actor dialogue trees (`"dialogue"`).

## Offline (xengine-cli / MCP server)

| Tool | Purpose |
|---|---|
| `nodeplay_graph_inspect` | Read a graph: `$type` envelope + flat path/value list + SHA-256 content hash. |
| `nodeplay_graph_patch` | Drift-guarded Echo-path patching (`Nodes/$values/0/Name`, `PrimeNodeId`, …) with `--intent` provenance, idempotency key, `expectedBeforeHash`, `--dry-run`. |
| `nodeplay_graph_compile` | Pure validation: wire endpoints resolve, prime/start ids exist, duplicate/empty node ids, dangling/self connections. Returns issue list. |

```bash
xengine-cli nodeplay_graph_inspect --project NodePlayTestProject --graph Assets/NodePlay/SoldierChase.nodeplay
xengine-cli nodeplay_graph_compile --project NodePlayTestProject --graph Assets/NodePlay/SoldierChase.nodeplay
```

## Live editor (--serve MCP)

| Tool | Purpose |
|---|---|
| `nodeplay_open` | Open a `.nodeplay` in the graph editor window (main-thread dispatched). |
| `nodeplay_focus` | Select + pan to a node (by Id, display name, or comment); sets the Inspector target. |

Screenshots for evidence: `runtime_panel_screenshot panelType=XEngine.NodePlay.Editor.NodePlayWindow`.

## Format notes

- Flat Echo text — AI-diffable; every node carries its own `$type` envelope.
- `PrimeNodeId` (BT/dialogue) and `StartStateId` (FSM) are explicit ids, no index shuffling.
- Public blackboard variables (LocalBlackboard/Variables/$n/IsPublic) are the sub-graph
  parameter surface; connections reference stable node ids.
- Node type names live under `XEngine.NodePlay[.BT/.FSM/.Dialogue]` and custom scripts in
  `NodePlayCustom`.

## M21 live tools (added)

- `nodeplay_open { graphPath }` — opens the graph in the NodePlay editor window.
- `nodeplay_focus { node, graphPath? }` — select+pan to a node by Id/display-name/comment.

Both run on the editor main thread (in-process `--serve` only).
