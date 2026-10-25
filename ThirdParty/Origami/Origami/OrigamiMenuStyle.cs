using System.Drawing;

namespace XEngine.OrigamiUI;

/// <summary>Optional desktop menu presentation. Hosts opt in without changing
/// standalone Origami menus or allocating a separate profile per widget.</summary>
public readonly record struct OrigamiMenuStyle(
    float RowHeight, float FontSize, float MinimumWidth, float Rounding,
    Color Background, Color Border, Color Text, Color DisabledText,
    Color HoverBackground, Color HoverText)
{
    internal static OrigamiMenuStyle? Lerp(OrigamiMenuStyle? a, OrigamiMenuStyle? b, float t)
    {
        if (a is not { } x || b is not { } y) return t < .5f ? a : b;
        return new(Mix(x.RowHeight, y.RowHeight, t), Mix(x.FontSize, y.FontSize, t),
            Mix(x.MinimumWidth, y.MinimumWidth, t), Mix(x.Rounding, y.Rounding, t),
            OrigamiRamp.LerpColor(x.Background, y.Background, t),
            OrigamiRamp.LerpColor(x.Border, y.Border, t),
            OrigamiRamp.LerpColor(x.Text, y.Text, t),
            OrigamiRamp.LerpColor(x.DisabledText, y.DisabledText, t),
            OrigamiRamp.LerpColor(x.HoverBackground, y.HoverBackground, t),
            OrigamiRamp.LerpColor(x.HoverText, y.HoverText, t));
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;
}
