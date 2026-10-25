// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Reflection;
using System.Runtime.InteropServices;

using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WG6: Stage C uniform packing and GlobalUniforms auto-bind on desktop WebGPU.
/// GPU-less machines skip with the same availability exceptions as WG1–WG5.
/// </summary>
public class WebGPUStageCTests
{
    [Fact]
    public void Optional_WebGPU_Unlit_DefaultShader_Compiles()
    {
        Resources.Shader shader = Resources.Shader.LoadDefault(Resources.DefaultShader.Unlit);
        foreach (Rendering.Shaders.ShaderPass pass in shader.Passes)
        {
            ShaderCompileRequest request = pass.CreateHlslCompileRequest(GraphicsBackend.WebGPU, null, string.Empty);
            ShaderCompileResult result = ShaderCompilerFactory.GetCompiler(GraphicsBackend.WebGPU).Compile(request);
            Assert.True(result.Success, $"{pass.Name}: {result.ErrorMessage}");
        }
    }

    [Fact]
    public void Optional_WebGPU_FXAA_DefaultShader_Compiles()
    {
        Resources.Shader shader = Resources.Shader.LoadDefault(Resources.DefaultShader.FXAA);
        foreach (Rendering.Shaders.ShaderPass pass in shader.Passes)
        {
            ShaderCompileRequest request = pass.CreateHlslCompileRequest(GraphicsBackend.WebGPU, null, string.Empty);
            ShaderCompileResult result = ShaderCompilerFactory.GetCompiler(GraphicsBackend.WebGPU).Compile(request);
            Assert.True(result.Success, $"{pass.Name}: {result.ErrorMessage}");
        }
    }

    [Fact]
    public void Optional_WebGPU_Blit_DefaultShader_Compiles()
    {
        Resources.Shader shader = Resources.Shader.LoadDefault(Resources.DefaultShader.Blit);
        foreach (Rendering.Shaders.ShaderPass pass in shader.Passes)
        {
            ShaderCompileRequest request = pass.CreateHlslCompileRequest(GraphicsBackend.WebGPU, null, string.Empty);
            ShaderCompileResult result = ShaderCompilerFactory.GetCompiler(GraphicsBackend.WebGPU).Compile(request);
            Assert.True(result.Success, $"{pass.Name}: {result.ErrorMessage}");
        }
    }
    [Fact]
    public void Optional_WebGPU_GlobalUniforms_AutoBind_Or_Skip()
    {
        ShaderCompileResult compiled = new RuntimeSlangShaderCompiler().Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.WebGPU,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "struct VSInput { float3 position : POSITION; }; float4 vertexMain(VSInput input) : SV_Position { return float4(input.position, 1); }",
            FragmentSource = "cbuffer GlobalUniforms : register(b0) { float4 Tint; }; float4 fragmentMain() : SV_Target { return Tint; }",
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
        });
        Assert.True(compiled.Success, compiled.ErrorMessage);
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        GraphicsBuffer? previousGlobalBuffer = GetGlobalUniformBuffer();
        using (device)
        {
            try
            {
                device.Initialize(null);
                using ShaderVariant variant = CreateVariant(compiled);
                float[] vertices = [-1f, -1f, 0f, 0f, 1f, 0f, 1f, -1f, 0f];
                Float4 tint = new(0.25f, 0.5f, 0.75f, 1f);
                ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes(vertices.AsSpan());
                ReadOnlySpan<byte> uniformBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref tint, 1));
                using var vertexBuffer = new GraphicsBuffer(BufferType.VertexBuffer, vertexBytes, dynamic: true);
                using var uniformBuffer = new GraphicsBuffer(BufferType.UniformBuffer, uniformBytes, dynamic: true);
                using var color = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
                GraphicsFrameBuffer framebuffer = GraphicsFrameBuffer.CreateDeferred(
                    [new GraphicsFrameBuffer.Attachment { Texture = color }],
                    1,
                    1);
                var format = new VertexFormat([new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3)]);
                using var vertexArray = new GraphicsVertexArray(format, vertexBuffer, null);
                using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-global-uniform-create"))
                {
                    create.EncodeCreateBuffer(vertexBuffer, dynamic: true, vertexBytes);
                    create.EncodeCreateBuffer(uniformBuffer, dynamic: true, uniformBytes);
                    create.EncodeCreateTexture(color);
                    create.EncodeAllocateTexture2D(color, 0, 1, 1, 0, ReadOnlySpan<byte>.Empty);
                    create.EncodeCreateFramebuffer(framebuffer);
                    create.EncodeCreateVertexArray(vertexArray);
                    device.Execute(create, wait: true);
                }

                SetGlobalUniformBuffer(uniformBuffer);
                RasterizerState raster = DisabledDepth();
                using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-global-uniform-draw"))
                {
                    draw.SetRenderTarget(framebuffer);
                    draw.SetViewport(0, 0, 1, 1);
                    draw.DisableScissor();
                    draw.SetShader(variant);
                    draw.SetRasterState(in raster);
                    draw.DrawArrays(vertexArray, Topology.Triangles, 0, 3);
                    device.Execute(draw, wait: true);
                }

                byte[] readback = device.ReadTexture2D(device.Textures[color.Handle], 0);
                Assert.InRange(readback[0], (byte)63, (byte)64);
                Assert.InRange(readback[1], (byte)127, (byte)128);
                Assert.InRange(readback[2], (byte)191, (byte)192);
                Assert.Equal((byte)255, readback[3]);

                using CommandBuffer dispose = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-global-uniform-dispose");
                dispose.EncodeDisposeFramebuffer(framebuffer);
                device.Execute(dispose, wait: true);
            }
            finally
            {
                SetGlobalUniformBuffer(previousGlobalBuffer);
            }
        }
    }

    [Fact]
    public void Optional_WebGPU_UnlitMaterial_Packs_Defaults_And_Overrides_Or_Skip()
    {
        ShaderCompileResult compiled = new RuntimeSlangShaderCompiler().Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.WebGPU,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "struct VSInput { float3 position : POSITION; }; float4 vertexMain(VSInput input) : SV_Position { return float4(input.position, 1); }",
            FragmentSource = "cbuffer UnlitMaterial : register(b2) { float2 _Tiling; float2 _Offset; float4 _MainColor; }; float4 fragmentMain() : SV_Target { return float4(_Tiling.x, _Offset.y, _MainColor.r, _MainColor.a); }",
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
        });
        Assert.True(compiled.Success, compiled.ErrorMessage);
        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            using ShaderVariant variant = CreateVariant(compiled);
            using var shader = CreateUnlitMaterialContractShader();
            using var defaultsMaterial = new Resources.Material(shader);
            using var overridesMaterial = new Resources.Material(shader);
            overridesMaterial.SetVector("_Tiling", new Float2(0.75f, 1f));
            overridesMaterial.SetVector("_Offset", new Float2(0f, 0.25f));
            overridesMaterial.SetColor("_MainColor", new Color(0.5f, 0f, 0f, 0.5f));

            float[] vertices = [-1f, -1f, 0f, 0f, 1f, 0f, 1f, -1f, 0f];
            ReadOnlySpan<byte> vertexBytes = MemoryMarshal.AsBytes(vertices.AsSpan());
            using var vertexBuffer = new GraphicsBuffer(BufferType.VertexBuffer, vertexBytes, dynamic: true);
            using var color = new GraphicsTexture(TextureType.Texture2D, TextureImageFormat.Color4b);
            GraphicsFrameBuffer framebuffer = GraphicsFrameBuffer.CreateDeferred(
                [new GraphicsFrameBuffer.Attachment { Texture = color }],
                2,
                1);
            var format = new VertexFormat([new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3)]);
            using var vertexArray = new GraphicsVertexArray(format, vertexBuffer, null);
            using (CommandBuffer create = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-unlit-material-create"))
            {
                create.EncodeCreateBuffer(vertexBuffer, dynamic: true, vertexBytes);
                create.EncodeCreateTexture(color);
                create.EncodeAllocateTexture2D(color, 0, 2, 1, 0, ReadOnlySpan<byte>.Empty);
                create.EncodeCreateFramebuffer(framebuffer);
                create.EncodeCreateVertexArray(vertexArray);
                device.Execute(create, wait: true);
            }

            RasterizerState raster = DisabledDepth();
            using (CommandBuffer draw = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-unlit-material-draw"))
            {
                draw.SetRenderTarget(framebuffer);
                draw.DisableScissor();
                draw.SetShader(variant);
                draw.SetRasterState(in raster);
                draw.SetMaterialProperties(defaultsMaterial);
                draw.SetViewport(0, 0, 1, 1);
                draw.DrawArrays(vertexArray, Topology.Triangles, 0, 3);
                draw.SetMaterialProperties(overridesMaterial);
                draw.SetViewport(1, 0, 1, 1);
                draw.DrawArrays(vertexArray, Topology.Triangles, 0, 3);
                device.Execute(draw, wait: true);
            }

            byte[] readback = device.ReadTexture2D(device.Textures[color.Handle], 0);
            Assert.InRange(readback[0], (byte)63, (byte)64);
            Assert.InRange(readback[1], (byte)127, (byte)128);
            Assert.InRange(readback[2], (byte)191, (byte)192);
            Assert.Equal((byte)255, readback[3]);
            Assert.InRange(readback[4], (byte)191, (byte)192);
            Assert.InRange(readback[5], (byte)63, (byte)64);
            Assert.InRange(readback[6], (byte)127, (byte)128);
            Assert.InRange(readback[7], (byte)127, (byte)128);

            using CommandBuffer dispose = global::XEngine.Runtime.Graphics.GetCommandBuffer("webgpu-unlit-material-dispose");
            dispose.EncodeDisposeFramebuffer(framebuffer);
            device.Execute(dispose, wait: true);
        }
    }

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

    private static Resources.Shader CreateUnlitMaterialContractShader()
    {
        Rendering.Shaders.ShaderProperty tiling = new(new Float2(0.25f, 1f)) { Name = "_Tiling", DisplayName = "Tiling" };
        Rendering.Shaders.ShaderProperty offset = new(new Float2(0f, 0.5f)) { Name = "_Offset", DisplayName = "Offset" };
        Rendering.Shaders.ShaderProperty color = new(new Color(0.75f, 0f, 0f, 1f)) { Name = "_MainColor", DisplayName = "Tint" };
        return new Resources.Shader("Unlit Contract", [tiling, offset, color], []);
    }

    private static GraphicsBuffer? GetGlobalUniformBuffer() =>
        (GraphicsBuffer?)typeof(Rendering.GlobalUniforms)
            .GetField("s_uniformBuffer", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null);

    private static void SetGlobalUniformBuffer(GraphicsBuffer? buffer) =>
        typeof(Rendering.GlobalUniforms)
            .GetField("s_uniformBuffer", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, buffer);

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
        || message.StartsWith("Failed to request a WebGPU device:", StringComparison.Ordinal);
}
