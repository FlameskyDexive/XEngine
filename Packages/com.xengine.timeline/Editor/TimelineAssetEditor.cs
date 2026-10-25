// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Inspector;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;

namespace XEngine.Timeline.Editor;

/// <summary>
/// Inspector for a <see cref="TimelineAsset"/>: duration, track count, and Open Timeline Window.
/// </summary>
[CustomAssetEditor(typeof(TimelineAsset))]
public class TimelineAssetEditor : AssetImporterEditor
{
    private Guid _currentGuid;
    private bool _dirty;

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        if (_currentGuid != entry.Guid)
        {
            _currentGuid = entry.Guid;
            _dirty = false;
        }

        id = $"{id}_{entry.Guid:N}";
        Origami.Header(paper, $"{id}_h", "Timeline").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();

        var ta = asset as TimelineAsset;
        if (ta == null)
        {
            Origami.Label(paper, $"{id}_none", "TimelineAsset failed to load.").Show();
            return;
        }

        EditorGUI.Row(paper, $"{id}_dur", "Duration", () =>
            Origami.NumericField(paper, $"{id}_dur_v", (float)ta.Duration, v =>
            {
                if (v < 0.05f) v = 0.05f;
                ta.Duration = v;
                _dirty = true;
            }).Show());

        Origami.Label(paper, $"{id}_n", $"Tracks: {ta.Tracks.Count}").Show();

        Origami.Button(paper, $"{id}_open", "Open Timeline Window", () =>
        {
            var app = EditorApplication.Instance;
            if (app == null) return;
            app.OpenPanel(typeof(TimelineWindow));
            if (app.FindOpenPanel(typeof(TimelineWindow)) is TimelineWindow win)
                win.SetTimeline(ta);
        }).Show();

        if (!_dirty) return;

        Origami.Separator(paper, $"{id}_sep_save").Show();
        Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset", () =>
        {
            if (AnimationAssetEditorUtil.Save(ta, entry))
                _dirty = false;
        }).Show();
    }
}
