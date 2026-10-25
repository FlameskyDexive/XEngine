// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class MeshDeviceTransitionTests : IDisposable
{
    private readonly GraphicsBackend? _previousBackend = GraphicsBackendSelection.Preferred;

    public void Dispose()
    {
        Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
    }

    [Fact]
    public void MorphTextures_RecreateBeforeUploadOnDeviceTransition()
    {
        Assert.Null(Graphics.Device);
        using var mesh = Mesh.CreateTriangle(Float3.Zero, Float3.UnitX, Float3.UnitY);
        mesh.BlendShapes = [new BlendShape
        {
            Name = "Offset",
            Frames = [new BlendShapeFrame
            {
                Weight = 100f, DeltaVertices = [Float3.UnitZ, Float3.UnitZ, Float3.UnitZ],
            }],
        }];
        // SkinnedMeshRenderer prepares morphs before the mesh's first draw/upload.
        mesh.EnsureMorphTextures();
        var oldTexture = mesh.MorphPositionTexture;
        Assert.NotNull(oldTexture);
        Initialize(GraphicsBackend.Null);
        mesh.EnsureMorphTextures();
        Assert.NotSame(oldTexture, mesh.MorphPositionTexture);
        Assert.True(oldTexture.IsDisposed);
        var currentTexture = mesh.MorphPositionTexture;
        mesh.Upload();
        mesh.EnsureMorphTextures();
        Assert.Same(currentTexture, mesh.MorphPositionTexture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Upload_RecreatesDeviceResourcesAndRetainsSameDeviceReuse(bool firstNullDevice)
    {
        Assert.Null(Graphics.Device);
        if (firstNullDevice) Initialize(GraphicsBackend.Null);
        using var mesh = Mesh.CreateCube(Float3.One);
        mesh.Upload();
        var oldInstancedVao = mesh.EnsureInstanceVAO(2, out var oldInstances);
        var oldVao = mesh.VertexArrayObject;
        var oldVertices = mesh.VertexBuffer;
        var oldIndices = mesh.IndexBuffer;

        Graphics.Dispose();
        Initialize(GraphicsBackend.Null);
        mesh.Upload();
        Assert.NotSame(oldVao, mesh.VertexArrayObject);
        Assert.NotSame(oldVertices, mesh.VertexBuffer);
        Assert.NotSame(oldIndices, mesh.IndexBuffer);
        Assert.True(oldVao!.IsDisposed);
        Assert.True(oldVertices.IsDisposed);
        Assert.True(oldIndices.IsDisposed);
        var newInstancedVao = mesh.EnsureInstanceVAO(2, out var newInstances);
        Assert.NotSame(oldInstancedVao, newInstancedVao);
        Assert.NotSame(oldInstances, newInstances);
        Assert.True(oldInstancedVao.IsDisposed);
        Assert.True(oldInstances.IsDisposed);

        var currentVao = mesh.VertexArrayObject;
        var currentVertices = mesh.VertexBuffer;
        var currentIndices = mesh.IndexBuffer;
        mesh.Upload();
        // Updating the same-sized geometry must also retain the new device's buffers.
        mesh.Vertices = (Float3[])mesh.Vertices.Clone();
        mesh.Upload();
        Assert.Same(currentVao, mesh.VertexArrayObject);
        Assert.Same(currentVertices, mesh.VertexBuffer);
        Assert.Same(currentIndices, mesh.IndexBuffer);
        Assert.Same(newInstancedVao, mesh.EnsureInstanceVAO(2, out var reusedInstances));
        Assert.Same(newInstances, reusedInstances);
    }

    [MacOSVulkanTransitionTheory]
    [InlineData(null)]
    [InlineData(GraphicsBackend.Null)]
    [InlineData(GraphicsBackend.Vulkan)]
    public void FullscreenBlit_AfterDeviceTransition_CopiesActualPixels(GraphicsBackend? previous)
    {
        Assert.Null(Graphics.Device);
        // This cache is also used by direct CommandBuffer.Blit before URP has acquired it.
        Mesh.GetFullscreenQuad().Dispose();
        if (previous.HasValue) Initialize(previous.Value);
        Mesh quad = Mesh.GetFullscreenQuad();
        quad.Upload();
        var oldVao = quad.VertexArrayObject;
        uint oldHandle = oldVao!.Handle;
        Graphics.Dispose();
        Assert.Same(quad, Mesh.GetFullscreenQuad());

        Initialize(GraphicsBackend.Vulkan);
        GlobalUniforms.Initialize();
        var device = Assert.IsType<VulkanGraphicsDevice>(Graphics.BackendDevice);
        const int width = 32, height = 16;
        using var source = new RenderTexture(width, height, false, [TextureImageFormat.Color4b]);
        using var target = new RenderTexture(width, height, false, [TextureImageFormat.Color4b]);
        using (var clear = Graphics.GetCommandBuffer("mesh-transition-clear"))
        {
            clear.SetViewport(0, 0, width, height);
            clear.SetRenderTarget(source.frameBuffer);
            clear.ClearRenderTarget(ClearFlags.Color, new Color(1f, 0f, 0f, 1f));
            clear.SetRenderTarget(target.frameBuffer);
            clear.ClearRenderTarget(ClearFlags.Color, new Color(0f, 0f, 1f, 1f));
            Graphics.SubmitAndWait(clear);
        }
        using (var blit = Graphics.GetCommandBuffer("mesh-transition-blit"))
        {
            blit.Blit(source, target);
            Graphics.SubmitAndWait(blit);
        }
        device.WaitIdle();
        byte[] pixels = device.ReadTexture2D(device.Images[target.MainTexture.Handle.Handle], 4);
        string? evidence = Environment.GetEnvironmentVariable("XENGINE_TEST_SCREENSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            string path = Path.Combine(evidence, "mesh-transition-" + (previous?.ToString() ?? "NoDevice"));
            int centerOffset = (width * height / 2 + width / 2) * 4;
            File.WriteAllBytes(path + ".rgba", pixels);
            File.WriteAllText(path + ".json", JsonSerializer.Serialize(new
            {
                width, height, previous = previous?.ToString() ?? "NoDevice", oldHandle,
                newHandle = quad.VertexArrayObject!.Handle,
                vaoRecreated = !ReferenceEquals(oldVao, quad.VertexArrayObject),
                vaoPresent = device.VertexArrays.ContainsKey(quad.VertexArrayObject.Handle),
                vertexPresent = device.Buffers.ContainsKey(quad.VertexBuffer.Handle),
                indexPresent = device.Buffers.ContainsKey(quad.IndexBuffer.Handle),
                center = new int[] { pixels[centerOffset], pixels[centerOffset + 1], pixels[centerOffset + 2], pixels[centerOffset + 3] },
            }));
        }

        for (int i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i]);
            Assert.Equal(0, pixels[i + 1]);
            Assert.Equal(0, pixels[i + 2]);
            Assert.Equal(255, pixels[i + 3]);
        }
        Assert.NotSame(oldVao, quad.VertexArrayObject);
        Assert.True(device.VertexArrays.ContainsKey(quad.VertexArrayObject!.Handle));
        Assert.True(device.Buffers.ContainsKey(quad.VertexBuffer.Handle));
        Assert.True(device.Buffers.ContainsKey(quad.IndexBuffer.Handle));
        var currentVao = quad.VertexArrayObject;
        quad.Upload();
        Assert.Same(currentVao, quad.VertexArrayObject);
    }

    private static void Initialize(GraphicsBackend backend)
    {
        GraphicsBackendSelection.Preferred = backend;
        Graphics.Initialize(debug: false);
    }
}

// This delivery gate requires the installed Vulkan/MoltenVK stack on macOS. Other
// hosts report an explicit skip; a missing device on macOS fails in the test body.
public sealed class MacOSVulkanTransitionTheoryAttribute : TheoryAttribute
{
    public MacOSVulkanTransitionTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "Requires macOS Vulkan/MoltenVK device-transition acceptance.";
    }
}
