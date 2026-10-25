// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Toggles Ground-Truth AO as a renderer feature (URP SSAO pattern).
/// Reuses <see cref="GTAOEffect"/>; requests prepass Depth/Normal/(Motion) only while enabled.
/// </summary>
public sealed class GTAORendererFeature : RendererFeature
{
    public GTAOEffect Effect { get; } = new();

    public override void Create() { }

    public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context)
    {
        if (!QualitySettings.ImageEffectsEnabled)
            return;
        ConfigureInput(RequiredInputs(Effect.UseTemporal));
    }

    internal static ScriptableRenderPassInput RequiredInputs(bool useTemporal)
    {
        ScriptableRenderPassInput input =
            ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal;
        if (useTemporal)
            input |= ScriptableRenderPassInput.Motion;
        return input;
    }
}
