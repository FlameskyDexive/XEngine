// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing.Effects;

[PostProcess(typeof(ScreenSpaceReflectionsRenderer), PostProcessEvent.BeforeTransparent, "XEngine/Screen-space Reflections")]
public sealed class ScreenSpaceReflections : PostProcessEffectSettings
{
    public FloatParameter maximumIterationCount = new(70f);
    public BoolParameter useTemporal = new(true);
    public FloatParameter vignette = new(0.25f);

    public override bool IsEnabledAndSupported() => base.IsEnabledAndSupported() && maximumIterationCount.value > 0f;
}

public sealed class ScreenSpaceReflectionsRenderer : PostProcessEffectRenderer<ScreenSpaceReflections> { }
