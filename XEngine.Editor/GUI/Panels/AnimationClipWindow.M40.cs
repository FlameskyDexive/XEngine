// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.GUI.Panels;

public partial class AnimationClipWindow
{
    private int _selectedEventIndex = -1;
    private bool _draggingEvent;
    private bool _eventDragUndoArmed;

    public int SelectedEventIndex => _selectedEventIndex;
    public int ClipEventCount => _model.Clip?.Events.Count ?? 0;

    /// <summary>Moves the selected event to <paramref name="time"/> (frame-snapped). Unity: drag a marker to retime it.</summary>
    public bool MoveSelectedEvent(float time, bool snapshot = true)
    {
        var clip = _model.Clip;
        if (clip == null || _model.ClipIsReadOnly) return false;
        if (_selectedEventIndex < 0 || _selectedEventIndex >= clip.Events.Count) return false;
        if (time < 0f) time = 0f;
        if (snapshot) SnapshotClip();
        AnimationClipEvent e = clip.Events[_selectedEventIndex];
        e.Time = _model.SnapToFrame(time);
        clip.Events[_selectedEventIndex] = e;
        _model.BumpContent();
        _model.BumpVisual();
        return true;
    }

    public bool AddClipEvent(float time, string functionName)
    {
        var clip = _model.Clip;
        if (clip == null || _model.ClipIsReadOnly) return false;
        if (time < 0f) time = 0f;
        SnapshotClip();
        clip.Events.Add(new AnimationClipEvent
        {
            Time = _model.SnapToFrame(time),
            FunctionName = string.IsNullOrEmpty(functionName) ? "NewEvent" : functionName,
            StringParam = "",
        });
        _selectedEventIndex = clip.Events.Count - 1;
        _lastAddEventTime = time;
        _model.BumpContent();
        _model.BumpVisual();
        return true;
    }

    public bool RemoveSelectedEvent()
    {
        var clip = _model.Clip;
        if (clip == null || _model.ClipIsReadOnly) return false;
        if (_selectedEventIndex < 0 || _selectedEventIndex >= clip.Events.Count) return false;
        SnapshotClip();
        clip.Events.RemoveAt(_selectedEventIndex);
        _selectedEventIndex = -1;
        _model.BumpContent();
        _model.BumpVisual();
        return true;
    }

    public bool SelectEvent(int index)
    {
        int n = ClipEventCount;
        if (index < 0 || index >= n) return false;
        _selectedEventIndex = index;
        _model.BumpVisual();
        return true;
    }

    public bool SetSelectedEventFunction(string name)
    {
        var clip = _model.Clip;
        if (clip == null || _selectedEventIndex < 0 || _selectedEventIndex >= clip.Events.Count)
            return false;
        AnimationClipEvent e = clip.Events[_selectedEventIndex];
        e.FunctionName = name ?? "";
        clip.Events[_selectedEventIndex] = e;
        _model.BumpContent();
        return true;
    }

    private void DrawEventTrack(Paper paper, float width)
    {
        float h = AnimationEditorChrome.EventTrackHeight;
        paper.Box("animclip_events")
            .Width(width).Height(h)
            .BackgroundColor(AnimationEditorChrome.Raised)
            .OnClick(0, (_, e) =>
            {
                float t = _model.ScreenXToTime((float)e.RelativePosition.X, width);
                int hit = HitEvent(t, width);
                if (hit >= 0) _selectedEventIndex = hit;
                else _selectedEventIndex = -1;
            })
            .OnDoubleClick(e =>
            {
                if (_model.ClipIsReadOnly) return;
                float t = _model.ScreenXToTime((float)e.RelativePosition.X, width);
                AddClipEvent(t, "NewEvent");
            })
            .OnDragStart(e =>
            {
                float t = _model.ScreenXToTime((float)e.StartPosition.X, width);
                int hit = HitEvent(t, width);
                _draggingEvent = hit >= 0 && !_model.ClipIsReadOnly;
                _eventDragUndoArmed = false;
                if (hit >= 0) _selectedEventIndex = hit;
            })
            .OnDragging(e =>
            {
                if (!_draggingEvent || _model.ClipIsReadOnly) return;
                if (!_eventDragUndoArmed)
                {
                    SnapshotClip();
                    _eventDragUndoArmed = true;
                }
                float t = _model.ScreenXToTime((float)e.RelativePosition.X, width);
                MoveSelectedEvent(t, snapshot: false);
            })
            .OnDragEnd(_ =>
            {
                _draggingEvent = false;
                _eventDragUndoArmed = false;
            })
            .OnPostLayout((handle, _) =>
            {
                _drawEventsWidth = width;
                PaintEvents(paper, ref handle);
            });
    }

    private void DrawEventInspector(Paper paper, float width)
    {
        var clip = _model.Clip;
        if (clip == null || _selectedEventIndex < 0 || _selectedEventIndex >= clip.Events.Count)
            return;
        AnimationClipEvent ev = clip.Events[_selectedEventIndex];
        using (paper.Row("animclip_evins").Width(width).Height(AnimationEditorChrome.EventInspectorHeight)
            .BackgroundColor(AnimationEditorChrome.Panel).Padding(4, 2, 2, 2).ColBetween(4).Enter())
        {
            Origami.Label(paper, "animclip_evfnl", "Function").Show();
            Origami.TextField(paper, "animclip_evfn", ev.FunctionName ?? "", v =>
            {
                SetSelectedEventFunction(v);
            }).Height(20).Show();
            Origami.NumericField(paper, "animclip_evf", ev.FloatParam, v =>
            {
                AnimationClipEvent e = clip.Events[_selectedEventIndex];
                e.FloatParam = v;
                clip.Events[_selectedEventIndex] = e;
            }).Height(20).Show();
            Origami.NumericField(paper, "animclip_evi", ev.IntParam, v =>
            {
                AnimationClipEvent e = clip.Events[_selectedEventIndex];
                e.IntParam = v;
                clip.Events[_selectedEventIndex] = e;
            }).Height(20).Show();
            Origami.TextField(paper, "animclip_evs", ev.StringParam ?? "", v =>
            {
                AnimationClipEvent e = clip.Events[_selectedEventIndex];
                e.StringParam = v ?? "";
                clip.Events[_selectedEventIndex] = e;
            }).Height(20).Show();
        }
    }

    private void DrawEventMarkers(Canvas canvas, Rect rect, float width)
    {
        var clip = _model.Clip;
        if (clip == null) return;
        Float2 origin = rect.Min;
        var events = clip.Events;
        for (int i = 0; i < events.Count; i++)
        {
            float x = origin.X + _model.TimeToScreenX(events[i].Time, width);
            bool sel = i == _selectedEventIndex;
            canvas.SetFillColor(sel ? AnimationEditorChrome.Selection32 : AnimationEditorChrome.Playhead32);
            float y = origin.Y + rect.Size.Y * 0.5f;
            canvas.MoveTo(x, y - 4);
            canvas.LineTo(x + 4, y);
            canvas.LineTo(x, y + 4);
            canvas.LineTo(x - 4, y);
            canvas.ClosePath();
            canvas.Fill();
        }
    }

    private int HitEvent(float time, float width)
    {
        var clip = _model.Clip;
        if (clip == null) return -1;
        float span = Math.Max(1e-4f, _model.ViewEndTime - _model.ViewStartTime);
        float radius = span * 8f / Math.Max(1f, width);
        int best = -1;
        float bestD = radius;
        var events = clip.Events;
        for (int i = 0; i < events.Count; i++)
        {
            float d = events[i].Time - time;
            if (d < 0f) d = -d;
            if (d <= bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }
}
