// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Toggles screen-space reflections as a renderer feature.
/// Reuses <see cref="ScreenSpaceReflectionEffect"/>; requests prepass Depth/Normal/(Motion) only while enabled.
/// </summary>
public sealed class ScreenSpaceReflectionRendererFeature : RendererFeature
{
    public ScreenSpaceReflectionEffect Effect { get; } = new();

    public override void Create() { }

    public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context)
    {
        if (!QualitySettings.ImageEffectsEnabled)
            return;
        ScriptableRenderPassInput input =
            ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal;
        if (Effect.UseTemporal)
            input |= ScriptableRenderPassInput.Motion;
        ConfigureInput(input);
    }
}
