// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

namespace XEngine.Runtime.RHI.Shaders;

#if XENGINE_MOBILE && !XENGINE_WEB
/// <summary>Mobile players consume build-time artifacts and never compile shader source.</summary>
public static class ShaderCompilerFactory
{
    private static readonly IShaderCompiler s_offlineOnly = new OfflineOnlyShaderCompiler();

    public static IShaderCompiler GetCompiler(GraphicsBackend backend, string? shaderName = null) =>
        s_offlineOnly;

    public static ShaderCompilerMode GetConfiguredMode() => ShaderCompilerMode.Auto;

    public static ShaderCompilerKind GetSelectedCompilerKind(
        GraphicsBackend backend,
        string? shaderName = null) => ShaderCompilerKind.Unknown;

    internal static bool IsDxcFallbackAllowed(string? shaderName) => false;

    public static ShaderCompilerTelemetry GetTelemetry() => default;

    internal static void ResetTelemetryForTests() { }

    public static bool RequiresHlsl(GraphicsBackend backend) =>
        backend is GraphicsBackend.Vulkan
            or GraphicsBackend.Direct3D12
            or GraphicsBackend.WebGPU
            or GraphicsBackend.Metal
            or GraphicsBackend.WebGL2;

    private sealed class OfflineOnlyShaderCompiler : IShaderCompiler
    {
        public ShaderCompileResult Compile(ShaderCompileRequest request) => ShaderCompileResult.Fail(
            "Runtime shader compilation is disabled on this mobile target. Package the required Vulkan artifact during the Player build.");
    }
}
#elif XENGINE_WEB
/// <summary>
/// Browser / mini-game WebGL2: compile authored GLSL at runtime. Slang/DXC stay off
/// because those compilers are not in the WeChat/Douyin wasm pack.
/// </summary>
public static class ShaderCompilerFactory
{
    private static readonly GlslShaderCompiler s_glsl = new();

    public static IShaderCompiler GetCompiler(GraphicsBackend backend, string? shaderName = null) =>
        s_glsl;

    public static ShaderCompilerMode GetConfiguredMode() => ShaderCompilerMode.Auto;

    public static ShaderCompilerKind GetSelectedCompilerKind(
        GraphicsBackend backend,
        string? shaderName = null) =>
        backend is GraphicsBackend.OpenGL
            or GraphicsBackend.Null
            or GraphicsBackend.Auto
            or GraphicsBackend.WebGL2
            ? ShaderCompilerKind.Glsl
            : ShaderCompilerKind.Unknown;

    internal static bool IsDxcFallbackAllowed(string? shaderName) => false;

    public static ShaderCompilerTelemetry GetTelemetry() => default;

    internal static void ResetTelemetryForTests() { }

    public static bool RequiresHlsl(GraphicsBackend backend) =>
        backend is GraphicsBackend.Vulkan
            or GraphicsBackend.Direct3D12
            or GraphicsBackend.WebGPU
            or GraphicsBackend.Metal;
}
#else
/// <summary>Selects an <see cref="IShaderCompiler"/> for a <see cref="GraphicsBackend"/>.</summary>
public static class ShaderCompilerFactory
{
    private static readonly GlslShaderCompiler s_glsl = new();
    private static readonly DxcShaderCompiler s_dxc = new();
    private static readonly RuntimeSlangShaderCompiler s_slang = new();
    private static readonly IShaderCompiler s_selectedDxc = new SelectedShaderCompiler(s_dxc, ShaderCompilerKind.Dxc);
    private static readonly IShaderCompiler s_selectedSlang = new SelectedShaderCompiler(s_slang, ShaderCompilerKind.Slang);
    private static readonly IShaderCompiler s_autoSlang = new SelectedShaderCompiler(
        s_slang,
        ShaderCompilerKind.Slang,
        s_dxc,
        ShaderCompilerKind.Dxc);
    private static long s_slangCompileAttempts;
    private static long s_dxcCompileAttempts;
    private static long s_successfulRequests;
    private static long s_failedRequests;
    private static long s_fallbackCount;

    /// <summary>
    /// OpenGL / Null / Auto use GLSL. Vulkan and D3D12 honor
    /// <c>XENGINE_SHADER_COMPILER=auto|dxc|slang</c>. WebGPU, Metal, and WebGL2 always use Slang
    /// because DXC cannot emit WGSL, MetalLib, or GLSL ES.
    /// </summary>
    public static IShaderCompiler GetCompiler(GraphicsBackend backend, string? shaderName = null)
    {
        return backend switch
        {
            GraphicsBackend.Vulkan or GraphicsBackend.Direct3D12 =>
                GetModernCompiler(GetConfiguredMode(), shaderName),
            // DXC cannot emit WGSL, MetalLib, or GLSL ES, so these backends ignore XENGINE_SHADER_COMPILER.
            GraphicsBackend.WebGPU or GraphicsBackend.Metal or GraphicsBackend.WebGL2 => s_selectedSlang,
            GraphicsBackend.OpenGL or GraphicsBackend.Null or GraphicsBackend.Auto => s_glsl,
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown graphics backend."),
        };
    }

    public static ShaderCompilerMode GetConfiguredMode()
    {
        string? value = Environment.GetEnvironmentVariable("XENGINE_SHADER_COMPILER");
        if (string.IsNullOrWhiteSpace(value))
            return ShaderCompilerMode.Auto;

        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => ShaderCompilerMode.Auto,
            "dxc" => ShaderCompilerMode.Dxc,
            "slang" => ShaderCompilerMode.Slang,
            _ => throw new InvalidOperationException(
                $"Unknown XENGINE_SHADER_COMPILER value '{value}'. Expected auto, dxc, or slang."),
        };
    }

    public static ShaderCompilerKind GetSelectedCompilerKind(GraphicsBackend backend, string? shaderName = null)
    {
        if (backend is GraphicsBackend.Vulkan or GraphicsBackend.Direct3D12)
        {
            return GetConfiguredMode() switch
            {
                ShaderCompilerMode.Dxc => ShaderCompilerKind.Dxc,
                ShaderCompilerMode.Slang => ShaderCompilerKind.Slang,
                _ => ShaderCompilerKind.Slang,
            };
        }

        if (backend is GraphicsBackend.WebGPU or GraphicsBackend.Metal or GraphicsBackend.WebGL2)
            return ShaderCompilerKind.Slang;

        return backend is GraphicsBackend.OpenGL or GraphicsBackend.Null or GraphicsBackend.Auto
            ? ShaderCompilerKind.Glsl
            : throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown graphics backend.");
    }

    internal static bool IsDxcFallbackAllowed(string? shaderName) => false;

    private static IShaderCompiler GetModernCompiler(ShaderCompilerMode mode, string? shaderName) => mode switch
    {
        ShaderCompilerMode.Dxc => s_selectedDxc,
        ShaderCompilerMode.Slang => s_selectedSlang,
        ShaderCompilerMode.Auto when IsDxcFallbackAllowed(shaderName) => s_autoSlang,
        ShaderCompilerMode.Auto => s_selectedSlang,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown shader compiler mode."),
    };

    public static ShaderCompilerTelemetry GetTelemetry() => new(
        Interlocked.Read(ref s_slangCompileAttempts),
        Interlocked.Read(ref s_dxcCompileAttempts),
        Interlocked.Read(ref s_successfulRequests),
        Interlocked.Read(ref s_failedRequests),
        Interlocked.Read(ref s_fallbackCount));

    internal static void ResetTelemetryForTests()
    {
        Interlocked.Exchange(ref s_slangCompileAttempts, 0);
        Interlocked.Exchange(ref s_dxcCompileAttempts, 0);
        Interlocked.Exchange(ref s_successfulRequests, 0);
        Interlocked.Exchange(ref s_failedRequests, 0);
        Interlocked.Exchange(ref s_fallbackCount, 0);
    }

    /// <summary>Whether the backend expects HLSL bytecode rather than GLSL source.</summary>
    public static bool RequiresHlsl(GraphicsBackend backend) =>
        backend is GraphicsBackend.Vulkan
            or GraphicsBackend.Direct3D12
            or GraphicsBackend.WebGPU
            or GraphicsBackend.Metal
            or GraphicsBackend.WebGL2;

    private sealed class SelectedShaderCompiler(
        IShaderCompiler primary,
        ShaderCompilerKind primaryKind,
        IShaderCompiler? fallback = null,
        ShaderCompilerKind fallbackKind = ShaderCompilerKind.Unknown) : IShaderCompiler
    {
        public ShaderCompileResult Compile(ShaderCompileRequest request)
        {
            RecordAttempt(primaryKind);
            ShaderCompileResult primaryResult = primary.Compile(request);
            primaryResult.CompilerKind = primaryKind;
            primaryResult.Diagnostics = primaryResult.ErrorMessage;
            if (primaryResult.Success || fallback == null)
            {
                RecordCompletion(primaryResult.Success);
                return primaryResult;
            }

            Interlocked.Increment(ref s_fallbackCount);
            RecordAttempt(fallbackKind);
            ShaderCompileResult fallbackResult = fallback.Compile(request);
            string fallbackReason = DescribeFailure(request, primaryKind, primaryResult.ErrorMessage);
            fallbackResult.CompilerKind = fallbackKind;
            fallbackResult.FallbackFrom = primaryKind;
            fallbackResult.FallbackReason = fallbackReason;
            fallbackResult.Diagnostics = primaryResult.ErrorMessage;
            if (!fallbackResult.Success)
            {
                ShaderCompileResult failedResult = ShaderCompileResult.Fail(
                    $"{fallbackReason} {GetDisplayName(fallbackKind)} fallback also failed: {fallbackResult.ErrorMessage}");
                failedResult.CompilerKind = fallbackKind;
                failedResult.FallbackFrom = primaryKind;
                failedResult.FallbackReason = fallbackReason;
                failedResult.Diagnostics = primaryResult.ErrorMessage;
                RecordCompletion(success: false);
                return failedResult;
            }
            RecordCompletion(success: true);
            return fallbackResult;
        }

        private static string DescribeFailure(
            ShaderCompileRequest request,
            ShaderCompilerKind compilerKind,
            string? error)
        {
            string shader = string.IsNullOrWhiteSpace(request.ShaderName) ? "<unnamed>" : request.ShaderName;
            string pass = string.IsNullOrWhiteSpace(request.PassName) ? "<unnamed>" : request.PassName;
            string variant = string.IsNullOrWhiteSpace(request.VariantKey) ? "<default>" : request.VariantKey;
            return $"{GetDisplayName(compilerKind)} failed for {shader}/{pass} [{request.TargetBackend}, variant={variant}]: {error}";
        }

        private static string GetDisplayName(ShaderCompilerKind compilerKind) => compilerKind switch
        {
            ShaderCompilerKind.Dxc => "DXC",
            ShaderCompilerKind.Glsl => "GLSL",
            ShaderCompilerKind.Slang => "Slang",
            _ => compilerKind.ToString(),
        };
    }

    private static void RecordAttempt(ShaderCompilerKind compilerKind)
    {
        if (compilerKind == ShaderCompilerKind.Slang)
            Interlocked.Increment(ref s_slangCompileAttempts);
        else if (compilerKind == ShaderCompilerKind.Dxc)
            Interlocked.Increment(ref s_dxcCompileAttempts);
    }

    private static void RecordCompletion(bool success)
    {
        if (success)
            Interlocked.Increment(ref s_successfulRequests);
        else
            Interlocked.Increment(ref s_failedRequests);
    }
}
#endif
