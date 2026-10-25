// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Ordered injection points for <see cref="RenderPass"/> execution.
/// Numeric gaps leave room for <c>RendererFeature</c> injection (M2) without renumbering.
/// </summary>
public enum RenderPassEvent
{
    BeforeRendering = 0,
    BeforeRenderingShadows = 50,
    AfterRenderingShadows = 100,
    BeforeRenderingPrepasses = 150,
    AfterRenderingPrepasses = 200,
    BeforeRenderingOpaques = 250,
    /// <summary>Deferred lighting accumulation (after GBuffer, before AfterOpaques effects).</summary>
    AfterRenderingGbuffer = 270,
    AfterRenderingOpaques = 300,
    BeforeRenderingTransparents = 350,
    AfterRenderingTransparents = 400,
    BeforeRenderingPostProcessing = 450,
    AfterRenderingPostProcessing = 500,
    AfterRendering = 600,
}
