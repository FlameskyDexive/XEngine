// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Panels;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Scribe;
using XEngine.Vector;

using TextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Timeline.Editor;

/// <summary>
/// Timeline (Director) window: track headers with Bind Selection, time-ruler scrub,
/// clip blocks (move / trim / blend), and signal diamonds.
/// </summary>
public partial class TimelineWindow : DockPanel, IScriptReloadCleanup
{
    public override string Title => "Timeline";

    private enum DragKind : byte { None, Scrub, Move, TrimStart, TrimEnd, Signal }

    private PlayableDirector? _director;
    private TimelineAsset? _timelineAsset;
    private const float RowHeight = AnimationEditorChrome.RowHeight;
    private const float TrackListWidth = 168f;
    private const float ToolbarHeight = AnimationEditorChrome.ToolbarHeight;
    private const float RulerHeight = AnimationEditorChrome.RulerHeight;
    private const float InspectorHeight = 78f;
    private FontFile? _font;
    private bool _subscribed;
    private int _selectedTrackIndex = -1;
    private int _selectedClipIndex = -1;
    private int _selectedMarkerIndex = -1;
    private double _previewTime;
    private DragKind _dragKind;
    private int _dragTrack = -1;
    private int _dragClip = -1;
    private double _dragOrigStart;
    private double _dragOrigDuration;
    private double _dragPointerTime;
    private bool _dragUndoArmed;

    private TimelineAsset? ActiveAsset => _director?.Timeline.Res ?? _timelineAsset;

    private double PlayheadTime => _director is not null ? _director.Time : _previewTime;

    public void OnScriptReloadCleanup()
    {
        DisposePaintRegions();
        PublishContinuousPresent(false, ref _countedContinuous);
        _director = null;
        _timelineAsset = null;
    }

    public override void OnClosed()
    {
        DisposePaintRegions();
        PublishContinuousPresent(false, ref _countedContinuous);
        if (_subscribed) { Selection.OnSelectionChanged -= TryLoadFromSelection; _subscribed = false; }
    }

    public void SetDirector(PlayableDirector? director)
    {
        _director = director;
        _timelineAsset = null;
        _font ??= XEngine.Editor.Theming.EditorTheme.Font;
        _selectedTrackIndex = _selectedClipIndex = _selectedMarkerIndex = -1;
        BumpContent();
        BumpVisual();
    }

    public void SetTimeline(TimelineAsset? asset)
    {
        _timelineAsset = asset;
        _director = null;
        _font ??= XEngine.Editor.Theming.EditorTheme.Font;
        _selectedTrackIndex = _selectedClipIndex = _selectedMarkerIndex = -1;
        BumpContent();
        BumpVisual();
    }

    [MenuItem("Window/Animation/Timeline", priority: 22)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(TimelineWindow));

    private void TryLoadFromSelection()
    {
        if (Selection.ActiveObject is GameObject go)
        {
            var dir = go.GetComponent<PlayableDirector>();
            if (dir is not null && dir != _director)
                SetDirector(dir);
            return;
        }

        if (Selection.ActiveObject is not ContentItem ci || ci.IsFolder || ci.IsSubAsset) return;
        if (!ci.RelativePath.EndsWith(".timeline", StringComparison.OrdinalIgnoreCase)) return;

        Guid guid = ci.Guid;
        if (guid == Guid.Empty)
        {
            var entry = EditorAssetBackend.Instance?.GetEntry(ci.RelativePath);
            guid = entry?.Guid ?? Guid.Empty;
        }
        if (guid == Guid.Empty) return;

        var timeline = AssetDatabase.Get(guid) as TimelineAsset;
        if (timeline is not null && timeline != _timelineAsset)
            SetTimeline(timeline);
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        using var profilerScope = EditorProfiler.BeginCpuScope("Timeline");
        if (!_subscribed)
        {
            Selection.OnSelectionChanged += TryLoadFromSelection;
            _subscribed = true;
            TryLoadFromSelection();
        }

        TickPreviewTime();
        PublishContinuousPresent(IsPreviewPlaying || _dragKind != DragKind.None, ref _countedContinuous);

        var ta = ActiveAsset;
        if (paper.IsKeyPressed(PaperKey.Delete) && ta is not null)
            DeleteSelection(ta);

        using (paper.Column("tl_root").Size(width, height).Enter())
        {
            DrawToolbar(paper, width);

            if (ta is null)
            {
                paper.Box("tl_empty").Width(width).Height(height - ToolbarHeight)
                    .BackgroundColor(AnimationEditorChrome.Canvas)
                    .Text(AnimationEditorChrome.EmptyTimeline, XEngine.Editor.Theming.EditorTheme.DefaultFont!)
                    .TextColor(AnimationEditorChrome.TextMuted)
                    .Alignment(TextAlignment.MiddleCenter);
                return;
            }

            float bodyHeight = height - ToolbarHeight - InspectorHeight;
            if (bodyHeight < 40f) bodyHeight = 40f;

            using (paper.Row("tl_body").Width(width).Height(bodyHeight).Enter())
            {
                DrawTrackHeaders(paper, ta, bodyHeight);
                DrawTimelinePane(paper, ta, width - TrackListWidth, bodyHeight);
            }

            DrawInspector(paper, ta, width);
        }
    }

    private void DrawToolbar(Paper paper, float width)
    {
        using (paper.Row("tl_tb").Height(ToolbarHeight)
            .BackgroundColor(AnimationEditorChrome.Panel)
            .Padding(6, 2, 2, 0).ColBetween(4).Enter())
        {
            var ta = ActiveAsset;
            Origami.Label(paper, "tl_name", ta?.Name ?? "(no timeline)").Show();

            bool hasDirector = _director is not null;
            using (Origami.BeginReadOnlyScope(!hasDirector))
            {
                Origami.Button(paper, "tl_play", "▶ Play", () => _director?.Play()).Show();
                Origami.Button(paper, "tl_pause", "❚❚ Pause", () => _director?.Pause()).Show();
                Origami.Button(paper, "tl_stop", "■ Stop", () => _director?.Stop()).Show();
            }

            Origami.Label(paper, "tl_time", $"{PlayheadTime:F2}s").Show();

            if (ta is not null)
                Origami.Button(paper, "tl_save", "Save", SaveTimeline).Show();
        }
    }

    private void DrawTrackHeaders(Paper paper, TimelineAsset ta, float height)
    {
        using (paper.Column("tl_heads").Width(TrackListWidth).Height(height)
            .BackgroundColor(AnimationEditorChrome.Panel).Enter())
        {
            AttachPaintRegion(paper, ref _headerPaintRegion);
            paper.Box("tl_head_ruler").Width(TrackListWidth).Height(RulerHeight)
                .BackgroundColor(AnimationEditorChrome.Raised)
                .Text("Tracks", XEngine.Editor.Theming.EditorTheme.DefaultFont!)
                .TextColor(AnimationEditorChrome.TextMuted)
                .FontSize(11)
                .Alignment(TextAlignment.MiddleLeft);

            var tracks = ta.Tracks;
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                bool selected = i == _selectedTrackIndex;
                string bound = _director is null
                    ? ""
                    : " · " + TimelineBindingUtil.BoundName(_director, track);
                using (paper.Row($"tl_th{i}").Width(TrackListWidth).Height(RowHeight)
                    .BackgroundColor(selected ? AnimationEditorChrome.Selection : AnimationEditorChrome.Panel)
                    .Padding(4, 2, 0, 0).RowBetween(2).Enter())
                {
                    Origami.Label(paper, $"tl_tn{i}",
                        $"{TimelineBindingUtil.TypeLabel(track)} {track.Name}{bound}").Show();
                    if (_director is not null)
                    {
                        Origami.Button(paper, $"tl_bind{i}", "Bind",
                            () => { TimelineBindingUtil.BindSelection(_director, track); BumpVisual(); }).Show();
                    }
                }
            }
        }
    }

    private void DrawTimelinePane(Paper paper, TimelineAsset ta, float paneWidth, float height)
    {
        if (paneWidth < 8f) paneWidth = 8f;
        using (paper.Column("tl_pane").Width(paneWidth).Height(height).Enter())
        {
            AttachPaintRegion(paper, ref _sheetPaintRegion);
            _drawPaneWidth = paneWidth;
            _drawAsset = ta;
            paper.Box("tl_ruler").Width(paneWidth).Height(RulerHeight)
                .BackgroundColor(AnimationEditorChrome.Raised)
                .OnClick(0, (_, e) => ScrubTo((float)e.RelativePosition.X, paneWidth, ta))
                .OnDragStart(_ => _dragKind = DragKind.Scrub)
                .OnDragging(e => ScrubTo((float)e.RelativePosition.X, paneWidth, ta))
                .OnDragEnd(_ => _dragKind = DragKind.None)
                .OnPostLayout((handle, _) =>
                {
                    PaintRuler(paper, ref handle);
                });

            float sheetH = height - RulerHeight;
            _drawSheetHeight = sheetH;
            paper.Box("tl_sheet").Width(paneWidth).Height(sheetH)
                .BackgroundColor(AnimationEditorChrome.Canvas)
                .OnClick(0, (_, e) => HandleSheetClick((float)e.RelativePosition.X, (float)e.RelativePosition.Y, paneWidth, ta))
                .OnClick(1, (_, e) => HandleSheetRightClick((float)e.RelativePosition.X, (float)e.RelativePosition.Y, paneWidth, ta))
                .OnDragStart(e => HandleSheetDragStart((float)e.StartPosition.X, (float)e.StartPosition.Y, paneWidth, ta))
                .OnDragging(e => HandleSheetDragging((float)e.RelativePosition.X, paneWidth, ta))
                .OnDragEnd(_ =>
                {
                    if (_dragKind is DragKind.Move or DragKind.TrimStart or DragKind.TrimEnd or DragKind.Signal)
                        AfterClipEdit(ta);
                    _dragKind = DragKind.None;
                    _dragUndoArmed = false;
                })
                .OnPostLayout((handle, _) =>
                {
                    PaintSheet(paper, ref handle);
                });
        }
    }

    private void DrawInspector(Paper paper, TimelineAsset ta, float width)
    {
        using (paper.Column("tl_ins").Width(width).Height(InspectorHeight)
            .BackgroundColor(AnimationEditorChrome.Panel).Padding(6, 4, 2, 2).Enter())
        {
            if (_selectedTrackIndex < 0 || _selectedTrackIndex >= ta.Tracks.Count)
            {
                Origami.Label(paper, "tl_ins_empty", "Select a clip or signal.").Show();
                return;
            }

            var track = ta.Tracks[_selectedTrackIndex];
            if (_selectedMarkerIndex >= 0 && track is SignalTrack sig
                && _selectedMarkerIndex < sig.Markers.Count)
            {
                var m = sig.Markers[_selectedMarkerIndex];
                Origami.Label(paper, "tl_ins_sh", $"Signal  {m.Name}").Show();
                EditorGUI.Row(paper, "tl_ins_st", "Time", () =>
                    Origami.NumericField(paper, "tl_ins_stv", (float)m.Time, v =>
                    {
                        Snapshot(ta);
                        m.Time = v < 0f ? 0f : v;
                        GrowDuration(ta, m.Time);
                    }).Show());
                EditorGUI.Row(paper, "tl_ins_sn", "Name", () =>
                    Origami.TextField(paper, "tl_ins_snv", m.Name, v =>
                    {
                        Snapshot(ta);
                        m.Name = v ?? "";
                    }).Show());
                return;
            }

            if (_selectedClipIndex < 0 || !TryGetSchedule(track, _selectedClipIndex, out var sched))
            {
                Origami.Label(paper, "tl_ins_trk",
                    $"{TimelineBindingUtil.TypeLabel(track)}  {track.Name}  ·  {TimelineBindingUtil.BoundName(_director, track)}").Show();
                return;
            }

            Origami.Label(paper, "tl_ins_ch", $"{track.Name}  clip {_selectedClipIndex}").Show();
            using (paper.Row("tl_ins_row").Enter())
            {
                EditorGUI.Row(paper, "tl_ins_s", "Start", () =>
                    Origami.NumericField(paper, "tl_ins_sv", (float)sched.Start, v =>
                    {
                        Snapshot(ta);
                        sched.Start = v < 0f ? 0f : v;
                        SetSchedule(track, _selectedClipIndex, sched);
                        GrowDuration(ta, sched.End);
                        AfterClipEdit(ta);
                    }).Show());
                EditorGUI.Row(paper, "tl_ins_d", "Duration", () =>
                    Origami.NumericField(paper, "tl_ins_dv", (float)sched.Duration, v =>
                    {
                        Snapshot(ta);
                        sched.Duration = v < (float)TimelineEditModel.MinClipDuration
                            ? TimelineEditModel.MinClipDuration : v;
                        SetSchedule(track, _selectedClipIndex, sched);
                        GrowDuration(ta, sched.End);
                        AfterClipEdit(ta);
                    }).Show());
                EditorGUI.Row(paper, "tl_ins_bi", "BlendIn", () =>
                    Origami.NumericField(paper, "tl_ins_biv", sched.BlendIn, v =>
                    {
                        Snapshot(ta);
                        sched.BlendIn = v < 0f ? 0f : v;
                        SetSchedule(track, _selectedClipIndex, sched);
                        AfterClipEdit(ta);
                    }).Show());
                EditorGUI.Row(paper, "tl_ins_bo", "BlendOut", () =>
                    Origami.NumericField(paper, "tl_ins_bov", sched.BlendOut, v =>
                    {
                        Snapshot(ta);
                        sched.BlendOut = v < 0f ? 0f : v;
                        SetSchedule(track, _selectedClipIndex, sched);
                        AfterClipEdit(ta);
                    }).Show());
            }
        }
    }

    private void SaveTimeline()
    {
        var ta = ActiveAsset;
        if (ta is null) return;
        try
        {
            var echo = XEngine.Echo.Serializer.Serialize(ta, XEngine.Echo.TypeMode.Aggressive);
            string? path = ta.AssetPath;
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                echo.WriteToString(new System.IO.FileInfo(path));
                Runtime.Debug.Log($"[Timeline] Saved to {path}");
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[Timeline] Save failed: {ex.Message}");
        }
    }

    private static Color32 TrackColor(TimelineTrack track) => track switch
    {
        AnimationTrack => AnimationEditorChrome.TrackAnimation,
        AudioTrack => AnimationEditorChrome.TrackAudio,
        CameraTrack => AnimationEditorChrome.TrackCamera,
        ActivationTrack => AnimationEditorChrome.TrackActivation,
        ParticleTrack => AnimationEditorChrome.TrackParticle,
        SignalTrack => AnimationEditorChrome.TrackSignal,
        _ => AnimationEditorChrome.TrackUnknown,
    };

    private void DrawTimeRuler(Canvas canvas, Rect rect, float timelineWidth, TimelineAsset ta)
    {
        var font = _font ?? XEngine.Editor.Theming.EditorTheme.Font;
        Float2 origin = rect.Min;
        double duration = DurationOf(ta);

        double step = duration <= 1 ? 0.25 : duration <= 5 ? 0.5 : duration <= 10 ? 1 : duration <= 30 ? 2 : 5;
        for (double t = 0; t <= duration; t += step)
        {
            float x = origin.X + TimelineEditModel.TimeToScreenX(t, timelineWidth, duration);
            canvas.SetStrokeColor(AnimationEditorChrome.TextMuted32);
            canvas.SetStrokeWidth(1f);
            canvas.MoveTo(x, origin.Y);
            canvas.LineTo(x, origin.Y + RulerHeight);
            canvas.Stroke();
            if (font is not null)
                canvas.DrawText($"{t:F1}", x + 2, origin.Y + 4, AnimationEditorChrome.Text32, 10f, font);
        }

        float px = origin.X + TimelineEditModel.TimeToScreenX(PlayheadTime, timelineWidth, duration);
        canvas.SetStrokeColor(AnimationEditorChrome.Playhead32);
        canvas.SetStrokeWidth(1.5f);
        canvas.MoveTo(px, origin.Y);
        canvas.LineTo(px, origin.Y + RulerHeight);
        canvas.Stroke();
    }

    private void DrawSheet(Canvas canvas, Rect rect, float timelineWidth, float height, TimelineAsset ta)
    {
        var font = _font ?? XEngine.Editor.Theming.EditorTheme.Font;
        Float2 origin = rect.Min;
        double duration = DurationOf(ta);
        var tracks = ta.Tracks;

        for (int i = 0; i < tracks.Count; i++)
        {
            float y = origin.Y + i * RowHeight;
            canvas.RectFilled(origin.X, y, timelineWidth, RowHeight - 2f, AnimationEditorChrome.TrackRow32);
            DrawTrackClips(canvas, tracks[i], i, origin.X, y, timelineWidth, duration, TrackColor(tracks[i]), font);
        }

        float px = origin.X + TimelineEditModel.TimeToScreenX(PlayheadTime, timelineWidth, duration);
        canvas.SetStrokeColor(AnimationEditorChrome.Playhead32);
        canvas.SetStrokeWidth(1.5f);
        canvas.MoveTo(px, origin.Y);
        canvas.LineTo(px, origin.Y + tracks.Count * RowHeight);
        canvas.Stroke();
    }

    private void DrawTrackClips(Canvas canvas, TimelineTrack track, int trackIndex, float clipX, float y,
        float timelineWidth, double duration, Color32 color, FontFile? font)
    {
        float clipY = y + 3f;
        float clipH = RowHeight - 8f;
        bool trackSelected = trackIndex == _selectedTrackIndex;

        if (track is SignalTrack sig)
        {
            for (int m = 0; m < sig.Markers.Count; m++)
            {
                var marker = sig.Markers[m];
                float x = clipX + TimelineEditModel.TimeToScreenX(marker.Time, timelineWidth, duration);
                float cy = y + RowHeight * 0.5f;
                bool hi = trackSelected && m == _selectedMarkerIndex;
                canvas.SetFillColor(hi ? AnimationEditorChrome.Playhead32 : color);
                canvas.MoveTo(x, cy - 6f);
                canvas.LineTo(x + 5f, cy);
                canvas.LineTo(x, cy + 6f);
                canvas.LineTo(x - 5f, cy);
                canvas.ClosePath();
                canvas.Fill();
                if (font is not null)
                    canvas.DrawText(marker.Name, x + 7f, y + 5f, AnimationEditorChrome.TrackSignal, 9f, font);
            }
            return;
        }

        int count = ClipCount(track);
        for (int c = 0; c < count; c++)
        {
            if (!TryGetSchedule(track, c, out var sched)) continue;
            DrawClipBlock(canvas, sched, clipX, clipY, clipH, timelineWidth, duration, color, font,
                trackSelected && c == _selectedClipIndex);
        }
    }

    private static void DrawClipBlock(Canvas canvas, TimelineClipSchedule sched, float clipX, float clipY, float clipH,
        float timelineWidth, double duration, Color32 color, FontFile? font, bool highlight)
    {
        float x = clipX + TimelineEditModel.TimeToScreenX(sched.Start, timelineWidth, duration);
        float w = TimelineEditModel.TimeToScreenX(sched.Duration, timelineWidth, duration);
        if (w < 2f) w = 2f;
        Color32 blockColor = highlight
            ? new Color32((byte)Math.Min(255, color.R + 50), (byte)Math.Min(255, color.G + 50), (byte)Math.Min(255, color.B + 50), 255)
            : color;
        canvas.RoundedRectFilled(x, clipY, w, clipH, 3f, blockColor);
        if (highlight)
        {
            canvas.SetStrokeColor(AnimationEditorChrome.Playhead32);
            canvas.SetStrokeWidth(2f);
            canvas.RoundedRect(x, clipY, w, clipH, 3f);
            canvas.Stroke();
        }
        if (sched.BlendIn > 0f)
        {
            var fade = new Color32((byte)Math.Min(255, color.R + 40), (byte)Math.Min(255, color.G + 40), (byte)Math.Min(255, color.B + 40), 120);
            float bi = TimelineEditModel.TimeToScreenX(sched.BlendIn, timelineWidth, duration);
            canvas.RectFilled(x, clipY, Math.Min(bi, w), clipH, fade);
        }
        if (sched.BlendOut > 0f)
        {
            var fade = new Color32((byte)Math.Min(255, color.R + 40), (byte)Math.Min(255, color.G + 40), (byte)Math.Min(255, color.B + 40), 120);
            float bo = TimelineEditModel.TimeToScreenX(sched.BlendOut, timelineWidth, duration);
            canvas.RectFilled(x + w - Math.Min(bo, w), clipY, Math.Min(bo, w), clipH, fade);
        }
    }

    private void ScrubTo(float relX, float timelineWidth, TimelineAsset ta)
    {
        double t = TimelineEditModel.ScreenXToTime(relX, timelineWidth, DurationOf(ta));
        _previewTime = t;
        if (_director is null) return;
        _director.Pause();
        _director.Time = t;
        _director.Evaluate();
    }

    private void HandleSheetClick(float relX, float relY, float timelineWidth, TimelineAsset ta)
    {
        int trackIdx = TimelineEditModel.TrackIndexAt(relY, 0f, RowHeight, ta.Tracks.Count);
        if (trackIdx < 0)
        {
            _selectedTrackIndex = _selectedClipIndex = _selectedMarkerIndex = -1;
            BumpVisual();
            return;
        }

        _selectedTrackIndex = trackIdx;
        BumpVisual();
        var track = ta.Tracks[trackIdx];
        double duration = DurationOf(ta);

        if (track is SignalTrack sig)
        {
            _selectedClipIndex = -1;
            _selectedMarkerIndex = -1;
            for (int m = 0; m < sig.Markers.Count; m++)
            {
                float mx = TimelineEditModel.TimeToScreenX(sig.Markers[m].Time, timelineWidth, duration);
                if (TimelineEditModel.HitSignal(relX, mx))
                {
                    _selectedMarkerIndex = m;
                    return;
                }
            }
            return;
        }

        _selectedMarkerIndex = -1;
        double time = TimelineEditModel.ScreenXToTime(relX, timelineWidth, duration);
        _selectedClipIndex = FindClipAtTime(track, time);
    }

    private void HandleSheetRightClick(float relX, float relY, float timelineWidth, TimelineAsset ta)
    {
        int trackIdx = TimelineEditModel.TrackIndexAt(relY, 0f, RowHeight, ta.Tracks.Count);
        double time = TimelineEditModel.ScreenXToTime(relX, timelineWidth, DurationOf(ta));
        int clipIdx = trackIdx >= 0 ? FindClipAtTime(ta.Tracks[trackIdx], time) : -1;
        int markerIdx = -1;
        if (trackIdx >= 0 && ta.Tracks[trackIdx] is SignalTrack sig)
        {
            for (int m = 0; m < sig.Markers.Count; m++)
            {
                float mx = TimelineEditModel.TimeToScreenX(sig.Markers[m].Time, timelineWidth, DurationOf(ta));
                if (TimelineEditModel.HitSignal(relX, mx)) { markerIdx = m; break; }
            }
        }

        if (trackIdx >= 0)
        {
            _selectedTrackIndex = trackIdx;
            _selectedClipIndex = clipIdx;
            _selectedMarkerIndex = markerIdx;
        }

        Origami.ContextMenu(relX, relY, builder =>
        {
            builder.Item("Add Animation Track", () => AddTrack(ta, new AnimationTrack { Name = "Animation" }));
            builder.Item("Add Audio Track", () => AddTrack(ta, new AudioTrack { Name = "Audio" }));
            builder.Item("Add Activation Track", () => AddTrack(ta, new ActivationTrack { Name = "Activation" }));
            builder.Item("Add Camera Track", () => AddTrack(ta, new CameraTrack { Name = "Camera" }));
            builder.Item("Add Particle Track", () => AddTrack(ta, new ParticleTrack { Name = "Particle" }));
            builder.Item("Add Signal Track", () => AddTrack(ta, new SignalTrack { Name = "Signal" }));
            if (trackIdx >= 0)
            {
                builder.Item("Add Clip", () => AddClip(ta, ta.Tracks[trackIdx]));
                if (clipIdx >= 0)
                    builder.Item("Delete Clip", () => { Snapshot(ta); DeleteClip(ta.Tracks[trackIdx], clipIdx); AfterClipEdit(ta); }, danger: true);
                if (markerIdx >= 0 && ta.Tracks[trackIdx] is SignalTrack st)
                    builder.Item("Delete Marker", () => { Snapshot(ta); st.Markers.RemoveAt(markerIdx); _selectedMarkerIndex = -1; }, danger: true);
            }
        });
    }

    private void HandleSheetDragStart(float relX, float relY, float timelineWidth, TimelineAsset ta)
    {
        int trackIdx = TimelineEditModel.TrackIndexAt(relY, 0f, RowHeight, ta.Tracks.Count);
        if (trackIdx < 0)
        {
            _dragKind = DragKind.Scrub;
            ScrubTo(relX, timelineWidth, ta);
            return;
        }

        var track = ta.Tracks[trackIdx];
        double duration = DurationOf(ta);

        if (track is SignalTrack sig)
        {
            for (int m = 0; m < sig.Markers.Count; m++)
            {
                float mx = TimelineEditModel.TimeToScreenX(sig.Markers[m].Time, timelineWidth, duration);
                if (!TimelineEditModel.HitSignal(relX, mx)) continue;
                _selectedTrackIndex = trackIdx;
                _selectedMarkerIndex = m;
                _selectedClipIndex = -1;
                _dragKind = DragKind.Signal;
                _dragTrack = trackIdx;
                _dragClip = m;
                _dragOrigStart = sig.Markers[m].Time;
                _dragPointerTime = TimelineEditModel.ScreenXToTime(relX, timelineWidth, duration);
                return;
            }
            _dragKind = DragKind.Scrub;
            ScrubTo(relX, timelineWidth, ta);
            return;
        }

        double time = TimelineEditModel.ScreenXToTime(relX, timelineWidth, duration);
        int clipIdx = FindClipAtTime(track, time);
        if (clipIdx < 0 || !TryGetSchedule(track, clipIdx, out var sched))
        {
            _dragKind = DragKind.Scrub;
            ScrubTo(relX, timelineWidth, ta);
            return;
        }

        float clipPx = TimelineEditModel.TimeToScreenX(sched.Start, timelineWidth, duration);
        float clipW = TimelineEditModel.TimeToScreenX(sched.Duration, timelineWidth, duration);
        if (clipW < 2f) clipW = 2f;

        _selectedTrackIndex = trackIdx;
        _selectedClipIndex = clipIdx;
        _selectedMarkerIndex = -1;
        _dragTrack = trackIdx;
        _dragClip = clipIdx;
        _dragOrigStart = sched.Start;
        _dragOrigDuration = sched.Duration;
        _dragPointerTime = time;

        if (TimelineEditModel.HitTrimStart(relX, clipPx, clipW))
            _dragKind = DragKind.TrimStart;
        else if (TimelineEditModel.HitTrimEnd(relX, clipPx, clipW))
            _dragKind = DragKind.TrimEnd;
        else
            _dragKind = DragKind.Move;
    }

    private void HandleSheetDragging(float relX, float timelineWidth, TimelineAsset ta)
    {
        if (_dragKind == DragKind.None) return;
        if (_dragKind == DragKind.Scrub)
        {
            ScrubTo(relX, timelineWidth, ta);
            return;
        }

        double t = TimelineEditModel.ScreenXToTime(relX, timelineWidth, DurationOf(ta));
        if (!_dragUndoArmed)
        {
            Snapshot(ta);
            _dragUndoArmed = true;
        }

        if (_dragKind == DragKind.Signal)
        {
            if (_dragTrack < 0 || _dragTrack >= ta.Tracks.Count) return;
            if (ta.Tracks[_dragTrack] is not SignalTrack sig) return;
            if (_dragClip < 0 || _dragClip >= sig.Markers.Count) return;
            double nt = _dragOrigStart + (t - _dragPointerTime);
            if (nt < 0d) nt = 0d;
            sig.Markers[_dragClip].Time = nt;
            GrowDuration(ta, nt);
            return;
        }

        if (_dragTrack < 0 || _dragTrack >= ta.Tracks.Count) return;
        var track = ta.Tracks[_dragTrack];
        if (!TryGetSchedule(track, _dragClip, out var sched)) return;

        double start = _dragOrigStart;
        double dur = _dragOrigDuration;
        switch (_dragKind)
        {
            case DragKind.Move:
                TimelineEditModel.MoveClip(ref start, dur, _dragOrigStart + (t - _dragPointerTime));
                sched.Start = start;
                break;
            case DragKind.TrimStart:
                TimelineEditModel.TrimStart(ref start, ref dur, t);
                sched.Start = start;
                sched.Duration = dur;
                break;
            case DragKind.TrimEnd:
                TimelineEditModel.TrimEnd(ref dur, start, t);
                sched.Duration = dur;
                break;
        }

        SetSchedule(track, _dragClip, sched);
        GrowDuration(ta, sched.End);
    }

    private void AfterClipEdit(TimelineAsset ta)
    {
        BumpContent();
        BumpVisual();
        if (_director is null) return;
        _director.RebuildGraph();
        _director.Time = _previewTime > 0d || _director.Time > 0d ? PlayheadTime : _director.Time;
        _director.Evaluate();
    }

    private void AddTrack(TimelineAsset ta, TimelineTrack track)
    {
        Snapshot(ta);
        ta.Tracks.Add(track);
        _selectedTrackIndex = ta.Tracks.Count - 1;
        _selectedClipIndex = _selectedMarkerIndex = -1;
        AfterClipEdit(ta);
    }

    private void AddClip(TimelineAsset ta, TimelineTrack track)
    {
        Snapshot(ta);
        double t = PlayheadTime;
        var sched = new TimelineClipSchedule { Start = t, Duration = 1d };
        switch (track)
        {
            case AnimationTrack anim:
                anim.Clips.Add(new AnimationTrackClip { Schedule = sched });
                _selectedClipIndex = anim.Clips.Count - 1;
                break;
            case AudioTrack audio:
                audio.Clips.Add(new AudioTrackClip { Schedule = sched });
                _selectedClipIndex = audio.Clips.Count - 1;
                break;
            case CameraTrack cam:
                cam.Clips.Add(new CameraTrackClip { Schedule = sched });
                _selectedClipIndex = cam.Clips.Count - 1;
                break;
            case ActivationTrack act:
                act.Clips.Add(new ActivationTrackClip { Schedule = sched });
                _selectedClipIndex = act.Clips.Count - 1;
                break;
            case ParticleTrack part:
                part.Clips.Add(new ParticleTrackClip { Schedule = sched });
                _selectedClipIndex = part.Clips.Count - 1;
                break;
            case SignalTrack sig:
                sig.Markers.Add(new SignalMarker { Time = t, Name = "Signal" });
                _selectedMarkerIndex = sig.Markers.Count - 1;
                _selectedClipIndex = -1;
                break;
        }
        GrowDuration(ta, t + 1d);
        AfterClipEdit(ta);
    }

    private void DeleteSelection(TimelineAsset ta)
    {
        if (_selectedTrackIndex < 0 || _selectedTrackIndex >= ta.Tracks.Count) return;
        var track = ta.Tracks[_selectedTrackIndex];
        Snapshot(ta);
        if (_selectedMarkerIndex >= 0 && track is SignalTrack sig
            && _selectedMarkerIndex < sig.Markers.Count)
        {
            sig.Markers.RemoveAt(_selectedMarkerIndex);
            _selectedMarkerIndex = -1;
            BumpContent();
            BumpVisual();
            return;
        }
        if (_selectedClipIndex >= 0)
        {
            DeleteClip(track, _selectedClipIndex);
            _selectedClipIndex = -1;
            AfterClipEdit(ta);
        }
    }

    private static void Snapshot(TimelineAsset ta) => Undo.Snapshot(ta);

    private static void GrowDuration(TimelineAsset ta, double clipEnd)
        => ta.Duration = TimelineEditModel.ExpandDuration(ta.Duration, clipEnd);

    private static double DurationOf(TimelineAsset ta) => ta.Duration > 0d ? ta.Duration : 1d;

    private static int ClipCount(TimelineTrack track) => track switch
    {
        AnimationTrack anim => anim.Clips.Count,
        AudioTrack audio => audio.Clips.Count,
        CameraTrack cam => cam.Clips.Count,
        ActivationTrack act => act.Clips.Count,
        ParticleTrack part => part.Clips.Count,
        _ => 0,
    };

    private static bool TryGetSchedule(TimelineTrack track, int clipIndex, out TimelineClipSchedule schedule)
    {
        schedule = default;
        switch (track)
        {
            case AnimationTrack anim when clipIndex >= 0 && clipIndex < anim.Clips.Count:
                schedule = anim.Clips[clipIndex].Schedule; return true;
            case AudioTrack audio when clipIndex >= 0 && clipIndex < audio.Clips.Count:
                schedule = audio.Clips[clipIndex].Schedule; return true;
            case CameraTrack cam when clipIndex >= 0 && clipIndex < cam.Clips.Count:
                schedule = cam.Clips[clipIndex].Schedule; return true;
            case ActivationTrack act when clipIndex >= 0 && clipIndex < act.Clips.Count:
                schedule = act.Clips[clipIndex].Schedule; return true;
            case ParticleTrack part when clipIndex >= 0 && clipIndex < part.Clips.Count:
                schedule = part.Clips[clipIndex].Schedule; return true;
            default:
                return false;
        }
    }

    private static void SetSchedule(TimelineTrack track, int clipIndex, TimelineClipSchedule schedule)
    {
        switch (track)
        {
            case AnimationTrack anim when clipIndex >= 0 && clipIndex < anim.Clips.Count:
                anim.Clips[clipIndex].Schedule = schedule; break;
            case AudioTrack audio when clipIndex >= 0 && clipIndex < audio.Clips.Count:
                audio.Clips[clipIndex].Schedule = schedule; break;
            case CameraTrack cam when clipIndex >= 0 && clipIndex < cam.Clips.Count:
                cam.Clips[clipIndex].Schedule = schedule; break;
            case ActivationTrack act when clipIndex >= 0 && clipIndex < act.Clips.Count:
                act.Clips[clipIndex].Schedule = schedule; break;
            case ParticleTrack part when clipIndex >= 0 && clipIndex < part.Clips.Count:
                part.Clips[clipIndex].Schedule = schedule; break;
        }
    }

    private static int FindClipAtTime(TimelineTrack track, double time)
    {
        int n = ClipCount(track);
        for (int i = 0; i < n; i++)
        {
            if (TryGetSchedule(track, i, out var s) && s.Contains(time))
                return i;
        }
        return -1;
    }

    internal void ProbeSelectFirstClip()
    {
        if (ActiveAsset is null || ActiveAsset.Tracks.Count == 0) return;
        _selectedTrackIndex = 0;
        _selectedClipIndex = ClipCount(ActiveAsset.Tracks[0]) > 0 ? 0 : -1;
        _selectedMarkerIndex = -1;
        BumpVisual();
    }

    internal void ProbeAddClip()
    {
        var ta = ActiveAsset;
        if (ta is null || ta.Tracks.Count == 0) return;
        AddClip(ta, ta.Tracks[0]);
    }

    private static void DeleteClip(TimelineTrack track, int clipIndex)
    {
        switch (track)
        {
            case AnimationTrack anim when clipIndex >= 0 && clipIndex < anim.Clips.Count:
                anim.Clips.RemoveAt(clipIndex); break;
            case AudioTrack audio when clipIndex >= 0 && clipIndex < audio.Clips.Count:
                audio.Clips.RemoveAt(clipIndex); break;
            case CameraTrack cam when clipIndex >= 0 && clipIndex < cam.Clips.Count:
                cam.Clips.RemoveAt(clipIndex); break;
            case ActivationTrack act when clipIndex >= 0 && clipIndex < act.Clips.Count:
                act.Clips.RemoveAt(clipIndex); break;
            case ParticleTrack part when clipIndex >= 0 && clipIndex < part.Clips.Count:
                part.Clips.RemoveAt(clipIndex); break;
        }
    }
}
