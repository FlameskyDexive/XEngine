// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

public partial class AnimationClipEditorModel
{
    /// <summary>★ M39 — Unique key times across bones + generic curves, unsorted.</summary>
    public void CollectAllKeyTimes(List<float> dest)
    {
        dest.Clear();
        if (Clip == null) return;
        var bones = Clip.Bones;
        for (int i = 0; i < bones.Count; i++)
        {
            CollectTimes(bones[i], Channel.Position, dest, float.NegativeInfinity, float.PositiveInfinity);
            CollectTimes(bones[i], Channel.Rotation, dest, float.NegativeInfinity, float.PositiveInfinity);
            CollectTimes(bones[i], Channel.Scale, dest, float.NegativeInfinity, float.PositiveInfinity);
        }
        var gens = Clip.GenericCurves;
        for (int i = 0; i < gens.Count; i++)
        {
            if (gens[i].Curve != null)
                CollectCurveTimes(gens[i].Curve, dest);
        }
    }

    public bool TryFindPrevKeyTime(float time, out float result)
    {
        result = float.NaN;
        var times = new List<float>();
        CollectAllKeyTimes(times);
        float best = float.NegativeInfinity;
        for (int i = 0; i < times.Count; i++)
        {
            float t = times[i];
            if (t < time - KeyTimeEpsilon && t > best)
                best = t;
        }
        if (float.IsNegativeInfinity(best)) return false;
        result = best;
        return true;
    }

    public bool TryFindNextKeyTime(float time, out float result)
    {
        result = float.NaN;
        var times = new List<float>();
        CollectAllKeyTimes(times);
        float best = float.PositiveInfinity;
        for (int i = 0; i < times.Count; i++)
        {
            float t = times[i];
            if (t > time + KeyTimeEpsilon && t < best)
                best = t;
        }
        if (float.IsPositiveInfinity(best)) return false;
        result = best;
        return true;
    }

    public bool TrySetTicksPerSecond(float samples)
    {
        if (Clip == null || ClipIsReadOnly) return false;
        if (samples < 1f) samples = 1f;
        if (samples > 240f) samples = 240f;
        Clip.TicksPerSecond = samples;
        BumpContent();
        return true;
    }

    /// <summary>Insert a key at <paramref name="time"/> on the selected binding, or the first bone / generic track.</summary>
    public bool AddKeyframeAtTime(float time)
    {
        if (Clip == null || ClipIsReadOnly) return false;
        time = SnapToFrame(time);
        if (SelectedChannel == Channel.Generic)
        {
            string typeName = SelectedKeys.Count > 0 ? SelectedKeys[0].TypeName : "";
            string property = SelectedKeys.Count > 0 ? SelectedKeys[0].Property : "";
            if (!string.IsNullOrEmpty(property))
                return AddGenericProperty(SelectedBonePath ?? "", typeName, property, time);
        }
        if (SelectedChannel == Channel.BlendShape)
        {
            string shapeName = SelectedKeys.Count > 0 ? SelectedKeys[0].Property : "";
            if (!string.IsNullOrEmpty(shapeName))
                return AddBlendShapeProperty(SelectedBonePath ?? "", shapeName, time);
        }
        if (!string.IsNullOrEmpty(SelectedBonePath) && SelectedChannel != Channel.Generic && SelectedChannel != Channel.BlendShape)
            return AddKeyframe(SelectedBonePath, SelectedChannel, time);
        if (Clip.Bones.Count > 0)
            return AddKeyframe(Clip.Bones[0].BoneName, Channel.Position, time);
        if (Clip.GenericCurves.Count > 0)
        {
            var g = Clip.GenericCurves[0];
            return AddGenericProperty(g.Path, g.TypeName, g.Property, time);
        }
        return false;
    }
}
