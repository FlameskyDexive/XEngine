// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Rendering;

namespace XEngine.PostProcessing;

/// <summary>Optional GPU renderer for a custom effect. Built-in effects use ImageEffects instead.</summary>
public abstract class PostProcessEffectRenderer
{
    protected bool m_ResetHistory = true;

    public virtual void Init() { }
    public virtual void ResetHistory() => m_ResetHistory = true;
    public virtual void Release() => ResetHistory();
    public virtual void Render(PostProcessRenderContext context) { }
    internal abstract void SetSettings(PostProcessEffectSettings settings);
}

public abstract class PostProcessEffectRenderer<T> : PostProcessEffectRenderer
    where T : PostProcessEffectSettings
{
    public T settings { get; internal set; } = null!;

    internal override void SetSettings(PostProcessEffectSettings settings)
        => this.settings = (T)settings;
}

/// <summary>Minimal context passed to custom <see cref="PostProcessEffectRenderer.Render"/>.</summary>
public sealed class PostProcessRenderContext
{
    public Camera? Camera;
    public RenderContext? RenderContext;
    public int Width;
    public int Height;
}
