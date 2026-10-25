// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

/// <summary>
/// S1: Metal as a shader artifact target. Artifacts carry MetalLib bytes. Windows has no Xcode
/// metallib toolchain, so compile tests skip; format mapping and illegal combinations always run.
/// </summary>
[Collection(ShaderCompilerEnvironmentCollection.Name)]
public class MetalShaderTargetTests
{
    [Fact]
    public void Metal_MapsToMetalLibAndCompilesThroughSlang()
    {
        Assert.True(ShaderBytecodeFormats.IsArtifactBackend(GraphicsBackend.Metal));
        Assert.Equal(ShaderBytecodeFormat.MetalLib, ShaderBytecodeFormats.GetExpected(GraphicsBackend.Metal));
        Assert.Equal(ShaderCompilerKind.Slang, ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.Metal));
        Assert.True(ShaderCompilerFactory.RequiresHlsl(GraphicsBackend.Metal));
        Assert.Equal("metal", RuntimeSlangShaderCompiler.GetTargetProfile(GraphicsBackend.Metal));
    }

    [Theory]
    [InlineData(ShaderTargetEnvironment.Default, "metal")]
    [InlineData(ShaderTargetEnvironment.MacOS, "metal")]
    [InlineData(ShaderTargetEnvironment.IOS, "metal-ios")]
    [InlineData(ShaderTargetEnvironment.IOSSimulator, "metal-ios-simulator")]
    public void MetalArtifactProfileSeparatesPlatformEnvironments(
        ShaderTargetEnvironment environment,
        string expectedProfile)
    {
        Assert.Equal(
            expectedProfile,
            RuntimeSlangShaderCompiler.GetArtifactTargetProfile(GraphicsBackend.Metal, environment));
    }

    [Fact]
    public void IOSDeviceAndSimulatorUseDistinctMetalTargetTriples()
    {
        Assert.Equal(
            "air64-apple-ios16.0",
            RuntimeSlangShaderCompiler.GetMetalTargetTriple(ShaderTargetEnvironment.IOS));
        Assert.Equal(
            "air64-apple-ios16.0-simulator",
            RuntimeSlangShaderCompiler.GetMetalTargetTriple(ShaderTargetEnvironment.IOSSimulator));
        Assert.Null(RuntimeSlangShaderCompiler.GetMetalTargetTriple(ShaderTargetEnvironment.MacOS));
    }

    [Fact]
    public void Metal_IgnoresAnExplicitDxcSelection()
    {
        string? previous = Environment.GetEnvironmentVariable("XENGINE_SHADER_COMPILER");
        try
        {
            Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "dxc");

            Assert.Equal(ShaderCompilerKind.Dxc, ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.Vulkan));
            Assert.Equal(ShaderCompilerKind.Slang, ShaderCompilerFactory.GetSelectedCompilerKind(GraphicsBackend.Metal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", previous);
        }
    }

    [Fact]
    public void Artifact_RejectsBytecodeThatIsNotMetalLib()
    {
        var ex = Assert.Throws<ArgumentException>(() => CreateArtifact(GraphicsBackend.Metal, ShaderBytecodeFormat.SpirV));
        Assert.Contains("MetalLib", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Artifact_RejectsMetalLibForABackendThatCannotReadIt()
    {
        Assert.Throws<ArgumentException>(() => CreateArtifact(GraphicsBackend.Vulkan, ShaderBytecodeFormat.MetalLib));
    }

    [Fact]
    public void Artifact_AcceptsMetalLibForMetal()
    {
        ShaderArtifact artifact = CreateArtifact(GraphicsBackend.Metal, ShaderBytecodeFormat.MetalLib);

        Assert.Equal(GraphicsBackend.Metal, artifact.Backend);
        Assert.Equal(ShaderBytecodeFormat.MetalLib, artifact.Bytecode.Format);
    }

    [RequiresAppleMetalCompilerFact]
    public void FactoryCompilesHlslToMetalLib()
    {
        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(GraphicsBackend.Metal);
        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Metal,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 vertexMain(uint id : SV_VertexID) : SV_Position { float2 p = float2((id << 1) & 2, id & 2); return float4(p * 2 - 1, 0, 1); }",
            FragmentSource = "float4 fragmentMain() : SV_Target { return float4(1, 0, 1, 1); }",
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
            ShaderName = "Test/Metal",
            PassName = "Pass",
        });

        Assert.True(result.Success, $"Unexpected MetalLib compile failure: {result.ErrorMessage}");
        Assert.Equal(ShaderBytecodeFormat.MetalLib, result.Format);
        Assert.NotNull(result.VertexBytecode);
        Assert.NotNull(result.FragmentBytecode);
        Assert.NotEmpty(result.VertexBytecode);
        Assert.NotEmpty(result.FragmentBytecode);
    }

    [RequiresAppleMetalCompilerFact]
    public void MetalCompactsSparseHighSamplerRegistersBeforeAppleCompilation()
    {
        var fragmentSource = new StringBuilder();
        var samples = new StringBuilder();
        for (int i = 0; i < 12; i++)
        {
            fragmentSource.AppendLine($"Texture2D Texture{i} : register(t{i});");
            fragmentSource.AppendLine($"SamplerState Sampler{i} : register(s{i});");
            if (samples.Length > 0)
                samples.Append(" + ");
            samples.Append($"Texture{i}.Sample(Sampler{i}, uv)");
        }
        fragmentSource.AppendLine("Texture2D ShadowAtlas : register(t24);");
        fragmentSource.AppendLine("SamplerState ShadowAtlasSampler : register(s24);");
        fragmentSource.AppendLine("Texture2D ScreenShadow : register(t29);");
        fragmentSource.AppendLine("SamplerState ScreenShadowSampler : register(s29);");
        samples.Append(" + ShadowAtlas.Sample(ShadowAtlasSampler, uv) + ScreenShadow.Sample(ScreenShadowSampler, uv)");
        fragmentSource.AppendLine($"float4 fragmentMain(float2 uv : TEXCOORD0) : SV_Target {{ return {samples}; }}");

        ShaderCompileResult result = new RuntimeSlangShaderCompiler().Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Metal,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 vertexMain(uint id : SV_VertexID) : SV_Position { return float4(0, 0, 0, 1); }",
            FragmentSource = fragmentSource.ToString(),
            ComputeSource = "Texture2D<float4> ComputeTexture : register(t38); " +
                "SamplerState ComputeSampler : register(s38); RWTexture2D<float4> ComputeOutput : register(u35); " +
                "[numthreads(1, 1, 1)] void computeMain(uint3 id : SV_DispatchThreadID) " +
                "{ ComputeOutput[id.xy] = ComputeTexture.SampleLevel(ComputeSampler, float2(0, 0), 0); }",
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
            ComputeEntryPoint = "computeMain",
            ShaderName = "Test/Metal/SparseSamplers",
            PassName = "Pass",
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("MTLB", Encoding.ASCII.GetString(result.FragmentBytecode!, 0, 4));
        for (int i = 0; i < 12; i++)
            Assert.Equal(i, Assert.Single(result.BindingLayout!.Samplers, slot => slot.Name == $"Sampler{i}").PhysicalBinding);
        Assert.Equal(12, Assert.Single(result.BindingLayout!.Samplers, slot => slot.Name == "ShadowAtlasSampler").PhysicalBinding);
        Assert.Equal(13, Assert.Single(result.BindingLayout.Samplers, slot => slot.Name == "ScreenShadowSampler").PhysicalBinding);
        Assert.Equal(38, Assert.Single(result.BindingLayout.Samplers, slot => slot.Name == "ComputeSampler").Slot);
        Assert.Equal(0, Assert.Single(result.BindingLayout.Samplers, slot => slot.Name == "ComputeSampler").PhysicalBinding);
    }

    [RequiresIosMetalToolchainTheory]
    [InlineData(ShaderTargetEnvironment.IOS, "iphoneos", "Platform: IOS")]
    [InlineData(ShaderTargetEnvironment.IOSSimulator, "iphonesimulator", "Platform: IOS (SIMULATOR)")]
    public void FactoryCompilesHlslToApplePlatformMetalLib(
        ShaderTargetEnvironment targetEnvironment,
        string sdkName,
        string expectedPlatform)
    {
        IShaderCompiler compiler = ShaderCompilerFactory.GetCompiler(GraphicsBackend.Metal);
        ShaderCompileResult result = compiler.Compile(new ShaderCompileRequest
        {
            TargetBackend = GraphicsBackend.Metal,
            TargetEnvironment = targetEnvironment,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 vertexMain(uint id : SV_VertexID) : SV_Position { float2 p = float2((id << 1) & 2, id & 2); return float4(p * 2 - 1, 0, 1); }",
            FragmentSource = "Texture2D<float4> HighTexture : register(t34); SamplerState HighSampler : register(s34); float4 fragmentMain(float2 uv : TEXCOORD0) : SV_Target { return HighTexture.Sample(HighSampler, uv); }",
            ComputeSource = "Texture2D<float4> ComputeTexture : register(t39); SamplerState ComputeSampler : register(s39); " +
                "RWTexture2D<float4> HighOutput : register(u35); [numthreads(1, 1, 1)] " +
                "void computeMain(uint3 id : SV_DispatchThreadID) { " +
                "HighOutput[id.xy] = ComputeTexture.SampleLevel(ComputeSampler, float2(0, 0), 0); }",
            VertexEntryPoint = "vertexMain",
            FragmentEntryPoint = "fragmentMain",
            ComputeEntryPoint = "computeMain",
            ShaderName = $"Test/Metal/{targetEnvironment}",
            PassName = "Pass",
        });

        Assert.True(result.Success, $"Unexpected {targetEnvironment} MetalLib compile failure: {result.ErrorMessage}");
        Assert.Equal(ShaderBytecodeFormat.MetalLib, result.Format);
        Assert.NotNull(result.VertexBytecode);
        Assert.NotNull(result.FragmentBytecode);
        Assert.NotNull(result.ComputeBytecode);
        Assert.NotEmpty(result.VertexBytecode);
        Assert.NotEmpty(result.FragmentBytecode);
        Assert.NotEmpty(result.ComputeBytecode);

        AssertMetalLibTarget(result.VertexBytecode, sdkName, expectedPlatform);
        AssertMetalLibTarget(result.FragmentBytecode, sdkName, expectedPlatform);
        AssertMetalLibTarget(result.ComputeBytecode, sdkName, expectedPlatform);

        ShaderBindingLayout bindings = Assert.IsType<ShaderBindingLayout>(result.BindingLayout);
        ShaderBindingSlot texture = Assert.Single(bindings.Textures, x => x.Name == "HighTexture");
        ShaderBindingSlot sampler = Assert.Single(bindings.Samplers, x => x.Name == "HighSampler");
        ShaderBindingSlot computeSampler = Assert.Single(bindings.Samplers, x => x.Name == "ComputeSampler");
        ShaderBindingSlot output = Assert.Single(bindings.Textures, x => x.Name == "HighOutput");
        Assert.Equal(34, texture.Slot);
        Assert.Equal(0, texture.PhysicalBinding);
        Assert.Equal(34, sampler.Slot);
        Assert.Equal(0, sampler.PhysicalBinding);
        Assert.Equal(39, computeSampler.Slot);
        Assert.Equal(0, computeSampler.PhysicalBinding);
        Assert.Equal(35, output.Slot);
        Assert.Equal(0, output.PhysicalBinding);
    }

    internal static bool CanInspectAppleMetalToolchain(string sdkName)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/xcrun"))
            return false;

        bool hasTools = TryRunXcrun(sdkName, ["--find", "metal"], out string metalPath)
            && File.Exists(metalPath.Trim())
            && TryRunXcrun(sdkName, ["--find", "metallib"], out string metallibPath)
            && File.Exists(metallibPath.Trim())
            && TryRunXcrun(sdkName, ["--find", "metal-objdump"], out string objdumpPath)
            && File.Exists(objdumpPath.Trim());
        if (!hasTools)
            return false;

        if (sdkName is "macosx")
            return true;

        return TryRunXcrun(sdkName, ["--show-sdk-path"], out string sdkPath)
            && Directory.Exists(sdkPath.Trim());
    }

    internal static bool CanCompileWithAppleMetalToolchain()
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return false;

        bool hasMetal = false;
        bool hasMetallib = false;
        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            hasMetal |= File.Exists(Path.Combine(directory, "metal"));
            hasMetallib |= File.Exists(Path.Combine(directory, "metallib"));
            if (hasMetal && hasMetallib)
                return true;
        }
        return false;
    }

    private static void AssertMetalLibTarget(byte[] bytecode, string sdkName, string expectedPlatform)
    {
        Assert.True(bytecode.Length >= 4);
        Assert.Equal("MTLB", Encoding.ASCII.GetString(bytecode, 0, 4));

        string directory = Path.Combine(Path.GetTempPath(), "xengine-metal-target-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string libraryPath = Path.Combine(directory, "shader.metallib");
            File.WriteAllBytes(libraryPath, bytecode);
            Assert.True(
                TryRunXcrun(sdkName, ["metal-objdump", "--metallib", "--all-headers", libraryPath], out string header),
                "metal-objdump could not read the generated MetalLib.");
            Assert.Contains(expectedPlatform, header, StringComparison.Ordinal);
            Assert.Contains("PlatformMajor: 16", header, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static bool TryRunXcrun(string sdkName, string[] arguments, out string output)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/xcrun")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--sdk");
        startInfo.ArgumentList.Add(sdkName);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                output = string.Empty;
                return false;
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data != null)
                    stdout.AppendLine(eventArgs.Data);
            };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data != null)
                    stderr.AppendLine(eventArgs.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                output = "xcrun timed out.";
                return false;
            }
            process.WaitForExit();
            output = process.ExitCode == 0 ? stdout.ToString() : stderr.ToString();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            output = ex.Message;
            return false;
        }
    }

    private static ShaderArtifact CreateArtifact(GraphicsBackend backend, ShaderBytecodeFormat format)
    {
        var request = new ShaderCompileRequest
        {
            TargetBackend = backend,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
            FragmentSource = "float4 main() : SV_Target { return 1; }",
            ShaderName = "Test/Shader",
            PassName = "Pass",
            VariantKey = "",
        };

        return new ShaderArtifact(
            backend,
            ShaderCompilerKind.Slang,
            "Slang 2026.12",
            ShaderArtifactKey.ComputeSourceHash(request),
            RuntimeSlangShaderCompiler.GetTargetProfile(backend),
            ShaderArtifact.DefaultMatrixLayout,
            "Test/Shader",
            "Pass",
            "",
            "main",
            "main",
            [],
            "s1-test",
            new CompiledShaderBytecode(ShaderLanguage.Hlsl, format, [1, 2, 3, 4], [5, 6, 7, 8]));
    }
}

public sealed class RequiresAppleMetalToolchainFactAttribute : FactAttribute
{
    public RequiresAppleMetalToolchainFactAttribute(string sdkName)
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "Requires macOS and the Apple Metal toolchain.";
        else if (!MetalShaderTargetTests.CanInspectAppleMetalToolchain(sdkName))
            Skip = $"Requires the Apple Metal toolchain for SDK '{sdkName}'.";
    }
}

public sealed class RequiresAppleMetalCompilerFactAttribute : FactAttribute
{
    public RequiresAppleMetalCompilerFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "Requires macOS and the Apple Metal compiler/linker.";
        else if (!MetalShaderTargetTests.CanCompileWithAppleMetalToolchain())
            Skip = "Requires the Apple Metal compiler/linker on PATH.";
    }
}

public sealed class RequiresIosMetalToolchainTheoryAttribute : TheoryAttribute
{
    public RequiresIosMetalToolchainTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS()
            || !MetalShaderTargetTests.CanInspectAppleMetalToolchain("iphoneos")
            || !MetalShaderTargetTests.CanInspectAppleMetalToolchain("iphonesimulator"))
            Skip = "Requires macOS, the Apple Metal toolchain, and both iOS SDKs.";
    }
}
