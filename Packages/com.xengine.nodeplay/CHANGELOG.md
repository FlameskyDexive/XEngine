# Changelog

All notable changes to the NodePlay package are documented here.

## 0.1.0 - 2026-08-27

- Optional-package skeleton (installByDefault: false, Runtime/Editor asmdefs, docs).
- Runtime core: flat Echo-text graph model with `$type` polymorphism; scoped blackboards
  (graph/object/global, slash-syntax, dynamic `_vars`), scene data-binding, converters;
  tri-state parameters; cooperative action/condition tasks; NPRunner host with clone-per-run
  instances and nested-subgraph engine (Write-In/Read-Out mappings, per-owner isolation).
- Behaviour trees: reactive Selector/Sequencer, Parallel policies, utility + probability
  selectors, 13 decorators (conditional gate, interruptor, guard, filter, iterator, …),
  action/condition/subtree leaves, runtime status coloring + breakpoints.
- Hierarchical FSMs: reactive/OnFinish/manual transitions, Normal/Stacked/Clean call modes,
  Any-State, OnEnter/Update/Exit pseudo-states, concurrent sub-graphs, Nested FSM/BT/DT.
- Dialogue trees: statement/choice/check/jumper/sub-dialogue nodes, `[var]` interpolation,
  SELF actor resolution, UI-decoupled request events + simple sample UI.
- Editor: graph window (Origami canvas, undo, copy/paste, JSON side-car import/export),
  tabbed side panels (Vars/Find/Edit/Log/Actors), add-node browser (categories/favorites/
  preferred/recent), minimap strip, console with node-jump, task wizard, preferred-types
  project settings page.
- 40 built-in tasks across seven categories; task-scaffold wizard.
- AI tooling: nodeplay_graph_inspect/patch/compile (offline, drift-guarded) +
  nodeplay_open/focus (live editor).
- Tests: 66/66 green in XEngine.Editor.Test (model, tasks, blackboard, runner, FSM, dialogue,
  browser, clipboard, task library).
