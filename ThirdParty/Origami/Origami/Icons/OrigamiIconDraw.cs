// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.PaperUI;
using System;
using XEngine.Quill;
using XEngine.Vector;

using Color = System.Drawing.Color;

namespace XEngine.OrigamiUI;

/// <summary>Helpers for painting an <see cref="IOrigamiIcon"/> into a Paper element.</summary>
public static class OrigamiIconDraw
{
    /// <summary>Paint <paramref name="icon"/> centered in this box after layout (no-op if null).
    /// When <paramref name="size"/> is positive the glyph is drawn at that pixel size, centered; otherwise it fills the box.</summary>
    public static ElementBuilder Icon(this ElementBuilder box, Paper paper, IOrigamiIcon? icon, Color color, float stroke = 1.5f, float size = 0f)
    {
        if (icon == null)
            return box;

        // Register the custom draw directly instead of the old OnPostLayout((h,r) => paper.Draw(...))
        // double closure: commands all execute during the render pass regardless of when they were
        // attached, and one capture instead of two halves the per-icon-per-frame garbage.
        var handle = box._handle;
        Action<Canvas, Rect> draw = (canvas, rr) =>
        {
            if (size > 0f)
            {
                float cx = (float)(rr.Min.X + rr.Size.X / 2), cy = (float)(rr.Min.Y + rr.Size.Y / 2);
                rr = new Rect(new Float2(cx - size / 2, cy - size / 2), new Float2(cx + size / 2, cy + size / 2));
            }
            icon.Draw(canvas, rr, color, stroke);
        };
        if (icon is IStableOrigamiIcon stable)
            paper.DrawVersioned(ref handle, "origami.icon", unchecked((uint)color.ToArgb()),
                ((ulong)unchecked((uint)BitConverter.SingleToInt32Bits(size)) << 32)
                    | unchecked((uint)BitConverter.SingleToInt32Bits(stroke)), icon, stable.PaintDependency, draw);
        else paper.Draw(ref handle, draw);
        return box;
    }
}
