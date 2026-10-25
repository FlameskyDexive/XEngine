// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.PostProcessing.Effects;

public enum ColorGradingTonemapper
{
    None,
    AgX,
    ACES,
    Neutral,
}

[PostProcess(typeof(ColorGradingRenderer), "XEngine/Color Grading")]
public sealed class ColorGrading : PostProcessEffectSettings
{
    public FloatParameter postExposure = new();
    public FloatParameter contrast = new();
    public FloatParameter saturation = new();
    public FloatParameter temperature = new();
    public ColorParameter lift = new(new Color(0, 0, 0, 0));
    public ColorParameter gamma = new(new Color(0, 0, 0, 0));
    public ColorParameter gain = new(new Color(0, 0, 0, 0));
    public ParameterOverride<ColorGradingTonemapper> tonemapper = new(ColorGradingTonemapper.AgX);
    public FloatParameter tonemapContrast = new(1.1f);
    public FloatParameter tonemapSaturation = new(1.1f);
}

public sealed class ColorGradingRenderer : PostProcessEffectRenderer<ColorGrading> { }
