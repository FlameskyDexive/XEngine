// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Rendering;

/// <summary>Binds a pipeline instance method to a <see cref="RenderPassEvent"/> without per-frame alloc.</summary>
internal sealed class BuiltinForwardPass : RenderPass
{
    private readonly Action<ScriptableRenderContext> _execute;

    public BuiltinForwardPass(RenderPassEvent renderPassEvent, Action<ScriptableRenderContext> execute, string? displayName = null)
        : base(renderPassEvent, displayName)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public override void Execute(ScriptableRenderContext context) => _execute(context);
}
