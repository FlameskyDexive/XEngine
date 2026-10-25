// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing.Effects;

[PostProcess(typeof(BloomRenderer), "XEngine/Bloom")]
public sealed class Bloom : PostProcessEffectSettings
{
    public FloatParameter intensity = new(0f);
    public FloatParameter threshold = new(0.6f);
    public FloatParameter diffusion = new(4f);

    public override bool IsEnabledAndSupported() => base.IsEnabledAndSupported() && intensity.value > 0f;
}

public sealed class BloomRenderer : PostProcessEffectRenderer<Bloom> { }
