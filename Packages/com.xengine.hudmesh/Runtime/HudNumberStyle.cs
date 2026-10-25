using XEngine.Runtime;
using RuntimeAnimationCurve = XEngine.Runtime.AnimationCurve;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>How a floating number renders its glyphs.</summary>
public enum HudNumberMode
{
    /// <summary>SDF text via the shared font atlas — any text, gradients, outline.</summary>
    SdfText = 0,
    /// <summary>Digit art sprites named <c>&lt;prefix&gt;&lt;char&gt;</c> (e.g. "b5", "b+").</summary>
    DigitAtlas = 1,
}

/// <summary>
/// Style of a floating damage number (UnityHUD HUDNumberConfig port): glyph mode, digit-set
/// prefix / font options, and the three animation curves (world-space rise, alpha fade, scale)
/// whose last key positions define the number's lifetime. Uses the engine's Echo-serializable
/// <see cref="RuntimeAnimationCurve"/> so configs persist directly.
/// </summary>
public sealed class HudNumberStyle
{
    public HudNumberMode Mode = HudNumberMode.DigitAtlas;

    // ----- DigitAtlas mode -----
    public char Prefix = 'b';
    public int Gap = 4;
    public bool Sign = true;
    public HudAlignment Align = HudAlignment.Middle;

    // ----- SdfText mode -----
    public int FontSize = 32;
    public Color Color = Color.White;
    public float OutlineWidth = 2f;
    public Color OutlineColor = new(0f, 0f, 0f, 255f);

    // ----- Animation (world-space units for move; 0..1 alpha; scale multiplier) -----
    public RuntimeAnimationCurve MoveCurve = CreateDefaultMoveCurve();
    public RuntimeAnimationCurve AlphaCurve = CreateDefaultAlphaCurve();
    public RuntimeAnimationCurve ScaleCurve = CreateDefaultScaleCurve();

    /// <summary>Lifetime in seconds: the largest last-key position across the three curves.</summary>
    public float Lifetime
    {
        get
        {
            float t = 0f;
            if (MoveCurve.Keys.Count > 0) t = float.Max(t, MoveCurve.Keys[MoveCurve.Keys.Count - 1].Position);
            if (AlphaCurve.Keys.Count > 0) t = float.Max(t, AlphaCurve.Keys[AlphaCurve.Keys.Count - 1].Position);
            if (ScaleCurve.Keys.Count > 0) t = float.Max(t, ScaleCurve.Keys[ScaleCurve.Keys.Count - 1].Position);
            return t;
        }
    }

    /// <summary>UnityHUD's default move curve: rise 1.5 → 2 world units, hold, then pop to 2.5 at 1.2 s.</summary>
    public static RuntimeAnimationCurve CreateDefaultMoveCurve() => new(
    [
        new KeyFrame(0f, 1.5f),
        new KeyFrame(0.4f, 2f),
        new KeyFrame(0.9f, 2f),
        new KeyFrame(1.2f, 2.5f),
    ]);

    /// <summary>Hold fully visible for most of the life, then fade out.</summary>
    public static RuntimeAnimationCurve CreateDefaultAlphaCurve() => new(
    [
        new KeyFrame(0f, 1f),
        new KeyFrame(0.7f, 1f),
        new KeyFrame(1.2f, 0f),
    ]);

    /// <summary>Slight pop-in at spawn, settling to 1.</summary>
    public static RuntimeAnimationCurve CreateDefaultScaleCurve() => new(
    [
        new KeyFrame(0f, 1.3f),
        new KeyFrame(0.15f, 1f),
        new KeyFrame(1.2f, 1f),
    ]);
}
