// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class RenderingDebuggerPanelTests
{
    [Fact]
    public void Panel_HasStableTitleAndMenuTypeName()
    {
        var panel = new RenderingDebuggerPanel();
        Assert.Equal("Rendering Debugger", panel.Title);
        Assert.Equal("XEngine.Editor.GUI.Panels.RenderingDebuggerPanel", panel.GetType().FullName);
    }

    [Fact]
    public void RenderDebugResult_PreservesPassAndVolumeFields()
    {
        var result = new EditorRuntimeTools.RenderDebugResult(
            DrawCalls: 5,
            RenderablesDrawn: 3,
            OverdrawProxy: 2,
            Lights: 4,
            DirectionalLights: 1,
            PointLights: 2,
            SpotLights: 1,
            ShadowCasters: 1,
            ShadowCascades: 4,
            EnqueuedPasses: 7,
            ExecutedPasses: 7,
            CulledGraphPasses: 2,
            CopyColorPasses: 0,
            CopyDepthPasses: 0,
            RtWidth: 1280,
            RtHeight: 720,
            OpaqueAllocated: false,
            PrepassAllocated: false,
            PassEvents: ["BeforeRendering", "AfterRendering"],
            Features: ["ScreenSpaceDecalRendererFeature"],
            VolumeStack: ["FogVolumeComponent"],
            Volumes: [new EditorRuntimeTools.RenderDebugVolume("Fog", true, 1f, 0, true)]);

        Assert.Equal(2, result.OverdrawProxy);
        Assert.Equal(4, result.ShadowCascades);
        Assert.Equal(7, result.EnqueuedPasses);
        Assert.Equal(2, result.CulledGraphPasses);
        Assert.Equal("ScreenSpaceDecalRendererFeature", result.Features[0]);
        Assert.Equal("Fog", result.Volumes[0].Name);
        Assert.True(result.Volumes[0].IsGlobal);
    }

    [Fact]
    public void PhysicsDebugger_OverlayFlagsDefaultOff()
    {
        Assert.False(XEngine.Runtime.PhysicsDebugger.AnyOverlayEnabled);
    }
}
