// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.AnimationEditor;
using XEngine.OrigamiUI;
using XEngine.PaperUI;

namespace XEngine.Editor.GUI.Panels;

public partial class AnimationClipWindow
{
    private bool _recording;
    private bool _showTimeInFrames = true;
    private float? _lastAddEventTime;

    public bool IsPreviewPlaying
    {
        get => _isPlaying;
        set
        {
            if (value) IsRecording = false;
            _isPlaying = value;
            if (value)
                PublishContinuousPresent(true, ref _countedContinuous);
        }
    }

    public bool IsRecording
    {
        get => _recording;
        set
        {
            if (value == _recording) return;
            if (value)
            {
                if (_model.Clip == null || _model.Root == null || _model.ClipIsReadOnly)
                    return;
                _isPlaying = false;
                _recording = true;
                AnimationPreviewSession.Instance.Stop();
                AnimationRecording.Instance.Begin(_model.Root, _model.Clip);
            }
            else
            {
                StopRecordingSession();
            }
        }
    }

    public bool ShowTimeInFrames
    {
        get => _showTimeInFrames;
        set => _showTimeInFrames = value;
    }

    /// <summary>★ M45 — Scrub the playhead and sample preview at <paramref name="time"/> seconds.</summary>
    public void ScrubTo(float time)
    {
        if (_model.Clip != null && _model.Clip.Duration > 0f)
        {
            if (time < 0f) time = 0f;
            else if (time > _model.Clip.Duration) time = _model.Clip.Duration;
        }
        else if (time < 0f)
            time = 0f;
        _isPlaying = false;
        _playbackTime = time;
        if (!_recording)
            AnimationPreviewSession.Instance.Sync(_model.Clip, (float)_playbackTime, _model.Root);
    }

    public float? LastAddEventTime => _lastAddEventTime;

    public int PlaybackFrame
    {
        get => (int)MathF.Round((float)_playbackTime * _model.FrameRate);
        set
        {
            float fps = _model.FrameRate;
            if (fps <= 0f) fps = 30f;
            float t = value / fps;
            if (t < 0f) t = 0f;
            if (_model.Clip != null && _model.Clip.Duration > 0f && t > _model.Clip.Duration)
                t = _model.Clip.Duration;
            _playbackTime = t;
            _isPlaying = false;
        }
    }

    public float SampleRate
    {
        get => _model.FrameRate;
        set => _model.TrySetTicksPerSecond(value);
    }

    public bool GoToPrevKey()
    {
        if (!_model.TryFindPrevKeyTime((float)_playbackTime, out float t)) return false;
        _playbackTime = t;
        _isPlaying = false;
        return true;
    }

    public bool GoToNextKey()
    {
        if (!_model.TryFindNextKeyTime((float)_playbackTime, out float t)) return false;
        _playbackTime = t;
        _isPlaying = false;
        return true;
    }

    public bool AddKeyframeAtPlayhead()
    {
        if (_model.ClipIsReadOnly) return false;
        SnapshotClip();
        return _model.AddKeyframeAtTime((float)_playbackTime);
    }

    /// <summary>★ M39/M40 — Add an authored clip event at the playhead.</summary>
    public bool RequestAddEvent()
    {
        if (_model.Clip == null) return false;
        _lastAddEventTime = (float)_playbackTime;
        return AddClipEvent((float)_playbackTime, "NewEvent");
    }

    private void DrawM39ToolbarControls(Paper paper)
    {
        var previewBtn = Origami.Button(paper, "animclip_preview", _isPlaying ? "Stop" : "Preview", () =>
        {
            IsPreviewPlaying = !_isPlaying;
        }).Ghost().Height(20);
        if (_isPlaying) previewBtn.Primary();
        previewBtn.Show();

        var recBtn = Origami.Button(paper, "animclip_rec", "Rec", () =>
        {
            IsRecording = !_recording;
        }).Ghost().Height(20);
        if (_recording) recBtn.Danger();
        recBtn.Show();

        Origami.Button(paper, "animclip_prevkey", "◀K", () => GoToPrevKey())
            .Ghost().Height(20).Show();
        Origami.Button(paper, "animclip_nextkey", "K▶", () => GoToNextKey())
            .Ghost().Height(20).Show();
        Origami.Button(paper, "animclip_addkey", "Key", () => AddKeyframeAtPlayhead())
            .Ghost().Disabled(_model.ClipIsReadOnly).Height(20).Show();
        Origami.Button(paper, "animclip_addevent", "Event", () => RequestAddEvent())
            .Ghost().Height(20).Show();

        Origami.Label(paper, "animclip_framelbl", "F").Show();
        Origami.NumericField(paper, "animclip_frame", PlaybackFrame, v => PlaybackFrame = v)
            .Height(20).Show();

        Origami.Label(paper, "animclip_samplbl", "Samples").Show();
        Origami.NumericField(paper, "animclip_samples", SampleRate, v => SampleRate = v)
            .Height(20).ReadOnly(_model.ClipIsReadOnly).Show();

        Origami.Button(paper, "animclip_units", _showTimeInFrames ? "Frames" : "Seconds", () =>
        {
            _showTimeInFrames = !_showTimeInFrames;
        }).Ghost().Height(20).Show();

        string clock = _showTimeInFrames
            ? $"{PlaybackFrame} / {(int)MathF.Round(_model.Clip!.Duration * _model.FrameRate)}"
            : $"{_playbackTime:F2}s / {_model.Clip!.Duration:F2}s";
        Origami.Label(paper, "animclip_time", clock).Show();
    }
}
