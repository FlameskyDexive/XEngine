// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.AnimationEditor;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.Editor.GUI.Panels;

public partial class AnimationClipWindow
{
    private int _draggingTangent; // 0 none, -1 in, +1 out
    private const float TangentHandleLen = 40f;
    private const float TangentHandleHit = 8f;

    public bool ShowCurves
    {
        get => _showCurves;
        set => _showCurves = value;
    }

    public bool ApplyTangentPreset(string preset)
    {
        if (_model.ClipIsReadOnly) return false;
        SnapshotClip();
        return _model.ApplyTangentPreset(preset);
    }

    public bool SetSelectedSideTangentMode(bool incoming, AnimationTangentMode mode)
    {
        if (_model.ClipIsReadOnly) return false;
        SnapshotClip();
        return _model.SetSelectedSideTangentMode(incoming, mode);
    }

    public bool SetSelectedWeighted(bool incoming, bool on)
    {
        if (_model.ClipIsReadOnly) return false;
        SnapshotClip();
        return _model.SetSelectedWeighted(incoming, on);
    }

    private void OpenTangentContextMenu()
    {
        if (!_model.HasKeyframeSelection || _model.ClipIsReadOnly) return;
        Origami.ContextMenuAtPointer(b =>
        {
            b.Item("Auto", () => ApplyTangentPreset("Auto"));
            b.Item("Clamped Auto", () => ApplyTangentPreset("ClampedAuto"));
            b.Item("Free Smooth", () => ApplyTangentPreset("FreeSmooth"));
            b.Item("Flat", () => ApplyTangentPreset("Flat"));
            b.Item("Broken", () => ApplyTangentPreset("Broken"));
            b.Separator();
            b.Item("Left Free", () => SetSelectedSideTangentMode(true, AnimationTangentMode.Free));
            b.Item("Left Linear", () => SetSelectedSideTangentMode(true, AnimationTangentMode.Linear));
            b.Item("Left Constant", () => SetSelectedSideTangentMode(true, AnimationTangentMode.Constant));
            b.Item("Left Weighted", () => ToggleWeighted(true));
            b.Separator();
            b.Item("Right Free", () => SetSelectedSideTangentMode(false, AnimationTangentMode.Free));
            b.Item("Right Linear", () => SetSelectedSideTangentMode(false, AnimationTangentMode.Linear));
            b.Item("Right Constant", () => SetSelectedSideTangentMode(false, AnimationTangentMode.Constant));
            b.Item("Right Weighted", () => ToggleWeighted(false));
        });
    }

    private void ToggleWeighted(bool incoming)
    {
        if (!_model.TryGetSelectedCurve(out var curve, out int idx)) return;
        KeyFrame key = curve.Keys[idx];
        bool on = incoming
            ? (key.WeightedMode & AnimationWeightedMode.In) == 0
            : (key.WeightedMode & AnimationWeightedMode.Out) == 0;
        SetSelectedWeighted(incoming, on);
    }

    private void DrawSelectedTangentHandles(Canvas canvas, Float2 origin, float width,
        float curveAreaY, float curveAreaH, float minVal, float range)
    {
        if (!_model.TryGetSelectedCurve(out AnimationCurve curve, out int idx))
            return;
        KeyFrame key = curve.Keys[idx];
        float kx = origin.X + _model.TimeToScreenX(key.Position, width);
        float ky = curveAreaY + curveAreaH - (key.Value - minVal) / range * curveAreaH;
        float tToX = width / Math.Max(1e-4f, _model.ViewEndTime - _model.ViewStartTime);
        float vToY = curveAreaH / range;

        canvas.SetStrokeColor(AnimationEditorChrome.Playhead32);
        canvas.SetStrokeWidth(1.25f);
        if (idx > 0)
        {
            float w = (key.WeightedMode & AnimationWeightedMode.In) != 0 ? key.InWeight : KeyFrame.DefaultWeight;
            var (hx, hy) = TangentHandlePos(kx, ky, key.TangentIn, -1f, tToX, vToY, w);
            canvas.MoveTo(kx, ky);
            canvas.LineTo(hx, hy);
            canvas.Stroke();
            canvas.CircleFilled(hx, hy, 3.5f, AnimationEditorChrome.Playhead32);
        }
        if (idx < curve.Keys.Count - 1)
        {
            float w = (key.WeightedMode & AnimationWeightedMode.Out) != 0 ? key.OutWeight : KeyFrame.DefaultWeight;
            var (hx, hy) = TangentHandlePos(kx, ky, key.TangentOut, 1f, tToX, vToY, w);
            canvas.MoveTo(kx, ky);
            canvas.LineTo(hx, hy);
            canvas.Stroke();
            canvas.CircleFilled(hx, hy, 3.5f, AnimationEditorChrome.Playhead32);
        }
    }

    private static (float x, float y) TangentHandlePos(float kx, float ky, float tangent, float dir,
        float tToX, float vToY, float weight)
    {
        float len = TangentHandleLen * (weight / KeyFrame.DefaultWeight);
        if (len < 12f) len = 12f;
        float dx = dir * len;
        float dy = -tangent * dir * (len / Math.Max(1e-4f, tToX)) * vToY;
        float mag = MathF.Sqrt(dx * dx + dy * dy);
        if (mag > 0f) { dx = dx / mag * len; dy = dy / mag * len; }
        return (kx + dx, ky + dy);
    }

    private bool TryBeginTangentDrag(float relX, float relY, float width, float height)
    {
        _draggingTangent = 0;
        if (!_model.TryGetSelectedCurve(out AnimationCurve curve, out int idx))
            return false;
        if (!TryCurveContext(out _, out var bone, out var channel)) return false;
        CurveValueRange(bone, channel, out float minVal, out float maxVal);
        float range = Math.Max(1e-4f, maxVal - minVal);
        float areaH = height - CurveHeaderH - 8f;
        KeyFrame key = curve.Keys[idx];
        float kx = _model.TimeToScreenX(key.Position, width);
        float ky = CurveHeaderH + areaH - (key.Value - minVal) / range * areaH;
        float tToX = width / Math.Max(1e-4f, _model.ViewEndTime - _model.ViewStartTime);
        float vToY = areaH / range;
        float hit2 = TangentHandleHit * TangentHandleHit;
        if (idx > 0)
        {
            float w = (key.WeightedMode & AnimationWeightedMode.In) != 0 ? key.InWeight : KeyFrame.DefaultWeight;
            var (hx, hy) = TangentHandlePos(kx, ky, key.TangentIn, -1f, tToX, vToY, w);
            float dx = hx - relX, dy = hy - relY;
            if (dx * dx + dy * dy <= hit2) { _draggingTangent = -1; return true; }
        }
        if (idx < curve.Keys.Count - 1)
        {
            float w = (key.WeightedMode & AnimationWeightedMode.Out) != 0 ? key.OutWeight : KeyFrame.DefaultWeight;
            var (hx, hy) = TangentHandlePos(kx, ky, key.TangentOut, 1f, tToX, vToY, w);
            float dx = hx - relX, dy = hy - relY;
            if (dx * dx + dy * dy <= hit2) { _draggingTangent = 1; return true; }
        }
        return false;
    }

    private void HandleTangentDragging(float relX, float relY, float width, float height)
    {
        if (_draggingTangent == 0 || _model.ClipIsReadOnly) return;
        if (!_model.TryGetSelectedCurve(out AnimationCurve curve, out int idx)) return;
        if (!TryCurveContext(out _, out var bone, out var channel)) return;
        if (!_dragUndoArmed)
        {
            SnapshotClip();
            _dragUndoArmed = true;
        }
        CurveValueRange(bone, channel, out float minVal, out float maxVal);
        float range = Math.Max(1e-4f, maxVal - minVal);
        float areaH = height - CurveHeaderH - 8f;
        KeyFrame key = curve.Keys[idx];
        float kx = _model.TimeToScreenX(key.Position, width);
        float ky = CurveHeaderH + areaH - (key.Value - minVal) / range * areaH;
        float tToX = width / Math.Max(1e-4f, _model.ViewEndTime - _model.ViewStartTime);
        float vToY = areaH / range;
        float dir = _draggingTangent;
        float dx = relX - kx;
        float dy = relY - ky;
        float handleDt = dir * dx / Math.Max(1e-4f, tToX);
        float handleDv = -dy / Math.Max(1e-4f, vToY);
        if (MathF.Abs(handleDt) < 1e-4f) return;
        float tangent = handleDv / handleDt;
        float pixLen = MathF.Sqrt(dx * dx + dy * dy);
        float weight = (pixLen / TangentHandleLen) * KeyFrame.DefaultWeight;
        bool incoming = _draggingTangent < 0;
        bool weighted = incoming
            ? (key.WeightedMode & AnimationWeightedMode.In) != 0
            : (key.WeightedMode & AnimationWeightedMode.Out) != 0;
        _model.SetSelectedTangent(incoming, tangent, weighted ? weight : null);
    }

    private void SampleCurve(Canvas canvas, AnimationCurve curve, Float2 origin, float width,
        float curveAreaY, float curveAreaH, float minVal, float range)
    {
        int steps = Math.Max(8, (int)width);
        bool first = true;
        float t0 = _model.ViewStartTime;
        float t1 = _model.ViewEndTime;
        if (curve.Keys.Count > 0)
        {
            float k0 = curve.Keys[0].Position;
            float k1 = curve.Keys[curve.Keys.Count - 1].Position;
            if (t0 < k0) t0 = k0;
            if (t1 > k1) t1 = k1;
        }
        float span = t1 - t0;
        if (span <= 1e-6f) return;
        for (int i = 0; i <= steps; i++)
        {
            float t = t0 + span * i / steps;
            float v = curve.Evaluate(t);
            float x = origin.X + _model.TimeToScreenX(t, width);
            float y = curveAreaY + curveAreaH - (v - minVal) / range * curveAreaH;
            if (first) { canvas.MoveTo(x, y); first = false; }
            else canvas.LineTo(x, y);
        }
        canvas.Stroke();
    }
}
