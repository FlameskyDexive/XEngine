// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WG5: command translator, including render-pass coalescing of SetRenderTarget / Clear /
/// Draw. GPU-less machines skip with the same availability exceptions as WG1–WG4.
/// </summary>
public class WebGPUTranslatorTests
{
    [Fact]
    public void ClearThenDraw_CoalescesToGreen_NotMagenta_Or_Skip()
    {
        ShaderCompileResult compiled = CompileGreen();
        Assert.True(compiled.Success, compiled.ErrorMessage);
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            using ShaderVariant variant = CreateVariant(compiled);
            using GraphicsResources resources = CreateTriangle(device, indexed: false);
            RasterizerState raster = DisabledDepth();

            using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg5-coalesce"))
            {
                draw.SetRenderTarget(resources.Framebuffer);
                draw.SetViewport(0, 0, 4, 4);
                draw.DisableScissor();
                draw.ClearRenderTarget(ClearFlags.Color, Color.Magenta);
                draw.SetShader(variant);
                draw.SetRasterState(in raster);
                draw.DrawArrays(resources.VertexArray, Topology.Triangles, 0, 3);
                device.Execute(draw, wait: true);
            }

            byte[] pixels = device.ReadTexture2D(device.Textures[resources.Color.Handle], 0);
            Assert.Equal(0, pixels[0]);
            Assert.Equal(255, pixels[1]);
            Assert.Equal(0, pixels[2]);
            Assert.Equal(255, pixels[3]);
            Assert.NotEqual(0ul, device.GetFenceValue());
            Console.WriteLine($"WG5 coalesced draw readback=({pixels[0]},{pixels[1]},{pixels[2]},{pixels[3]}) fence={device.GetFenceValue()}");

            DisposeFramebuffer(device, resources.Framebuffer);
        }
    }

    [Fact]
    public void ClearThenRebindSameTarget_KeepsClearColor_Or_Skip()
    {
        ShaderCompileResult compiled = CompileGreen();
        Assert.True(compiled.Success, compiled.ErrorMessage);
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            using GraphicsResources resources = CreateTriangle(device, indexed: false);

            using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg6-rebind-clear"))
            {
                draw.SetRenderTarget(resources.Framebuffer);
                draw.SetViewport(0, 0, 4, 4);
                draw.DisableScissor();
                draw.ClearRenderTarget(ClearFlags.Color, new Color(0.125f, 0.375f, 0.625f, 1f));
                draw.SetRenderTarget(resources.Framebuffer);
                draw.SetViewport(0, 0, 4, 4);
                device.Execute(draw, wait: true);
            }

            byte[] pixels = device.ReadTexture2D(device.Textures[resources.Color.Handle], 0);
            Assert.InRange(pixels[0], (byte)31, (byte)33);
            Assert.InRange(pixels[1], (byte)95, (byte)97);
            Assert.InRange(pixels[2], (byte)158, (byte)160);
            Assert.Equal((byte)255, pixels[3]);
            DisposeFramebuffer(device, resources.Framebuffer);
        }
    }

    [Fact]
    public void DrawIndexed_WritesGreen_Or_Skip()
    {
        ShaderCompileResult compiled = CompileGreen();
        Assert.True(compiled.Success, compiled.ErrorMessage);
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            using ShaderVariant variant = CreateVariant(compiled);
            using GraphicsResources resources = CreateTriangle(device, indexed: true);
            RasterizerState raster = DisabledDepth();

            using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg5-indexed"))
            {
                draw.SetRenderTarget(resources.Framebuffer);
                draw.SetViewport(0, 0, 4, 4);
                draw.DisableScissor();
                draw.ClearRenderTarget(ClearFlags.Color, Color.Magenta);
                draw.SetShader(variant);
                draw.SetRasterState(in raster);
                draw.DrawIndexed(resources.VertexArray, Topology.Triangles, 3, 0, 0, index32bit: false);
                device.Execute(draw, wait: true);
            }

            byte[] pixels = device.ReadTexture2D(device.Textures[resources.Color.Handle], 0);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels.AsSpan(0, 4).ToArray());
            DisposeFramebuffer(device, resources.Framebuffer);
        }
    }

    [Fact]
    public void FormerWg5Opcodes_DoNotThrowWg5_Or_Skip()
    {
        ShaderCompileResult compiled = CompileGreen();
        Assert.True(compiled.Success, compiled.ErrorMessage);
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            using ShaderVariant variant = CreateVariant(compiled);
            using GraphicsResources resources = CreateTriangle(device, indexed: false);
            RasterizerState raster = DisabledDepth();

            using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg5-mixed"))
            {
                draw.BeginSample("wg5");
                draw.SetRenderTarget(resources.Framebuffer);
                draw.SetViewport(0, 0, 4, 4);
                draw.SetScissor(0, 0, 4, 4);
                draw.DisableScissor();
                draw.ClearRenderTarget(ClearFlags.Color, Color.Magenta);
                draw.SetShader(variant);
                draw.SetRasterState(in raster);
                draw.DrawArrays(resources.VertexArray, Topology.Triangles, 0, 3);
                draw.EndSample();
                device.Execute(draw, wait: true);
            }

            Assert.NotEqual(0ul, device.GetFenceValue());
            DisposeFramebuffer(device, resources.Framebuffer);
        }
    }

    private static ShaderCompileResult CompileGreen() =>
        new RuntimeSlangShaderCompiler().Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.WebGPU,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "struct VSInput { float3 position : POSITION; }; float4 vertexMain(VSInput input) : SV_Position { return float4(input.position, 1); }",
            FragmentSource = "float4 fragmentMain() : SV_Target { return float4(0, 1, 0, 1); }",
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
        });

    private static ShaderVariant CreateVariant(ShaderCompileResult compiled) =>
        new(new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            ShaderBytecodeFormat.Wgsl,
            compiled.VertexBytecode!,
            compiled.FragmentBytecode!,
            compiled.BindingLayout,
            compiled.VertexInputLayout));

    private static RasterizerState DisabledDepth() => new()
    {
        DepthTest = false,
        DepthWrite = false,
        CullFace = RasterizerState.PolyFace.None,
    };

    private static GraphicsResources CreateTriangle(WebGPUGraphicsDevice device, bool indexed)
    {
        float[] vertices = [-1f, -1f, 0f, 3f, -1f, 0f, -1f, 3f, 0f];
        ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes(vertices.AsSpan());
        var vertexBuffer = new GraphicsBuffer(BufferType.VertexBuffer, vertexBytes, dynamic: true);
        GraphicsBuffer? indexBuffer = null;
        ReadOnlySpan<byte> indexBytes = default;
        if (indexed)
        {
            ushort[] indices = [0, 1, 2];
            indexBytes = MemoryMarshal.AsBytes(indices.AsSpan());
            indexBuffer = new GraphicsBuffer(BufferType.ElementsBuffer, indexBytes, dynamic: false);
        }

        var color = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
        GraphicsFrameBuffer framebuffer = GraphicsFrameBuffer.CreateDeferred(
            [new GraphicsFrameBuffer.Attachment { Texture = color }],
            4,
            4);
        var format = new VertexFormat([new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3)]);
        var vertexArray = new GraphicsVertexArray(format, vertexBuffer, indexBuffer);

        using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg5-create"))
        {
            create.EncodeCreateBuffer(vertexBuffer, dynamic: true, vertexBytes);
            if (indexBuffer != null)
                create.EncodeCreateBuffer(indexBuffer, dynamic: false, indexBytes);
            create.EncodeCreateTexture(color);
            create.EncodeAllocateTexture2D(color, 0, 4, 4, 0, ReadOnlySpan<byte>.Empty);
            create.EncodeCreateFramebuffer(framebuffer);
            create.EncodeCreateVertexArray(vertexArray);
            device.Execute(create, wait: true);
        }

        return new GraphicsResources(vertexBuffer, indexBuffer, color, framebuffer, vertexArray);
    }

    private static void DisposeFramebuffer(WebGPUGraphicsDevice device, GraphicsFrameBuffer framebuffer)
    {
        using CommandBuffer dispose = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-wg5-dispose-fb");
        dispose.EncodeDisposeFramebuffer(framebuffer);
        device.Execute(dispose, wait: true);
    }

    private static bool TryCreateDevice(out WebGPUGraphicsDevice device)
    {
        try
        {
            device = new WebGPUGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.WebGPU });
            return true;
        }
        catch (Exception ex)
        {
            Assert.True(
                IsExpectedGpuUnavailable(ex),
                $"Unexpected WebGPU create failure: {ex.GetType().FullName}: {ex.Message}");
            device = null!;
            return false;
        }
    }

    private static bool IsExpectedGpuUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                return true;
            }

            if (current is InvalidOperationException && IsExplicitGpuAvailabilityMessage(current.Message))
                return true;
        }

        return false;
    }

    private static bool IsExplicitGpuAvailabilityMessage(string message) =>
        message.StartsWith("Failed to create WebGPU device. wgpu-native is unavailable", StringComparison.Ordinal)
        || message.StartsWith("wgpuCreateInstance returned null", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU adapter:", StringComparison.Ordinal)
        || message.StartsWith("Failed to request a WebGPU device:", StringComparison.Ordinal)
        || message.Equals("wgpuDeviceGetQueue returned null.", StringComparison.Ordinal);

    private sealed class GraphicsResources : IDisposable
    {
        public GraphicsBuffer VertexBuffer { get; }
        public GraphicsBuffer? IndexBuffer { get; }
        public GraphicsTexture Color { get; }
        public GraphicsFrameBuffer Framebuffer { get; }
        public GraphicsVertexArray VertexArray { get; }

        public GraphicsResources(
            GraphicsBuffer vertexBuffer,
            GraphicsBuffer? indexBuffer,
            GraphicsTexture color,
            GraphicsFrameBuffer framebuffer,
            GraphicsVertexArray vertexArray)
        {
            VertexBuffer = vertexBuffer;
            IndexBuffer = indexBuffer;
            Color = color;
            Framebuffer = framebuffer;
            VertexArray = vertexArray;
        }

        public void Dispose()
        {
            VertexArray.Dispose();
            IndexBuffer?.Dispose();
            VertexBuffer.Dispose();
            Color.Dispose();
        }
    }
}
