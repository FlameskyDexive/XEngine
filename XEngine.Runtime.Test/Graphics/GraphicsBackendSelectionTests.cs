// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.RHI;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

public class GraphicsBackendSelectionTests
{
    [Theory]
    [InlineData("vulkan", GraphicsBackend.Vulkan)]
    [InlineData("vk", GraphicsBackend.Vulkan)]
    [InlineData("d3d12", GraphicsBackend.Direct3D12)]
    [InlineData("dx12", GraphicsBackend.Direct3D12)]
    [InlineData("opengl", GraphicsBackend.OpenGL)]
    [InlineData("webgpu", GraphicsBackend.WebGPU)]
    [InlineData("wgpu", GraphicsBackend.WebGPU)]
    [InlineData("metal", GraphicsBackend.Metal)]
    [InlineData("mtl", GraphicsBackend.Metal)]
    [InlineData("webgl2", GraphicsBackend.WebGL2)]
    [InlineData("webgl", GraphicsBackend.WebGL2)]
    [InlineData("gles3", GraphicsBackend.WebGL2)]
    [InlineData("null", GraphicsBackend.Null)]
    [InlineData("auto", GraphicsBackend.Auto)]
    public void TryParseName_RecognizesAliases(string name, GraphicsBackend expected)
    {
        Assert.True(GraphicsBackendSelection.TryParseName(name, out GraphicsBackend backend));
        Assert.Equal(expected, backend);
    }

    [Fact]
    public void Parse_ReadsEqualsForm()
    {
        GraphicsBackend previous = GraphicsBackendSelection.Preferred ?? GraphicsBackend.Auto;
        try
        {
            GraphicsBackendSelection.Preferred = null;
            GraphicsBackend backend = GraphicsBackendSelection.Parse(["--graphics=vulkan"]);
            Assert.Equal(GraphicsBackend.Vulkan, backend);
        }
        finally
        {
            GraphicsBackendSelection.Preferred = previous == GraphicsBackend.Auto ? null : previous;
        }
    }

    [Fact]
    public void WindowedAuto_UsesNativeMacBackend_AndPreservesExplicitSelection()
    {
        GraphicsBackend expected = OperatingSystem.IsMacOS()
            ? GraphicsBackend.Metal
            : GraphicsBackend.Vulkan;
        Assert.Equal(expected, GraphicsBackendSelection.ResolveWindowedAuto(GraphicsBackend.Auto));
        Assert.Equal(GraphicsBackend.Vulkan,
            GraphicsBackendSelection.ResolveWindowedAuto(GraphicsBackend.Vulkan));
    }

    [Fact]
    public void NullDevice_RecyclesCommandBuffers()
    {
        var device = new NullGraphicsDevice();
        device.Initialize(null);
        CommandBuffer cmd = global::XEngine.Runtime.Graphics.GetCommandBuffer("test");
        // Execute takes ownership and returns to pool.
        device.Execute(cmd, wait: false);
        Assert.True(device.GetFenceValue() > 0);
        device.Dispose();
    }

    [Fact]
    public void TopologyUtilities_MarksLegacyTopologies()
    {
        Assert.True(TopologyUtilities.IsPortable(Topology.Triangles));
        Assert.False(TopologyUtilities.IsPortable(Topology.Quads));
        Assert.False(TopologyUtilities.IsPortable(Topology.LineLoop));
        Assert.False(TopologyUtilities.IsPortable(Topology.TriangleFan));
    }
}
