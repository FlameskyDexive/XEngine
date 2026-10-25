// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Editor.Core;
using XEngine.PaperUI;
using XEngine.Runtime;

using Color = System.Drawing.Color;

namespace XEngine.Editor.GUI;

/// <summary>
/// GameObject icon rendering + selection (Unity's "Assign icons"): a preset label color capsule
/// or a custom texture thumbnail from a project asset, shown in the hierarchy and the
/// GameObject header instead of the component-derived icon.
/// </summary>
public static class GameObjectIcons
{
    /// <summary>Unity-style label icon colors: gray, blue, cyan, green, yellow, orange, red, purple.</summary>
    public static readonly Color[] LabelColors =
    {
        Color.FromArgb(0x8E, 0x8E, 0x8E),
        Color.FromArgb(0x3D, 0x76, 0xFE),
        Color.FromArgb(0x00, 0xA2, 0xFF),
        Color.FromArgb(0x35, 0xCE, 0x5D),
        Color.FromArgb(0xFC, 0xE3, 0x54),
        Color.FromArgb(0xFC, 0xA1, 0x45),
        Color.FromArgb(0xE0, 0x52, 0x52),
        Color.FromArgb(0xA1, 0x55, 0xB3),
    };

    private const int LabelColorCount = 8;

    public static bool HasIcon(GameObject go)
        => (go.LabelIcon >= 1 && go.LabelIcon <= LabelColorCount) || TryParseIconGuid(go, out _);

    private static bool TryParseIconGuid(GameObject go, out Guid guid)
        => Guid.TryParse(go.IconAssetGuid, out guid) && guid != Guid.Empty;

    /// <summary>
    /// Draw the GameObject's icon (custom texture thumbnail or label color capsule) inside a
    /// <paramref name="slot"/>-wide box; call instead of the default hierarchy icon.
    /// </summary>
    public static void Draw(Paper paper, string id, GameObject go, float size, float slot = 18f, float? height = null)
    {
        float rowHeight = height ?? slot;
        float padY = (rowHeight - size) * 0.5f;
        if (TryParseIconGuid(go, out var guid))
        {
            var tex = EditorAssetBackend.Instance?.GetThumbnailTexture(guid);
            if (tex != null)
            {
                var pad = (slot - size) * 0.5f;
                paper.Box(UiId.Underscore(id, "img")).Width(slot).Height(rowHeight).IsNotInteractable().Rounded(3)
                    .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
                        canvas.DrawImage(tex,
                            (float)r.Min.X + pad, (float)r.Min.Y + padY,
                            size, size)));
                return;
            }
            // Broken custom icon reference falls through to the label color (if any).
        }

        int idx = go.LabelIcon - 1;
        if (idx >= 0 && idx < LabelColors.Length)
        {
            float pad = (slot - size) * 0.5f;
            paper.Box(UiId.Underscore(id, "dot")).Width(slot).Height(rowHeight).IsNotInteractable()
                .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
                {
                    float radius = size * 0.5f;
                    canvas.SetFillColor(LabelColors[idx]);
                    canvas.BeginPath();
                    canvas.RoundedRect((float)r.Min.X + pad, (float)r.Min.Y + padY, size, size,
                        radius, radius, radius, radius);
                    canvas.Fill();
                }));
        }
    }
}
