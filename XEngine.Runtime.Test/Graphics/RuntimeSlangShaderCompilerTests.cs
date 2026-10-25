// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

public sealed class RuntimeSlangShaderCompilerTests(ITestOutputHelper output)
{
    private const string VertexSource = """
#if !XENGINE_SM2
#error XENGINE_SM2 must be defined
#endif
float4 vertexMain(uint id : SV_VertexID) : SV_Position
{
    float2 p = float2((id << 1) & 2, id & 2);
    return float4(p * 2 - 1, 0, 1);
}
""";

    private const string FragmentSource = """
#if !XENGINE_COLOR
#error XENGINE_COLOR must be defined
#endif
float4 fragmentMain() : SV_Target
{
    return float4(1, 0, 1, 1);
}
""";

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, ShaderBytecodeFormat.SpirV)]
    [InlineData(GraphicsBackend.Direct3D12, ShaderBytecodeFormat.Dxil)]
    [InlineData(GraphicsBackend.WebGPU, ShaderBytecodeFormat.Wgsl)]
    [InlineData(GraphicsBackend.WebGL2, ShaderBytecodeFormat.GlslEs300)]
    public void CompilesVertexAndFragmentBytecode(GraphicsBackend backend, ShaderBytecodeFormat expectedFormat)
    {
        // DXIL emission requires the DXC downstream compiler (Windows-only vendor).
        if (backend == GraphicsBackend.Direct3D12 && !OperatingSystem.IsWindows())
            return;

        ShaderCompileResult result = Compile(backend);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expectedFormat, result.Format);
        Assert.NotNull(result.VertexBytecode);
        Assert.NotNull(result.FragmentBytecode);
        Assert.NotEmpty(result.VertexBytecode!);
        Assert.NotEmpty(result.FragmentBytecode!);
        Assert.NotNull(result.BindingLayout);
        Assert.Empty(result.BindingLayout!.Textures);
        Assert.Empty(result.BindingLayout.Buffers);
        Assert.Empty(result.BindingLayout.Samplers);

        if (expectedFormat == ShaderBytecodeFormat.SpirV)
        {
            Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(result.VertexBytecode));
            Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(result.FragmentBytecode));
            Assert.Equal(0, result.VertexBytecode.Length % sizeof(uint));
            Assert.Equal(0, result.FragmentBytecode.Length % sizeof(uint));
        }
        else if (expectedFormat == ShaderBytecodeFormat.Wgsl)
        {
            string vertexWgsl = Encoding.UTF8.GetString(result.VertexBytecode);
            string fragmentWgsl = Encoding.UTF8.GetString(result.FragmentBytecode);
            Assert.Contains("@vertex", vertexWgsl, StringComparison.Ordinal);
            Assert.Contains("@fragment", fragmentWgsl, StringComparison.Ordinal);
        }
        else if (expectedFormat == ShaderBytecodeFormat.GlslEs300)
        {
            string vertexGlsl = Encoding.UTF8.GetString(result.VertexBytecode);
            string fragmentGlsl = Encoding.UTF8.GetString(result.FragmentBytecode);
            Assert.Contains("#version", vertexGlsl, StringComparison.Ordinal);
            Assert.Contains("#version", fragmentGlsl, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("DXBC", Encoding.ASCII.GetString(result.VertexBytecode, 0, 4));
            Assert.Equal("DXBC", Encoding.ASCII.GetString(result.FragmentBytecode, 0, 4));
        }

        output.WriteLine($"Backend: {backend}");
        output.WriteLine($"Format: {result.Format}");
        output.WriteLine($"Vertex bytes: {result.VertexBytecode.Length}");
        output.WriteLine($"Fragment bytes: {result.FragmentBytecode.Length}");
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, ShaderBytecodeFormat.SpirV)]
    [InlineData(GraphicsBackend.Direct3D12, ShaderBytecodeFormat.Dxil)]
    public void CompilesComputeBytecode(GraphicsBackend backend, ShaderBytecodeFormat expectedFormat)
    {
        // DXIL emission requires the DXC downstream compiler (Windows-only vendor).
        if (backend == GraphicsBackend.Direct3D12 && !OperatingSystem.IsWindows())
            return;

        var compiler = new RuntimeSlangShaderCompiler();
        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = backend,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "",
            FragmentSource = "",
            ComputeSource = """
            [numthreads(1, 1, 1)]
            void main() {}
            """,
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(expectedFormat, result.Format);
        Assert.NotNull(result.ComputeBytecode);
        Assert.NotEmpty(result.ComputeBytecode!);
        Assert.Empty(result.VertexBytecode ?? []);
        Assert.Empty(result.FragmentBytecode ?? []);

        if (expectedFormat == ShaderBytecodeFormat.SpirV)
        {
            Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(result.ComputeBytecode));
            Assert.Equal(0, result.ComputeBytecode.Length % sizeof(uint));
        }
        else
        {
            Assert.Equal("DXBC", Encoding.ASCII.GetString(result.ComputeBytecode, 0, 4));
        }

        output.WriteLine($"Backend: {backend}");
        output.WriteLine($"Compute bytes: {result.ComputeBytecode.Length}");
    }

    [Fact]
    public void InvalidVertexSourceReportsStageAndSourcePath()
    {
        var compiler = new RuntimeSlangShaderCompiler();
        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Vulkan,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 vertexMain( : SV_Position { return 0; }",
            FragmentSource = FragmentSource,
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
            ExtraDefines = ["XENGINE_COLOR"],
        });

        Assert.False(result.Success);
        Assert.Contains("vertex", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("xengine_vertex.slang", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneHundredWarmCompilesDoNotGrowProcessHandles()
    {
        for (int i = 0; i < 100; i++)
            CompileAndRequireSuccess(i % 2 == 0 ? GraphicsBackend.Vulkan : GraphicsBackend.Direct3D12);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        int handlesBefore = process.HandleCount;

        for (int i = 0; i < 100; i++)
            CompileAndRequireSuccess(i % 2 == 0 ? GraphicsBackend.Vulkan : GraphicsBackend.Direct3D12);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        process.Refresh();
        int handlesAfter = process.HandleCount;

        output.WriteLine($"Process handles before: {handlesBefore}");
        output.WriteLine($"Process handles after: {handlesAfter}");
        Assert.True(handlesAfter <= handlesBefore, $"Native handle count grew from {handlesBefore} to {handlesAfter}.");
    }

    [Fact]
    public void DxilCreatesD3D12GraphicsPipelineOrReportsUnavailable()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ShaderCompileResult compiled = Compile(GraphicsBackend.Direct3D12);
        Assert.True(compiled.Success, compiled.ErrorMessage);

        try
        {
            using var device = new global::XEngine.Runtime.Backends.D3D12.D3D12GraphicsDevice(new GraphicsDeviceOptions
            {
                Backend = GraphicsBackend.Direct3D12,
                Debug = false,
            });
            device.Initialize(null);

            var bytecode = new CompiledShaderBytecode(
                ShaderLanguage.Hlsl,
                ShaderBytecodeFormat.Dxil,
                compiled.VertexBytecode!,
                compiled.FragmentBytecode!,
                compiled.BindingLayout);
            using var variant = new ShaderVariant(bytecode);
            RasterizerState rasterizer = new()
            {
                DepthTest = false,
                DepthWrite = false,
                CullFace = RasterizerState.PolyFace.None,
            };
            var key = new GraphicsPipelineKey(
                variant,
                0,
                Topology.Triangles,
                in rasterizer,
                index32Bit: false);

            Vortice.Direct3D12.ID3D12PipelineState pipeline = device.GetOrCreateGraphicsPipeline(key, variant);
            Assert.NotEqual(nint.Zero, pipeline.NativePointer);
            output.WriteLine($"D3D12 PSO native pointer: 0x{pipeline.NativePointer:X}");
        }
        catch (Exception ex)
        {
            Assert.True(IsExpectedD3D12Unavailable(ex), $"Unexpected D3D12 PSO failure: {ex}");
            output.WriteLine($"D3D12 unavailable: {ex.Message}");
        }
    }

    private static ShaderCompileResult Compile(GraphicsBackend backend)
    {
        var compiler = new RuntimeSlangShaderCompiler();
        return compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = backend,
            Language = ShaderLanguage.Hlsl,
            VertexSource = VertexSource,
            FragmentSource = FragmentSource,
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
            Keywords = new System.Collections.Generic.Dictionary<string, bool>
            {
                ["XENGINE_SM2"] = true,
                ["XENGINE_DISABLED"] = false,
            },
            ExtraDefines = ["XENGINE_COLOR", "XENGINE_SM2"],
        });
    }

    private static void CompileAndRequireSuccess(GraphicsBackend backend)
    {
        ShaderCompileResult result = Compile(backend);
        Assert.True(result.Success, result.ErrorMessage);
    }

    private static bool IsExpectedD3D12Unavailable(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is PlatformNotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
            {
                return true;
            }

            if (current is InvalidOperationException
                && (current.Message.StartsWith("Failed to create Direct3D 12 device.", StringComparison.Ordinal)
                    || current.Message.StartsWith("Failed to create DXGI factory:", StringComparison.Ordinal)
                    || current.Message.Equals("No Direct3D 12-capable DXGI adapter was found.", StringComparison.Ordinal)
                    || current.Message.StartsWith("Failed to create D3D12 device:", StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
