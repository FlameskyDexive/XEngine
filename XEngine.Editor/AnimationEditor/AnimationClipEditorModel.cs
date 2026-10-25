// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Backing state for the Animation Clip editor: which clip is open, which bone/channel/keyframe is
/// selected, the visible time range, and the time↔screen + keyframe hit-testing math. Pure data/math
/// — no rendering — so it's unit-testable. The window renders over this.
/// </summary>
public partial class AnimationClipEditorModel
{
    /// <summary>Which transform channel a keyframe belongs to.</summary>
    public enum Channel : byte
    {
        Position = 0,
        Rotation = 1,
        Scale = 2,
        Generic = 3,
        BlendShape = 4,
    }

    public const float DefaultKeyframePickRadius = 0.05f;
    public const float KeyTimeEpsilon = 1e-4f;
    public const float MinViewSpan = 0.05f;
    public const float MaxViewSpan = 600f;

    public AnimationClip? Clip { get; private set; }

    /// <summary>Scene object the window is bound to (Unity Animation window root). Null when editing a Project clip with no scene selection.</summary>
    public GameObject? Root { get; set; }

    /// <summary>Clips available on <see cref="Root"/> (Animator / AnimationComponent).</summary>
    public readonly List<AnimationClip> AvailableClips = new();

    /// <summary>True when the open clip is a model sub-asset (edit via Duplicate to edit).</summary>
    public bool ClipIsReadOnly { get; private set; }

    /// <summary>Root is selected but has no Animator/AnimationComponent and no clip — show Create.</summary>
    public bool NeedsCreatePrompt =>
        Root != null && Clip == null && !AnimationWindowBinding.HasAnimationComponent(Root);

    /// <summary>Bone path of the currently selected bone, or null.</summary>
    public string? SelectedBonePath;
    /// <summary>The channel shown/edited for the selected bone.</summary>
    public Channel SelectedChannel = Channel.Position;
    /// <summary>Index of the selected keyframe on the channel's first curve, or -1.</summary>
    public int SelectedKeyframeIndex = -1;
    /// <summary>Time of the selected keyframe, or <see cref="float.NaN"/> if none.</summary>
    public float SelectedKeyframeTime = float.NaN;
    /// <summary>Component index (X/Y/Z/W) selected in the curve view, or -1 for the whole channel.</summary>
    public int SelectedComponentIndex = -1;

    /// <summary>All selected keys (bone + channel + time). Primary fields above track the last click.</summary>
    public readonly List<KeyRef> SelectedKeys = new();

    readonly HashSet<string> _collapsed = new();
    readonly HashSet<string> _expandedComponents = new();
    readonly AnimationCurve?[] _curveScratch = new AnimationCurve?[4];

    /// <summary>Bumps when curve keys, events, or clip identity change. Selection and view use
    /// <see cref="VisualStamp"/> so a compared paint region can re-key without walking every key.</summary>
    public int ContentStamp { get; private set; }
    /// <summary>Bumps when the visible rows, selection, or time window change.</summary>
    public int VisualStamp { get; private set; }

    internal void BumpContent() => ContentStamp++;
    internal void BumpVisual() => VisualStamp++;

    /// <summary>Visible time range (seconds). Defaults to [0, clip duration].</summary>
    public float ViewStartTime;
    public float ViewEndTime = 1f;

    public bool HasKeyframeSelection => SelectedKeys.Count > 0 || !float.IsNaN(SelectedKeyframeTime);

    /// <summary>Clip ticks-per-second, or 30 when unset.</summary>
    public float FrameRate => Clip != null && Clip.TicksPerSecond > 0f ? Clip.TicksPerSecond : 30f;

    public void SetClip(AnimationClip? clip)
    {
        Clip = clip;
        ClipIsReadOnly = AnimationWindowBinding.IsReadOnlyClip(clip);
        SelectedBonePath = null;
        SelectedChannel = Channel.Position;
        SelectedKeyframeIndex = -1;
        SelectedKeyframeTime = float.NaN;
        SelectedComponentIndex = -1;
        SelectedKeys.Clear();
        _collapsed.Clear();
        _expandedComponents.Clear();
        ViewStartTime = 0f;
        ViewEndTime = clip != null && clip.Duration > 0f ? clip.Duration : 1f;
        RefreshAvailableClips();
        BumpContent();
        BumpVisual();
    }

    public void Bind(GameObject? root, AnimationClip? clip)
    {
        Root = root;
        SetClip(clip);
    }

    public void RefreshAvailableClips()
    {
        var previous = Clip;
        AnimationWindowBinding.CollectClips(Root, AvailableClips);
        if (previous != null)
        {
            bool found = false;
            for (int i = 0; i < AvailableClips.Count; i++)
            {
                if (ReferenceEquals(AvailableClips[i], previous))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                AvailableClips.Insert(0, previous);
        }
    }

    /// <summary>The selected bone's <see cref="AnimationClip.AnimBone"/>, or null.</summary>
    public AnimationClip.AnimBone? SelectedBone => SelectedBonePath != null ? Clip?.GetBone(SelectedBonePath) : null;

    /// <summary>Time → screen X in [0, width]. Clamped to the view range; out-of-range times map outside [0,width].</summary>
    public float TimeToScreenX(float time, float width)
    {
        float span = ViewEndTime - ViewStartTime;
        if (span <= 0f) return 0f;
        return (time - ViewStartTime) / span * width;
    }

    /// <summary>Screen X → time.</summary>
    public float ScreenXToTime(float x, float width)
    {
        if (width <= 0f) return ViewStartTime;
        return ViewStartTime + x / width * (ViewEndTime - ViewStartTime);
    }

    public void FrameAll()
    {
        ViewStartTime = 0f;
        ViewEndTime = Clip != null && Clip.Duration > 0f ? Clip.Duration : 1f;
        BumpVisual();
    }

    /// <summary>Zoom the view around <paramref name="anchorTime"/>. Factor &gt; 1 zooms out.</summary>
    public void ZoomView(float factor, float anchorTime)
    {
        float span = ViewEndTime - ViewStartTime;
        if (span <= 0f) span = 1f;
        if (factor <= 0f) return;
        float newSpan = span * factor;
        if (newSpan < MinViewSpan) newSpan = MinViewSpan;
        if (newSpan > MaxViewSpan) newSpan = MaxViewSpan;
        float t = (anchorTime - ViewStartTime) / span;
        if (t < 0f) t = 0f;
        else if (t > 1f) t = 1f;
        ViewStartTime = anchorTime - t * newSpan;
        ViewEndTime = ViewStartTime + newSpan;
        BumpVisual();
    }

    public void PanView(float deltaTime)
    {
        ViewStartTime += deltaTime;
        ViewEndTime += deltaTime;
        BumpVisual();
    }

    /// <summary>Step <paramref name="time"/> by whole frames. Direction is -1 / 0 / +1.</summary>
    public float StepFrame(float time, int direction)
    {
        float fps = FrameRate;
        float frame = MathF.Round(time * fps) + direction;
        if (frame < 0f) frame = 0f;
        float max = Clip != null && Clip.Duration > 0f ? Clip.Duration * fps : frame;
        if (frame > max) frame = max;
        return frame / fps;
    }

    public static int ComponentCount(Channel channel) => channel switch
    {
        Channel.Rotation => 4,
        Channel.Generic => 1,
        Channel.BlendShape => 1,
        _ => 3,
    };

    /// <summary>Fills <paramref name="dest"/> with the channel's curves (null slots allowed). Returns count.</summary>
    public static int FillCurves(AnimationClip.AnimBone bone, Channel channel, AnimationCurve?[] dest)
    {
        int n = ComponentCount(channel);
        for (int i = 0; i < dest.Length; i++)
            dest[i] = null;
        switch (channel)
        {
            case Channel.Position:
                dest[0] = bone.PosX; dest[1] = bone.PosY; dest[2] = bone.PosZ;
                break;
            case Channel.Rotation:
                dest[0] = bone.RotX; dest[1] = bone.RotY; dest[2] = bone.RotZ; dest[3] = bone.RotW;
                break;
            case Channel.Scale:
                dest[0] = bone.ScaleX; dest[1] = bone.ScaleY; dest[2] = bone.ScaleZ;
                break;
        }
        return n;
    }

    /// <summary>Returns the curves for a bone's channel (PosX/Y/Z, RotX/Y/Z/W, or ScaleX/Y/Z).</summary>
    public static IReadOnlyList<AnimationCurve> GetCurves(AnimationClip.AnimBone bone, Channel channel)
    {
        var tmp = new AnimationCurve?[4];
        int n = FillCurves(bone, channel, tmp);
        var list = new List<AnimationCurve>(n);
        for (int i = 0; i < n; i++)
            list.Add(tmp[i]!);
        return list;
    }

    public static bool HasChannelCurves(AnimationClip.AnimBone bone, Channel channel)
    {
        var tmp = new AnimationCurve?[4];
        int n = FillCurves(bone, channel, tmp);
        for (int i = 0; i < n; i++)
        {
            if (tmp[i] != null) return true;
        }
        return false;
    }

    public static void EnsureCurves(AnimationClip.AnimBone bone, Channel channel)
    {
        switch (channel)
        {
            case Channel.Position:
                bone.PosX ??= CreateEmptyCurve();
                bone.PosY ??= CreateEmptyCurve();
                bone.PosZ ??= CreateEmptyCurve();
                break;
            case Channel.Rotation:
                bone.RotX ??= CreateEmptyCurve();
                bone.RotY ??= CreateEmptyCurve();
                bone.RotZ ??= CreateEmptyCurve();
                bone.RotW ??= CreateEmptyCurve();
                break;
            case Channel.Scale:
                bone.ScaleX ??= CreateEmptyCurve();
                bone.ScaleY ??= CreateEmptyCurve();
                bone.ScaleZ ??= CreateEmptyCurve();
                break;
        }
    }

    static AnimationCurve CreateEmptyCurve()
    {
        var c = new AnimationCurve();
        c.Keys.Clear();
        return c;
    }

    public static int IndexOfKeyNear(AnimationCurve? curve, float time, float epsilon = KeyTimeEpsilon)
    {
        if (curve == null) return -1;
        var keys = curve.Keys;
        for (int i = 0; i < keys.Count; i++)
        {
            float d = keys[i].Position - time;
            if (d < 0f) d = -d;
            if (d <= epsilon) return i;
        }
        return -1;
    }

    /// <summary>
    /// Finds the index of the keyframe nearest <paramref name="time"/> within
    /// <paramref name="radius"/> seconds on the selected bone/channel (searching the channel's first
    /// curve), or -1. Used by click-to-select in the dopesheet.
    /// </summary>
    public int HitKeyframe(string bonePath, Channel channel, float time, float radius = DefaultKeyframePickRadius)
    {
        if (!TryHitKeyframe(bonePath, channel, time, radius, out int index, out _))
            return -1;
        return index;
    }

    /// <summary>Hit-test across every component of the channel. <paramref name="index"/> is on the first curve when it has a matching key.</summary>
    public bool TryHitKeyframe(string bonePath, Channel channel, float time, float radius, out int index, out float keyTime)
    {
        index = -1;
        keyTime = float.NaN;
        if (Clip == null) return false;
        if (channel == Channel.BlendShape)
        {
            // Blend-shape tracks have no AnimBone; their key ops carry the shape name in
            // Property and route through AddBlendShapeProperty / the blendshape branches below.
            return false;
        }
        var bone = Clip.GetBone(bonePath);
        if (bone == null) return false;

        var tmp = new AnimationCurve?[4];
        int n = FillCurves(bone, channel, tmp);
        int best = -1;
        float bestDist = radius;
        float bestTime = float.NaN;
        AnimationCurve? bestCurve = null;
        for (int c = 0; c < n; c++)
        {
            var curve = tmp[c];
            if (curve == null) continue;
            var keys = curve.Keys;
            for (int i = 0; i < keys.Count; i++)
            {
                float d = keys[i].Position - time;
                if (d < 0f) d = -d;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                    bestTime = keys[i].Position;
                    bestCurve = curve;
                }
            }
        }
        if (best < 0 || bestCurve == null) return false;

        keyTime = bestTime;
        index = IndexOfKeyNear(tmp[0], bestTime);
        if (index < 0) index = best;
        return true;
    }

    public void SelectKeyframe(string bonePath, Channel channel, int index, float time, int component = -1, bool additive = false, string typeName = "", string property = "")
    {
        var key = new KeyRef(bonePath, channel, time, typeName, property);
        if (!additive)
            SelectedKeys.Clear();
        int existing = IndexOfSelected(key);
        if (additive && existing >= 0)
            SelectedKeys.RemoveAt(existing);
        else if (existing < 0)
            SelectedKeys.Add(key);

        SelectedBonePath = bonePath;
        SelectedChannel = channel;
        SelectedKeyframeIndex = index;
        SelectedKeyframeTime = time;
        SelectedComponentIndex = component;
        if (SelectedKeys.Count == 0)
            ClearKeyframeSelection();
        else
            BumpVisual();
    }

    public void ClearKeyframeSelection()
    {
        SelectedKeyframeIndex = -1;
        SelectedKeyframeTime = float.NaN;
        SelectedComponentIndex = -1;
        SelectedKeys.Clear();
        BumpVisual();
    }

    public bool IsKeySelected(string bonePath, Channel channel, float time)
    {
        for (int i = 0; i < SelectedKeys.Count; i++)
        {
            KeyRef k = SelectedKeys[i];
            if (k.Channel == channel && Near(k.Time, time) && k.BonePath == bonePath)
                return true;
        }
        return false;
    }

    int IndexOfSelected(KeyRef key)
    {
        for (int i = 0; i < SelectedKeys.Count; i++)
        {
            KeyRef k = SelectedKeys[i];
            if (k.Channel == key.Channel && Near(k.Time, key.Time) && k.BonePath == key.BonePath
                && k.TypeName == key.TypeName && k.Property == key.Property)
                return i;
        }
        return -1;
    }

    /// <summary>Round <paramref name="time"/> to the nearest whole frame.</summary>
    public float SnapToFrame(float time)
    {
        if (time < 0f) time = 0f;
        float fps = FrameRate;
        return MathF.Round(time * fps) / fps;
    }

    public static string BoneCollapseId(string bonePath) => "b:" + bonePath;
    public static string ChannelCollapseId(string bonePath, Channel channel) => "c:" + bonePath + "|" + (int)channel;

    public bool IsBoneExpanded(string bonePath) => !_collapsed.Contains(BoneCollapseId(bonePath));
    public bool IsShowingComponents(string bonePath, Channel channel) => _expandedComponents.Contains(ChannelCollapseId(bonePath, channel));

    public void ToggleBoneExpanded(string bonePath)
    {
        string id = BoneCollapseId(bonePath);
        if (!_collapsed.Add(id)) _collapsed.Remove(id);
        BumpVisual();
    }

    public void ToggleComponentsVisible(string bonePath, Channel channel)
    {
        string id = ChannelCollapseId(bonePath, channel);
        if (!_expandedComponents.Add(id)) _expandedComponents.Remove(id);
        BumpVisual();
    }

    public static string ChannelLabel(Channel channel) => channel switch
    {
        Channel.Rotation => "Rotation",
        Channel.Scale => "Scale",
        Channel.Generic => "Generic",
        Channel.BlendShape => "BlendShape",
        _ => "Position",
    };

    public static string ComponentLabel(Channel channel, int component)
    {
        if (channel == Channel.Rotation)
            return component switch { 0 => "X", 1 => "Y", 2 => "Z", _ => "W" };
        return component switch { 0 => "X", 1 => "Y", _ => "Z" };
    }

    /// <summary>Visible left-pane / dopesheet rows: bone → channel → optional X/Y/Z(/W).</summary>
    public void FillVisibleRows(List<PropertyRow> dest)
    {
        dest.Clear();
        if (Clip == null) return;
        var bones = Clip.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            string path = bone.BoneName;
            bool boneOpen = IsBoneExpanded(path);
            dest.Add(new PropertyRow
            {
                Kind = PropertyRowKind.Bone,
                BonePath = path,
                Channel = Channel.Position,
                Component = -1,
                Label = path,
                Depth = 0,
                HasChildren = true,
                Expanded = boneOpen,
            });
            if (!boneOpen) continue;

            AppendChannelRow(dest, bone, path, Channel.Position);
            AppendChannelRow(dest, bone, path, Channel.Rotation);
            AppendChannelRow(dest, bone, path, Channel.Scale);
        }
        AppendGenericRows(dest);
        AppendBlendShapeRows(dest);
    }

    void AppendChannelRow(List<PropertyRow> dest, AnimationClip.AnimBone bone, string path, Channel channel)
    {
        bool showComps = IsShowingComponents(path, channel) && HasChannelCurves(bone, channel);
        dest.Add(new PropertyRow
        {
            Kind = PropertyRowKind.Channel,
            BonePath = path,
            Channel = channel,
            Component = -1,
            Label = ChannelLabel(channel),
            Depth = 1,
            HasChildren = HasChannelCurves(bone, channel),
            Expanded = showComps,
        });
        if (!showComps) return;

        int n = ComponentCount(channel);
        FillCurves(bone, channel, _curveScratch);
        for (int c = 0; c < n; c++)
        {
            if (_curveScratch[c] == null) continue;
            dest.Add(new PropertyRow
            {
                Kind = PropertyRowKind.Component,
                BonePath = path,
                Channel = channel,
                Component = c,
                Label = ComponentLabel(channel, c),
                Depth = 2,
                HasChildren = false,
                Expanded = true,
            });
        }
    }

    /// <summary>Times of keys that should draw on this row (union of the row's curves).</summary>
    public void FillRowKeyTimes(in PropertyRow row, List<float> dest)
        => FillRowKeyTimes(row, dest, float.NegativeInfinity, float.PositiveInfinity);

    /// <summary>Same as <see cref="FillRowKeyTimes(in PropertyRow, List{float})"/> but only
    /// keys whose time is inside <paramref name="viewStart"/>…<paramref name="viewEnd"/>.
    /// The curve is walked from a lower-bound index so a long take does not scan keys
    /// that sit left of the window.</summary>
    public void FillRowKeyTimes(in PropertyRow row, List<float> dest, float viewStart, float viewEnd)
    {
        dest.Clear();
        if (Clip == null) return;
        if (row.Kind == PropertyRowKind.Generic)
        {
            var g = Clip.FindGenericCurve(row.BonePath, row.TypeName, row.Property);
            if (g?.Curve != null)
                CollectCurveTimes(g.Curve, dest, viewStart, viewEnd);
            return;
        }
        if (row.Kind == PropertyRowKind.BlendShape)
        {
            var bs = Clip.FindBlendShapeTrack(row.BonePath, row.Property);
            if (bs?.Weight != null)
                CollectCurveTimes(bs.Weight, dest, viewStart, viewEnd);
            return;
        }
        var bone = Clip.GetBone(row.BonePath);
        if (bone == null) return;
        if (row.Kind == PropertyRowKind.Bone)
        {
            CollectTimes(bone, Channel.Position, dest, viewStart, viewEnd);
            CollectTimes(bone, Channel.Rotation, dest, viewStart, viewEnd);
            CollectTimes(bone, Channel.Scale, dest, viewStart, viewEnd);
            return;
        }
        if (row.Kind == PropertyRowKind.Component)
        {
            FillCurves(bone, row.Channel, _curveScratch);
            if (row.Component >= 0 && row.Component < 4 && _curveScratch[row.Component] != null)
                CollectCurveTimes(_curveScratch[row.Component]!, dest, viewStart, viewEnd);
            return;
        }
        CollectTimes(bone, row.Channel, dest, viewStart, viewEnd);
    }

    void CollectTimes(AnimationClip.AnimBone bone, Channel channel, List<float> dest, float viewStart, float viewEnd)
    {
        int n = FillCurves(bone, channel, _curveScratch);
        for (int i = 0; i < n; i++)
        {
            if (_curveScratch[i] != null)
                CollectCurveTimes(_curveScratch[i]!, dest, viewStart, viewEnd);
        }
    }

    static void CollectCurveTimes(AnimationCurve curve, List<float> dest)
        => CollectCurveTimes(curve, dest, float.NegativeInfinity, float.PositiveInfinity);

    static void CollectCurveTimes(AnimationCurve curve, List<float> dest, float viewStart, float viewEnd)
    {
        var keys = curve.Keys;
        int n = keys.Count;
        if (n == 0) return;
        int i = float.IsNegativeInfinity(viewStart) ? 0 : LowerBoundKey(keys, viewStart);
        for (; i < n; i++)
        {
            float t = keys[i].Position;
            if (t > viewEnd) break;
            bool found = false;
            for (int d = 0; d < dest.Count; d++)
            {
                if (Near(dest[d], t)) { found = true; break; }
            }
            if (!found) dest.Add(t);
        }
    }

    static int LowerBoundKey(CurveKeyCollection keys, float time)
    {
        int lo = 0, hi = keys.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (keys[mid].Position < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Select every visible-row key whose time is in <paramref name="t0"/>…<paramref name="t1"/>
    /// and whose row index is in <paramref name="row0"/>…<paramref name="row1"/>.
    /// </summary>
    public void MarqueeSelect(IReadOnlyList<PropertyRow> rows, int row0, int row1, float t0, float t1, bool additive)
    {
        if (!additive) SelectedKeys.Clear();
        if (row0 > row1) { int s = row0; row0 = row1; row1 = s; }
        if (t0 > t1) { float s = t0; t0 = t1; t1 = s; }
        var times = new List<float>();
        for (int r = row0; r <= row1 && r < rows.Count; r++)
        {
            if (r < 0) continue;
            var row = rows[r];
            if (row.Kind == PropertyRowKind.Bone) continue;
            FillRowKeyTimes(row, times);
            for (int i = 0; i < times.Count; i++)
            {
                float t = times[i];
                if (t < t0 - KeyTimeEpsilon || t > t1 + KeyTimeEpsilon) continue;
                var key = new KeyRef(row.BonePath, row.Channel, t, row.TypeName, row.Property);
                if (IndexOfSelected(key) < 0)
                    SelectedKeys.Add(key);
            }
        }
        if (SelectedKeys.Count > 0)
        {
            KeyRef last = SelectedKeys[SelectedKeys.Count - 1];
            SelectedBonePath = last.BonePath;
            SelectedChannel = last.Channel;
            SelectedKeyframeTime = last.Time;
            SelectedKeyframeIndex = 0;
        }
        else
            ClearKeyframeSelection();
        BumpVisual();
    }

    /// <summary>Shift every selected key by <paramref name="deltaTime"/>. Snap when requested.</summary>
    public bool MoveSelectedKeys(float deltaTime, bool snap)
    {
        if (Clip == null || SelectedKeys.Count == 0) return false;

        var items = new List<KeyRef>(SelectedKeys.Count);
        for (int i = 0; i < SelectedKeys.Count; i++)
            items.Add(SelectedKeys[i]);
        if (deltaTime >= 0f)
            items.Sort(static (a, b) => b.Time.CompareTo(a.Time));
        else
            items.Sort(static (a, b) => a.Time.CompareTo(b.Time));

        var moved = new List<KeyRef>(items.Count);
        bool any = false;
        for (int i = 0; i < items.Count; i++)
        {
            KeyRef k = items[i];
            float t = k.Time + deltaTime;
            if (snap) t = SnapToFrame(t);
            if (t < 0f) t = 0f;
            if (MoveKeyframe(k.BonePath, k.Channel, k.Time, t, updateSelection: false))
            {
                any = true;
                moved.Add(new KeyRef(k.BonePath, k.Channel, t));
            }
            else
                moved.Add(k);
        }
        SelectedKeys.Clear();
        for (int i = 0; i < moved.Count; i++)
        {
            if (IndexOfSelected(moved[i]) < 0)
                SelectedKeys.Add(moved[i]);
        }
        if (SelectedKeys.Count > 0)
        {
            KeyRef last = SelectedKeys[SelectedKeys.Count - 1];
            SelectedBonePath = last.BonePath;
            SelectedChannel = last.Channel;
            SelectedKeyframeTime = last.Time;
        }
        if (any)
        {
            BumpContent();
            BumpVisual();
        }
        return any;
    }

    public bool RemoveSelectedKeys()
    {
        if (Clip == null || SelectedKeys.Count == 0) return false;
        var items = new List<KeyRef>(SelectedKeys.Count);
        for (int i = 0; i < SelectedKeys.Count; i++)
            items.Add(SelectedKeys[i]);
        bool any = false;
        for (int i = 0; i < items.Count; i++)
        {
            if (RemoveKeyframe(items[i].BonePath, items[i].Channel, items[i].Time,
                    items[i].TypeName, items[i].Property))
                any = true;
        }
        ClearKeyframeSelection();
        return any;
    }

    /// <summary>Adds a key at <paramref name="time"/> on every component of the channel (evaluated, or a TRS default).</summary>
    public bool AddKeyframe(string bonePath, Channel channel, float time)
    {
        if (Clip == null) return false;
        var bone = Clip.GetBone(bonePath);
        if (bone == null) return false;
        if (time < 0f) time = 0f;

        EnsureCurves(bone, channel);
        var tmp = new AnimationCurve?[4];
        int n = FillCurves(bone, channel, tmp);
        bool added = false;
        for (int i = 0; i < n; i++)
        {
            var curve = tmp[i];
            if (curve == null) continue;
            if (IndexOfKeyNear(curve, time) >= 0) continue;
            float value = curve.Keys.Count > 0 ? curve.Evaluate(time) : DefaultComponentValue(channel, i);
            curve.Keys.Add(new KeyFrame(time, value));
            curve.SmoothTangents(CurveTangent.Smooth);
            added = true;
        }
        if (!added && IndexOfKeyNear(tmp[0], time) < 0)
            return false;

        ExpandDuration(time);
        BumpContent();
        SelectKeyframe(bonePath, channel, IndexOfKeyNear(tmp[0], time), time);
        return true;
    }

    /// <summary>Removes keys at the selected time from every component of the selected channel.</summary>
    public bool RemoveSelectedKeyframe()
    {
        if (Clip == null || SelectedBonePath == null || !HasKeyframeSelection)
            return false;
        return RemoveKeyframe(SelectedBonePath, SelectedChannel, SelectedKeyframeTime,
            SelectedKeys.Count > 0 ? SelectedKeys[0].TypeName : "",
            SelectedKeys.Count > 0 ? SelectedKeys[0].Property : "");
    }

    public bool RemoveKeyframe(string bonePath, Channel channel, float time, string typeName = "", string property = "")
    {
        if (Clip == null) return false;
        if (channel == Channel.BlendShape)
        {
            var t = Clip.FindBlendShapeTrack(bonePath, property);
            if (t?.Weight == null) return false;
            int bsIdx = IndexOfKeyNear(t.Weight, time);
            if (bsIdx < 0) return false;
            t.Weight.Keys.RemoveAt(bsIdx);
            if (SelectedChannel == Channel.BlendShape && Near(SelectedKeyframeTime, time))
                ClearKeyframeSelection();
            BumpContent();
            return true;
        }
        if (channel == Channel.Generic)
        {
            var g = Clip.FindGenericCurve(bonePath, typeName, property);
            if (g?.Curve == null) return false;
            int idx = IndexOfKeyNear(g.Curve, time);
            if (idx < 0) return false;
            g.Curve.Keys.RemoveAt(idx);
            if (SelectedChannel == Channel.Generic && Near(SelectedKeyframeTime, time))
                ClearKeyframeSelection();
            BumpContent();
            return true;
        }
        var bone = Clip.GetBone(bonePath);
        if (bone == null) return false;

        var tmp = new AnimationCurve?[4];
        int n = FillCurves(bone, channel, tmp);
        bool removed = false;
        for (int i = 0; i < n; i++)
        {
            var curve = tmp[i];
            if (curve == null) continue;
            int idx = IndexOfKeyNear(curve, time);
            if (idx < 0) continue;
            curve.Keys.RemoveAt(idx);
            if (curve.Keys.Count > 0)
                curve.SmoothTangents(CurveTangent.Smooth);
            removed = true;
        }
        if (!removed) return false;

        if (SelectedBonePath == bonePath && SelectedChannel == channel && Near(SelectedKeyframeTime, time))
            ClearKeyframeSelection();
        BumpContent();
        return true;
    }

    /// <summary>Moves every component key at <paramref name="fromTime"/> to <paramref name="toTime"/>.</summary>
    public bool MoveKeyframe(string bonePath, Channel channel, float fromTime, float toTime, bool updateSelection = true)
    {
        if (Clip == null) return false;
        var bone = Clip.GetBone(bonePath);
        if (bone == null) return false;
        if (toTime < 0f) toTime = 0f;
        if (Near(fromTime, toTime)) return true;

        var tmp = new AnimationCurve?[4];
        int n = FillCurves(bone, channel, tmp);
        bool moved = false;
        for (int i = 0; i < n; i++)
        {
            var curve = tmp[i];
            if (curve == null) continue;
            int idx = IndexOfKeyNear(curve, fromTime);
            if (idx < 0) continue;
            KeyFrame old = curve.Keys[idx];
            curve.Keys[idx] = old.WithPosition(toTime);
            curve.RecalculateNonFreeTangents();
            moved = true;
        }
        if (!moved) return false;

        ExpandDuration(toTime);
        BumpContent();
        if (updateSelection)
            SelectKeyframe(bonePath, channel, IndexOfKeyNear(tmp[0], toTime), toTime, SelectedComponentIndex);
        return true;
    }

    public bool SetKeyframeValue(string bonePath, Channel channel, float time, int component, float value)
    {
        if (Clip == null) return false;
        var bone = Clip.GetBone(bonePath);
        if (bone == null) return false;
        var tmp = new AnimationCurve?[4];
        int n = FillCurves(bone, channel, tmp);
        if (component < 0 || component >= n) return false;
        var curve = tmp[component];
        if (curve == null) return false;
        int idx = IndexOfKeyNear(curve, time);
        if (idx < 0) return false;
        curve.Keys[idx].Value = value;
        curve.RecalculateNonFreeTangents();
        BumpContent();
        return true;
    }

    public bool TryGetKeyframeValue(string bonePath, Channel channel, float time, int component, out float value)
    {
        value = 0f;
        if (Clip == null) return false;
        var bone = Clip.GetBone(bonePath);
        if (bone == null) return false;
        var tmp = new AnimationCurve?[4];
        int n = FillCurves(bone, channel, tmp);
        if (component < 0 || component >= n) return false;
        var curve = tmp[component];
        if (curve == null) return false;
        int idx = IndexOfKeyNear(curve, time);
        if (idx < 0) return false;
        value = curve.Keys[idx].Value;
        return true;
    }

    /// <summary>Creates missing TRS curves on an existing bone and keys them at <paramref name="time"/>.</summary>
    public bool AddProperty(string bonePath, Channel channel, float time)
    {
        if (Clip == null) return false;
        var bone = Clip.GetBone(bonePath);
        if (bone == null) return false;
        EnsureCurves(bone, channel);
        return AddKeyframe(bonePath, channel, time);
    }

    public void FillAddableProperties(List<(string Bone, Channel Channel)> dest)
    {
        dest.Clear();
        if (Clip == null) return;
        var bones = Clip.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            if (!HasChannelCurves(bone, Channel.Position))
                dest.Add((bone.BoneName, Channel.Position));
            if (!HasChannelCurves(bone, Channel.Rotation))
                dest.Add((bone.BoneName, Channel.Rotation));
            if (!HasChannelCurves(bone, Channel.Scale))
                dest.Add((bone.BoneName, Channel.Scale));
        }
    }

    /// <summary>Lists the bone paths in the clip (for the hierarchy panel).</summary>
    public IReadOnlyList<string> GetBonePaths()
    {
        var result = new List<string>();
        if (Clip == null) return result;
        var bones = Clip.Bones;
        for (int i = 0; i < bones.Count; i++)
            result.Add(bones[i].BoneName);
        return result;
    }

    void ExpandDuration(float time)
    {
        if (Clip != null && time > Clip.Duration)
            Clip.Duration = time;
    }

    static float DefaultComponentValue(Channel channel, int component)
    {
        if (channel == Channel.Scale) return 1f;
        if (channel == Channel.Rotation && component == 3) return 1f;
        return 0f;
    }

    public static bool Near(float a, float b, float epsilon = KeyTimeEpsilon)
    {
        float d = a - b;
        if (d < 0f) d = -d;
        return d <= epsilon;
    }

    /// <summary>One selected keyframe (a time on a bone channel; components share the identity).</summary>
    public readonly struct KeyRef
    {
        public readonly string BonePath;
        public readonly Channel Channel;
        public readonly float Time;
        public readonly string TypeName;
        public readonly string Property;
        public KeyRef(string bonePath, Channel channel, float time)
            : this(bonePath, channel, time, "", "") { }
        public KeyRef(string bonePath, Channel channel, float time, string typeName, string property)
        {
            BonePath = bonePath;
            Channel = channel;
            Time = time;
            TypeName = typeName ?? "";
            Property = property ?? "";
        }
    }

    public enum PropertyRowKind : byte { Bone, Channel, Component, Generic, BlendShape }

    public struct PropertyRow
    {
        public PropertyRowKind Kind;
        public string BonePath;
        public Channel Channel;
        public int Component;
        public string Label;
        public int Depth;
        public bool HasChildren;
        public bool Expanded;
        public string TypeName;
        public string Property;
    }
}
