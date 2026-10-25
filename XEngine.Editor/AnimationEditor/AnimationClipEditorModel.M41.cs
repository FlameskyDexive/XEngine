// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

public partial class AnimationClipEditorModel
{
    /// <summary>★ M41 — The curve currently edited in the curve view (selected component, or first of the channel).</summary>
    public bool TryGetSelectedCurve(out AnimationCurve curve, out int keyIndex)
    {
        curve = null!;
        keyIndex = -1;
        if (Clip == null || SelectedBonePath == null || !HasKeyframeSelection)
            return false;
        var tmp = new AnimationCurve?[4];
        AnimationClip.AnimBone? bone = Clip.GetBone(SelectedBonePath);
        int n = 0;
        if (SelectedChannel == Channel.Generic && SelectedKeys.Count > 0)
        {
            var g = Clip.FindGenericCurve(SelectedBonePath, SelectedKeys[0].TypeName, SelectedKeys[0].Property);
            if (g?.Curve == null) return false;
            tmp[0] = g.Curve;
            n = 1;
        }
        else if (SelectedChannel == Channel.BlendShape && SelectedKeys.Count > 0)
        {
            var bs = Clip.FindBlendShapeTrack(SelectedBonePath, SelectedKeys[0].Property);
            if (bs?.Weight == null) return false;
            tmp[0] = bs.Weight;
            n = 1;
        }
        else
        {
            if (bone == null) return false;
            n = FillCurves(bone, SelectedChannel, tmp);
        }
        int comp = SelectedComponentIndex >= 0 ? SelectedComponentIndex : 0;
        if (comp >= n || tmp[comp] == null) return false;
        curve = tmp[comp]!;
        keyIndex = IndexOfKeyNear(curve, SelectedKeyframeTime);
        return keyIndex >= 0;
    }

    public bool ApplyTangentPreset(string preset)
    {
        if (!TryGetSelectedCurve(out AnimationCurve curve, out int idx))
            return false;
        KeyFrame key = curve.Keys[idx];
        switch (preset)
        {
            case "Auto":
                key.InTangentMode = AnimationTangentMode.Auto;
                key.OutTangentMode = AnimationTangentMode.Auto;
                break;
            case "ClampedAuto":
                key.InTangentMode = AnimationTangentMode.ClampedAuto;
                key.OutTangentMode = AnimationTangentMode.ClampedAuto;
                break;
            case "FreeSmooth":
                key.InTangentMode = AnimationTangentMode.Free;
                key.OutTangentMode = AnimationTangentMode.Free;
                curve.SmoothTangent(idx, CurveTangent.Smooth);
                return true;
            case "Flat":
                key.InTangentMode = AnimationTangentMode.Free;
                key.OutTangentMode = AnimationTangentMode.Free;
                key.TangentIn = 0f;
                key.TangentOut = 0f;
                return true;
            case "Broken":
                key.InTangentMode = AnimationTangentMode.Free;
                key.OutTangentMode = AnimationTangentMode.Free;
                return true;
            default:
                return false;
        }
        curve.ApplyTangentModes(idx);
        return true;
    }

    public bool SetSelectedSideTangentMode(bool incoming, AnimationTangentMode mode)
    {
        if (!TryGetSelectedCurve(out AnimationCurve curve, out int idx))
            return false;
        KeyFrame key = curve.Keys[idx];
        if (incoming) key.InTangentMode = mode;
        else key.OutTangentMode = mode;
        curve.ApplyTangentModes(idx);
        return true;
    }

    public bool SetSelectedWeighted(bool incoming, bool on)
    {
        if (!TryGetSelectedCurve(out AnimationCurve curve, out int idx))
            return false;
        KeyFrame key = curve.Keys[idx];
        AnimationWeightedMode bit = incoming ? AnimationWeightedMode.In : AnimationWeightedMode.Out;
        if (on) key.WeightedMode |= bit;
        else key.WeightedMode &= ~bit;
        return true;
    }

    public bool SetSelectedTangent(bool incoming, float tangent, float? weight)
    {
        if (!TryGetSelectedCurve(out AnimationCurve curve, out int idx))
            return false;
        KeyFrame key = curve.Keys[idx];
        if (incoming)
        {
            key.InTangentMode = AnimationTangentMode.Free;
            key.TangentIn = tangent;
            if (weight.HasValue) key.InWeight = ClampWeight(weight.Value);
        }
        else
        {
            key.OutTangentMode = AnimationTangentMode.Free;
            key.TangentOut = tangent;
            if (weight.HasValue) key.OutWeight = ClampWeight(weight.Value);
        }
        BumpContent();
        return true;
    }

    static float ClampWeight(float w)
    {
        if (w < 0.01f) return 0.01f;
        if (w > 1f) return 1f;
        return w;
    }
}
