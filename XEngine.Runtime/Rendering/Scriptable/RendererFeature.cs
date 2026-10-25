// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Injects extra <see cref="RenderPass"/> instances into a <see cref="ScriptableRenderer"/>.
/// Call <see cref="Create"/> once; <see cref="AddRenderPasses"/> runs every camera.
/// </summary>
public abstract class RendererFeature
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Camera inputs every pass from this feature needs. Merged even when the feature
    /// enqueues no extra pass (CopyColor can run on ConfigureInput alone).
    /// </summary>
    public ScriptableRenderPassInput InputRequirements { get; private set; }

    public void ConfigureInput(ScriptableRenderPassInput input) => InputRequirements |= input;

    /// <summary>Allocate passes and GPU resources. Invoked once before the first camera that uses this feature.</summary>
    public abstract void Create();

    /// <summary>
    /// Enqueue zero or more passes. Context has <see cref="ScriptableRenderContext.Camera"/> set;
    /// snapshot and color targets are filled later by built-in setup/color passes.
    /// </summary>
    public abstract void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context);
}
