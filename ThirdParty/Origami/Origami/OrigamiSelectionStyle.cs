using System.Drawing;

namespace XEngine.OrigamiUI;

/// <summary>Optional collection selection colors. Null on the theme preserves standalone behavior.</summary>
public readonly record struct OrigamiSelectionStyle(Color InactiveBackground, Color ActiveText, Color InactiveText)
{
    internal static OrigamiSelectionStyle? Lerp(OrigamiSelectionStyle? a, OrigamiSelectionStyle? b, float t)
    {
        if (a is not { } from || b is not { } to) return b;
        return new(OrigamiRamp.LerpColor(from.InactiveBackground, to.InactiveBackground, t),
            OrigamiRamp.LerpColor(from.ActiveText, to.ActiveText, t),
            OrigamiRamp.LerpColor(from.InactiveText, to.InactiveText, t));
    }
}
