// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

using XEngine.Runtime;
using XEngine.Runtime.Backends.Metal;
using XEngine.Runtime.Backends.Metal.Native;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// M4: command translator, including render-pass coalescing of SetRenderTarget / Clear / Draw.
/// Non-macOS hosts are reported as skipped. Draw tests bind runtime MSL (not S1 MetalLib).
/// </summary>
public class MetalTranslatorTests
{
    private const string GreenMsl = """
#include <metal_stdlib>
using namespace metal;
vertex float4 vertexMain(uint id [[vertex_id]]) {
    float2 p = float2(float((id << 1) & 2u), float(id & 2u));
    return float4(p * 2.0 - 1.0, 0.0, 1.0);
}
fragment float4 fragmentMain() {
    return float4(0.0, 1.0, 0.0, 1.0);
}
""";

    private const string IndexedGreenMsl = """
#include <metal_stdlib>
using namespace metal;
struct VertexIn {
    float3 position [[attribute(0)]];
    float2 texCoord [[attribute(1)]];
};
struct ObjectUniforms {
    float4x4 xengine_ObjectToWorld;
};
vertex float4 vertexMain(VertexIn input [[stage_in]],
                         constant ObjectUniforms& object [[buffer(1)]]) {
    return object.xengine_ObjectToWorld * float4(input.position + float3(input.texCoord.x * 0.1, 0.0, 0.0), 1.0);
}
fragment float4 fragmentMain() {
    return float4(0.0, 1.0, 0.0, 1.0);
}
""";

    [MacOSMetalFact]
    public void ScissorRect_ClipsDrawToRequestedInteriorPixels()
    {
        using var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });
        device.Initialize(null);
        using ShaderVariant variant = CreateDummyVariant();
        using GraphicsResources resources = CreateTarget(device);
        BindGreen(device);
        RasterizerState raster = DisabledDepth();
        using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-scissor-abi"))
        {
            draw.SetRenderTarget(resources.Framebuffer);
            draw.SetViewport(0, 0, 4, 4);
            draw.ClearRenderTarget(ClearFlags.Color, Color.Magenta);
            draw.SetScissor(1, 1, 2, 2);
            draw.SetShader(variant);
            draw.SetRasterState(in raster);
            draw.DrawArrays(resources.VertexArray, Topology.Triangles, 0, 3);
            device.Execute(draw, wait: true);
        }
        byte[] pixels = device.ReadTexture2D(device.Textures[resources.Color.Handle], 0);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                bool interior = x >= 1 && x < 3 && y >= 1 && y < 3;
                int offset = (y * 4 + x) * 4;
                Assert.Equal(interior ? (byte)0 : (byte)255, pixels[offset]);
                Assert.Equal(interior ? (byte)255 : (byte)0, pixels[offset + 1]);
                Assert.Equal(interior ? (byte)0 : (byte)255, pixels[offset + 2]);
                Assert.Equal((byte)255, pixels[offset + 3]);
            }
        }
        DisposeFramebuffer(device, resources.Framebuffer);
    }

    [MacOSMetalFact]
    public void ClearThenRebindSameTarget_KeepsClearColor()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using GraphicsResources resources = CreateTarget(device);

            using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m4-rebind-clear"))
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

    [MacOSMetalFact]
    public void ClearThenDraw_CoalescesToGreen_NotMagenta()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using ShaderVariant variant = CreateDummyVariant();
            using GraphicsResources resources = CreateTarget(device);
            BindGreen(device);
            RasterizerState raster = DisabledDepth();

            using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m4-coalesce"))
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
            DisposeFramebuffer(device, resources.Framebuffer);
        }
    }

    [MacOSMetalFact]
    public void IndexedMeshWithDepth_RendersPixelsToAttachment()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using ShaderVariant variant = CreateObjectUniformVariant();
            using GraphicsResources resources = CreateIndexedTarget(device);
            BindIndexedGreen(device);
            RasterizerState raster = new()
            {
                DepthTest = true,
                DepthWrite = true,
                Depth = RasterizerState.DepthMode.Lequal,
                CullFace = RasterizerState.PolyFace.Back,
                Winding = RasterizerState.WindingOrder.CW,
            };

            using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m4-indexed-depth"))
            {
                draw.SetRenderTarget(resources.Framebuffer);
                draw.SetViewport(0, 0, 8, 8);
                draw.DisableScissor();
                draw.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, Color.Black, depth: 1f);
                draw.SetShader(variant);
                draw.SetRasterState(in raster);
                Float4x4 model = Float4x4.CreateTranslation(new Float3(0.5f, 0f, 0f));
                draw.SetMatrix("xengine_ObjectToWorld", in model);
                draw.DrawIndexed(resources.VertexArray, Topology.Triangles, 3);
                device.Execute(draw, wait: true);
            }

            byte[] pixels = device.ReadTexture2D(device.Textures[resources.Color.Handle], 0);
            DisposeFramebuffer(device, resources.Framebuffer);

            int translatedAway = (4 * 8 + 3) * 4;
            Assert.Equal(0, pixels[translatedAway]);
            Assert.Equal(0, pixels[translatedAway + 1]);
            Assert.Equal(0, pixels[translatedAway + 2]);
            Assert.Equal(255, pixels[translatedAway + 3]);

            int texCoordShiftedAway = (4 * 8 + 5) * 4;
            Assert.Equal(0, pixels[texCoordShiftedAway]);
            Assert.Equal(0, pixels[texCoordShiftedAway + 1]);
            Assert.Equal(0, pixels[texCoordShiftedAway + 2]);
            Assert.Equal(255, pixels[texCoordShiftedAway + 3]);

            int texCoordShifted = (4 * 8 + 6) * 4;
            Assert.Equal(0, pixels[texCoordShifted]);
            Assert.Equal(255, pixels[texCoordShifted + 1]);
            Assert.Equal(0, pixels[texCoordShifted + 2]);
            Assert.Equal(255, pixels[texCoordShifted + 3]);

            int outside = 0;
            Assert.Equal(0, pixels[outside]);
            Assert.Equal(0, pixels[outside + 1]);
            Assert.Equal(0, pixels[outside + 2]);
            Assert.Equal(255, pixels[outside + 3]);
        }
    }

    [MacOSMetalFact]
    public void FormerM4Opcodes_DoNotThrow()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using ShaderVariant variant = CreateDummyVariant();
            using GraphicsResources resources = CreateTarget(device);
            BindGreen(device);
            RasterizerState raster = DisabledDepth();

            using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m4-mixed"))
            {
                draw.BeginSample("m4");
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

    private static void BindGreen(MetalGraphicsDevice device)
    {
        nint library = device.NewLibraryFromSource(GreenMsl);
        nint vertex = MetalNative.NewFunction(library, "vertexMain");
        nint fragment = MetalNative.NewFunction(library, "fragmentMain");
        device.BindMslFunctions(vertex, fragment);
        MetalNative.Release(fragment);
        MetalNative.Release(vertex);
        MetalNative.Release(library);
    }

    private static void BindIndexedGreen(MetalGraphicsDevice device)
    {
        nint library = device.NewLibraryFromSource(IndexedGreenMsl);
        nint vertex = MetalNative.NewFunction(library, "vertexMain");
        nint fragment = MetalNative.NewFunction(library, "fragmentMain");
        device.BindMslFunctions(vertex, fragment);
        MetalNative.Release(fragment);
        MetalNative.Release(vertex);
        MetalNative.Release(library);
    }

    private static ShaderVariant CreateDummyVariant() =>
        new(new CompiledShaderBytecode(ShaderLanguage.Hlsl, ShaderBytecodeFormat.MetalLib, [1], [1]));

    private static ShaderVariant CreateObjectUniformVariant() =>
        new(new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            ShaderBytecodeFormat.MetalLib,
            [1],
            [1],
            bindingLayout: new ShaderBindingLayout
            {
                Buffers =
                [
                    new ShaderBindingSlot(
                        ShaderBindingKind.Buffer,
                        1,
                        "ObjectUniforms",
                        stages: ShaderStageMask.Vertex,
                        byteSize: 64,
                        physicalBinding: 1,
                        physicalSpace: 0),
                ],
            },
            vertexInputLayout: new ShaderVertexInputLayout
            {
                Inputs =
                [
                    new ShaderVertexInputSlot((uint)VertexFormat.VertexSemantic.Position, 0),
                    new ShaderVertexInputSlot((uint)VertexFormat.VertexSemantic.TexCoord0, 1),
                ],
            }));

    private static RasterizerState DisabledDepth() => new()
    {
        DepthTest = false,
        DepthWrite = false,
        CullFace = RasterizerState.PolyFace.None,
    };

    private static GraphicsResources CreateTarget(MetalGraphicsDevice device)
    {
        float[] vertices = [-1f, -1f, 0f, 3f, -1f, 0f, -1f, 3f, 0f];
        ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes(vertices.AsSpan());
        var vertexBuffer = new GraphicsBuffer(BufferType.VertexBuffer, vertexBytes, dynamic: true);
        var color = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
        GraphicsFrameBuffer framebuffer = GraphicsFrameBuffer.CreateDeferred(
            [new GraphicsFrameBuffer.Attachment { Texture = color }],
            4,
            4);
        var format = new VertexFormat([new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3)]);
        var vertexArray = new GraphicsVertexArray(format, vertexBuffer, null);

        using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m4-create"))
        {
            create.EncodeCreateBuffer(vertexBuffer, dynamic: true, vertexBytes);
            create.EncodeCreateTexture(color);
            create.EncodeAllocateTexture2D(color, 0, 4, 4, 0, ReadOnlySpan<byte>.Empty);
            create.EncodeCreateFramebuffer(framebuffer);
            create.EncodeCreateVertexArray(vertexArray);
            device.Execute(create, wait: true);
        }

        return new GraphicsResources(vertexBuffer, color, framebuffer, vertexArray);
    }

    private static GraphicsResources CreateIndexedTarget(MetalGraphicsDevice device)
    {
        // Clockwise order matches the engine's default raster winding and exercises indexed
        // vertex fetch through Metal's vertex descriptor, with a real depth attachment.
        // Match the Zonezero ground's interleaved Position/UV/Normal/Tangent stride. The
        // Unlit vertex stage consumes only Position and UV; the descriptor must retain the full
        // stride while leaving the unused normal/tangent attributes unmapped.
        float[] vertices = [
            -0.75f, -0.75f, 0.5f, 4f, 0f, 0f, 1f, 0f, 1f, 0f, 0f, 1f,
            0.75f, -0.75f, 0.5f, 4f, 0f, 0f, 1f, 0f, 1f, 0f, 0f, 1f,
            0f, 0.75f, 0.5f, 4f, 0f, 0f, 1f, 0f, 1f, 0f, 0f, 1f,
        ];
        ushort[] indices = [0, 2, 1];
        ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes(vertices.AsSpan());
        ReadOnlySpan<byte> indexBytes = MemoryMarshal.AsBytes(indices.AsSpan());
        var vertexBuffer = new GraphicsBuffer(BufferType.VertexBuffer, vertexBytes, dynamic: false);
        var indexBuffer = new GraphicsBuffer(BufferType.ElementsBuffer, indexBytes, dynamic: false);
        var color = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
        var depth = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Depth32f);
        GraphicsFrameBuffer framebuffer = GraphicsFrameBuffer.CreateDeferred(
            [
                new GraphicsFrameBuffer.Attachment { Texture = color },
                new GraphicsFrameBuffer.Attachment { Texture = depth, IsDepth = true },
            ],
            8,
            8);
        var format = new VertexFormat([
            new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3),
            new(VertexFormat.VertexSemantic.TexCoord0, VertexFormat.VertexType.Float, 2),
            new(VertexFormat.VertexSemantic.Normal, VertexFormat.VertexType.Float, 3, normalized: true),
            new(VertexFormat.VertexSemantic.Tangent, VertexFormat.VertexType.Float, 4, normalized: true),
        ]);
        var vertexArray = new GraphicsVertexArray(format, vertexBuffer, indexBuffer);

        using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m4-indexed-create"))
        {
            create.EncodeCreateBuffer(vertexBuffer, dynamic: false, vertexBytes);
            create.EncodeCreateBuffer(indexBuffer, dynamic: false, indexBytes);
            create.EncodeCreateTexture(color);
            create.EncodeAllocateTexture2D(color, 0, 8, 8, 0, ReadOnlySpan<byte>.Empty);
            create.EncodeCreateTexture(depth);
            create.EncodeAllocateTexture2D(depth, 0, 8, 8, 0, ReadOnlySpan<byte>.Empty);
            create.EncodeCreateFramebuffer(framebuffer);
            create.EncodeCreateVertexArray(vertexArray);
            device.Execute(create, wait: true);
        }

        return new GraphicsResources(vertexBuffer, color, framebuffer, vertexArray, indexBuffer, depth);
    }

    private static void DisposeFramebuffer(MetalGraphicsDevice device, GraphicsFrameBuffer framebuffer)
    {
        using CommandBuffer dispose = global::XEngine.Runtime.Graphics.GetCommandBuffer("metal-m4-dispose-fb");
        dispose.EncodeDisposeFramebuffer(framebuffer);
        device.Execute(dispose, wait: true);
    }

    private sealed class GraphicsResources : IDisposable
    {
        public GraphicsBuffer VertexBuffer { get; }
        public GraphicsTexture Color { get; }
        public GraphicsFrameBuffer Framebuffer { get; }
        public GraphicsVertexArray VertexArray { get; }
        private readonly GraphicsBuffer? _indexBuffer;
        private readonly GraphicsTexture? _depth;

        public GraphicsResources(
            GraphicsBuffer vertexBuffer,
            GraphicsTexture color,
            GraphicsFrameBuffer framebuffer,
            GraphicsVertexArray vertexArray,
            GraphicsBuffer? indexBuffer = null,
            GraphicsTexture? depth = null)
        {
            VertexBuffer = vertexBuffer;
            Color = color;
            Framebuffer = framebuffer;
            VertexArray = vertexArray;
            _indexBuffer = indexBuffer;
            _depth = depth;
        }

        public void Dispose()
        {
            VertexArray.Dispose();
            _indexBuffer?.Dispose();
            VertexBuffer.Dispose();
            Color.Dispose();
            _depth?.Dispose();
        }
    }
}
