// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

public partial class AnimationClipEditorModel
{
    /// <summary>
    /// One copied keyframe column: every component key that existed at the source time on a
    /// bone channel (or the single key of a generic track). Times are stored relative to the
    /// earliest copied key so a paste lays the block down starting at the paste time.
    /// </summary>
    public sealed class CopiedKey
    {
        public string BonePath = "";
        public Channel Channel;
        public float RelTime;
        public string TypeName = "";
        public string Property = "";
        /// <summary>Cloned per-component keys (slot = curve index); null where the component had no key.</summary>
        public readonly KeyFrame?[] Components = new KeyFrame?[4];
    }

    readonly List<CopiedKey> _keyClipboard = new();

    /// <summary>Number of key columns currently on the clipboard.</summary>
    public int CopiedKeyCount => _keyClipboard.Count;

    /// <summary>
    /// Copies the selected keys (values, tangents, modes, weights) to the clipboard.
    /// Returns the number of key columns captured.
    /// </summary>
    public int CopySelectedKeys()
    {
        _keyClipboard.Clear();
        if (Clip == null || SelectedKeys.Count == 0) return 0;

        float minTime = float.MaxValue;
        for (int i = 0; i < SelectedKeys.Count; i++)
        {
            if (SelectedKeys[i].Time < minTime)
                minTime = SelectedKeys[i].Time;
        }

        var tmp = new AnimationCurve?[4];
        for (int i = 0; i < SelectedKeys.Count; i++)
        {
            KeyRef k = SelectedKeys[i];
            var entry = new CopiedKey
            {
                BonePath = k.BonePath,
                Channel = k.Channel,
                RelTime = k.Time - minTime,
                TypeName = k.TypeName,
                Property = k.Property,
            };
            bool any = false;
            if (k.Channel == Channel.Generic)
            {
                var g = Clip.FindGenericCurve(k.BonePath, k.TypeName, k.Property);
                int idx = IndexOfKeyNear(g?.Curve, k.Time);
                if (g?.Curve != null && idx >= 0)
                {
                    entry.Components[0] = g.Curve.Keys[idx].WithPosition(0f);
                    any = true;
                }
            }
            else if (k.Channel == Channel.BlendShape)
            {
                var bs = Clip.FindBlendShapeTrack(k.BonePath, k.Property);
                int idx = IndexOfKeyNear(bs?.Weight, k.Time);
                if (bs?.Weight != null && idx >= 0)
                {
                    entry.Components[0] = bs.Weight.Keys[idx].WithPosition(0f);
                    any = true;
                }
            }
            else
            {
                var bone = Clip.GetBone(k.BonePath);
                if (bone == null) continue;
                int n = FillCurves(bone, k.Channel, tmp);
                for (int c = 0; c < n; c++)
                {
                    int idx = IndexOfKeyNear(tmp[c], k.Time);
                    if (idx < 0) continue;
                    entry.Components[c] = tmp[c]!.Keys[idx].WithPosition(0f);
                    any = true;
                }
            }
            if (any)
                _keyClipboard.Add(entry);
        }
        return _keyClipboard.Count;
    }

    /// <summary>
    /// Pastes the clipboard starting at <paramref name="atTime"/>, preserving the copied keys'
    /// relative offsets, values, tangents, and weights. Existing keys at a target time are
    /// replaced. The pasted keys become the new selection. Returns the pasted column count.
    /// </summary>
    public int PasteKeys(float atTime, bool snap = true)
    {
        if (Clip == null || _keyClipboard.Count == 0) return 0;
        if (atTime < 0f) atTime = 0f;

        var tmp = new AnimationCurve?[4];
        SelectedKeys.Clear();
        int pasted = 0;
        for (int i = 0; i < _keyClipboard.Count; i++)
        {
            CopiedKey entry = _keyClipboard[i];
            float t = atTime + entry.RelTime;
            if (snap) t = SnapToFrame(t);
            if (t < 0f) t = 0f;

            bool wrote = false;
            if (entry.Channel == Channel.Generic)
            {
                if (entry.Components[0] == null || string.IsNullOrEmpty(entry.Property)) continue;
                var g = Clip.FindGenericCurve(entry.BonePath, entry.TypeName, entry.Property);
                if (g == null)
                {
                    g = new AnimationClip.GenericCurve
                    {
                        Path = entry.BonePath,
                        TypeName = entry.TypeName,
                        Property = entry.Property,
                        Curve = CreateEmptyCurve(),
                    };
                    Clip.GenericCurves.Add(g);
                }
                g.Curve ??= CreateEmptyCurve();
                InsertOrReplaceKey(g.Curve, entry.Components[0]!, t);
                wrote = true;
            }
            else
 if (entry.Channel == Channel.BlendShape)
            {
                if (entry.Components[0] == null || string.IsNullOrEmpty(entry.Property)) continue;
                var bs = Clip.FindBlendShapeTrack(entry.BonePath, entry.Property);
                if (bs == null)
                {
                    bs = new AnimationClip.BlendShapeAnim
                    {
                        Path = entry.BonePath,
                        ShapeName = entry.Property,
                        Weight = CreateEmptyCurve(),
                    };
                    Clip.AddBlendShape(bs);
                }
                bs.Weight ??= CreateEmptyCurve();
                InsertOrReplaceKey(bs.Weight, entry.Components[0]!, t);
                wrote = true;
            }
            else
            {
                var bone = Clip.GetBone(entry.BonePath);
                if (bone == null) continue;
                EnsureCurves(bone, entry.Channel);
                int n = FillCurves(bone, entry.Channel, tmp);
                for (int c = 0; c < n; c++)
                {
                    if (entry.Components[c] == null || tmp[c] == null) continue;
                    InsertOrReplaceKey(tmp[c]!, entry.Components[c]!, t);
                    wrote = true;
                }
            }
            if (!wrote) continue;

            ExpandDuration(t);
            var pastedRef = new KeyRef(entry.BonePath, entry.Channel, t, entry.TypeName, entry.Property);
            if (IndexOfSelected(pastedRef) < 0)
                SelectedKeys.Add(pastedRef);
            pasted++;
        }

        if (SelectedKeys.Count > 0)
        {
            KeyRef last = SelectedKeys[SelectedKeys.Count - 1];
            SelectedBonePath = last.BonePath;
            SelectedChannel = last.Channel;
            SelectedKeyframeTime = last.Time;
            SelectedKeyframeIndex = 0;
            SelectedComponentIndex = -1;
        }
        if (pasted > 0)
        {
            BumpContent();
            BumpVisual();
        }
        return pasted;
    }

    /// <summary>Inserts a clone of <paramref name="source"/> at <paramref name="time"/>, replacing any key already there. Keeps the key list sorted.</summary>
    static void InsertOrReplaceKey(AnimationCurve curve, KeyFrame source, float time)
    {
        KeyFrame key = source.WithPosition(time);
        int existing = IndexOfKeyNear(curve, time);
        if (existing >= 0)
            curve.Keys[existing] = key;
        else
            curve.Keys.Add(key); // CurveKeyCollection.Add is a sorted insert.
        curve.RecalculateNonFreeTangents();
    }

    /// <summary>
    /// Frames the view to the selected keys' time range with a 10% margin (Unity's F).
    /// Returns false when nothing is selected.
    /// </summary>
    public bool FrameSelection()
    {
        if (SelectedKeys.Count == 0) return false;
        float min = float.MaxValue, max = float.MinValue;
        for (int i = 0; i < SelectedKeys.Count; i++)
        {
            float t = SelectedKeys[i].Time;
            if (t < min) min = t;
            if (t > max) max = t;
        }
        if (min > max) return false;
        float span = max - min;
        if (span < MinViewSpan)
        {
            float center = (min + max) * 0.5f;
            min = center - MinViewSpan * 0.5f;
            max = center + MinViewSpan * 0.5f;
            span = max - min;
        }
        float margin = span * 0.1f;
        ViewStartTime = min - margin;
        ViewEndTime = max + margin;
        BumpVisual();
        return true;
    }
}
