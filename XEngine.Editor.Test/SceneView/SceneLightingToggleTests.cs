// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Editor.GUI.SceneView;
using XEngine.Runtime.Rendering;

namespace XEngine.Editor.Test.SceneView;

/// <summary>
/// ★ M7 — The scene-view light-bulb toggle and the Unlit draw mode are one piece of state surfaced
/// two ways (Unity puts the button in the toolbar and the mode in the draw-mode dropdown), so the
/// two views have to stay in sync in both directions.
/// </summary>
public class SceneLightingToggleTests
{
    [Fact]
    public void LightingOff_SelectsUnlitDrawMode()
    {
        var settings = new SceneViewSettings();
        Assert.True(settings.SceneLightingEnabled);

        settings.SceneLightingEnabled = false;

        Assert.Equal(DebugViewMode.Unlit, settings.DrawMode);
        Assert.False(settings.SceneLightingEnabled);
    }

    [Fact]
    public void LightingBackOn_ReturnsToShaded()
    {
        var settings = new SceneViewSettings { DrawMode = DebugViewMode.Unlit };

        settings.SceneLightingEnabled = true;

        Assert.Equal(DebugViewMode.Shaded, settings.DrawMode);
    }

    [Fact]
    public void LightingBackOn_KeepsAnUnrelatedDrawMode()
    {
        // Turning lighting on from Wireframe must not silently drop the user back to Shaded.
        var settings = new SceneViewSettings { DrawMode = DebugViewMode.Wireframe };

        settings.SceneLightingEnabled = true;

        Assert.Equal(DebugViewMode.Wireframe, settings.DrawMode);
        Assert.True(settings.SceneLightingEnabled);
    }

    [Fact]
    public void LightingOff_FromAnotherMode_TakesOverTheMode()
    {
        var settings = new SceneViewSettings { DrawMode = DebugViewMode.Overdraw };

        settings.SceneLightingEnabled = false;

        Assert.Equal(DebugViewMode.Unlit, settings.DrawMode);
    }
}
