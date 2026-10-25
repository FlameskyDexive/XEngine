// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Timeline;

namespace XEngine.Timeline.Editor;

/// <summary>
/// Inspector for <see cref="PlayableDirector"/>: track bindings above the default inspector,
/// plus playhead state and Play/Pause/Stop.
/// </summary>
[CustomEditor(typeof(PlayableDirector))]
public class PlayableDirectorEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var director = (PlayableDirector)target;
        TimelineAsset? ta = director.Timeline.Res;

        Origami.Header(paper, id + "_bh", "Track Bindings").Show();
        if (ta == null || ta.Tracks.Count == 0)
        {
            Origami.Label(paper, id + "_nb", "No timeline / no tracks.").Show();
        }
        else
        {
            var tracks = ta.Tracks;
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                int idx = i;
                string name = string.IsNullOrEmpty(track.Name) ? $"Track {i}" : track.Name;
                EditorGUI.Row(paper, $"{id}_tr{i}", $"{TimelineBindingUtil.TypeLabel(track)} {name}", () =>
                {
                    Origami.Label(paper, $"{id}_bn{idx}", TimelineBindingUtil.BoundName(director, track)).Show();
                    Origami.Button(paper, $"{id}_bind{idx}", "Bind Selection",
                        () => TimelineBindingUtil.BindSelection(director, track)).Show();
                    Origami.Button(paper, $"{id}_clr{idx}", "Clear",
                        () => TimelineBindingUtil.ClearBinding(director, track)).Show();
                });
            }
        }

        DrawDefaultInspector(paper, id, target);

        using (Origami.BeginReadOnlyScope())
        {
            Origami.Label(paper, id + "_time", $"Time: {director.Time:F3}s / {director.Duration:F3}s");
            Origami.Label(paper, id + "_state", director.IsPlaying ? "Playing" : "Paused");
        }

        using (paper.Row(id + "_btns").Enter())
        {
            Origami.Button(paper, id + "_play", "Play", () => director.Play());
            Origami.Button(paper, id + "_pause", "Pause", () => director.Pause());
            Origami.Button(paper, id + "_stop", "Stop", () => director.Stop());
        }
    }
}
