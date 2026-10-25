// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// One unit of work in a <see cref="ScriptableRenderer"/> queue.
/// M1 built-in passes follow existing CommandBuffer submit boundaries so pixels stay identical.
/// </summary>
public abstract class RenderPass
{
    protected RenderPass(RenderPassEvent renderPassEvent, string? displayName = null)
    {
        Event = renderPassEvent;
        DisplayName = displayName ?? "";
    }

    public RenderPassEvent Event { get; }

    /// <summary>Optional UI / Frame Debugger label. Empty falls back to type name.</summary>
    public string DisplayName { get; set; }

    /// <summary>When false, <see cref="ScriptableRenderer.EnqueuePass"/> ignores this pass.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Camera textures this pass will sample. Drives CopyColor / CopyDepth / prepass.</summary>
    public ScriptableRenderPassInput Input { get; private set; }

    public void ConfigureInput(ScriptableRenderPassInput input) => Input |= input;

    public abstract void Execute(ScriptableRenderContext context);

    internal string ResolveDisplayName()
        => !string.IsNullOrEmpty(DisplayName) ? DisplayName : GetType().Name;
}
