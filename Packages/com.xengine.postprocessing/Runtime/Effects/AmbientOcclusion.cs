// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing.Effects;

[PostProcess(typeof(AmbientOcclusionRenderer), PostProcessEvent.BeforeTransparent, "XEngine/Ambient Occlusion")]
public sealed class AmbientOcclusion : PostProcessEffectSettings
{
    public FloatParameter intensity = new(1f);
    public FloatParameter radius = new(1f);
    public IntParameter slices = new(6);
    public IntParameter samples = new(32);
    public BoolParameter useTemporal = new(true);

    public override bool IsEnabledAndSupported() => base.IsEnabledAndSupported() && intensity.value > 0f;
}

public sealed class AmbientOcclusionRenderer : PostProcessEffectRenderer<AmbientOcclusion> { }
