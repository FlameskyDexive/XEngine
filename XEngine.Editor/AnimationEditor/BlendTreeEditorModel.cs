// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Animation;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Editor-side 1D BlendTree helpers: inline create, threshold sort, and weight preview matching
/// <see cref="BlendTreePlayable"/> (not used on the runtime hot path).
/// </summary>
internal static class BlendTreeEditorModel
{
    public static BlendTree CreateInline(string name, string parameterName)
    {
        return new BlendTree
        {
            Name = name,
            Type = BlendType.OneD,
            ParameterName = parameterName ?? string.Empty,
        };
    }

    public static void AttachInline(AnimatorController.State state, BlendTree tree)
        => state.BlendTree = new AssetRef<BlendTree>(tree);

    public static BlendTree? GetTree(AnimatorController.State state)
        => state.BlendTree?.Res;

    public static int ChildCount(AnimatorController.State state)
        => GetTree(state)?.Children.Count ?? 0;

    public static bool HasTree(AnimatorController.State state)
        => GetTree(state) != null;

    public static void SortChildrenByThreshold(BlendTree tree)
    {
        var children = tree.Children;
        int n = children.Count;
        for (int i = 1; i < n; i++)
        {
            BlendTree.Child key = children[i];
            int j = i - 1;
            while (j >= 0 && children[j].Threshold > key.Threshold)
            {
                children[j + 1] = children[j];
                j--;
            }
            children[j + 1] = key;
        }
    }

    /// <summary>
    /// Writes 1D blend weights into <paramref name="weights"/> (length must be &gt;= child count).
    /// Children are assumed sorted by threshold.
    /// </summary>
    public static void ComputeWeights1D(BlendTree tree, float parameter, float[] weights)
    {
        var children = tree.Children;
        int n = children.Count;
        int m = weights.Length;
        int limit = n < m ? n : m;
        for (int i = 0; i < limit; i++)
            weights[i] = 0f;
        if (n == 0 || m == 0)
            return;
        if (n == 1)
        {
            weights[0] = 1f;
            return;
        }

        float v = parameter;
        if (v <= children[0].Threshold)
        {
            weights[0] = 1f;
            return;
        }
        if (v >= children[n - 1].Threshold)
        {
            weights[n - 1] = 1f;
            return;
        }

        for (int i = 0; i < n - 1; i++)
        {
            float t0 = children[i].Threshold;
            float t1 = children[i + 1].Threshold;
            if (v >= t0 && v <= t1)
            {
                float range = t1 - t0;
                float frac = range > 0f ? (v - t0) / range : 0f;
                weights[i] = 1f - frac;
                weights[i + 1] = frac;
                return;
            }
        }
    }

    /// <summary>★ M43 — Evenly space 1D thresholds in [0, 1]. No-op when child count is 0.</summary>
    public static void ApplyAutomaticThresholds(BlendTree tree)
    {
        var children = tree.Children;
        int n = children.Count;
        if (n == 0) return;
        if (n == 1)
        {
            children[0].Threshold = 0f;
            return;
        }
        for (int i = 0; i < n; i++)
            children[i].Threshold = i / (float)(n - 1);
    }

    /// <summary>★ M43 — 1D / 2D / Direct preview weights matching <see cref="BlendTreePlayable"/>.</summary>
    public static void ComputeWeights(BlendTree tree, float parameterX, float parameterY, float[] weights)
    {
        if (tree.Type == BlendType.OneD)
        {
            ComputeWeights1D(tree, parameterX, weights);
            return;
        }

        var children = tree.Children;
        int n = children.Count;
        int m = weights.Length;
        int limit = n < m ? n : m;
        for (int i = 0; i < limit; i++)
            weights[i] = 0f;
        if (n == 0 || m == 0) return;

        if (tree.Type == BlendType.Direct)
        {
            for (int i = 0; i < limit; i++)
            {
                float v = children[i].Threshold;
                weights[i] = v < 0f ? 0f : v;
            }
            return;
        }

        bool directional = tree.Type == BlendType.TwoDDirectional;
        float sx = parameterX, sy = parameterY;
        float sum = 0f;
        for (int i = 0; i < limit; i++)
        {
            float wi = 1f;
            Float2 pi = children[i].Position;
            float pix = pi.X, piy = pi.Y;
            if (directional)
                Polar(pix, piy, out pix, out piy);
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                Float2 pj = children[j].Position;
                float pjx = pj.X, pjy = pj.Y;
                if (directional)
                    Polar(pjx, pjy, out pjx, out pjy);
                float ijx = pjx - pix, ijy = pjy - piy;
                float magSq = ijx * ijx + ijy * ijy;
                if (magSq < 1e-12f) continue;
                float qx = sx, qy = sy;
                if (directional)
                    Polar(qx, qy, out qx, out qy);
                float h = 1f - ((qx - pix) * ijx + (qy - piy) * ijy) / magSq;
                if (h < 0f) h = 0f;
                wi *= h;
            }
            weights[i] = wi;
            sum += wi;
        }
        if (sum > 1e-8f)
        {
            float inv = 1f / sum;
            for (int i = 0; i < limit; i++)
                weights[i] *= inv;
        }
    }

    static void Polar(float x, float y, out float u, out float v)
    {
        u = MathF.Atan2(y, x);
        v = MathF.Sqrt(x * x + y * y);
    }
}
