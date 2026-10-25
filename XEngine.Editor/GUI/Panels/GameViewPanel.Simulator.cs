using XEngine.Editor.Theming;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;

using Color = System.Drawing.Color;
using Color32 = XEngine.Vector.Color32;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// Device simulator chrome for the Game view: the phone body behind the game image, its notch, and the
/// safe-area guides. Purely visual — the game still renders at the device's aspect and resolution, which
/// is what layout actually reacts to.
/// </summary>
public partial class GameViewPanel
{
    private static readonly Color32 BodyFill = Color32.FromArgb(255, 24, 24, 27);
    private static readonly Color32 BodyEdge = Color32.FromArgb(255, 63, 63, 70);
    private static readonly Color32 SafeAreaLine = Color32.FromArgb(150, 96, 165, 250);

    /// <summary>The phone body: a rounded slab slightly larger than the screen, plus the top cutout.</summary>
    private static void DrawDeviceBody(Paper paper, GameViewDevice device,
        float x, float y, float w, float h, float scale)
    {
        float bezel = device.Bezel * scale;
        float bodyRadius = device.CornerRadius * scale + bezel;
        float notchW = device.NotchWidth * scale;
        float notchH = device.NotchHeight * scale;
        bool landscape = w > h;

        paper.Box("gv_sim_body")
            .PositionType(PositionType.SelfDirected).Position(x, y).Size(w, h)
            .IsNotInteractable()
            .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
            {
                float bx = (float)r.Min.X;
                float by = (float)r.Min.Y;
                float bw = (float)r.Size.X;
                float bh = (float)r.Size.Y;

                canvas.RoundedRectFilled(bx, by, bw, bh, bodyRadius, bodyRadius, bodyRadius, bodyRadius,
                    BodyFill);

                canvas.RoundedRect(bx + 0.5f, by + 0.5f, bw - 1f, bh - 1f,
                    bodyRadius, bodyRadius, bodyRadius, bodyRadius);
                canvas.SetStrokeColor(BodyEdge);
                canvas.SetStrokeWidth(1f);
                canvas.Stroke();

                // Dynamic-island style cutout, drawn on top of the screen's leading edge. In landscape
                // it moves to the left edge, which is where the camera housing ends up.
                if (notchW <= 0f || notchH <= 0f) return;

                float islandH = notchH * 0.62f;
                float round = islandH * 0.5f;
                if (landscape)
                {
                    float cy = by + bh * 0.5f - notchW * 0.5f;
                    canvas.RoundedRectFilled(bx + bezel, cy, islandH, notchW, round, round, round, round,
                        BodyFill);
                }
                else
                {
                    float cx = bx + bw * 0.5f - notchW * 0.5f;
                    canvas.RoundedRectFilled(cx, by + bezel, notchW, islandH, round, round, round, round,
                        BodyFill);
                }
            }));
    }

    /// <summary>
    /// Outlines the area a mobile layout should keep its content inside: below the status bar / notch
    /// and above the home indicator.
    /// </summary>
    private static void DrawDeviceSafeArea(Paper paper, GameViewDevice device,
        float x, float y, float w, float h, float scale)
    {
        bool landscape = w > h;
        float top = device.NotchHeight * scale;
        float bottom = device.HomeIndicator * scale;

        // Rotating the device moves the cutout to a side edge; the gesture bar stays on the "bottom"
        // relative to the user, which is the same screen edge in both orientations.
        float insetLeft = landscape ? top : 0f;
        float insetTop = landscape ? 0f : top;

        paper.Box("gv_sim_safe")
            .PositionType(PositionType.SelfDirected).Position(x, y).Size(w, h)
            .IsNotInteractable()
            .OnPostLayout((handle, rect) => paper.Draw(ref handle, (canvas, r) =>
            {
                float sx = (float)r.Min.X + insetLeft;
                float sy = (float)r.Min.Y + insetTop;
                float sw = (float)r.Size.X - insetLeft;
                float sh = (float)r.Size.Y - insetTop - bottom;
                if (sw <= 2f || sh <= 2f) return;

                canvas.Rect(sx, sy, sw, sh);
                canvas.SetStrokeColor(SafeAreaLine);
                canvas.SetStrokeWidth(1f);
                canvas.Stroke();
            }));
    }
}
