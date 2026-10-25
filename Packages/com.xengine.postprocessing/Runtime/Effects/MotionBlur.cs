// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing.Effects;

[PostProcess(typeof(MotionBlurRenderer), "XEngine/Motion Blur")]
public sealed class MotionBlur : PostProcessEffectSettings
{
    public FloatParameter shutterAngle = new(270f);
    public IntParameter sampleCount = new(8);
    public FloatParameter maxBlurRadius = new(40f);

    public override bool IsEnabledAndSupported() => base.IsEnabledAndSupported() && shutterAngle.value > 0f;
}

public sealed class MotionBlurRenderer : PostProcessEffectRenderer<MotionBlur> { }
