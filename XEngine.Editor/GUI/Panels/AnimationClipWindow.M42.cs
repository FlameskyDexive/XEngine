// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;

namespace XEngine.Editor.GUI.Panels;

public partial class AnimationClipWindow
{
    /// <summary>★ M42 — Drive record (write keys) or preview sample from the playhead.</summary>
    void TickRecordingOrPreview()
    {
        if (_recording)
            AnimationRecording.Instance.Tick((float)_playbackTime);
        else
            AnimationPreviewSession.Instance.Sync(_model.Clip, (float)_playbackTime, _model.Root);
    }

    /// <summary>★ M42 — Explicit key at the playhead (AI / tests). Channel is PosX..ScaleZ or a generic property name.</summary>
    public bool RecordKey(string path, string channel, float value, string? typeName = null)
    {
        if (_model.Clip == null || _model.ClipIsReadOnly) return false;
        return AnimationRecording.WriteKey(
            _model.Clip, _model.Root, path ?? "", channel, (float)_playbackTime, value, typeName);
    }

    void StopRecordingSession()
    {
        if (!_recording && !AnimationRecording.Instance.IsRecording)
            return;
        _recording = false;
        AnimationRecording.Instance.End();
    }

    void DrawRecordingBanner(Paper paper, float width)
    {
        using (paper.Row("animclip_rec_banner").Width(width).Height(22)
            .BackgroundColor(EditorTheme.Red500)
            .Padding(6, 2, 2, 0).Enter())
        {
            Origami.Label(paper, "animclip_rec_msg", "Recording — scene edits write keys at the playhead.")
                .Show();
        }
    }
}
