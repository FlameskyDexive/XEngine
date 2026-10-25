// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing.Effects;

[PostProcess(typeof(ChromaticAberrationRenderer), "XEngine/Chromatic Aberration")]
public sealed class ChromaticAberration : PostProcessEffectSettings
{
    public FloatParameter intensity = new(3f);
    public FloatParameter distortion = new(0.5f);

    public override bool IsEnabledAndSupported() => base.IsEnabledAndSupported() && intensity.value > 0f;
}

public sealed class ChromaticAberrationRenderer : PostProcessEffectRenderer<ChromaticAberration> { }
