---
name: edit-timeline
description: Bind, scrub, and trim clips in the Timeline window on a .timeline asset or PlayableDirector.
---

# edit-timeline

Use when an agent needs to operate the Timeline (Director) window — bind tracks, scrub the
playhead, move/trim clips — in a live editor. Requires `XEngine.Editor --serve`.

## Select the asset, then screenshot

```csharp
// runtime_eval
var db = EditorAssetBackend.Instance;
var entry = db.GetEntry("Cutscene.timeline") ?? db.GetEntry("Assets/Cutscene.timeline");
Selection.Select(new XEngine.Editor.GUI.Panels.ContentItem {
    Name = "Cutscene.timeline",
    RelativePath = entry.Path,
    Guid = entry.Guid
});
```

Then `runtime_panel_screenshot` with
`panelType = "XEngine.Timeline.Editor.TimelineWindow"`.

Selecting a scene `PlayableDirector` also loads that director (track headers show Bind Selection).

Sample: [`Samples/AnimationDemo/Cutscene.timeline`](../../../Samples/AnimationDemo/Cutscene.timeline)
+ `AnimationDemo.scene` (`CutsceneDirector`). GUID `6666…6` (not the locomotion controller).

## Window actions (Origami / Paper, not Unity IMGUI)
- Ruler click/drag → `PlayableDirector.Time` + `Evaluate()`
- Track header **Bind Selection** → bind Hierarchy GO (`GetComponentInChildren`)
- Right-click: add Animation / Audio / Activation / Camera / Particle / Signal tracks and clips
- Drag clip body to move; drag edges to trim; BlendIn / BlendOut in the inspector
- Signal diamonds are selectable and draggable; Delete removes; mutations wrap `Undo.Snapshot`

Offline: `asset_inspect` / read the Echo `.timeline`. Package Editor compiles via ScriptCompiler
(not in the .sln).
