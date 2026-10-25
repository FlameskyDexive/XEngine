// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class RenderPipelineGrabTests
{
    [Theory]
    [InlineData(GraphicsBackend.Vulkan, TextureImageFormat.Short4)]
    [InlineData(GraphicsBackend.Direct3D12, TextureImageFormat.Short4)]
    [InlineData(GraphicsBackend.Vulkan, TextureImageFormat.Float4)]
    [InlineData(GraphicsBackend.Direct3D12, TextureImageFormat.Float4)]
    public void GpuGrab_PreservesDarkLinearPrecisionAndHdrRange(GraphicsBackend backend, TextureImageFormat format)
    {
        GraphicsBackend? previous = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = backend;
        try
        {
            Graphics.Initialize(debug: false);
            Mesh.GetFullscreenQuad().Dispose();
            Assert.Equal(backend, Graphics.Device!.Backend);
            const string source = """
                Shader "Test/GrabPrecision"
                Pass "Main"
                {
                    GrabTexture "_GrabTexture"
                    Tags { "RenderOrder" = "Transparent" }
                    Cull Off
                    ZWrite Off
                    ZTest Off
                    Blend Off
                    SLANGPROGRAM
                    Vertex
                    {
                        struct VSInput { float3 position : POSITION; };
                        float4 main(VSInput input) : SV_Position { return float4(input.position, 1); }
                    }
                    Fragment
                    {
                        Texture2D _GrabTexture : register(t0);
                        float4 main(float4 pixel : SV_Position) : SV_Target
                        {
                            float4 color = _GrabTexture.Load(int3(uint2(pixel.xy), 0));
                            return float4(color.rgb * 2, color.a);
                        }
                    }
                    ENDSLANG
                }
                """;
            Assert.True(ShaderParser.ParseShader("GrabPrecision.shader", source, _ => null, out Shader? parsed));
            using Shader shader = parsed!;
            using var material = new Material(shader);
            Mesh mesh = Mesh.GetFullscreenQuad();
            using var args = new GraphicsBuffer(BufferType.IndirectBuffer, new byte[20], dynamic: true);
            using var target = new RenderTexture(2, 1, false, [format]);
            using var pipeline = new TestPipeline();
            float[] values = [0.002321982f, 0.002708978f, 0.00393594f, 0.125f, 1.25f, 2.5f, 4f, 0.75f];
            if (format == TextureImageFormat.Short4)
            {
                var halfValues = new Half[values.Length];
                for (int i = 0; i < values.Length; i++) halfValues[i] = (Half)values[i];
                target.MainTexture.SetData<Half>(halfValues.AsMemory());
            }
            else
            {
                target.MainTexture.SetData<float>(values.AsMemory());
            }

            Graphics.BeginFrame();
            using (CommandBuffer cmd = Graphics.GetCommandBuffer("HDR grab precision regression"))
            {
                // Match the render pipeline contract: currentRT is the bound scene target
                // before a transparent draw temporarily switches to its grab target.
                cmd.SetRenderTarget(target.frameBuffer);
                pipeline.DrawRenderables(cmd, [new TestRenderable(material, mesh, 0, args)],
                    "RenderOrder", "Transparent", default, [false], false, target);
                Graphics.Submit(cmd);
            }
            Graphics.EndFrameAndWait();
            Graphics.Device.WaitIdle();
            var actual = new float[values.Length];
            if (format == TextureImageFormat.Short4)
            {
                var halfValues = new Half[values.Length];
                target.MainTexture.GetData<Half>(halfValues.AsMemory());
                for (int i = 0; i < actual.Length; i++) actual[i] = (float)halfValues[i];
            }
            else
            {
                target.MainTexture.GetData<float>(actual.AsMemory());
            }
            for (int i = 0; i < values.Length; i++)
            {
                float expected = values[i] * (i % 4 == 3 ? 1f : 2f);
                Assert.InRange(actual[i], expected - 0.00001f, expected + 0.00001f);
            }
        }
        catch (Exception ex) when (GraphicsTestAvailability.IsBackendUnavailable(ex))
        {
            // Host without this backend's machinery (e.g. macOS without a Vulkan loader) — skip.
            return;
        }
        finally
        {
            RenderTexture.DisposeTemporaryPool();
            Mesh.GetFullscreenQuad().Dispose();
            Graphics.Dispose();
            GraphicsBackendSelection.Preferred = previous;
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void DrawRenderables_CapturesAndBindsOnlyForRequestedPasses(int drawMode, bool wantsGrab)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string name = wantsGrab ? "Distortion" : "Add_CenterGlow";
        using Shader shader = Shader.LoadFromFile(Path.Combine(root, "Packages", "com.xengine.rpgvfx",
            "Runtime", "Shaders", name + ".shader"));
        using var material = new Material(shader);
        using Mesh mesh = Mesh.CreateCube(Float3.One);
        using var args = new GraphicsBuffer(BufferType.IndirectBuffer, new byte[20], dynamic: true);
        using var target = new RenderTexture(41, 23, false, [TextureImageFormat.Color4b]);
        using var pipeline = new TestPipeline();
        var renderable = new TestRenderable(material, mesh, drawMode, args);
        using CommandBuffer cmd = Graphics.GetCommandBuffer("Grab routing regression");
        try
        {
            pipeline.DrawRenderables(cmd, [renderable], "RenderOrder", "Transparent", default,
                [false], false, target);
            var commands = new List<CapturedOp>();
            var owned = new List<PropertyState>();
            CommandBufferDecoder.Decode(cmd, cmd.Name, null, commands, owned);
            CommandOpcode drawOpcode = drawMode switch
            {
                1 => CommandOpcode.DrawIndexedInstanced,
                2 => CommandOpcode.DrawIndexedIndirect,
                _ => CommandOpcode.DrawIndexed,
            };
            int draw = commands.FindIndex(op => op.Opcode == drawOpcode);
            Assert.True(draw >= 0, "The test must reach the actual draw path.");
            int copy = commands.FindIndex(op => op.Opcode == CommandOpcode.BlitFramebuffer);
            int bind = commands.FindIndex(op => op.Opcode == CommandOpcode.SetGlobalTexture && op.UniformName == "_GrabTexture");
            int clear = commands.FindIndex(op => op.Opcode == CommandOpcode.ClearGlobalTexture && op.UniformName == "_GrabTexture");
            if (wantsGrab)
            {
                Assert.True(copy >= 0 && copy < bind && bind < draw && draw < clear,
                    "Grab copy and binding must precede every draw mode, with cleanup after the draw.");
                Assert.Single(commands, op => op.Opcode == CommandOpcode.BlitFramebuffer);
            }
            else
            {
                Assert.Equal(-1, copy);
                Assert.Equal(-1, bind);
                Assert.Equal(-1, clear);
            }
        }
        catch (Exception ex) when (GraphicsTestAvailability.IsBackendUnavailable(ex))
        {
            // Host without this backend's machinery (e.g. macOS without a Vulkan loader) — skip.
            return;
        }
        finally
        {
            RenderTexture.DisposeTemporaryPool();
        }
    }

    private sealed class TestPipeline : RenderPipeline { }

    private sealed class TestRenderable(Material material, Mesh mesh, int drawMode, GraphicsBuffer args) : IRenderable
    {
        private readonly PropertyState _properties = new();
        private readonly InstanceData[] _instances = [new(Float4x4.Identity, Color.White, new Float4(0f, 0f, 0f, 1f))];
        public Material GetMaterial() => material;
        public int GetLayer() => 0;
        public Float3 GetPosition() => Float3.Zero;
        public GraphicsBuffer? GetIndirectDrawArgs() => drawMode == 2 ? args : null;
        public void GetCullingData(out bool isRenderable, out AABB bounds)
        {
            isRenderable = true;
            bounds = default;
        }
        public void GetRenderingData(ViewerData viewer, out PropertyState properties, out Mesh renderMesh,
            out Float4x4 model, out InstanceData[]? instanceData)
        {
            properties = _properties;
            renderMesh = mesh;
            model = Float4x4.Identity;
            instanceData = drawMode == 1 ? _instances : null;
        }
    }
}
