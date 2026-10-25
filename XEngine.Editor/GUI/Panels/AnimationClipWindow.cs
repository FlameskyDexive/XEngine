// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

using XEngine.Echo;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Scribe;
using XEngine.Vector;

using TextAlignment = XEngine.PaperUI.TextAlignment;
using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// The Animation Clip editor — a dockable dopesheet over an <see cref="AnimationClip"/>: a left-side
/// bone list with Position/Rotation/Scale channels, a time ruler, a draggable playhead with Play/Pause,
/// and click-to-select / add / drag / delete keyframe diamonds on all channels.
/// </summary>
public partial class AnimationClipWindow : DockPanel, IScriptReloadCleanup
{
    public override string Title => "Animation";

    private readonly AnimationClipEditorModel _model = new();
    private readonly AnimationCurve?[] _curveBuf = new AnimationCurve?[4];
    private readonly List<AnimationClipEditorModel.PropertyRow> _rows = new();
    private readonly List<float> _rowTimes = new();
    private bool _subscribed;

    private const float ToolbarHeight = AnimationEditorChrome.ToolbarHeight;
    private const float RulerHeight = AnimationEditorChrome.RulerHeight;
    private const float ChannelRowHeight = AnimationEditorChrome.RowHeight;
    private const float TabsHeight = 22f;
    private const float DiamondRadiusPx = 6f;
    private const float SplitterSize = 4f;
    private const float MinLeft = 120f;
    private const float MaxLeft = 420f;

    private double _playbackTime;
    private bool _isPlaying;
    private DateTime _lastFrameTime = DateTime.UtcNow;
    private bool _scrubbing;
    private bool _showCurves;
    private bool _showAddProperty;
    private bool _timelineHovered;
    private float _lastTimelineWidth = 1f;
    private bool _draggingKey;
    private float _dragKeyFromTime;
    private bool _dragUndoArmed;
    private bool _marquee;
    private bool _marqueeAdditive;
    private float _marqueeX0, _marqueeY0, _marqueeX1, _marqueeY1;
    private bool _skipNextClick;
    private float _leftWidth = AnimationEditorChrome.SidebarWidth;
    private float _curveSplit = 0.35f;
    private bool _layoutLoaded;

    private FontFile? _font;

    private static readonly Color32 PosColor = AnimationEditorChrome.ChannelPosition;
    private static readonly Color32 RotColor = AnimationEditorChrome.ChannelRotation;
    private static readonly Color32 SclColor = AnimationEditorChrome.ChannelScale;
    private static readonly Color32 SelectedColor = AnimationEditorChrome.Keyframe32;
    private static readonly Color32 PlayheadColor = AnimationEditorChrome.Playhead32;
    private static readonly Color32 CompX = new(231, 76, 60, 255);
    private static readonly Color32 CompY = new(46, 204, 113, 255);
    private static readonly Color32 CompZ = new(52, 152, 219, 255);
    private static readonly Color32 CompW = new(241, 196, 15, 255);

    public AnimationClip? CurrentClip => _model.Clip;
    public GameObject? CurrentRoot => _model.Root;
    public float PlaybackTime => (float)_playbackTime;
    public AnimationClipEditorModel Model => _model;
    public float SidebarWidth
    {
        get => _leftWidth;
        set => _leftWidth = Math.Clamp(value, MinLeft, MaxLeft);
    }
    public float CurveSplit
    {
        get => _curveSplit;
        set => _curveSplit = Math.Clamp(value, 0.15f, 0.85f);
    }

    public override bool SerializeState(JsonObject state)
    {
        state["sidebarWidth"] = _leftWidth;
        state["curveSplit"] = _curveSplit;
        return true;
    }

    public override void RestoreState(JsonObject state)
    {
        if (state.TryGetPropertyValue("sidebarWidth", out JsonNode? sw) && sw is JsonValue sv && sv.TryGetValue(out float sidebar))
            _leftWidth = Math.Clamp(sidebar, MinLeft, MaxLeft);
        if (state.TryGetPropertyValue("curveSplit", out JsonNode? cs) && cs is JsonValue cv && cv.TryGetValue(out float split))
            _curveSplit = Math.Clamp(split, 0.15f, 0.85f);
        _layoutLoaded = true;
    }

    public void OnScriptReloadCleanup()
    {
        DisposePaintRegions();
        PublishContinuousPresent(false, ref _countedContinuous);
        SetClip(null);
    }

    public override void OnClosed()
    {
        DisposePaintRegions();
        PublishContinuousPresent(false, ref _countedContinuous);
        StopRecordingSession();
        AnimationPreviewSession.Instance.Stop();
        if (_subscribed) { Selection.OnSelectionChanged -= TryLoadFromSelection; _subscribed = false; }
    }

    [MenuItem("Window/Animation/Animation", priority: 21)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(AnimationClipWindow));

    public void Bind(GameObject? root, AnimationClip? clip)
    {
        _model.Root = root;
        SetClip(clip);
    }

    public void SetClip(AnimationClip? clip)
    {
        StopRecordingSession();
        _model.SetClip(clip);
        _font = EditorTheme.Font ?? _font;
        _playbackTime = 0;
        _isPlaying = false;
        _lastAddEventTime = null;
        _selectedEventIndex = -1;
        _showAddProperty = false;
        _draggingKey = false;
        _scrubbing = false;
        if (clip == null)
            AnimationPreviewSession.Instance.Stop();
    }

    private void TryLoadFromSelection()
    {
        if (Selection.ActiveObject is GameObject go)
        {
            BindFromSceneObject(go);
            return;
        }

        var selected = Selection.Selected;
        for (int i = 0; i < selected.Count; i++)
        {
            if (selected[i] is GameObject sceneGo)
            {
                BindFromSceneObject(sceneGo);
                return;
            }
        }

        if (Selection.ActiveObject is not ContentItem ci || ci.IsFolder) return;

        AnimationClip? clip = null;

        if (ci.IsSubAsset)
        {
            clip = AssetDatabase.Get(ci.Guid) as AnimationClip;
        }
        else if (ci.RelativePath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
        {
            Guid guid = ci.Guid;
            if (guid == Guid.Empty)
            {
                var entry = EditorAssetBackend.Instance?.GetEntry(ci.RelativePath);
                guid = entry?.Guid ?? Guid.Empty;
            }
            if (guid != Guid.Empty)
                clip = AssetDatabase.Get(guid) as AnimationClip;
        }

        if (clip != null && clip != _model.Clip)
        {
            _model.Root = AnimationWindowBinding.FindAnimationRoot(SelectedSceneObject());
            SetClip(clip);
        }
    }

    private void BindFromSceneObject(GameObject go)
    {
        GameObject root = AnimationWindowBinding.FindAnimationRoot(go) ?? go;
        _model.Root = root;
        var clips = new List<AnimationClip>();
        AnimationWindowBinding.CollectClips(root, clips);
        if (clips.Count == 0)
        {
            SetClip(null);
            return;
        }
        if (_model.Clip == null || !clips.Contains(_model.Clip))
            SetClip(clips[0]);
        else
            _model.RefreshAvailableClips();
    }

    private static GameObject? SelectedSceneObject()
    {
        if (Selection.ActiveObject is GameObject go) return go;
        var selected = Selection.Selected;
        for (int i = 0; i < selected.Count; i++)
            if (selected[i] is GameObject g)
                return g;
        return null;
    }

    public override void OnGUI(Paper paper, float width, float height)
    {
        using var profilerScope = EditorProfiler.BeginCpuScope("Animation");
        if (!_subscribed)
        {
            Selection.OnSelectionChanged += TryLoadFromSelection;
            _subscribed = true;
            TryLoadFromSelection();
        }

        var now = DateTime.UtcNow;
        double dt = (now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;
        if (_isPlaying && !_scrubbing && _model.Clip != null && _model.Clip.Duration > 0)
        {
            _playbackTime += dt;
            if (_playbackTime >= _model.Clip.Duration)
                _playbackTime %= _model.Clip.Duration;
        }

        if (paper.IsKeyPressed(PaperKey.Delete) && !_model.ClipIsReadOnly)
        {
            if (_model.HasKeyframeSelection)
            {
                SnapshotClip();
                _model.RemoveSelectedKeys();
            }
            else if (_selectedEventIndex >= 0)
                RemoveSelectedEvent();
        }

        // Keyboard shortcuts scoped to the timeline area (Unity: window focus; here: pointer over it).
        if (_timelineHovered && _model.Clip != null)
        {
            bool ctrl = Ctrl(paper);
            if (ctrl && paper.IsKeyPressed(PaperKey.C) && _model.SelectedKeys.Count > 0)
                _model.CopySelectedKeys();
            else if (ctrl && paper.IsKeyPressed(PaperKey.V) && !_model.ClipIsReadOnly && _model.CopiedKeyCount > 0)
            {
                SnapshotClip();
                _model.PasteKeys((float)_playbackTime);
            }
            else if (!ctrl && paper.IsKeyPressed(PaperKey.K) && !_model.ClipIsReadOnly)
                AddKeyframeAtPlayhead();
            else if (!ctrl && paper.IsKeyPressed(PaperKey.F) && _model.HasKeyframeSelection)
                _model.FrameSelection();
            else if (!ctrl && paper.IsKeyPressed(PaperKey.A))
                _model.FrameAll();
        }

        if (_timelineHovered && paper.IsPointerDown(PaperMouseBtn.Middle) && paper.IsPointerMoving)
        {
            float span = _model.ViewEndTime - _model.ViewStartTime;
            _model.PanView(-(float)paper.PointerDelta.X / Math.Max(1f, _lastTimelineWidth) * span);
        }

        TickRecordingOrPreview();
        EnsureLayoutLoaded();
        _model.FillVisibleRows(_rows);
        PublishContinuousPresent(_isPlaying || _recording || _scrubbing || _draggingKey || _marquee || _draggingEvent,
            ref _countedContinuous);

        using (paper.Column("animclip_root").Size(width, height).Enter())
        {
            DrawToolbar(paper, width);
            if (_model.Clip != null)
            {
                float bannerH = (_model.ClipIsReadOnly ? 22f : 0f) + (_recording ? 22f : 0f);
                float bodyH = height - ToolbarHeight - bannerH;
                if (_model.ClipIsReadOnly)
                    DrawReadOnlyBanner(paper, width);
                if (_recording)
                    DrawRecordingBanner(paper, width);
                float left = Math.Min(_leftWidth, Math.Max(MinLeft, width - 180f));
                using (paper.Row("animclip_body").Width(width).Height(bodyH).Enter())
                {
                    DrawLeftPane(paper, left, bodyH);
                    DrawVSplitter(paper, bodyH);
                    DrawRightPane(paper, width - left - SplitterSize, bodyH);
                }
            }
            else if (_model.NeedsCreatePrompt)
                DrawCreatePrompt(paper, width, height - ToolbarHeight);
            else
            {
                string empty = _model.Root != null
                    ? $"No clips on {_model.Root.Name}. Create an Animation Clip to begin."
                    : AnimationEditorChrome.EmptyClip;
                paper.Box("animclip_empty").Width(width).Height(height - ToolbarHeight)
                    .BackgroundColor(AnimationEditorChrome.Canvas)
                    .Text(empty, EditorTheme.DefaultFont!)
                    .TextColor(AnimationEditorChrome.TextMuted)
                    .Alignment(TextAlignment.MiddleCenter);
            }
        }
    }

    private void DrawToolbar(Paper paper, float width)
    {
        using (paper.Row("animclip_tb").Height(ToolbarHeight)
            .BackgroundColor(AnimationEditorChrome.Panel)
            .Padding(6, 2, 2, 0).ColBetween(4).Enter())
        {
            // Unity-style clip picker: the dropdown itself carries the current clip name, and lists
            // every available clip plus a trailing "Create New Clip…" entry (Animator-window feel).
            const string createNew = "+ Create New Clip…";
            if (_model.AvailableClips.Count > 0)
            {
                var names = new List<string>(_model.AvailableClips.Count + 1);
                int sel = -1;
                for (int i = 0; i < _model.AvailableClips.Count; i++)
                {
                    var c = _model.AvailableClips[i];
                    string n = string.IsNullOrEmpty(c.Name) ? "(unnamed)" : c.Name;
                    names.Add(n);
                    if (ReferenceEquals(c, _model.Clip)) sel = i;
                }
                names.Add(createNew);
                int createIndex = names.Count - 1;
                Origami.Dropdown(paper, "animclip_pick", sel, si =>
                {
                    if (si == createIndex)
                    {
                        PromptCreateClip();
                        return;
                    }
                    if ((uint)si < (uint)_model.AvailableClips.Count)
                        SetClip(_model.AvailableClips[si]);
                }, names).Width(220).Height(20).Show();
            }
            else
            {
                Origami.Button(paper, "animclip_new_empty", createNew, PromptCreateClip)
                    .Ghost().Height(20).Show();
            }

            if (_model.Clip != null)
            {
                Origami.Button(paper, "animclip_prev", "◀", () =>
                {
                    _isPlaying = false;
                    _playbackTime = _model.StepFrame((float)_playbackTime, -1);
                }).Ghost().Height(20).Show();
                Origami.Button(paper, "animclip_next", "▶", () =>
                {
                    _isPlaying = false;
                    _playbackTime = _model.StepFrame((float)_playbackTime, 1);
                }).Ghost().Height(20).Show();

                DrawM39ToolbarControls(paper);

                paper.Box("animclip_tb_spring").Width(width).Height(1);
                Origami.Button(paper, "animclip_save", "Save", SaveClip)
                    .Disabled(_model.ClipIsReadOnly).Show();
            }
        }
    }

    private void DrawCreatePrompt(Paper paper, float width, float height)
    {
        string name = _model.Root?.Name ?? "the selected object";
        using (paper.Column("animclip_create").Width(width).Height(height)
            .BackgroundColor(AnimationEditorChrome.Canvas)
            .Enter())
        {
            paper.Box("animclip_create_msg").Width(width).Height(Math.Max(40f, height - 36f))
                .Text(string.Format(AnimationEditorChrome.EmptyClipCreate, name), EditorTheme.DefaultFont!)
                .TextColor(AnimationEditorChrome.TextMuted)
                .Alignment(TextAlignment.MiddleCenter);
            Origami.Button(paper, "animclip_create_btn", "Create", PromptCreateClip)
                .Height(24).Show();
        }
    }

    private void DrawReadOnlyBanner(Paper paper, float width)
    {
        using (paper.Row("animclip_ro").Width(width).Height(22)
            .BackgroundColor(AnimationEditorChrome.Selection)
            .Padding(6, 2, 2, 0).ColBetween(8).Enter())
        {
            Origami.Label(paper, "animclip_ro_msg", "Imported clip is read-only.").Show();
            Origami.Button(paper, "animclip_dup", "Duplicate to edit", DuplicateToEdit)
                .Ghost().Height(18).Show();
        }
    }

    private void PromptCreateClip()
    {
        string? start = Project.Current?.AssetsPath;
        EditorApplication.OpenFileDialog(FileDialogMode.Save, path =>
        {
            if (string.IsNullOrEmpty(path)) return;
            FinishCreateClip(path);
        }, startPath: start, filters: new[] { "*.anim" }, filterLabels: new[] { "Animation Clip (*.anim)" });
    }

    private void FinishCreateClip(string path)
    {
        var db = EditorAssetBackend.Instance;
        var clip = AnimationWindowBinding.CreateEmptyClip(Path.GetFileNameWithoutExtension(path));
        if (db != null && Project.Current != null)
        {
            string assets = Project.Current.AssetsPath;
            string relative = path.StartsWith(assets, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(assets, path).Replace('\\', '/')
                : Path.GetFileName(path);
            if (!relative.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                relative += ".anim";
            db.CreateAsset(clip, relative);
        }

        GameObject? root = _model.Root ?? SelectedSceneObject();
        if (root != null)
        {
            AnimationWindowBinding.AssignClipToRoot(root, clip);
            Bind(root, clip);
        }
        else
            SetClip(clip);
    }

    private void DuplicateToEdit()
    {
        if (_model.Clip == null) return;
        var copy = AnimationWindowBinding.DuplicateClip(_model.Clip);
        var db = EditorAssetBackend.Instance;
        if (db != null && Project.Current != null)
        {
            string folder = "";
            if (!string.IsNullOrEmpty(_model.Clip.AssetPath))
                folder = Path.GetDirectoryName(_model.Clip.AssetPath)?.Replace('\\', '/') ?? "";
            string stem = copy.Name;
            string relative = string.IsNullOrEmpty(folder) ? stem + ".anim" : $"{folder}/{stem}.anim";
            db.CreateAsset(copy, relative);
        }
        if (_model.Root != null)
            AnimationWindowBinding.AssignClipToRoot(_model.Root, copy);
        SetClip(copy);
    }

    private void EnsureLayoutLoaded()
    {
        if (_layoutLoaded) return;
        _layoutLoaded = true;
        var settings = EditorSettings.Instance;
        _leftWidth = Math.Clamp(settings.AnimationSidebarWidth, MinLeft, MaxLeft);
        _curveSplit = Math.Clamp(settings.AnimationCurveSplit, 0.15f, 0.85f);
    }

    private void DrawVSplitter(Paper paper, float h)
    {
        paper.Box("animclip_vsplit").Width(SplitterSize).Height(h)
            .BackgroundColor(AnimationEditorChrome.Splitter)
            .Cursor(PaperCursor.ResizeHorizontal)
            .OnDragging(e => _leftWidth = Math.Clamp(_leftWidth + (float)e.Delta.X, MinLeft, MaxLeft))
            .OnDragEnd(_ => PersistLayout());
    }

    private void DrawHSplitter(Paper paper, float w)
    {
        paper.Box("animclip_hsplit").Width(w).Height(SplitterSize)
            .BackgroundColor(AnimationEditorChrome.Splitter)
            .Cursor(PaperCursor.ResizeVertical)
            .OnDragging(e =>
            {
                // Delta.Y positive grows the top (dopesheet) strip.
                float body = Math.Max(80f, _lastSheetHeight);
                _curveSplit = Math.Clamp(_curveSplit + (float)e.Delta.Y / body, 0.15f, 0.85f);
            })
            .OnDragEnd(_ => PersistLayout());
    }

    private float _lastSheetHeight = 200f;

    // Vertical scroll of the left property list, mirrored into the dopesheet so both sides
    // show the same row window (row i draws at i*ChannelRowHeight - _propsScrollY).
    private float _propsScrollY;

    private void PersistLayout()
    {
        var settings = EditorSettings.Instance;
        settings.AnimationSidebarWidth = _leftWidth;
        settings.AnimationCurveSplit = _curveSplit;
        settings.Save();
    }

    private static bool Ctrl(Paper paper)
        => paper.IsKeyDown(PaperKey.LeftControl) || paper.IsKeyDown(PaperKey.RightControl);

    private void DrawLeftPane(Paper paper, float w, float h)
    {
        bool hasKey = _model.HasKeyframeSelection;
        int comps = AnimationClipEditorModel.ComponentCount(_model.SelectedChannel);
        float valueH = hasKey ? 22f + comps * 22f : 0f;
        float addH = _showAddProperty ? Math.Min(h * 0.55f, 280f) : 22f;
        float listH = Math.Max(40f, h - TabsHeight - addH - valueH);

        using (paper.Column("animclip_left").Width(w).Height(h)
            .BackgroundColor(AnimationEditorChrome.Raised)
            .Enter())
        {
            using (paper.Column("animclip_props_paint").Width(w).Height(listH).Enter())
            {
                AttachPaintRegion(paper, ref _listPaintRegion);
                DrawPropertyList(paper, w, listH);
            }
            if (hasKey)
                DrawKeyframeInspector(paper, w, valueH);
            DrawAddProperty(paper, w, addH);
            Origami.Tabs(paper, "animclip_view_tabs", _showCurves ? 1 : 0, v => _showCurves = v == 1)
                .Underline()
                .Height(TabsHeight)
                .Tab("Dopesheet")
                .Tab("Curves")
                .Show();
        }
    }

    private void DrawPropertyList(Paper paper, float w, float h)
    {
        Origami.ScrollView(paper, "animclip_props", w, h).Padding(2).Body(vp =>
        {
            _propsScrollY = vp.ScrollY;
            // Virtualize: rigged characters have hundreds of channels; materializing a Row widget for
            // every one each frame made the window crawl. Only build rows intersecting the viewport
            // and pad with fixed-height spacers so the scrollbar spans the full list.
            int first = Math.Max(0, (int)(_propsScrollY / ChannelRowHeight) - 6);
            int visible = (int)(h / ChannelRowHeight) + 12;
            int last = Math.Min(_rows.Count, first + visible);

            if (first > 0)
                paper.Box("animclip_pad_top").Width(w).Height(first * ChannelRowHeight).IsNotInteractable();

            for (int i = first; i < last; i++)
            {
                var row = _rows[i];
                bool sel = row.Kind != AnimationClipEditorModel.PropertyRowKind.Bone
                    && _model.SelectedBonePath == row.BonePath
                    && _model.SelectedChannel == row.Channel
                    && (row.Kind != AnimationClipEditorModel.PropertyRowKind.Component
                        || _model.SelectedComponentIndex == row.Component);
                int idx = i;
                using (paper.Row($"animclip_pr{idx}").Width(w).Height(ChannelRowHeight)
                    .BackgroundColor(sel ? AnimationEditorChrome.Selection : AnimationEditorChrome.Raised)
                    .OnClick(0, (_, _) =>
                    {
                        if (row.Kind == AnimationClipEditorModel.PropertyRowKind.Bone)
                            _model.ToggleBoneExpanded(row.BonePath);
                        else
                        {
                            _model.SelectedBonePath = row.BonePath;
                            _model.SelectedChannel = row.Channel;
                            _model.SelectedComponentIndex = row.Component;
                        }
                    })
                    .Enter())
                {
                    string prefix = row.HasChildren ? (row.Expanded ? "▾ " : "▸ ") : "  ";
                    string pad = row.Depth == 0 ? "" : row.Depth == 1 ? "  " : "    ";
                    Origami.Label(paper, $"animclip_prl{idx}", $"{pad}{prefix}{row.Label}").Show();
                    if (row.Kind == AnimationClipEditorModel.PropertyRowKind.Channel && row.HasChildren)
                    {
                        Origami.Button(paper, $"animclip_prx{idx}", row.Expanded ? "−" : "+", () =>
                            _model.ToggleComponentsVisible(row.BonePath, row.Channel)).Ghost().Height(16).Show();
                    }
                }
            }

            if (last < _rows.Count)
                paper.Box("animclip_pad_bottom").Width(w).Height((_rows.Count - last) * ChannelRowHeight).IsNotInteractable();
        });
    }

    private void DrawKeyframeInspector(Paper paper, float w, float h)
    {
        using (paper.Column("animclip_keyins").Width(w).Height(h).Padding(4, 2, 2, 2).Enter())
        {
            Origami.Label(paper, "animclip_kt", $"t  {_model.SelectedKeyframeTime:F3}s").Show();
            int n = AnimationClipEditorModel.ComponentCount(_model.SelectedChannel);
            string[] names = n == 4 ? new[] { "X", "Y", "Z", "W" } : new[] { "X", "Y", "Z" };
            string bone = _model.SelectedBonePath!;
            var ch = _model.SelectedChannel;
            float time = _model.SelectedKeyframeTime;
            for (int i = 0; i < n; i++)
            {
                int comp = i;
                if (!_model.TryGetKeyframeValue(bone, ch, time, comp, out float val))
                    continue;
                using (paper.Row($"animclip_kv{i}").Width(w).Height(20).ColBetween(4).Enter())
                {
                    Origami.Label(paper, $"animclip_kvl{i}", names[i]).Show();
                    Origami.NumericField(paper, $"animclip_kvv{i}", val, v =>
                    {
                        if (_model.ClipIsReadOnly) return;
                        SnapshotClip();
                        _model.SetKeyframeValue(bone, ch, time, comp, v);
                    }).Format("F3").Height(20).Show();
                }
            }
        }
    }

    private void DrawAddProperty(Paper paper, float w, float h) => DrawAddPropertyPopup(paper, w, h);

    private void DrawRightPane(Paper paper, float w, float h)
    {
        _lastTimelineWidth = w;
        float eventH = AnimationEditorChrome.EventTrackHeight;
        float inspectH = _selectedEventIndex >= 0 ? AnimationEditorChrome.EventInspectorHeight : 0f;
        _lastSheetHeight = Math.Max(80f, h - RulerHeight - eventH - inspectH);
        using (paper.Column("animclip_right").Width(w).Height(h).Enter())
        {
            using (paper.Column("animclip_timeline_paint").Width(w).Height(RulerHeight + eventH).Enter())
            {
                AttachPaintRegion(paper, ref _timelinePaintRegion);
                DrawTimeRuler(paper, w);
                DrawEventTrack(paper, w);
            }
            if (_selectedEventIndex >= 0)
                DrawEventInspector(paper, w);
            float sheetH = h - RulerHeight - eventH - inspectH;
            using (paper.Column("animclip_sheet_paint").Width(w).Height(sheetH).Enter())
            {
                AttachPaintRegion(paper, ref _sheetPaintRegion);
                if (_showCurves)
                {
                    float dopeH = Math.Clamp(sheetH * _curveSplit, 40f, Math.Max(40f, sheetH - 40f));
                    DrawDopesheetArea(paper, w, dopeH);
                    DrawHSplitter(paper, w);
                    DrawCurveArea(paper, w, sheetH - dopeH - SplitterSize);
                }
                else
                    DrawDopesheetArea(paper, w, sheetH);
            }
        }
    }

    private void DrawTimeRuler(Paper paper, float width)
    {
        paper.Box("animclip_ruler")
            .Width(width).Height(RulerHeight)
            .BackgroundColor(AnimationEditorChrome.Raised)
            .OnEnter(_ => _timelineHovered = true)
            .OnLeave(_ => _timelineHovered = false)
            .OnClick(0, (_, e) =>
            {
                _playbackTime = _model.ScreenXToTime((float)e.RelativePosition.X, width);
                if (_playbackTime < 0) _playbackTime = 0;
            })
            .OnDragStart(_ => _scrubbing = true)
            .OnDragging(e =>
            {
                _scrubbing = true;
                _isPlaying = false;
                _playbackTime = _model.ScreenXToTime((float)e.RelativePosition.X, width);
                if (_playbackTime < 0) _playbackTime = 0;
            })
            .OnDragEnd(_ => _scrubbing = false)
            .OnScroll(e => HandleTimelineZoom(e, width))
            .OnPostLayout((handle, _) =>
            {
                _drawRulerWidth = width;
                PaintRuler(paper, ref handle);
            });
    }

    private void HandleTimelineZoom(XEngine.PaperUI.Events.ScrollEvent e, float width)
    {
        float factor = e.Delta > 0 ? 1.1f : 0.9f;
        float anchor = _model.ScreenXToTime((float)e.RelativePosition.X, width);
        _model.ZoomView(factor, anchor);
    }

    private void DrawRulerContent(Canvas canvas, Rect rect, float rulerWidth)
    {
        var font = _font ?? EditorTheme.Font;
        Float2 origin = rect.Min;
        double start = _model.ViewStartTime;
        double end = _model.ViewEndTime;
        double duration = end - start;
        if (duration <= 0) return;
        double step = ChooseTickStep(duration);
        double first = Math.Ceiling(start / step) * step;
        for (double t = first; t <= end + 1e-6; t += step)
        {
            float x = origin.X + _model.TimeToScreenX((float)t, rulerWidth);
            canvas.SetFillColor(AnimationEditorChrome.TextMuted32);
            canvas.MoveTo(x, origin.Y);
            canvas.LineTo(x, origin.Y + RulerHeight);
            canvas.Stroke();
            if (font != null)
                canvas.DrawText($"{t:F1}", x + 2, origin.Y + 4, AnimationEditorChrome.Text32, 10f, font);
        }

        float px = origin.X + _model.TimeToScreenX((float)_playbackTime, rulerWidth);
        canvas.SetFillColor(PlayheadColor);
        canvas.MoveTo(px, origin.Y);
        canvas.LineTo(px, origin.Y + RulerHeight);
        canvas.Stroke();
    }

    private static double ChooseTickStep(double duration)
    {
        if (duration <= 1) return 0.25;
        if (duration <= 5) return 0.5;
        if (duration <= 10) return 1;
        if (duration <= 30) return 2;
        return 5;
    }

    private void DrawDopesheetArea(Paper paper, float width, float height)
    {
        paper.Box("animclip_dopesheet").Width(width).Height(height)
            .BackgroundColor(AnimationEditorChrome.Canvas)
            .OnEnter(_ => _timelineHovered = true)
            .OnLeave(_ => _timelineHovered = false)
            .OnClick(0, (_, e) => HandleDopesheetClick((float)e.RelativePosition.X, (float)e.RelativePosition.Y, width, paper))
            .OnDragStart(e => HandleDopesheetDragStart((float)e.StartPosition.X, (float)e.StartPosition.Y, width, paper))
            .OnDragging(e => HandleDopesheetDragging((float)e.RelativePosition.X, (float)e.RelativePosition.Y, width, paper))
            .OnDragEnd(_ =>
            {
                if (_marquee)
                    CommitMarquee(width);
                _draggingKey = false;
                _dragUndoArmed = false;
                _marquee = false;
            })
            .OnScroll(e => HandleTimelineZoom(e, width))
            .OnPostLayout((handle, _) =>
            {
                _drawSheetWidth = width;
                _drawSheetHeight = height;
                PaintDopesheet(paper, ref handle);
            });
    }

    private float PickRadiusSeconds(float timelineWidth)
    {
        float a = _model.ScreenXToTime(0f, timelineWidth);
        float b = _model.ScreenXToTime(DiamondRadiusPx, timelineWidth);
        float r = b - a;
        if (r < 0f) r = -r;
        return Math.Max(r, 1e-4f);
    }

    private void HandleDopesheetClick(float relX, float relY, float width, Paper paper)
    {
        if (_skipNextClick) { _skipNextClick = false; return; }
        if (_model.Clip == null || !TryRowAt(relY, out var row))
        {
            if (!Ctrl(paper))
                _model.ClearKeyframeSelection();
            return;
        }
        if (row.Kind == AnimationClipEditorModel.PropertyRowKind.Bone)
            return;

        float time = _model.ScreenXToTime(relX, width);
        float radius = PickRadiusSeconds(width);
        if (_model.TryHitKeyframe(row.BonePath, row.Channel, time, radius, out int keyIndex, out float keyTime))
        {
            _model.SelectKeyframe(row.BonePath, row.Channel, keyIndex, keyTime, row.Component, additive: Ctrl(paper));
            return;
        }

        if (Ctrl(paper) || _model.ClipIsReadOnly) return;
        SnapshotClip();
        _model.AddKeyframe(row.BonePath, row.Channel, _model.SnapToFrame(time));
    }

    private void HandleDopesheetDragStart(float relX, float relY, float width, Paper paper)
    {
        _marquee = false;
        _draggingKey = false;
        if (_model.Clip == null) return;
        float time = _model.ScreenXToTime(relX, width);
        float radius = PickRadiusSeconds(width);
        if (TryRowAt(relY, out var row)
            && row.Kind != AnimationClipEditorModel.PropertyRowKind.Bone
            && _model.TryHitKeyframe(row.BonePath, row.Channel, time, radius, out int keyIndex, out float keyTime))
        {
            bool additive = Ctrl(paper);
            if (!_model.IsKeySelected(row.BonePath, row.Channel, keyTime))
                _model.SelectKeyframe(row.BonePath, row.Channel, keyIndex, keyTime, row.Component, additive);
            _draggingKey = true;
            _dragKeyFromTime = keyTime;
            _dragUndoArmed = false;
            return;
        }
        _marquee = true;
        _marqueeAdditive = Ctrl(paper);
        _marqueeX0 = _marqueeX1 = relX;
        _marqueeY0 = _marqueeY1 = relY;
    }

    private void HandleDopesheetDragging(float relX, float relY, float width, Paper paper)
    {
        if (_marquee)
        {
            _marqueeX1 = relX;
            _marqueeY1 = relY;
            return;
        }
        if (!_draggingKey || _model.SelectedKeys.Count == 0 || _model.ClipIsReadOnly) return;
        if (!_dragUndoArmed)
        {
            SnapshotClip();
            _dragUndoArmed = true;
        }
        float toTime = _model.ScreenXToTime(relX, width);
        if (toTime < 0f) toTime = 0f;
        bool snap = !Ctrl(paper);
        if (snap) toTime = _model.SnapToFrame(toTime);
        float from = _dragKeyFromTime;
        float delta = toTime - from;
        if (_model.MoveSelectedKeys(delta, snap))
            _dragKeyFromTime = _model.SelectedKeyframeTime;
    }

    private void CommitMarquee(float width)
    {
        _skipNextClick = true;
        int r0 = RowIndexAt(_marqueeY0);
        int r1 = RowIndexAt(_marqueeY1);
        float t0 = _model.ScreenXToTime(_marqueeX0, width);
        float t1 = _model.ScreenXToTime(_marqueeX1, width);
        _model.MarqueeSelect(_rows, r0, r1, t0, t1, _marqueeAdditive);
    }

    private int RowIndexAt(float relY)
    {
        int i = (int)((relY + _propsScrollY) / ChannelRowHeight);
        if (i < 0) i = 0;
        if (i >= _rows.Count) i = _rows.Count - 1;
        return i;
    }

    private bool TryRowAt(float relY, out AnimationClipEditorModel.PropertyRow row)
    {
        int i = (int)((relY + _propsScrollY) / ChannelRowHeight);
        if (i < 0 || i >= _rows.Count)
        {
            row = default;
            return false;
        }
        row = _rows[i];
        return true;
    }

    private bool _curveOverlayAll;
    private const float CurveToolsH = 20f;

    public bool CurveOverlayAll
    {
        get => _curveOverlayAll;
        set => _curveOverlayAll = value;
    }

    private void DrawCurveArea(Paper paper, float width, float height)
    {
        using (paper.Column("animclip_curvewrap").Width(width).Height(height).Enter())
        {
            using (paper.Row("animclip_curvetools").Width(width).Height(CurveToolsH)
                .BackgroundColor(AnimationEditorChrome.Panel).Padding(4, 0, 1, 1).ColBetween(4).Enter())
            {
                var allBtn = Origami.Button(paper, "animclip_cv_all",
                    _curveOverlayAll ? "All Channels ✓" : "All Channels",
                    () => _curveOverlayAll = !_curveOverlayAll).Ghost().Height(18);
                if (_curveOverlayAll) allBtn.Primary();
                allBtn.Show();
                Origami.Button(paper, "animclip_cv_f", "F", () =>
                {
                    if (_model.HasKeyframeSelection) _model.FrameSelection();
                }).Ghost().Height(18).Show();
                Origami.Button(paper, "animclip_cv_a", "A", () => _model.FrameAll())
                    .Ghost().Height(18).Show();
            }
            float bodyH = Math.Max(30f, height - CurveToolsH);
            paper.Box("animclip_curves").Width(width).Height(bodyH)
                .BackgroundColor(AnimationEditorChrome.Canvas)
                .OnEnter(_ => _timelineHovered = true)
                .OnLeave(_ => _timelineHovered = false)
                .OnClick(0, (_, e) => HandleCurveClick((float)e.RelativePosition.X, (float)e.RelativePosition.Y, width, bodyH))
                .OnDragStart(e => HandleCurveDragStart((float)e.StartPosition.X, (float)e.StartPosition.Y, width, bodyH))
                .OnDragging(e => HandleCurveDragging((float)e.RelativePosition.X, (float)e.RelativePosition.Y, width, bodyH, paper))
                .OnDragEnd(_ => { _draggingKey = false; _draggingTangent = 0; _dragUndoArmed = false; })
                .OnRightClick(_ =>
                {
                    if (!_curveOverlayAll) OpenTangentContextMenu();
                })
                .OnScroll(e => HandleTimelineZoom(e, width))
                .OnPostLayout((handle, _) =>
                {
                    _drawCurveWidth = width;
                    _drawCurveHeight = bodyH;
                    PaintCurves(paper, ref handle);
                });
        }
    }

    private const float CurveHeaderH = 24f;

    private void CurveValueRange(AnimationClip.AnimBone bone, AnimationClipEditorModel.Channel channel, out float minVal, out float maxVal)
    {
        minVal = float.MaxValue;
        maxVal = float.MinValue;
        int n = AnimationClipEditorModel.FillCurves(bone, channel, _curveBuf);
        for (int ci = 0; ci < n; ci++)
        {
            if (_curveBuf[ci] == null) continue;
            var keys = _curveBuf[ci]!.Keys;
            for (int k = 0; k < keys.Count; k++)
            {
                float v = keys[k].Value;
                if (v < minVal) minVal = v;
                if (v > maxVal) maxVal = v;
            }
        }
        if (maxVal - minVal < 0.001f) { minVal -= 0.5f; maxVal += 0.5f; }
    }

    private float ScreenYToValue(float relY, float height, float minVal, float maxVal)
    {
        float areaH = height - CurveHeaderH - 8f;
        float y = relY - CurveHeaderH;
        float t = 1f - y / Math.Max(1f, areaH);
        return minVal + t * (maxVal - minVal);
    }

    private void HandleCurveClick(float relX, float relY, float width, float height)
    {
        if (_curveOverlayAll)
        {
            HandleOverlayClick(relX, relY, width, height);
            return;
        }
        if (!TryCurveContext(out string bonePath, out var bone, out var channel)) return;
        CurveValueRange(bone, channel, out float minVal, out float maxVal);
        if (TryHitCurveKey(relX, relY, width, height, bone, channel, minVal, maxVal, out int comp, out int keyIndex, out float keyTime))
        {
            _model.SelectKeyframe(bonePath, channel, keyIndex, keyTime, comp);
            return;
        }
        if (_model.ClipIsReadOnly) return;
        SnapshotClip();
        _model.AddKeyframe(bonePath, channel, _model.SnapToFrame(_model.ScreenXToTime(relX, width)));
    }

    private void HandleCurveDragStart(float relX, float relY, float width, float height)
    {
        if (_curveOverlayAll) { _draggingKey = false; _draggingTangent = 0; return; }
        if (!TryCurveContext(out string bonePath, out var bone, out var channel)) return;
        CurveValueRange(bone, channel, out float minVal, out float maxVal);
        if (TryBeginTangentDrag(relX, relY, width, height))
        {
            _draggingKey = false;
            return;
        }
        if (!TryHitCurveKey(relX, relY, width, height, bone, channel, minVal, maxVal, out int comp, out int keyIndex, out float keyTime))
        {
            _draggingKey = false;
            return;
        }
        _model.SelectKeyframe(bonePath, channel, keyIndex, keyTime, comp);
        _draggingKey = true;
        _dragKeyFromTime = keyTime;
        _dragUndoArmed = false;
    }

    private void HandleCurveDragging(float relX, float relY, float width, float height, Paper paper)
    {
        if (_curveOverlayAll) return;
        if (_draggingTangent != 0)
        {
            HandleTangentDragging(relX, relY, width, height);
            return;
        }
        if (!_draggingKey || _model.SelectedBonePath == null || _model.ClipIsReadOnly) return;
        if (!TryCurveContext(out _, out var bone, out var channel)) return;
        if (!_dragUndoArmed)
        {
            SnapshotClip();
            _dragUndoArmed = true;
        }
        CurveValueRange(bone, channel, out float minVal, out float maxVal);
        float toTime = _model.ScreenXToTime(relX, width);
        if (toTime < 0f) toTime = 0f;
        bool snap = !Ctrl(paper);
        if (snap) toTime = _model.SnapToFrame(toTime);
        float from = _dragKeyFromTime;
        float delta = toTime - from;
        if (_model.MoveSelectedKeys(delta, snap))
            _dragKeyFromTime = _model.SelectedKeyframeTime;
        if (_model.SelectedComponentIndex >= 0 && _model.SelectedBonePath != null)
        {
            float val = ScreenYToValue(relY, height, minVal, maxVal);
            _model.SetKeyframeValue(_model.SelectedBonePath, channel, _model.SelectedKeyframeTime, _model.SelectedComponentIndex, val);
        }
    }

    private bool TryCurveContext(out string bonePath, out AnimationClip.AnimBone bone, out AnimationClipEditorModel.Channel channel)
    {
        bonePath = "";
        bone = null!;
        channel = _model.SelectedChannel;
        var paths = _model.GetBonePaths();
        bonePath = _model.SelectedBonePath ?? (paths.Count > 0 ? paths[0] : "");
        if (string.IsNullOrEmpty(bonePath)) return false;
        var b = _model.Clip?.GetBone(bonePath);
        if (b == null) return false;
        bone = b;
        return true;
    }

    private bool TryHitCurveKey(float relX, float relY, float width, float height,
        AnimationClip.AnimBone bone, AnimationClipEditorModel.Channel channel,
        float minVal, float maxVal, out int component, out int keyIndex, out float keyTime)
    {
        component = -1;
        keyIndex = -1;
        keyTime = float.NaN;
        float areaH = height - CurveHeaderH - 8f;
        float range = maxVal - minVal;
        if (range <= 0f) range = 1f;
        int n = AnimationClipEditorModel.FillCurves(bone, channel, _curveBuf);
        float best = DiamondRadiusPx * DiamondRadiusPx;
        for (int c = 0; c < n; c++)
        {
            if (_curveBuf[c] == null) continue;
            var keys = _curveBuf[c]!.Keys;
            for (int k = 0; k < keys.Count; k++)
            {
                float x = _model.TimeToScreenX(keys[k].Position, width);
                float ny = (keys[k].Value - minVal) / range;
                float y = CurveHeaderH + areaH - ny * areaH;
                float dx = x - relX;
                float dy = y - relY;
                float d2 = dx * dx + dy * dy;
                if (d2 < best)
                {
                    best = d2;
                    component = c;
                    keyIndex = k;
                    keyTime = keys[k].Position;
                }
            }
        }
        return component >= 0;
    }

    private static readonly AnimationClipEditorModel.Channel[] OverlayChannels =
    {
        AnimationClipEditorModel.Channel.Position,
        AnimationClipEditorModel.Channel.Rotation,
        AnimationClipEditorModel.Channel.Scale,
    };

    private void OverlayValueRange(AnimationClip.AnimBone bone, out float minVal, out float maxVal)
    {
        minVal = float.MaxValue;
        maxVal = float.MinValue;
        for (int ch = 0; ch < OverlayChannels.Length; ch++)
        {
            int n = AnimationClipEditorModel.FillCurves(bone, OverlayChannels[ch], _curveBuf);
            for (int ci = 0; ci < n; ci++)
            {
                if (_curveBuf[ci] == null) continue;
                var keys = _curveBuf[ci]!.Keys;
                for (int k = 0; k < keys.Count; k++)
                {
                    float v = keys[k].Value;
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }
        }
        if (minVal > maxVal) { minVal = 0f; maxVal = 1f; }
        if (maxVal - minVal < 0.001f) { minVal -= 0.5f; maxVal += 0.5f; }
    }

    /// <summary>All-channels overlay: every P/R/S component curve of the context bone on one shared value axis.</summary>
    private void DrawOverlayContent(Canvas canvas, Rect rect, float width, float height)
    {
        var font = _font ?? EditorTheme.Font;
        Float2 origin = rect.Min;
        if (!TryCurveContext(out string bonePath, out var bone, out _)) return;

        if (font != null)
            canvas.DrawText($"{bonePath} — All Channels", origin.X + 6f, origin.Y + 4f,
                AnimationEditorChrome.TextBright32, 12f, font);

        float curveAreaY = origin.Y + CurveHeaderH;
        float curveAreaH = height - CurveHeaderH - 8f;
        OverlayValueRange(bone, out float minVal, out float maxVal);
        float range = maxVal - minVal;

        canvas.SetStrokeColor(AnimationEditorChrome.GridLine32);
        canvas.SetStrokeWidth(1f);
        for (int i = 0; i <= 4; i++)
        {
            float y = curveAreaY + i * curveAreaH / 4f;
            canvas.MoveTo(origin.X, y);
            canvas.LineTo(origin.X + width, y);
            canvas.Stroke();
        }

        for (int ch = 0; ch < OverlayChannels.Length; ch++)
        {
            var channel = OverlayChannels[ch];
            Color32 color = channel switch
            {
                AnimationClipEditorModel.Channel.Rotation => RotColor,
                AnimationClipEditorModel.Channel.Scale => SclColor,
                _ => PosColor,
            };
            int n = AnimationClipEditorModel.FillCurves(bone, channel, _curveBuf);
            for (int c = 0; c < n; c++)
            {
                var curve = _curveBuf[c];
                if (curve == null || curve.Keys.Count == 0) continue;
                canvas.SetStrokeColor(color);
                canvas.SetStrokeWidth(1.25f);
                SampleCurve(canvas, curve, origin, width, curveAreaY, curveAreaH, minVal, range);

                var keys = curve.Keys;
                for (int k = 0; k < keys.Count; k++)
                {
                    float x = origin.X + _model.TimeToScreenX(keys[k].Position, width);
                    float normalized = (keys[k].Value - minVal) / range;
                    float y = curveAreaY + curveAreaH - normalized * curveAreaH;
                    bool sel = bonePath == _model.SelectedBonePath && channel == _model.SelectedChannel
                               && _model.HasKeyframeSelection
                               && AnimationClipEditorModel.Near(keys[k].Position, _model.SelectedKeyframeTime);
                    canvas.CircleFilled(x, y, 2.5f, sel ? SelectedColor : color);
                }
            }
        }

        float px = origin.X + _model.TimeToScreenX((float)_playbackTime, width);
        canvas.SetStrokeColor(PlayheadColor);
        canvas.SetStrokeWidth(1.5f);
        canvas.MoveTo(px, curveAreaY);
        canvas.LineTo(px, curveAreaY + curveAreaH);
        canvas.Stroke();
    }

    /// <summary>Overlay click: hit-test across every channel's keys; selecting one switches the edit context to its channel.</summary>
    private void HandleOverlayClick(float relX, float relY, float width, float height)
    {
        if (!TryCurveContext(out string bonePath, out var bone, out _)) return;
        OverlayValueRange(bone, out float minVal, out float maxVal);
        float range = maxVal - minVal;
        if (range <= 0f) range = 1f;
        float areaH = height - CurveHeaderH - 8f;
        float best = DiamondRadiusPx * DiamondRadiusPx;
        var bestChannel = AnimationClipEditorModel.Channel.Position;
        int bestComp = -1, bestKey = -1;
        float bestTime = float.NaN;
        for (int ch = 0; ch < OverlayChannels.Length; ch++)
        {
            var channel = OverlayChannels[ch];
            int n = AnimationClipEditorModel.FillCurves(bone, channel, _curveBuf);
            for (int c = 0; c < n; c++)
            {
                if (_curveBuf[c] == null) continue;
                var keys = _curveBuf[c]!.Keys;
                for (int k = 0; k < keys.Count; k++)
                {
                    float x = _model.TimeToScreenX(keys[k].Position, width);
                    float ny = (keys[k].Value - minVal) / range;
                    float y = CurveHeaderH + areaH - ny * areaH;
                    float dx = x - relX;
                    float dy = y - relY;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < best)
                    {
                        best = d2;
                        bestChannel = channel;
                        bestComp = c;
                        bestKey = k;
                        bestTime = keys[k].Position;
                    }
                }
            }
        }
        if (bestComp >= 0)
            _model.SelectKeyframe(bonePath, bestChannel, bestKey, bestTime, bestComp);
    }

    private void DrawCurvesContent(Canvas canvas, Rect rect, float width, float height)
    {
        if (_curveOverlayAll)
        {
            DrawOverlayContent(canvas, rect, width, height);
            return;
        }
        var font = _font ?? EditorTheme.Font;
        Float2 origin = rect.Min;
        if (!TryCurveContext(out string bonePath, out var bone, out var channel)) return;

        if (font != null)
            canvas.DrawText($"{bonePath} — {channel}", origin.X + 6f, origin.Y + 4f,
                AnimationEditorChrome.TextBright32, 12f, font);

        float curveAreaY = origin.Y + CurveHeaderH;
        float curveAreaH = height - CurveHeaderH - 8f;
        CurveValueRange(bone, channel, out float minVal, out float maxVal);
        float range = maxVal - minVal;

        canvas.SetStrokeColor(AnimationEditorChrome.GridLine32);
        canvas.SetStrokeWidth(1f);
        for (int i = 0; i <= 4; i++)
        {
            float y = curveAreaY + i * curveAreaH / 4f;
            canvas.MoveTo(origin.X, y);
            canvas.LineTo(origin.X + width, y);
            canvas.Stroke();
        }

        int n = AnimationClipEditorModel.FillCurves(bone, channel, _curveBuf);
        for (int c = 0; c < n; c++)
        {
            var curve = _curveBuf[c];
            if (curve == null || curve.Keys.Count == 0) continue;
            Color32 color = c switch { 0 => CompX, 1 => CompY, 2 => CompZ, _ => CompW };

            canvas.SetStrokeColor(color);
            canvas.SetStrokeWidth(1.5f);
            SampleCurve(canvas, curve, origin, width, curveAreaY, curveAreaH, minVal, range);

            var keys = curve.Keys;
            for (int k = 0; k < keys.Count; k++)
            {
                float x = origin.X + _model.TimeToScreenX(keys[k].Position, width);
                float normalized = (keys[k].Value - minVal) / range;
                float y = curveAreaY + curveAreaH - normalized * curveAreaH;
                bool sel = bonePath == _model.SelectedBonePath && channel == _model.SelectedChannel
                           && _model.HasKeyframeSelection
                           && AnimationClipEditorModel.Near(keys[k].Position, _model.SelectedKeyframeTime)
                           && (_model.SelectedComponentIndex < 0 || _model.SelectedComponentIndex == c);
                canvas.CircleFilled(x, y, 3f, sel ? SelectedColor : color);
            }
        }

        DrawSelectedTangentHandles(canvas, origin, width, curveAreaY, curveAreaH, minVal, range);

        float px = origin.X + _model.TimeToScreenX((float)_playbackTime, width);
        canvas.SetStrokeColor(PlayheadColor);
        canvas.SetStrokeWidth(1.5f);
        canvas.MoveTo(px, curveAreaY);
        canvas.LineTo(px, curveAreaY + curveAreaH);
        canvas.Stroke();
    }

    private void DrawDopesheetContent(Canvas canvas, Rect rect, float width, float height)
    {
        Float2 origin = rect.Min;

        canvas.SaveState();
        canvas.IntersectScissor((float)origin.X, (float)origin.Y, width, height);

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            float y = origin.Y + i * ChannelRowHeight - _propsScrollY;
            if (y + ChannelRowHeight <= origin.Y) continue;
            if (y > origin.Y + height) break;

            bool alt = i % 2 == 0;
            Color32 rowBg = alt ? AnimationEditorChrome.RowEven32 : AnimationEditorChrome.RowOdd32;
            canvas.RectFilled(origin.X, y, width, ChannelRowHeight - 1f, rowBg);

            float pad = Math.Max(AnimationClipEditorModel.KeyTimeEpsilon,
                (_model.ViewEndTime - _model.ViewStartTime) * 0.02f);
            _model.FillRowKeyTimes(row, _rowTimes, _model.ViewStartTime - pad, _model.ViewEndTime + pad);
            float rowCenterY = y + ChannelRowHeight * 0.5f;
            float diamondR = row.Kind == AnimationClipEditorModel.PropertyRowKind.Bone ? 2.5f : 3.5f;
            for (int k = 0; k < _rowTimes.Count; k++)
            {
                float t = _rowTimes[k];
                float x = origin.X + _model.TimeToScreenX(t, width);
                if (x < origin.X || x > origin.X + width) continue;
                bool sel = row.Kind != AnimationClipEditorModel.PropertyRowKind.Bone
                           && _model.IsKeySelected(row.BonePath, row.Channel, t);
                canvas.SetFillColor(sel ? AnimationEditorChrome.Selection32 : AnimationEditorChrome.Keyframe32);
                DrawDiamond(canvas, x, rowCenterY, diamondR);
            }
        }

        if (_marquee)
        {
            float x = origin.X + Math.Min(_marqueeX0, _marqueeX1);
            float y = origin.Y + Math.Min(_marqueeY0, _marqueeY1);
            float mw = Math.Abs(_marqueeX1 - _marqueeX0);
            float mh = Math.Abs(_marqueeY1 - _marqueeY0);
            canvas.RectFilled(x, y, mw, mh, new Color32(44, 93, 135, 40));
            canvas.SetStrokeColor(AnimationEditorChrome.Selection32);
            canvas.SetStrokeWidth(1f);
            canvas.BeginPath();
            canvas.Rect(x, y, mw, mh);
            canvas.Stroke();
        }

        float px = origin.X + _model.TimeToScreenX((float)_playbackTime, width);
        canvas.SetStrokeColor(PlayheadColor);
        canvas.SetStrokeWidth(1.5f);
        canvas.MoveTo(px, origin.Y);
        canvas.LineTo(px, origin.Y + height);
        canvas.Stroke();

        canvas.RestoreState();
    }

    private static void DrawDiamond(Canvas canvas, float cx, float cy, float r)
    {
        // Fill preserves the path; each key must not refill all preceding keys.
        canvas.BeginPath();
        canvas.MoveTo(cx, cy - r);
        canvas.LineTo(cx + r, cy);
        canvas.LineTo(cx, cy + r);
        canvas.LineTo(cx - r, cy);
        canvas.ClosePath();
        canvas.Fill();
    }

    private void SnapshotClip()
    {
        if (_model.Clip != null && !_model.ClipIsReadOnly)
            Undo.Snapshot(_model.Clip);
    }

    private void SaveClip()
    {
        var clip = _model.Clip;
        if (clip == null || _model.ClipIsReadOnly) return;
        try
        {
            if (clip.AssetID != Guid.Empty)
            {
                var entry = EditorAssetBackend.Instance?.GetEntry(clip.AssetID);
                if (entry != null && AnimationAssetEditorUtil.Save(clip, entry))
                {
                    Runtime.Debug.Log($"[Animation] Saved clip {entry.Path}");
                    return;
                }
            }

            string? path = clip.AssetPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var echo = Serializer.Serialize(clip, TypeMode.Aggressive);
                echo.WriteToString(new FileInfo(path));
                Runtime.Debug.Log($"[Animation] Saved clip to {path}");
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[Animation] Save failed: {ex.Message}");
        }
    }
}
