// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing.Effects;

[PostProcess(typeof(GrainRenderer), "XEngine/Grain")]
public sealed class Grain : PostProcessEffectSettings
{
    public FloatParameter intensity = new(0.15f);
    public FloatParameter response = new(0.5f);

    public override bool IsEnabledAndSupported() => base.IsEnabledAndSupported() && intensity.value > 0f;
}

public sealed class GrainRenderer : PostProcessEffectRenderer<Grain> { }
