// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Projects <see cref="DecalProjector"/> boxes onto scene depth after opaques.
/// Disabled or zero projectors: no extra pass, no prepass request (7 built-in passes).
/// </summary>
public sealed class ScreenSpaceDecalRendererFeature : RendererFeature
{
    private ScreenSpaceDecalPass? _pass;

    public override void Create()
    {
        _pass ??= new ScreenSpaceDecalPass();
        ScreenSpaceDecalPass.EnsureResources();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context)
    {
        if (DecalSystem.Instance.Count == 0)
            return;
        renderer.EnqueuePass(_pass);
    }
}
