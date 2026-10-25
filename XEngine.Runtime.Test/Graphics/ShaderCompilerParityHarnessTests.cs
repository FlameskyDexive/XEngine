// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

public sealed class ShaderCompilerParityHarnessTests(ITestOutputHelper output)
{
    private const string ParticleReason =
        "Default Particle omits inactive instancing inputs; SM8 covers active instanced inputs with focused reflection and GPU contracts.";

    private const string ComputeReason =
        "Compute-only SLANGPROGRAM pass; DXC has no compute pipeline path and modern backends always compile compute " +
        "through Slang (ShaderCompilerFactory Auto selects Slang, WebGPU/Metal ignore XENGINE_SHADER_COMPILER). " +
        "Slang compute compile is gated by ShaderCompilerTests.All_Default_Passes_Compile_For_Modern_Backends_When_Dxc_Is_Available.";

    private static readonly ShaderParityKnownGap[] s_knownGaps =
    [
        new("Particle/Particle/default/Direct3D12", ShaderParityFailureCategory.Reflection, ParticleReason),
        new("Particle/Particle/default/Vulkan", ShaderParityFailureCategory.Reflection, ParticleReason),
        // zonezero M9: the additive pass's default (non-instanced) DXC reflection counts the
        // GPU_INSTANCING-only vertex attributes; Slang's preprocessor drops them — same class
        // of reflection disagreement as the base Particle gap above.
        new("Particle/ParticleAdditive/default/Direct3D12", ShaderParityFailureCategory.Reflection, ParticleReason),
        new("Particle/ParticleAdditive/default/Vulkan", ShaderParityFailureCategory.Reflection, ParticleReason),
        new("VirtualGeometryCull/CullCount/default/Direct3D12", ShaderParityFailureCategory.Compile, ComputeReason),
        new("VirtualGeometryCull/CullCount/default/Vulkan", ShaderParityFailureCategory.Compile, ComputeReason),
        new("VirtualGeometryCull/CullPrefix/default/Direct3D12", ShaderParityFailureCategory.Compile, ComputeReason),
        new("VirtualGeometryCull/CullPrefix/default/Vulkan", ShaderParityFailureCategory.Compile, ComputeReason),
        new("VirtualGeometryCull/CullWrite/default/Direct3D12", ShaderParityFailureCategory.Compile, ComputeReason),
        new("VirtualGeometryCull/CullWrite/default/Vulkan", ShaderParityFailureCategory.Compile, ComputeReason),
    ];

    [Fact]
    public void ManifestCoversEveryDefaultPassForBothModernBackends()
    {
        ShaderParityCase[] cases = ShaderCompilerParityManifest.CreateDefaultCases();

        Assert.Equal(160, cases.Length);
        Assert.Equal(43, cases.Select(static parityCase => parityCase.Manifest.SourceAsset).Distinct().Count());
        Assert.Equal(
            80,
            cases.Select(static parityCase => $"{parityCase.Manifest.SourceAsset}/{parityCase.Manifest.Pass}")
                .Distinct()
                .Count());
        Assert.All(cases, static parityCase => Assert.Equal("default", parityCase.Manifest.Variant));
        Assert.Equal(
            80,
            cases.Count(static parityCase => parityCase.Manifest.Backend == GraphicsBackend.Direct3D12));
        Assert.Equal(
            80,
            cases.Count(static parityCase => parityCase.Manifest.Backend == GraphicsBackend.Vulkan));

        string serialized = ShaderCompilerParityHarness.SerializeManifest(cases);
        Assert.Equal(
            serialized,
            ShaderCompilerParityHarness.SerializeManifest(
                ShaderCompilerParityManifest.CreateDefaultCases()));
        using JsonDocument manifest = JsonDocument.Parse(serialized);
        Assert.Equal(160, manifest.RootElement.GetArrayLength());
    }

    [Fact]
    public void ErrorInjectionClassifiesCompileReflectionAndGpuFailures()
    {
        ShaderParityCase parityCase = CreateSyntheticCase();
        ShaderParityCaseResult compile = RunSingle(
            parityCase,
            new StubCompiler(ShaderCompileResult.Fail("injected compile failure")),
            new StubCompiler(SuccessResult(0)));
        Assert.Equal(ShaderParityFailureCategory.Compile, compile.FailureCategory);

        ShaderParityCaseResult reflection = RunSingle(
            parityCase,
            new StubCompiler(SuccessResult(0)),
            new StubCompiler(SuccessResult(1)));
        Assert.Equal(ShaderParityFailureCategory.Reflection, reflection.FailureCategory);

        var gpuHarness = new ShaderCompilerParityHarness(
            new StubCompiler(SuccessResult(0)),
            new StubCompiler(SuccessResult(0)),
            static _ => ShaderParityGpuResult.Fail("injected GPU mismatch"));
        ShaderParityReport gpuReport = gpuHarness.Run([parityCase], [], parityCase);
        Assert.Equal(ShaderParityFailureCategory.Gpu, gpuReport.Results[0].FailureCategory);

        var gpuExceptionHarness = new ShaderCompilerParityHarness(
            new StubCompiler(SuccessResult(0)),
            new StubCompiler(SuccessResult(0)),
            static _ => throw new InvalidOperationException("injected GPU exception"));
        ShaderParityReport gpuExceptionReport = gpuExceptionHarness.Run([parityCase], [], parityCase);
        Assert.Equal(ShaderParityFailureCategory.Gpu, gpuExceptionReport.Results[0].FailureCategory);
        Assert.Contains("injected GPU exception", gpuExceptionReport.Results[0].Diagnostic);
    }

    [Fact]
    public void FullDefaultManifestProducesStructuredParityReport()
    {
        ShaderParityCase[] cases = ShaderCompilerParityManifest.CreateDefaultCases();
        ShaderParityCase benchmarkCase = cases.Single(parityCase =>
            parityCase.Manifest.Id == "Invalid/Invalid/default/Direct3D12");
        var dxc = new DxcShaderCompiler();
        if (dxc.ResolvedDxcPath == null)
            return;

        var harness = new ShaderCompilerParityHarness(
            dxc,
            new RuntimeSlangShaderCompiler());

        ShaderParityReport report = harness.Run(cases, s_knownGaps, benchmarkCase);
        string outputDirectory = ValidationDirectory();
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(
            Path.Combine(outputDirectory, "sm6-manifest.json"),
            ShaderCompilerParityHarness.SerializeManifest(cases));
        File.WriteAllText(
            Path.Combine(outputDirectory, "sm6-parity-report.json"),
            ShaderCompilerParityHarness.SerializeReport(report));

        output.WriteLine(
            $"Parity: {report.Summary.Passed} passed, {report.Summary.Allowlisted} allowlisted, " +
            $"{report.Summary.Failed} failed");
        output.WriteLine(
            $"Failures: compile={report.Summary.CompileFailures}, " +
            $"reflection={report.Summary.ReflectionFailures}, gpu={report.Summary.GpuFailures}");
        output.WriteLine(
            $"Artifacts: DXC={report.Summary.DxcArtifactBytes}, Slang={report.Summary.SlangArtifactBytes}");
        output.WriteLine(
            $"Benchmark DXC cold/warm: {report.Benchmark.Dxc.ColdMilliseconds:F3}/" +
            $"{report.Benchmark.Dxc.WarmMedianMilliseconds:F3} ms");
        output.WriteLine(
            $"Benchmark Slang cold/warm: {report.Benchmark.Slang.ColdMilliseconds:F3}/" +
            $"{report.Benchmark.Slang.WarmMedianMilliseconds:F3} ms");

        Assert.Equal(160, report.Summary.Total);
        Assert.Empty(report.UnusedKnownGaps);
        Assert.True(
            report.Summary.Failed == 0,
            string.Join(
                Environment.NewLine,
                report.Results
                    .Where(static result => result.Status == ShaderParityStatus.Failed)
                    .Select(static result => $"{result.Id} [{result.FailureCategory}]: {result.Diagnostic}")));
    }

    private static ShaderParityCaseResult RunSingle(
        ShaderParityCase parityCase,
        IShaderCompiler dxc,
        IShaderCompiler slang)
    {
        var harness = new ShaderCompilerParityHarness(dxc, slang);
        return harness.Run([parityCase], [], parityCase).Results[0];
    }

    private static ShaderParityCase CreateSyntheticCase()
    {
        ShaderCompileRequest request = new()
        {
            TargetBackend = GraphicsBackend.Direct3D12,
            Language = ShaderLanguage.Hlsl,
            VertexSource = "float4 main() : SV_Position { return 0; }",
            FragmentSource = "float4 main() : SV_Target { return 1; }",
            ShaderName = "Test/Parity",
            PassName = "Main",
            VariantKey = "default",
        };
        ShaderParityExpectedResource[] expected =
            ShaderCompilerParityManifest.FlattenResources(SuccessResult(0).BindingLayout);
        return new ShaderParityCase(
            new ShaderParityManifestEntry(
                "Test/Parity/Main/default/Direct3D12",
                "Test/Parity",
                "Synthetic",
                "Main",
                "default",
                GraphicsBackend.Direct3D12,
                expected),
            request);
    }

    private static ShaderCompileResult SuccessResult(int slot) => new()
    {
        Success = true,
        Format = ShaderBytecodeFormat.Dxil,
        VertexBytecode = [1, 2, 3],
        FragmentBytecode = [4, 5, 6],
        BindingLayout = new ShaderBindingLayout
        {
            Textures = [new ShaderBindingSlot(ShaderBindingKind.Texture, slot, "MainTexture")],
        },
        VertexInputLayout = new ShaderVertexInputLayout(),
    };

    private static string ValidationDirectory() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "Build",
        "Validation",
        "sm6"));

    private sealed class StubCompiler(ShaderCompileResult result) : IShaderCompiler
    {
        public ShaderCompileResult Compile(ShaderCompileRequest request) => result;
    }
}
