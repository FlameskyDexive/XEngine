// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Inputs a <see cref="RenderPass"/> or <see cref="RendererFeature"/> needs from the camera.
/// CopyColor / CopyDepth and the unified prepass run only when a consumer is present (M7).
/// </summary>
[Flags]
public enum ScriptableRenderPassInput
{
    None = 0,
    Depth = 1 << 0,
    Normal = 1 << 1,
    Color = 1 << 2,
    Motion = 1 << 3,
}
