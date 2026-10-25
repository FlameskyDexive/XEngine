namespace XEngine.OrigamiUI;

/// <summary>Optional density for form fields, in logical pixels. A value type so scoped
/// themes and widget construction do not allocate a separate profile object.</summary>
public readonly record struct OrigamiFieldMetrics(
    float MinimumHeight,
    float FontSize,
    float PaddingLeft,
    float PaddingRight,
    float RowSpacing,
    float FocusGlowRadius)
{
    internal static OrigamiFieldMetrics? Lerp(OrigamiFieldMetrics? a, OrigamiFieldMetrics? b, float t)
    {
        // An unspecified profile preserves the host's original widget defaults.
        if (a is not { } x || b is not { } y) return t < .5f ? a : b;
        return new(
            Mix(x.MinimumHeight, y.MinimumHeight, t), Mix(x.FontSize, y.FontSize, t),
            Mix(x.PaddingLeft, y.PaddingLeft, t), Mix(x.PaddingRight, y.PaddingRight, t),
            Mix(x.RowSpacing, y.RowSpacing, t), Mix(x.FocusGlowRadius, y.FocusGlowRadius, t));
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;
}
