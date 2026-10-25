// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing.Effects;

[PostProcess(typeof(VignetteRenderer), "XEngine/Vignette")]
public sealed class Vignette : PostProcessEffectSettings
{
    public FloatParameter intensity = new(0.4f);
    public FloatParameter smoothness = new(0.2f);
    public FloatParameter roundness = new(1f);

    public override bool IsEnabledAndSupported() => base.IsEnabledAndSupported() && intensity.value > 0f;
}

public sealed class VignetteRenderer : PostProcessEffectRenderer<Vignette> { }
