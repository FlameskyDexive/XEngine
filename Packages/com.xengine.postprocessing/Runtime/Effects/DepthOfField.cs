// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing.Effects;

[PostProcess(typeof(DepthOfFieldRenderer), "XEngine/Depth Of Field")]
public sealed class DepthOfField : PostProcessEffectSettings
{
    public BoolParameter useAutoFocus = new(true);
    public FloatParameter focusDistance = new(0.5f);
    public FloatParameter aperture = new(200f);
    public FloatParameter focalLength = new(4f);
}

public sealed class DepthOfFieldRenderer : PostProcessEffectRenderer<DepthOfField> { }
