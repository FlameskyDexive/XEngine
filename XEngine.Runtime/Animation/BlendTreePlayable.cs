// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Playables;
using XEngine.Runtime;
using XEngine.Vector;
using XEngine;

namespace XEngine.Animation;

/// <summary>
/// Blends N child <see cref="AnimationPlayable"/>s (clips or nested trees) by parameter into one
/// output <see cref="Pose"/>. 1D brackets two thresholds; 2D Freeform Cartesian/Directional uses a
/// gradient-band product; Direct uses per-child parameter weights (not renormalized).
/// </summary>
public sealed class BlendTreePlayable : AnimationPlayable
{
    public BlendType Type = BlendType.OneD;

    /// <summary>
    /// The blend input value for this frame (1D, or X for 2D). Set by the FSM runtime from the
    /// controller parameter before the graph evaluates (or directly in code-driven mode).
    /// </summary>
    public float ParameterValue;

    /// <summary>★ M43 — Y blend input for 2D Freeform types.</summary>
    public float ParameterValueY;

    /// <summary>★ M43 — Resolved controller parameter index for <see cref="ParameterValue"/> (-1 if none).</summary>
    public int ParamIndex = -1;

    /// <summary>★ M43 — Resolved controller parameter index for <see cref="ParameterValueY"/> (-1 if none).</summary>
    public int ParamIndexY = -1;

    private readonly List<AnimationPlayable> _children = new();
    private readonly List<float> _thresholds = new();
    private readonly List<Float2> _positions = new();
    private readonly List<float> _weights = new();
    private readonly List<int> _directParamIndex = new();
    // M60 — per-child clock alignment (parallel to _children; default None keeps legacy behaviour).
    private readonly List<BlendTimeSync> _timeSync = new();
    private readonly List<float> _phases = new();
    private bool _anySync;

    public override int InputCount => _children.Count;
    public override Playable GetInput(int index) => _children[index];

    // The evaluated pose uses these parameter weights, not the leaf's sampling Weight (always 1).
    internal float GetChildWeight(int index) => _weights[index];

    public int DirectParamIndex(int index)
        => (uint)index < (uint)_directParamIndex.Count ? _directParamIndex[index] : -1;

    /// <summary>Allocates the output pose against <paramref name="binding"/>.</summary>
    public void Bind(SkeletonBinding binding)
    {
        Binding = binding;
        AllocateOutput();
    }

    /// <summary>Adds a child clip playable at a 1D threshold. The child's Weight is forced to 1.</summary>
    public void AddChild(AnimationClipPlayable clip, float threshold)
        => AddChild(clip, threshold, Float2.Zero, -1, BlendTimeSync.None, 0f);

    /// <summary>★ M43 — Adds a clip or nested tree child with 1D/2D/Direct metadata.</summary>
    public void AddChild(AnimationPlayable child, float threshold, Float2 position, int directParamIndex)
        => AddChild(child, threshold, position, directParamIndex, BlendTimeSync.None, 0f);

    /// <summary>M60 — Full AddChild with clock-alignment metadata.</summary>
    public void AddChild(
        AnimationPlayable child,
        float threshold,
        Float2 position,
        int directParamIndex,
        BlendTimeSync timeSync,
        float phase)
    {
        child.Weight = 1f; // ensure the child samples each frame
        _children.Add(child);
        _thresholds.Add(threshold);
        _positions.Add(position);
        _weights.Add(0f);
        _directParamIndex.Add(directParamIndex);
        _timeSync.Add(timeSync);
        _phases.Add(phase);
        if (timeSync != BlendTimeSync.None)
            _anySync = true;
    }

    /// <summary>★ M43 — Direct blend: set this child's weight from its parameter (not renormalized).</summary>
    public void SetDirectWeight(int index, float value)
    {
        if ((uint)index >= (uint)_weights.Count) return;
        if (value < 0f) value = 0f;
        _weights[index] = value;
    }

    /// <summary>★ M43 — Build a playable graph from an authored tree (nested children, auto thresholds).</summary>
    public static BlendTreePlayable Build(BlendTree tree, SkeletonBinding binding, AnimatorController? author = null)
    {
        var btp = new BlendTreePlayable { Type = tree.Type };
        btp.Bind(binding);
        if (author != null)
        {
            if (!string.IsNullOrEmpty(tree.ParameterName))
                btp.ParamIndex = author.GetParameterIndex(tree.ParameterName);
            if (!string.IsNullOrEmpty(tree.ParameterNameY))
                btp.ParamIndexY = author.GetParameterIndex(tree.ParameterNameY);
        }
        var children = tree.Children;
        int n = children.Count;
        for (int i = 0; i < n; i++)
        {
            BlendTree.Child c = children[i];
            AnimationPlayable? childPlayable = null;
            BlendTree? nested = c.Tree.Res;
            if (nested != null)
                childPlayable = Build(nested, binding, author);
            else
            {
                AnimationClip? clip = c.Clip.Res;
                if (clip == null) continue;
                var cp = new AnimationClipPlayable(clip);
                cp.Bind(binding);
                cp.Play();
                cp.ResetPlaybackClock();
                childPlayable = cp;
            }

            float threshold = c.Threshold;
            if (tree.UseAutomaticThresholds && tree.Type == BlendType.OneD && n > 1)
                threshold = i / (float)(n - 1);

            int directIdx = -1;
            if (author != null && !string.IsNullOrEmpty(c.DirectParameterName))
                directIdx = author.GetParameterIndex(c.DirectParameterName);

            // M60 — per-child speed multiplier, then Duration sync stretches synced children to the
            // longest synced sibling so all complete a cycle together.
            if (childPlayable is AnimationClipPlayable synced && c.Speed != 1f)
                synced.Speed = c.Speed;

            btp.AddChild(childPlayable, threshold, c.Position, directIdx, c.TimeSync, c.Phase);
        }
        btp.ApplyDurationSync();
        return btp;
    }

    /// <summary>
    /// M60 — Duration sync: speed-scales every Duration-synced child so its cycle time equals the
    /// longest Duration-synced sibling (build-time, no per-frame cost).
    /// </summary>
    private void ApplyDurationSync()
    {
        double longest = 0d;
        for (int i = 0; i < _children.Count; i++)
        {
            if (_timeSync[i] != BlendTimeSync.Duration)
                continue;
            if (_children[i] is AnimationClipPlayable cp && cp.Duration > longest)
                longest = cp.Duration;
        }
        if (longest <= 0d)
            return;
        for (int i = 0; i < _children.Count; i++)
        {
            if (_timeSync[i] != BlendTimeSync.Duration)
                continue;
            if (_children[i] is AnimationClipPlayable cp && cp.Duration > 1e-6d)
                cp.Speed *= (float)(longest / cp.Duration);
        }
    }

    [HotPath]
    public override void ProcessFrame(PlayableContext ctx)
    {
        Pose? pose = Output;
        if (pose == null) return;
        pose.Clear();

        int n = _children.Count;
        if (n == 0) return;

        if (Type == BlendType.Direct)
        {
            // Weights were written by SetDirectWeight this frame; leave them.
        }
        else if (Type == BlendType.TwoDCartesian || Type == BlendType.TwoDDirectional)
            ComputeWeights2D();
        else
            ComputeWeights1D();

        for (int i = 0; i < n; i++)
        {
            float w = _weights[i];
            if (w <= 0f) continue;
            Pose? childPose = _children[i].Output;
            if (childPose == null) continue;
            pose.Accumulate(childPose, w);
        }

        pose.NormalizeRotations();

        // M60 — drift correction keeps StartTime/PhaseFoot-synced children aligned to the blended
        // average phase (applies to next frame's sampling; one frame of lag is imperceptible).
        if (_anySync)
            SyncChildTimes();
    }

    /// <summary>
    /// M60 — StartTime/PhaseFoot drift correction: synced children with weight track the weighted
    /// average of (normalized time − phase) across synced children, each landing on avg + own phase.
    /// </summary>
    private void SyncChildTimes()
    {
        int n = _children.Count;
        double weighted = 0d, weightSum = 0d;
        for (int i = 0; i < n; i++)
        {
            if (_timeSync[i] == BlendTimeSync.None)
                continue;
            if (_children[i] is not AnimationClipPlayable cp || cp.Duration <= 0d)
                continue;
            float w = _weights[i];
            if (w <= 0f)
                continue;
            weighted += WrapDelta(cp.NormalizedTime - _phases[i]) * w;
            weightSum += w;
        }
        if (weightSum <= 1e-6d)
            return;
        double average = weighted / weightSum;

        for (int i = 0; i < n; i++)
        {
            if (_timeSync[i] == BlendTimeSync.None)
                continue;
            if (_children[i] is not AnimationClipPlayable cp || cp.Duration <= 0d)
                continue;
            float w = _weights[i];
            if (w <= 0f)
                continue; // Zero-weight children drift until they blend in again.
            double target = average + _phases[i];
            // Only correct the drift (shortest wrap), scaled by the child's blend weight so a child
            // fading in converges instead of snapping.
            double current = cp.NormalizedTime;
            double delta = WrapDelta(target - current);
            cp.Time = current + delta * w;
        }
    }

    /// <summary>Wraps a normalized-time delta to (−0.5, 0.5].</summary>
    private static double WrapDelta(double x)
    {
        x -= Math.Floor(x);
        return x > 0.5d ? x - 1d : x;
    }

    private void ComputeWeights1D()
    {
        var w = _weights;
        int n = w.Count;
        for (int i = 0; i < n; i++) w[i] = 0f;

        var t = _thresholds;
        float v = ParameterValue;

        if (v <= t[0]) { w[0] = 1f; return; }
        if (v >= t[n - 1]) { w[n - 1] = 1f; return; }

        for (int i = 0; i < n - 1; i++)
        {
            float t0 = t[i], t1 = t[i + 1];
            if (v >= t0 && v <= t1)
            {
                float range = t1 - t0;
                float frac = range > 0f ? (v - t0) / range : 0f;
                w[i] = 1f - frac;
                w[i + 1] = frac;
                return;
            }
        }
    }

    private void ComputeWeights2D()
    {
        var w = _weights;
        int n = w.Count;
        var pos = _positions;
        float sx = ParameterValue;
        float sy = ParameterValueY;
        bool directional = Type == BlendType.TwoDDirectional;

        if (n == 1)
        {
            w[0] = 1f;
            return;
        }

        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            float wi = 1f;
            float pix = pos[i].X;
            float piy = pos[i].Y;
            if (directional)
                DirectionalPair(pix, piy, out pix, out piy);
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                float pjx = pos[j].X;
                float pjy = pos[j].Y;
                if (directional)
                    DirectionalPair(pjx, pjy, out pjx, out pjy);
                float ijx = pjx - pix;
                float ijy = pjy - piy;
                float magSq = ijx * ijx + ijy * ijy;
                if (magSq < 1e-12f) continue;
                float qx = sx;
                float qy = sy;
                if (directional)
                    DirectionalPair(qx, qy, out qx, out qy);
                float hx = qx - pix;
                float hy = qy - piy;
                float h = 1f - (hx * ijx + hy * ijy) / magSq;
                if (h < 0f) h = 0f;
                wi *= h;
            }
            w[i] = wi;
            sum += wi;
        }

        if (sum > 1e-8f)
        {
            float inv = 1f / sum;
            for (int i = 0; i < n; i++) w[i] *= inv;
            return;
        }

        int nearest = 0;
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            float dx = sx - pos[i].X;
            float dy = sy - pos[i].Y;
            float d = dx * dx + dy * dy;
            if (d < best) { best = d; nearest = i; }
            w[i] = 0f;
        }
        w[nearest] = 1f;
    }

    static void DirectionalPair(float x, float y, out float u, out float v)
    {
        u = MathF.Atan2(y, x);
        v = MathF.Sqrt(x * x + y * y);
    }
}
