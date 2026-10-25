// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Theming;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Vector;

namespace XEngine.Editor.GUI.Panels;

public partial class AnimatorWindow
{
    void DrawBlendTree2DPlot(Paper paper, BlendTree tree, List<BlendTree.Child> children, int n, float w)
    {
        float plotW = Math.Max(40f, w - 16f);
        float plotH = 160f;
        using (paper.Box("anim_bt_plot").Width(plotW).Height(plotH)
            .BackgroundColor(AnimationEditorChrome.Raised)
            .OnClick(e =>
            {
                _blendPreviewParam = e.NormalizedPosition.X * 2f - 1f;
                _blendPreviewParamY = 1f - e.NormalizedPosition.Y * 2f;
            })
            .Enter())
        {
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                Float2 p = children[idx].Position;
                float nx = (p.X + 1f) * 0.5f;
                float ny = (1f - p.Y) * 0.5f;
                if (nx < 0f) nx = 0f; else if (nx > 1f) nx = 1f;
                if (ny < 0f) ny = 0f; else if (ny > 1f) ny = 1f;
                float wt = idx < _blendWeightScratch.Length ? _blendWeightScratch[idx] : 0f;
                paper.Box($"anim_bt_dot{idx}")
                    .PositionType(PositionType.SelfDirected)
                    .Position(nx * plotW - 7f, ny * plotH - 7f)
                    .Width(14).Height(14)
                    .BackgroundColor(wt > 0.05f ? AnimationEditorChrome.Keyframe32 : AnimationEditorChrome.TextMuted)
                    .OnDragging(idx, (ci, e) =>
                    {
                        Float2 cur = children[ci].Position;
                        children[ci].Position = new Float2(
                            cur.X + e.Delta.X / plotW * 2f,
                            cur.Y - e.Delta.Y / plotH * 2f);
                    });
            }

            float rx = (_blendPreviewParam + 1f) * 0.5f;
            float ry = (1f - _blendPreviewParamY) * 0.5f;
            if (rx < 0f) rx = 0f; else if (rx > 1f) rx = 1f;
            if (ry < 0f) ry = 0f; else if (ry > 1f) ry = 1f;
            paper.Box("anim_bt_red")
                .PositionType(PositionType.SelfDirected)
                .Position(rx * plotW - 5f, ry * plotH - 5f)
                .Width(10).Height(10)
                .BackgroundColor(EditorTheme.Red500);
        }
    }
}
