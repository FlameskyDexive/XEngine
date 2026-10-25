// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Per-camera frame state shared by <see cref="RenderPass"/> implementations.
/// Owned and reused by <see cref="UniversalRenderPipeline"/>; do not allocate per frame.
/// </summary>
public sealed class ScriptableRenderContext
{
    public Camera Camera = null!;
    public RenderingData RenderingData;
    public RenderPipeline.CameraSnapshot Snapshot;
    public RenderTexture? Target;
    public RenderTexture? ColorRT;
    public RenderTexture? Prepass;
    public RenderTexture? OpaqueColor;
    public List<IRenderable> Renderables = null!;
    public List<IRenderableLight> Lights = null!;
    public bool[] CulledRenderableIndices = Array.Empty<bool>();
    public SceneLightSystem? LightSystem;
    public bool IsHdr;
    public RenderFeatureRequirements RequiredFeatures;

    public void ClearFrameTargets()
    {
        Target = null;
        ColorRT = null;
        Prepass = null;
        OpaqueColor = null;
        LightSystem = null;
        Camera = null!;
        CulledRenderableIndices = Array.Empty<bool>();
    }
}
