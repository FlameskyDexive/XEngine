// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Backends.WebGPU;
using XEngine.Runtime.Backends.WebGPU.Native;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// WG4: WebGPU PSO cache keyed by <see cref="GraphicsPipelineKey"/> plus color/depth formats.
/// GPU-less machines skip with the same availability exceptions as WG1/WG2.
/// </summary>
public class WebGPUPipelineTests
{
    [Fact]
    public void FullscreenPso_CacheHit_Or_Skip()
    {
        ShaderCompileResult compiled = CompileFullscreen();
        Assert.True(compiled.Success, compiled.ErrorMessage);

        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            using var variant = new ShaderVariant(new CompiledShaderBytecode(
                ShaderLanguage.Hlsl,
                ShaderBytecodeFormat.Wgsl,
                compiled.VertexBytecode!,
                compiled.FragmentBytecode!,
                compiled.BindingLayout,
                compiled.VertexInputLayout));
            RasterizerState raster = new()
            {
                DepthTest = false,
                DepthWrite = false,
                CullFace = RasterizerState.PolyFace.None,
            };
            var key = new GraphicsPipelineKey(variant, 0, Topology.Triangles, in raster, index32Bit: false);

            nint first = device.GetOrCreateGraphicsPipeline(key, variant);
            nint second = device.GetOrCreateGraphicsPipeline(key, variant);

            Assert.NotEqual(nint.Zero, first);
            Assert.Equal(first, second);
            Assert.Equal(1, device.PipelineCreateCount);
            Assert.Equal(1, device.PipelineCacheHitCount);
            Console.WriteLine($"WG4 PSO cache hit handle=0x{first:X}; creates={device.PipelineCreateCount}");
        }
    }

    [Fact]
    public void DifferentColorFormat_MissesCache_Or_Skip()
    {
        ShaderCompileResult compiled = CompileFullscreen();
        Assert.True(compiled.Success, compiled.ErrorMessage);

        if (!TryCreateDevice(out WebGPUGraphicsDevice device))
            return;

        using (device)
        {
            device.Initialize(null);
            using var variant = new ShaderVariant(new CompiledShaderBytecode(
                ShaderLanguage.Hlsl,
                ShaderBytecodeFormat.Wgsl,
                compiled.VertexBytecode!,
                compiled.FragmentBytecode!,
                compiled.BindingLayout,
                compiled.VertexInputLayout));
            RasterizerState raster = new()
            {
                DepthTest = false,
                DepthWrite = false,
                CullFace = RasterizerState.PolyFace.None,
            };
            var key = new GraphicsPipelineKey(variant, 0, Topology.Triangles, in raster, index32Bit: false);

            nint rgba = device.GetOrCreateGraphicsPipeline(key, variant, WGPUTextureFormat.RGBA8Unorm);
            nint bgra = device.GetOrCreateGraphicsPipeline(key, variant, WGPUTextureFormat.BGRA8Unorm);

            Assert.NotEqual(nint.Zero, rgba);
            Assert.NotEqual(nint.Zero, bgra);
            Assert.NotEqual(rgba, bgra);
            Assert.Equal(2, device.PipelineCreateCount);
            Assert.Equal(0, device.PipelineCacheHitCount);
        }
    }

    private static ShaderCompileResult CompileFullscreen() =>
        new RuntimeSlangShaderCompiler().Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.WebGPU,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 vertexMain(uint id : SV_VertexID) : SV_Position { float2 p = float2((id << 1) & 2, id & 2); return float4(p * 2 - 1, 0, 1); }",
            FragmentSource = "float4 fragmentMain() : SV_Target { return float4(1, 0, 1, 1); }",
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
        });

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
}
