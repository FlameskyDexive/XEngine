// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing.Effects;

[PostProcess(typeof(AutoExposureRenderer), "XEngine/Auto Exposure")]
public sealed class AutoExposure : PostProcessEffectSettings
{
    public FloatParameter keyValue = new();
    public FloatParameter speedUp = new(3f);
    public FloatParameter speedDown = new(1.5f);
    public FloatParameter minLuminance = new(0.1f);
    public FloatParameter maxLuminance = new(10f);
}

public sealed class AutoExposureRenderer : PostProcessEffectRenderer<AutoExposure> { }
