// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;
using XEngine.Runtime.Backends.Metal;
using XEngine.Runtime.Backends.Metal.Native;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// M3: Metal PSO cache keyed by <see cref="GraphicsPipelineKey"/> plus color/depth formats.
/// Runtime MSL via newLibraryWithSource is test-only; shipping artifacts stay S1 MetalLib.
/// Non-macOS hosts are reported as skipped; device failures on macOS fail the test.
/// </summary>
public class MetalPipelineTests
{
    private const string FullscreenMsl = """
#include <metal_stdlib>
using namespace metal;
vertex float4 vertexMain(uint id [[vertex_id]]) {
    float2 p = float2(float((id << 1) & 2u), float(id & 2u));
    return float4(p * 2.0 - 1.0, 0.0, 1.0);
}
fragment float4 fragmentMain() {
    return float4(1.0, 0.0, 1.0, 1.0);
}
""";

    private const string InstancedMsl = """
#include <metal_stdlib>
using namespace metal;
struct VertexIn {
    float3 position [[attribute(0)]];
    float4 instanceModel0 [[attribute(8)]];
};
vertex float4 vertexMain(VertexIn input [[stage_in]]) {
    return float4(input.position + input.instanceModel0.xyz, 1.0);
}
fragment float4 fragmentMain() {
    return float4(1.0, 0.0, 1.0, 1.0);
}
""";

    [MacOSMetalFact]
    public void FullscreenPso_CacheHit()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using var variant = CreateDummyVariant();
            RasterizerState raster = new()
            {
                DepthTest = false,
                DepthWrite = false,
                CullFace = RasterizerState.PolyFace.None,
            };
            var key = new GraphicsPipelineKey(variant, 0, Topology.Triangles, in raster, index32Bit: false);

            nint library = device.NewLibraryFromSource(FullscreenMsl);
            nint vertex = MetalNative.NewFunction(library, "vertexMain");
            nint fragment = MetalNative.NewFunction(library, "fragmentMain");
            try
            {
                nint first = device.GetOrCreateGraphicsPipeline(key, vertex, fragment);
                nint second = device.GetOrCreateGraphicsPipeline(key, vertex, fragment);

                Assert.NotEqual(nint.Zero, first);
                Assert.Equal(first, second);
                Assert.Equal(1, device.PipelineCreateCount);
                Assert.Equal(1, device.PipelineCacheHitCount);
            }
            finally
            {
                MetalNative.Release(fragment);
                MetalNative.Release(vertex);
                MetalNative.Release(library);
            }
        }
    }

    [MacOSMetalFact]
    public void DifferentColorFormat_MissesCache()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using var variant = CreateDummyVariant();
            RasterizerState raster = new()
            {
                DepthTest = false,
                DepthWrite = false,
                CullFace = RasterizerState.PolyFace.None,
            };
            var key = new GraphicsPipelineKey(variant, 0, Topology.Triangles, in raster, index32Bit: false);

            nint library = device.NewLibraryFromSource(FullscreenMsl);
            nint vertex = MetalNative.NewFunction(library, "vertexMain");
            nint fragment = MetalNative.NewFunction(library, "fragmentMain");
            try
            {
                nint rgba = device.GetOrCreateGraphicsPipeline(key, vertex, fragment, MTLPixelFormat.RGBA8Unorm);
                nint bgra = device.GetOrCreateGraphicsPipeline(key, vertex, fragment, MTLPixelFormat.BGRA8Unorm);

                Assert.NotEqual(nint.Zero, rgba);
                Assert.NotEqual(nint.Zero, bgra);
                Assert.NotEqual(rgba, bgra);
                Assert.Equal(2, device.PipelineCreateCount);
                Assert.Equal(0, device.PipelineCacheHitCount);
            }
            finally
            {
                MetalNative.Release(fragment);
                MetalNative.Release(vertex);
                MetalNative.Release(library);
            }
        }
    }

    [MacOSMetalFact]
    public void DepthOnlyPipeline_HasNoColorAttachment()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            using var variant = CreateDummyVariant();
            RasterizerState raster = new()
            {
                DepthTest = true,
                DepthWrite = true,
                CullFace = RasterizerState.PolyFace.None,
            };
            var key = new GraphicsPipelineKey(variant, 0, Topology.Triangles, in raster, index32Bit: false);

            nint library = device.NewLibraryFromSource(FullscreenMsl);
            nint vertex = MetalNative.NewFunction(library, "vertexMain");
            nint fragment = MetalNative.NewFunction(library, "fragmentMain");
            try
            {
                var depthOnly = new MetalRenderTargetFormats(
                    Array.Empty<MTLPixelFormat>(), MTLPixelFormat.Depth32Float);
                nint pipeline = device.GetOrCreateGraphicsPipeline(key, vertex, fragment, depthOnly);

                Assert.NotEqual(nint.Zero, pipeline);
                Assert.Equal(1, device.PipelineCreateCount);
            }
            finally
            {
                MetalNative.Release(fragment);
                MetalNative.Release(vertex);
                MetalNative.Release(library);
            }
        }
    }

    [MacOSMetalFact]
    public void InstancedVertexDescriptor_DeclaresInstanceAttributes()
    {
        var device = new MetalGraphicsDevice(new GraphicsDeviceOptions { Backend = GraphicsBackend.Metal });

        using (device)
        {
            device.Initialize(null);
            var meshFormat = new VertexFormat([
                new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 3),
            ]);
            var instanceFormat = new VertexFormat([
                new((VertexFormat.VertexSemantic)8, VertexFormat.VertexType.Float, 4, divisor: 1),
            ]);
            var vertexInputs = new ShaderVertexInputLayout
            {
                Inputs =
                [
                    new ShaderVertexInputSlot((uint)VertexFormat.VertexSemantic.Position, 0),
                    new ShaderVertexInputSlot(8, 8),
                ],
            };

            nint library = device.NewLibraryFromSource(InstancedMsl);
            nint vertex = MetalNative.NewFunction(library, "vertexMain");
            nint fragment = MetalNative.NewFunction(library, "fragmentMain");
            try
            {
                nint descriptor = device.GetOrCreateVertexDescriptor_NoLock(
                    meshFormat,
                    instanceFormat,
                    vertexInputs,
                    shaderVariantId: 701);
                Assert.Equal(
                    descriptor,
                    device.GetOrCreateVertexDescriptor_NoLock(
                        meshFormat,
                        instanceFormat,
                        vertexInputs,
                        shaderVariantId: 701));

                RasterizerState raster = new()
                {
                    DepthTest = false,
                    DepthWrite = false,
                    CullFace = RasterizerState.PolyFace.None,
                };
                nint pipeline = MetalNative.NewRenderPipelineState(
                    device.NativeDevice,
                    vertex,
                    fragment,
                    in raster,
                    MTLPixelFormat.RGBA8Unorm,
                    MTLPixelFormat.Invalid,
                    descriptor);
                Assert.NotEqual(nint.Zero, pipeline);
                MetalNative.Release(pipeline);
            }
            finally
            {
                MetalNative.Release(fragment);
                MetalNative.Release(vertex);
                MetalNative.Release(library);
            }
        }
    }

    private static ShaderVariant CreateDummyVariant() =>
        new(new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            ShaderBytecodeFormat.MetalLib,
            [1],
            [1]));


}
