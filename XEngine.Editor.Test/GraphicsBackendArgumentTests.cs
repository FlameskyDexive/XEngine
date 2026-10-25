using System;

using XEngine.Editor.Theming;
using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Editor.Test;

public class GraphicsBackendArgumentTests
{
    [Theory]
    [InlineData("vulkan", null, true, false, GraphicsBackend.Metal, 1)]
    [InlineData("vulkan", null, true, true, GraphicsBackend.Vulkan, 1)]
    [InlineData("vulkan", null, false, false, GraphicsBackend.Vulkan, 0)]
    [InlineData("metal", null, true, false, GraphicsBackend.Metal, 0)]
    [InlineData("auto", null, true, false, GraphicsBackend.Auto, 0)]
    [InlineData("vulkan", "vulkan", true, false, GraphicsBackend.Vulkan, 0)]
    [InlineData("vulkan", "auto", true, false, GraphicsBackend.Auto, 0)]
    [InlineData("vulkan", "invalid", true, false, GraphicsBackend.Metal, 1)]
    public void SavedPreference_RecoversUnavailableMacVulkan_AndPreservesExplicitEnvironment(
        string saved, string? environment, bool isMacOS, bool available,
        GraphicsBackend expected, int expectedProbes)
    {
        int probes = 0;
        var result = Program.ResolveSavedGraphicsBackend(saved, environment, isMacOS,
            () => { probes++; return available; });

        Assert.Equal(expected, result);
        Assert.Equal(expectedProbes, probes);
    }

    [Fact]
    public void UnknownSavedPreference_RemainsUnresolved()
    {
        Assert.Null(Program.ResolveSavedGraphicsBackend("unknown", null, true,
            () => throw new InvalidOperationException("Unexpected Vulkan probe")));
    }

    [Fact]
    public void EditorSettings_DefaultsToAuto()
    {
        var settings = new EditorSettings();

        Assert.Equal("auto", settings.PreferredGraphicsBackend);
    }

    [Theory]
    [InlineData("--graphics=d3d12", null, GraphicsBackend.Direct3D12)]
    [InlineData("--graphics", "vulkan", GraphicsBackend.Vulkan)]
    [InlineData("-g", "opengl", GraphicsBackend.OpenGL)]
    [InlineData("--graphics=webgpu", null, GraphicsBackend.WebGPU)]
    [InlineData("--graphics=metal", null, GraphicsBackend.Metal)]
    [InlineData("--graphics=webgl2", null, GraphicsBackend.WebGL2)]
    [InlineData("--graphics=gles3", null, GraphicsBackend.WebGL2)]
    public void EditorArguments_Accept_GraphicsBackend(string option, string? value, GraphicsBackend expected)
    {
        GraphicsBackend? previous = GraphicsBackendSelection.Preferred;
        try
        {
            Program.ReadArguments(value == null ? [option] : [option, value]);

            Assert.Equal(expected, GraphicsBackendSelection.Preferred);
        }
        finally
        {
            GraphicsBackendSelection.Preferred = previous;
        }
    }

    [Fact]
    public void EditorArguments_Reject_Unknown_GraphicsBackend()
    {
        GraphicsBackend? previous = GraphicsBackendSelection.Preferred;
        try
        {
            Exception error = Assert.Throws<Exception>(() => Program.ReadArguments(["--graphics=moltenvk"]));
            Assert.Contains("Unknown graphics backend", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            GraphicsBackendSelection.Preferred = previous;
        }
    }

    [Fact]
    public void EditorArguments_Accept_ServeMode()
    {
        Program.ReadArguments(["--serve"]);
        Assert.True(Program.Serve);
    }

    [Fact]
    public void EditorArguments_Accept_PanelScreenshot()
    {
        Program.ReadArguments([
            "--panel-screenshot", "XEngine.Editor.GUI.Panels.HierarchyPanel",
            "--screenshot-out", "docs/out.png"
        ]);
        Assert.Equal("XEngine.Editor.GUI.Panels.HierarchyPanel", Program.PanelScreenshotType);
        Assert.Equal("docs/out.png", Program.PanelScreenshotOut);
    }
}
