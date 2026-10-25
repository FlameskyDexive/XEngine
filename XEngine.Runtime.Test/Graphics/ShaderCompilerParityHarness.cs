// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test;

internal enum ShaderParityFailureCategory
{
    Compile,
    Reflection,
    Gpu,
}

internal enum ShaderParityStatus
{
    Passed,
    Allowlisted,
    Failed,
}

internal sealed record ShaderParityExpectedResource(
    ShaderBindingKind Kind,
    int Space,
    int Slot,
    int Count,
    string Name,
    ShaderStageMask Stages);

internal sealed record ShaderParityManifestEntry(
    string Id,
    string Shader,
    string SourceAsset,
    string Pass,
    string Variant,
    GraphicsBackend Backend,
    ShaderParityExpectedResource[] ExpectedResources);

internal sealed record ShaderParityKnownGap(
    string Id,
    ShaderParityFailureCategory Category,
    string Reason);

internal sealed record ShaderParityGpuResult(bool Passed, string? Diagnostic)
{
    public static ShaderParityGpuResult Pass() => new(true, null);

    public static ShaderParityGpuResult Fail(string diagnostic) => new(false, diagnostic);
}

internal sealed record ShaderParityCompilerMetrics(
    double ColdMilliseconds,
    double WarmMedianMilliseconds,
    int ArtifactBytes);

internal sealed record ShaderParityBenchmark(
    string CaseId,
    ShaderParityCompilerMetrics Dxc,
    ShaderParityCompilerMetrics Slang);

internal sealed record ShaderParityCaseResult(
    string Id,
    ShaderParityStatus Status,
    ShaderParityFailureCategory? FailureCategory,
    string? Diagnostic,
    string? AllowlistReason,
    double DxcMilliseconds,
    double SlangMilliseconds,
    int DxcArtifactBytes,
    int SlangArtifactBytes);

internal sealed record ShaderParitySummary(
    int Total,
    int Passed,
    int Allowlisted,
    int Failed,
    int CompileFailures,
    int ReflectionFailures,
    int GpuFailures,
    long DxcArtifactBytes,
    long SlangArtifactBytes);

internal sealed record ShaderParityReport(
    int SchemaVersion,
    ShaderParitySummary Summary,
    ShaderParityBenchmark Benchmark,
    ShaderParityCaseResult[] Results,
    ShaderParityKnownGap[] UnusedKnownGaps);

internal sealed record ShaderParityCase(
    ShaderParityManifestEntry Manifest,
    ShaderCompileRequest Request);

internal static class ShaderCompilerParityManifest
{
    private static readonly FieldInfo s_hlslVertexSource = typeof(ShaderPass).GetField(
        "_hlslVertexSource",
        BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_hlslFragmentSource = typeof(ShaderPass).GetField(
        "_hlslFragmentSource",
        BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo s_hlslComputeSource = typeof(ShaderPass).GetField(
        "_hlslComputeSource",
        BindingFlags.NonPublic | BindingFlags.Instance)!;

    public static ShaderParityCase[] CreateDefaultCases()
    {
        var cases = new List<ShaderParityCase>(128);
        GraphicsBackend[] backends = [GraphicsBackend.Direct3D12, GraphicsBackend.Vulkan];

        foreach (DefaultShader shaderId in Enum.GetValues<DefaultShader>())
        {
            Shader shader = Shader.LoadDefault(shaderId);
            foreach (ShaderPass pass in shader.Passes)
            {
                string vertexSource = (string)s_hlslVertexSource.GetValue(pass)!;
                string fragmentSource = (string)s_hlslFragmentSource.GetValue(pass)!;
                string computeSource = (string)s_hlslComputeSource.GetValue(pass)!;
                ShaderBindingLayout expectedLayout = DxcShaderCompiler.ParseBindingLayout(
                    vertexSource,
                    fragmentSource);
                ShaderParityExpectedResource[] expectedResources = FlattenResources(expectedLayout);

                foreach (GraphicsBackend backend in backends)
                {
                    const string variant = "default";
                    string id = $"{shaderId}/{pass.Name}/{variant}/{backend}";
                    var manifest = new ShaderParityManifestEntry(
                        id,
                        shader.Name,
                        $"Assets/Defaults/{shaderId}.shader",
                        pass.Name,
                        variant,
                        backend,
                        expectedResources);
                    var request = new ShaderCompileRequest
                    {
                        TargetBackend = backend,
                        Language = ShaderLanguage.Hlsl,
                        VertexSource = vertexSource,
                        FragmentSource = fragmentSource,
                        ComputeSource = string.IsNullOrEmpty(computeSource) ? null : computeSource,
                        ShaderName = shader.Name,
                        PassName = pass.Name,
                        VariantKey = variant,
                    };
                    cases.Add(new ShaderParityCase(manifest, request));
                }
            }
        }

        return [.. cases];
    }

    public static ShaderParityExpectedResource[] FlattenResources(ShaderBindingLayout? layout)
    {
        if (layout == null)
            return [];

        return layout.Buffers
            .Concat(layout.Textures)
            .Concat(layout.Samplers)
            .Select(static resource => new ShaderParityExpectedResource(
                resource.Kind,
                resource.Space,
                resource.Slot,
                resource.Count,
                resource.Name,
                resource.Stages))
            .ToArray();
    }
}

internal sealed class ShaderCompilerParityHarness(
    IShaderCompiler dxc,
    IShaderCompiler slang,
    Func<ShaderParityCase, ShaderParityGpuResult?>? gpuValidator = null)
{
    public ShaderParityReport Run(
        IReadOnlyList<ShaderParityCase> cases,
        IReadOnlyList<ShaderParityKnownGap> knownGaps,
        ShaderParityCase benchmarkCase)
    {
        var knownById = knownGaps.ToDictionary(static gap => gap.Id, StringComparer.Ordinal);
        var consumedKnownGaps = new HashSet<string>(StringComparer.Ordinal);
        var results = new ShaderParityCaseResult[cases.Count];

        for (int i = 0; i < cases.Count; i++)
            results[i] = RunCase(cases[i], knownById, consumedKnownGaps);

        ShaderParityKnownGap[] unusedKnownGaps = knownGaps
            .Where(gap => !consumedKnownGaps.Contains(gap.Id))
            .OrderBy(static gap => gap.Id, StringComparer.Ordinal)
            .ToArray();
        return new ShaderParityReport(
            1,
            CreateSummary(results),
            MeasureBenchmark(benchmarkCase),
            results,
            unusedKnownGaps);
    }

    public static string SerializeManifest(IReadOnlyList<ShaderParityCase> cases) =>
        JsonSerializer.Serialize(cases.Select(static parityCase => parityCase.Manifest), JsonOptions());

    public static string SerializeReport(ShaderParityReport report) =>
        JsonSerializer.Serialize(report, JsonOptions());

    private ShaderParityCaseResult RunCase(
        ShaderParityCase parityCase,
        IReadOnlyDictionary<string, ShaderParityKnownGap> knownGaps,
        ISet<string> consumedKnownGaps)
    {
        (ShaderCompileResult dxcResult, double dxcMilliseconds) = Compile(dxc, parityCase.Request);
        (ShaderCompileResult slangResult, double slangMilliseconds) = Compile(slang, parityCase.Request);
        int dxcBytes = ArtifactBytes(dxcResult);
        int slangBytes = ArtifactBytes(slangResult);

        if (!dxcResult.Success || !slangResult.Success)
        {
            string diagnostic =
                $"DXC: {(dxcResult.Success ? "passed" : dxcResult.ErrorMessage)}\n" +
                $"Slang: {(slangResult.Success ? "passed" : slangResult.ErrorMessage)}";
            return Classify(
                parityCase.Manifest.Id,
                ShaderParityFailureCategory.Compile,
                diagnostic,
                dxcMilliseconds,
                slangMilliseconds,
                dxcBytes,
                slangBytes,
                knownGaps,
                consumedKnownGaps);
        }

        ShaderParityExpectedResource[] actualResources =
            ShaderCompilerParityManifest.FlattenResources(dxcResult.BindingLayout);
        if (!parityCase.Manifest.ExpectedResources.SequenceEqual(actualResources))
        {
            return Classify(
                parityCase.Manifest.Id,
                ShaderParityFailureCategory.Reflection,
                "DXC reflection no longer matches the manifest expected-resource contract.",
                dxcMilliseconds,
                slangMilliseconds,
                dxcBytes,
                slangBytes,
                knownGaps,
                consumedKnownGaps);
        }

        string reflectionDifference = ShaderReflectionContractComparer.CompareLogical(dxcResult, slangResult);
        if (!string.IsNullOrEmpty(reflectionDifference))
        {
            return Classify(
                parityCase.Manifest.Id,
                ShaderParityFailureCategory.Reflection,
                reflectionDifference,
                dxcMilliseconds,
                slangMilliseconds,
                dxcBytes,
                slangBytes,
                knownGaps,
                consumedKnownGaps);
        }

        ShaderParityGpuResult? gpuResult;
        try
        {
            gpuResult = gpuValidator?.Invoke(parityCase);
        }
        catch (Exception ex)
        {
            return Classify(
                parityCase.Manifest.Id,
                ShaderParityFailureCategory.Gpu,
                ex.ToString(),
                dxcMilliseconds,
                slangMilliseconds,
                dxcBytes,
                slangBytes,
                knownGaps,
                consumedKnownGaps);
        }

        if (gpuResult is { Passed: false })
        {
            return Classify(
                parityCase.Manifest.Id,
                ShaderParityFailureCategory.Gpu,
                gpuResult.Diagnostic ?? "GPU parity failed without a diagnostic.",
                dxcMilliseconds,
                slangMilliseconds,
                dxcBytes,
                slangBytes,
                knownGaps,
                consumedKnownGaps);
        }

        return new ShaderParityCaseResult(
            parityCase.Manifest.Id,
            ShaderParityStatus.Passed,
            null,
            null,
            null,
            dxcMilliseconds,
            slangMilliseconds,
            dxcBytes,
            slangBytes);
    }

    private static ShaderParityCaseResult Classify(
        string id,
        ShaderParityFailureCategory category,
        string diagnostic,
        double dxcMilliseconds,
        double slangMilliseconds,
        int dxcBytes,
        int slangBytes,
        IReadOnlyDictionary<string, ShaderParityKnownGap> knownGaps,
        ISet<string> consumedKnownGaps)
    {
        bool allowlisted = knownGaps.TryGetValue(id, out ShaderParityKnownGap? knownGap) &&
            knownGap.Category == category;
        if (allowlisted)
            consumedKnownGaps.Add(id);

        return new ShaderParityCaseResult(
            id,
            allowlisted ? ShaderParityStatus.Allowlisted : ShaderParityStatus.Failed,
            category,
            diagnostic,
            allowlisted ? knownGap!.Reason : null,
            dxcMilliseconds,
            slangMilliseconds,
            dxcBytes,
            slangBytes);
    }

    private ShaderParityBenchmark MeasureBenchmark(ShaderParityCase benchmarkCase) => new(
        benchmarkCase.Manifest.Id,
        MeasureCompiler(dxc, benchmarkCase.Request),
        MeasureCompiler(slang, benchmarkCase.Request));

    private static ShaderParityCompilerMetrics MeasureCompiler(
        IShaderCompiler compiler,
        ShaderCompileRequest request)
    {
        (ShaderCompileResult coldResult, double coldMilliseconds) = Compile(compiler, request);
        if (!coldResult.Success)
            return new ShaderParityCompilerMetrics(coldMilliseconds, 0, 0);

        var warmSamples = new double[5];
        for (int i = 0; i < warmSamples.Length; i++)
            (_, warmSamples[i]) = Compile(compiler, request);

        Array.Sort(warmSamples);
        return new ShaderParityCompilerMetrics(
            coldMilliseconds,
            warmSamples[warmSamples.Length / 2],
            ArtifactBytes(coldResult));
    }

    private static (ShaderCompileResult Result, double Milliseconds) Compile(
        IShaderCompiler compiler,
        ShaderCompileRequest request)
    {
        long started = Stopwatch.GetTimestamp();
        ShaderCompileResult result = compiler.Compile(request);
        return (result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static int ArtifactBytes(ShaderCompileResult result) =>
        (result.VertexBytecode?.Length ?? 0) +
        (result.FragmentBytecode?.Length ?? 0) +
        (result.ComputeBytecode?.Length ?? 0);

    private static ShaderParitySummary CreateSummary(IReadOnlyList<ShaderParityCaseResult> results) => new(
        results.Count,
        results.Count(static result => result.Status == ShaderParityStatus.Passed),
        results.Count(static result => result.Status == ShaderParityStatus.Allowlisted),
        results.Count(static result => result.Status == ShaderParityStatus.Failed),
        results.Count(static result => result.FailureCategory == ShaderParityFailureCategory.Compile),
        results.Count(static result => result.FailureCategory == ShaderParityFailureCategory.Reflection),
        results.Count(static result => result.FailureCategory == ShaderParityFailureCategory.Gpu),
        results.Sum(static result => (long)result.DxcArtifactBytes),
        results.Sum(static result => (long)result.SlangArtifactBytes));

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
