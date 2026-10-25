// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

using XEngine.Slang;

using SlangShaderStage = XEngine.Slang.ShaderStage;

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>
/// In-process Slang adapter for Slang-compatible HLSL to SPIR-V, DXIL, WGSL, MetalLib, or GLSL ES.
/// </summary>
public sealed class RuntimeSlangShaderCompiler : IShaderCompiler
{
    internal const string MatrixLayout = "column-major";
    internal static readonly string[] GlslEsProfileCandidates =
    [
        "glsl_es_300",
        "glsl_300",
        "glsl_320",
        "glsl_330",
        "glsl_450",
    ];
    private const string VertexSourcePath = "xengine_vertex.slang";
    private const string FragmentSourcePath = "xengine_fragment.slang";
    private const string ComputeSourcePath = "xengine_compute.slang";
    private const int TargetIndex = 0;
    private const int MaximumCompilesPerSession = 512;
    private static readonly object s_compileLock = new();
    private static Session? s_logicalSession;
    private static Session? s_physicalSession;
    private static Session? s_wgslSession;
    private static readonly Session?[] s_metalSessions = new Session?[3];
    private static readonly string?[] s_metalCompilerPaths = new string?[3];
    private static readonly string?[] s_metallibLinkerPaths = new string?[3];
    private static readonly string?[] s_iosMetalSdkPaths = new string?[2];
    private static Session? s_glslSession;
    private static string? s_glslEsProfile;
    private static int s_sessionCompileCount;

    internal static string CompilerIdentity => $"Slang {GlobalSession.GetBuildTagString()}; XEngine adapter 8";

    internal static string GetTargetProfile(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.Vulkan => "spirv_1_5",
        GraphicsBackend.Direct3D12 => "sm_6_0",
        GraphicsBackend.WebGPU => "wgsl_1_0",
        GraphicsBackend.Metal => "metal",
        GraphicsBackend.WebGL2 => ResolveGlslEsProfile(),
        _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Slang profiles require a modern backend."),
    };

    /// <summary>
    /// Returns the artifact identity for a backend and platform environment. Metal's Slang
    /// language profile remains <c>metal</c>; these artifact names additionally partition its
    /// platform-specific compiler output and runtime catalog tier.
    /// </summary>
    internal static string GetArtifactTargetProfile(
        GraphicsBackend backend,
        ShaderTargetEnvironment targetEnvironment)
    {
        if (backend != GraphicsBackend.Metal)
        {
            if (targetEnvironment != ShaderTargetEnvironment.Default)
            {
                throw new ArgumentException(
                    $"Target environment {targetEnvironment} is only supported for Metal shaders.",
                    nameof(targetEnvironment));
            }
            return GetTargetProfile(backend);
        }

        return targetEnvironment switch
        {
            ShaderTargetEnvironment.Default or ShaderTargetEnvironment.MacOS => "metal",
            ShaderTargetEnvironment.IOS => "metal-ios",
            ShaderTargetEnvironment.IOSSimulator => "metal-ios-simulator",
            _ => throw new ArgumentOutOfRangeException(
                nameof(targetEnvironment), targetEnvironment, "Unknown Metal shader target environment."),
        };
    }

    /// <summary>Reads the platform environment encoded in an artifact's target profile.</summary>
    internal static ShaderTargetEnvironment GetArtifactTargetEnvironment(
        GraphicsBackend backend,
        string targetProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProfile);
        if (backend != GraphicsBackend.Metal)
            return ShaderTargetEnvironment.Default;

        return targetProfile switch
        {
            "metal" => ShaderTargetEnvironment.Default,
            "metal-ios" => ShaderTargetEnvironment.IOS,
            "metal-ios-simulator" => ShaderTargetEnvironment.IOSSimulator,
            _ => throw new InvalidDataException(
                $"Metal artifact has an unknown platform target profile '{targetProfile}'."),
        };
    }

    public ShaderCompileResult Compile(ShaderCompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Language != ShaderLanguage.Hlsl)
            return ShaderCompileResult.Fail($"RuntimeSlangShaderCompiler only accepts HLSL source (got {request.Language}).");

        try
        {
            _ = GetArtifactTargetProfile(request.TargetBackend, request.TargetEnvironment);
        }
        catch (ArgumentException ex)
        {
            return ShaderCompileResult.Fail(ex.Message);
        }

        if (request.TargetBackend is not GraphicsBackend.Vulkan
            and not GraphicsBackend.Direct3D12
            and not GraphicsBackend.WebGPU
            and not GraphicsBackend.Metal
            and not GraphicsBackend.WebGL2)
            return ShaderCompileResult.Fail($"Slang bytecode output is not supported for {request.TargetBackend}.");

        bool hasCompute = !string.IsNullOrWhiteSpace(request.ComputeSource);
        bool hasVertex = !string.IsNullOrWhiteSpace(request.VertexSource);
        bool hasFragment = !string.IsNullOrWhiteSpace(request.FragmentSource);
        bool hasGraphics = hasVertex && hasFragment;
        if (hasVertex != hasFragment)
            return ShaderCompileResult.Fail("Graphics compilation requires both vertex and fragment sources.");
        if (!hasGraphics && !hasCompute)
            return ShaderCompileResult.Fail("Vertex source is null or empty.");
        if (hasCompute && request.TargetBackend == GraphicsBackend.WebGL2)
        {
            if (!hasGraphics)
                return ShaderCompileResult.Fail("Slang compute-stage compilation is not supported for WebGL2.");
            hasCompute = false;
        }
        if (!string.IsNullOrWhiteSpace(request.GeometrySource))
            return ShaderCompileResult.Fail("Slang geometry-stage compilation is not enabled in the SM2 adapter.");

        lock (s_compileLock)
            return CompileLocked(request, hasGraphics, hasCompute);
    }

    private static ShaderCompileResult CompileLocked(ShaderCompileRequest request, bool hasGraphics, bool hasCompute)
    {
        try
        {
            (Session logicalSession, Session? emitSession, int compileId) =
                AcquireSessions(request.TargetBackend, request.TargetEnvironment);
            string preamble = CreatePreprocessorPreamble(request.Keywords, request.ExtraDefines);
            if (request.TargetBackend == GraphicsBackend.Metal)
                preamble = "#define XENGINE_METAL 1\n" + preamble;

            SlangStageCompilation? logicalCompute = null;
            if (hasCompute)
            {
                string computeSource = preamble.Length == 0
                    ? request.ComputeSource!
                    : string.Concat(preamble, request.ComputeSource);
                logicalCompute = CompileLogicalStage(
                    logicalSession,
                    computeSource,
                    $"xengine_compute_{compileId}",
                    $"xengine_compile_{compileId}/{ComputeSourcePath}",
                    request.ComputeEntryPoint,
                    SlangShaderStage.Compute);
            }

            if (!hasGraphics)
            {
                SlangStageCompilation computeOnlyStage = logicalCompute!.Value;
                byte[] computeOnlyBytecode = computeOnlyStage.Bytecode;
                ShaderBindingLayout? computeLayout = computeOnlyStage.Reflection.BindingLayout;
                ShaderBytecodeFormat format = ShaderBytecodeFormats.GetExpected(request.TargetBackend);
                if (request.TargetBackend != GraphicsBackend.Direct3D12)
                {
                    SlangStageCompilation physicalCompute = CompilePhysicalCompute(
                        emitSession!,
                        preamble.Length == 0 ? request.ComputeSource! : string.Concat(preamble, request.ComputeSource),
                        compileId,
                        request.ComputeEntryPoint,
                        request.TargetBackend == GraphicsBackend.Metal,
                        computeOnlyStage.Reflection.BindingLayout);
                    computeOnlyBytecode = physicalCompute.Bytecode;
                    computeLayout = physicalCompute.Reflection.BindingLayout;
                    if (request.TargetBackend == GraphicsBackend.Metal)
                    {
                        computeLayout = RuntimeSlangReflection.AttachMetalSourcePhysicalSlots(
                            computeLayout,
                            computeOnlyBytecode);
                        if (ShouldCompactMetalIndices(request.TargetBackend, request.TargetEnvironment))
                        {
                            computeOnlyBytecode = RuntimeSlangReflection.CompactMetalSourceIndices(
                                computeOnlyBytecode, computeLayout);
                            computeLayout = RuntimeSlangReflection.CompactMetalPhysicalSlots(computeLayout);
                        }
                        else if (ShouldCompactMetalSamplers(request.TargetBackend))
                        {
                            computeOnlyBytecode = RuntimeSlangReflection.CompactMetalSamplerSourceIndices(
                                computeOnlyBytecode, computeLayout);
                            computeLayout = RuntimeSlangReflection.CompactMetalSamplerPhysicalSlots(computeLayout);
                        }
                        computeOnlyBytecode = CompileMetalSourceForApplePlatform(computeOnlyBytecode, request.TargetEnvironment);
                    }
                }

                return new ShaderCompileResult
                {
                    Success = true,
                    Format = format,
                    VertexBytecode = [],
                    FragmentBytecode = [],
                    ComputeBytecode = computeOnlyBytecode,
                    BindingLayout = computeLayout,
                };
            }

            string vertexSource = preamble.Length == 0 ? request.VertexSource : string.Concat(preamble, request.VertexSource);
            string fragmentSource = preamble.Length == 0 ? request.FragmentSource : string.Concat(preamble, request.FragmentSource);
            SlangStageCompilation logicalVertex = CompileLogicalStage(
                logicalSession,
                vertexSource,
                $"xengine_vertex_{compileId}",
                $"xengine_compile_{compileId}/{VertexSourcePath}",
                request.VertexEntryPoint,
                SlangShaderStage.Vertex);
            SlangStageCompilation logicalFragment = CompileLogicalStage(
                logicalSession,
                fragmentSource,
                $"xengine_fragment_{compileId}",
                $"xengine_compile_{compileId}/{FragmentSourcePath}",
                request.FragmentEntryPoint,
                SlangShaderStage.Fragment);

            byte[] vertexBytecode;
            byte[] fragmentBytecode;
            ShaderBindingLayout graphicsLayout;
            ShaderVertexInputLayout? vertexInput;
            if (request.TargetBackend == GraphicsBackend.Direct3D12)
            {
                vertexBytecode = logicalVertex.Bytecode;
                fragmentBytecode = logicalFragment.Bytecode;
                graphicsLayout = RuntimeSlangReflection.Merge(
                    logicalVertex.Reflection.BindingLayout,
                    logicalFragment.Reflection.BindingLayout);
                vertexInput = logicalVertex.Reflection.VertexInputLayout;
            }
            else
            {
                SlangProgramCompilation emitted = CompilePhysicalProgram(
                    emitSession!,
                    vertexSource,
                    fragmentSource,
                    compileId,
                    request.VertexEntryPoint,
                    request.FragmentEntryPoint,
                    logicalVertex.Reflection,
                    logicalFragment.Reflection,
                    request.TargetBackend == GraphicsBackend.Metal);
                vertexBytecode = emitted.VertexBytecode;
                fragmentBytecode = emitted.FragmentBytecode;
                SlangStageReflection vertexReflection = emitted.VertexReflection;
                SlangStageReflection fragmentReflection = emitted.FragmentReflection;
                if (request.TargetBackend == GraphicsBackend.Metal)
                {
                    vertexReflection = new SlangStageReflection(
                        RuntimeSlangReflection.AttachMetalSourcePhysicalSlots(
                            vertexReflection.BindingLayout,
                            vertexBytecode),
                        vertexReflection.VertexInputLayout);
                    fragmentReflection = new SlangStageReflection(
                        RuntimeSlangReflection.AttachMetalSourcePhysicalSlots(
                            fragmentReflection.BindingLayout,
                            fragmentBytecode),
                        fragmentReflection.VertexInputLayout);
                    if (ShouldCompactMetalIndices(request.TargetBackend, request.TargetEnvironment))
                    {
                        vertexBytecode = RuntimeSlangReflection.CompactMetalSourceIndices(
                            vertexBytecode, vertexReflection.BindingLayout);
                        fragmentBytecode = RuntimeSlangReflection.CompactMetalSourceIndices(
                            fragmentBytecode, fragmentReflection.BindingLayout);
                        vertexReflection = new SlangStageReflection(
                            RuntimeSlangReflection.CompactMetalPhysicalSlots(vertexReflection.BindingLayout),
                            vertexReflection.VertexInputLayout);
                        fragmentReflection = new SlangStageReflection(
                            RuntimeSlangReflection.CompactMetalPhysicalSlots(fragmentReflection.BindingLayout),
                            fragmentReflection.VertexInputLayout);
                    }
                    else if (ShouldCompactMetalSamplers(request.TargetBackend))
                    {
                        vertexBytecode = RuntimeSlangReflection.CompactMetalSamplerSourceIndices(
                            vertexBytecode, vertexReflection.BindingLayout);
                        fragmentBytecode = RuntimeSlangReflection.CompactMetalSamplerSourceIndices(
                            fragmentBytecode, fragmentReflection.BindingLayout);
                        vertexReflection = new SlangStageReflection(
                            RuntimeSlangReflection.CompactMetalSamplerPhysicalSlots(vertexReflection.BindingLayout),
                            vertexReflection.VertexInputLayout);
                        fragmentReflection = new SlangStageReflection(
                            RuntimeSlangReflection.CompactMetalSamplerPhysicalSlots(fragmentReflection.BindingLayout),
                            fragmentReflection.VertexInputLayout);
                    }
                    vertexBytecode = CompileMetalSourceForApplePlatform(vertexBytecode, request.TargetEnvironment);
                    fragmentBytecode = CompileMetalSourceForApplePlatform(fragmentBytecode, request.TargetEnvironment);
                }
                graphicsLayout = RuntimeSlangReflection.Merge(
                    vertexReflection.BindingLayout,
                    fragmentReflection.BindingLayout,
                    request.TargetBackend == GraphicsBackend.Metal);
                vertexInput = vertexReflection.VertexInputLayout;
            }

            byte[]? computeBytecode = null;
            ShaderBindingLayout bindingLayout = graphicsLayout;
            if (logicalCompute is { } computeStage)
            {
                if (request.TargetBackend == GraphicsBackend.Direct3D12)
                {
                    computeBytecode = computeStage.Bytecode;
                    bindingLayout = RuntimeSlangReflection.Merge(
                        graphicsLayout,
                        computeStage.Reflection.BindingLayout);
                }
                else
                {
                    string computeSource = preamble.Length == 0
                        ? request.ComputeSource!
                        : string.Concat(preamble, request.ComputeSource);
                    SlangStageCompilation physicalCompute = CompilePhysicalCompute(
                        emitSession!,
                        computeSource,
                        compileId,
                        request.ComputeEntryPoint,
                        request.TargetBackend == GraphicsBackend.Metal,
                        computeStage.Reflection.BindingLayout);
                    computeBytecode = physicalCompute.Bytecode;
                    ShaderBindingLayout physicalComputeLayout = physicalCompute.Reflection.BindingLayout;
                    if (request.TargetBackend == GraphicsBackend.Metal)
                    {
                        physicalComputeLayout = RuntimeSlangReflection.AttachMetalSourcePhysicalSlots(
                            physicalComputeLayout,
                            computeBytecode);
                        if (ShouldCompactMetalIndices(request.TargetBackend, request.TargetEnvironment))
                        {
                            computeBytecode = RuntimeSlangReflection.CompactMetalSourceIndices(
                                computeBytecode, physicalComputeLayout);
                            physicalComputeLayout = RuntimeSlangReflection.CompactMetalPhysicalSlots(physicalComputeLayout);
                        }
                        else if (ShouldCompactMetalSamplers(request.TargetBackend))
                        {
                            computeBytecode = RuntimeSlangReflection.CompactMetalSamplerSourceIndices(
                                computeBytecode, physicalComputeLayout);
                            physicalComputeLayout = RuntimeSlangReflection.CompactMetalSamplerPhysicalSlots(physicalComputeLayout);
                        }
                        computeBytecode = CompileMetalSourceForApplePlatform(computeBytecode, request.TargetEnvironment);
                    }
                    bindingLayout = RuntimeSlangReflection.Merge(
                        graphicsLayout,
                        physicalComputeLayout,
                        request.TargetBackend == GraphicsBackend.Metal);
                }
            }

            return new ShaderCompileResult
            {
                Success = true,
                Format = ShaderBytecodeFormats.GetExpected(request.TargetBackend),
                VertexBytecode = vertexBytecode,
                FragmentBytecode = fragmentBytecode,
                ComputeBytecode = computeBytecode,
                BindingLayout = bindingLayout,
                VertexInputLayout = vertexInput,
            };
        }
        catch (SlangStageCompilationException ex)
        {
            return ShaderCompileResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return ShaderCompileResult.Fail($"Slang compile exception for {request.TargetBackend}: {ex.Message}");
        }
    }

    internal static MatrixLayoutMode GetMatrixLayoutMode() => MatrixLayoutMode.ColumnMajor;

    private static bool ShouldCompactMetalIndices(
        GraphicsBackend backend,
        ShaderTargetEnvironment targetEnvironment) =>
        backend == GraphicsBackend.Metal
        && targetEnvironment is ShaderTargetEnvironment.IOS or ShaderTargetEnvironment.IOSSimulator;

    private static bool ShouldCompactMetalSamplers(GraphicsBackend backend) => backend == GraphicsBackend.Metal;

    private static (Session Logical, Session? Emit, int CompileId) AcquireSessions(
        GraphicsBackend backend,
        ShaderTargetEnvironment targetEnvironment)
    {
        if (s_logicalSession == null || s_sessionCompileCount >= MaximumCompilesPerSession)
        {
            s_logicalSession?.Dispose();
            s_physicalSession?.Dispose();
            s_wgslSession?.Dispose();
            for (int i = 0; i < s_metalSessions.Length; i++)
            {
                s_metalSessions[i]?.Dispose();
                s_metalSessions[i] = null;
            }
            s_glslSession?.Dispose();
            s_logicalSession = CreateLogicalSession();
            s_physicalSession = null;
            s_wgslSession = null;
            s_glslSession = null;
            s_sessionCompileCount = 0;
        }

        Session? emit = null;
        if (backend == GraphicsBackend.Vulkan)
        {
            s_physicalSession ??= CreatePhysicalSession();
            emit = s_physicalSession;
        }
        else if (backend == GraphicsBackend.WebGPU)
        {
            s_wgslSession ??= CreateWgslSession();
            emit = s_wgslSession;
        }
        else if (backend == GraphicsBackend.Metal)
        {
            int metalSessionIndex = GetMetalSessionIndex(targetEnvironment);
            s_metalSessions[metalSessionIndex] ??= CreateMetalLibSession(targetEnvironment);
            emit = s_metalSessions[metalSessionIndex];
        }
        else if (backend == GraphicsBackend.WebGL2)
        {
            s_glslSession ??= CreateGlslEsSession();
            emit = s_glslSession;
        }

        s_sessionCompileCount++;
        return (s_logicalSession, emit, s_sessionCompileCount);
    }

    private static Session CreateLogicalSession()
    {
        SessionDescription sessionDescription = new()
        {
            Targets =
            [
                new TargetDescription
                {
                    // The logical session exists for validation and reflection; only the D3D12
                    // backend (Windows, DXC vendored) consumes its bytecode as product DXIL.
                    // Non-Windows hosts have no DXC downstream compiler, and a DXIL target makes
                    // every compile fail there (E52002/E00100) before the physical emit runs —
                    // including Vulkan/Metal/WGSL. Use the HLSL text target instead: it emits
                    // in-process, and its reflection uses the same HLSL register layout.
                    Format = OperatingSystem.IsWindows() ? CompileTarget.Dxil : CompileTarget.Hlsl,
                    Profile = GlobalSession.FindProfile(GetTargetProfile(GraphicsBackend.Direct3D12)),
                },
            ],
            DefaultMatrixLayoutMode = GetMatrixLayoutMode(),
        };
        return GlobalSession.CreateSession(sessionDescription);
    }

    private static Session CreatePhysicalSession()
    {
        SessionDescription sessionDescription = new()
        {
            Targets =
            [
                new TargetDescription
                {
                    Format = CompileTarget.Spirv,
                    Profile = GlobalSession.FindProfile(GetTargetProfile(GraphicsBackend.Vulkan)),
                    CompilerOptionEntries = CreateSpirvBindingNamespaceOptions(),
                },
            ],
            DefaultMatrixLayoutMode = GetMatrixLayoutMode(),
        };
        return GlobalSession.CreateSession(sessionDescription);
    }

    private static Session CreateWgslSession()
    {
        SessionDescription sessionDescription = new()
        {
            Targets =
            [
                new TargetDescription
                {
                    Format = CompileTarget.Wgsl,
                    Profile = GlobalSession.FindProfile(GetTargetProfile(GraphicsBackend.WebGPU)),
                    // WGSL has a single @binding namespace per @group. HLSL b0 and t0 would both
                    // become @binding(0) without a kind shift, which Slang then rejects. Reuse the
                    // SPIR-V namespace bases (CB 0, SRV 64, sampler 128, UAV 192) so default
                    // shaders keep their HLSL registers without collisions.
                    CompilerOptionEntries = CreateSpirvBindingNamespaceOptions(),
                },
            ],
            DefaultMatrixLayoutMode = GetMatrixLayoutMode(),
        };
        return GlobalSession.CreateSession(sessionDescription);
    }

    private static Session CreateMetalLibSession(ShaderTargetEnvironment targetEnvironment)
    {
        SessionDescription sessionDescription = new()
        {
            Targets =
            [
                new TargetDescription
                {
                    // Emit Metal source for every Apple target so binding indices can be normalized
                    // before Apple's compiler enforces platform limits. Slang's MetalLib downstream
                    // compiles too early for sparse sampler compaction on macOS.
                    Format = CompileTarget.Metal,
                    Profile = GlobalSession.FindProfile(GetTargetProfile(GraphicsBackend.Metal)),
                },
            ],
            DefaultMatrixLayoutMode = GetMatrixLayoutMode(),
        };
        return GlobalSession.CreateSession(sessionDescription);
    }

    internal static string? GetMetalTargetTriple(ShaderTargetEnvironment targetEnvironment) => targetEnvironment switch
    {
        ShaderTargetEnvironment.Default or ShaderTargetEnvironment.MacOS => null,
        ShaderTargetEnvironment.IOS => "air64-apple-ios16.0",
        ShaderTargetEnvironment.IOSSimulator => "air64-apple-ios16.0-simulator",
        _ => throw new ArgumentOutOfRangeException(
            nameof(targetEnvironment), targetEnvironment, "Unknown Metal shader target environment."),
    };

    private static int GetMetalSessionIndex(ShaderTargetEnvironment targetEnvironment) => targetEnvironment switch
    {
        ShaderTargetEnvironment.Default or ShaderTargetEnvironment.MacOS => 0,
        ShaderTargetEnvironment.IOS => 1,
        ShaderTargetEnvironment.IOSSimulator => 2,
        _ => throw new ArgumentOutOfRangeException(
            nameof(targetEnvironment), targetEnvironment, "Unknown Metal shader target environment."),
    };

    private static byte[] CompileMetalSourceForApplePlatform(
        byte[] metalSource,
        ShaderTargetEnvironment targetEnvironment)
    {
        string? targetTriple = GetMetalTargetTriple(targetEnvironment);
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Building Apple MetalLib artifacts requires the macOS Apple Metal toolchain.");

        string sdkName = GetMetalSdkName(targetEnvironment);
        string metalCompiler = GetAppleMetalToolPath(targetEnvironment, "metal");
        string metallibLinker = GetAppleMetalToolPath(targetEnvironment, "metallib");
        string? sdkPath = targetTriple == null ? null : GetIosMetalSdkPath(sdkName);
        string tempRoot = Path.GetTempPath();
        string workDirectory = Path.Combine(tempRoot, "xengine-metal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            string sourcePath = Path.Combine(workDirectory, "shader.metal");
            string airPath = Path.Combine(workDirectory, "shader.air");
            string libraryPath = Path.Combine(workDirectory, "shader.metallib");
            File.WriteAllBytes(sourcePath, metalSource);

            var compileArguments = new List<string>(8);
            if (targetTriple != null)
            {
                compileArguments.Add("-target");
                compileArguments.Add(targetTriple);
                compileArguments.Add("-isysroot");
                compileArguments.Add(sdkPath!);
            }
            compileArguments.Add("-c");
            compileArguments.Add(sourcePath);
            compileArguments.Add("-o");
            compileArguments.Add(airPath);
            RunAppleMetalTool(metalCompiler, compileArguments, "Metal compilation");
            RunAppleMetalTool(metallibLinker, [airPath, "-o", libraryPath], "Metal library linking");

            byte[] library = File.ReadAllBytes(libraryPath);
            if (library.Length < 4 || library[0] != (byte)'M' || library[1] != (byte)'T'
                || library[2] != (byte)'L' || library[3] != (byte)'B')
                throw new InvalidDataException($"Apple Metal toolchain produced an invalid MetalLib for {targetEnvironment}.");
            return library;
        }
        finally
        {
            try
            {
                Directory.Delete(workDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string GetMetalSdkName(ShaderTargetEnvironment targetEnvironment) => targetEnvironment switch
    {
        ShaderTargetEnvironment.Default or ShaderTargetEnvironment.MacOS => "macosx",
        ShaderTargetEnvironment.IOS => "iphoneos",
        ShaderTargetEnvironment.IOSSimulator => "iphonesimulator",
        _ => throw new ArgumentOutOfRangeException(
            nameof(targetEnvironment), targetEnvironment, "Unknown Metal shader target environment."),
    };

    private static string GetAppleMetalToolPath(ShaderTargetEnvironment targetEnvironment, string tool)
    {
        int toolchainIndex = GetMetalSessionIndex(targetEnvironment);
        string?[] paths = tool == "metal" ? s_metalCompilerPaths : s_metallibLinkerPaths;
        if (paths[toolchainIndex] is string cachedPath)
            return cachedPath;

        string sdkName = GetMetalSdkName(targetEnvironment);
        string path = FindKnownAppleMetalToolOnPath(tool)
            ?? RunXcrun(sdkName, ["--find", tool], $"{tool} lookup").Trim();
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException($"Apple Metal tool '{tool}' was not found for SDK '{sdkName}'.", path);
        paths[toolchainIndex] = path;
        return path;
    }

    private static string? FindKnownAppleMetalToolOnPath(string tool)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string? developerDirectory = Environment.GetEnvironmentVariable("DEVELOPER_DIR");
        string? defaultToolchainBin = string.IsNullOrWhiteSpace(developerDirectory)
            ? null
            : Path.GetFullPath(Path.Combine(
                developerDirectory,
                "Toolchains",
                "XcodeDefault.xctoolchain",
                "usr",
                "bin"));
        string metalToolchainSuffix = Path.Combine("Metal.xctoolchain", "usr", "bin");
        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string candidate = Path.Combine(directory, tool);
            if (!File.Exists(candidate))
                continue;

            string fullDirectory = Path.GetFullPath(directory);
            if (fullDirectory.Contains(metalToolchainSuffix, StringComparison.Ordinal)
                || string.Equals(fullDirectory, defaultToolchainBin, StringComparison.Ordinal))
                return candidate;
        }

        return null;
    }

    private static void RunAppleMetalTool(string executable, IReadOnlyList<string> arguments, string operation)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        for (int i = 0; i < arguments.Count; i++)
            startInfo.ArgumentList.Add(arguments[i]);

        using Process process = Process.Start(startInfo)
            ?? throw new IOException($"Could not start {operation} with '{executable}'.");
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                standardOutput.AppendLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                standardError.AppendLine(eventArgs.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{operation} timed out using '{executable}'.");
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string details = standardError.ToString().Trim();
            string output = standardOutput.ToString().Trim();
            throw new IOException(
                $"{operation} failed with exit code {process.ExitCode}: " +
                (details.Length > 0 ? details : output));
        }
    }

    private static string GetIosMetalSdkPath(string sdkName)
    {
        int sdkIndex = sdkName == "iphoneos" ? 0 : 1;
        if (s_iosMetalSdkPaths[sdkIndex] is string cachedPath)
            return cachedPath;

        string sdkPath = RunXcrun(sdkName, ["--show-sdk-path"], "Apple SDK lookup").Trim();
        if (!Path.IsPathFullyQualified(sdkPath) || !Directory.Exists(sdkPath))
            throw new DirectoryNotFoundException($"Apple SDK '{sdkName}' was not found at '{sdkPath}'.");
        s_iosMetalSdkPaths[sdkIndex] = sdkPath;
        return sdkPath;
    }

    private static string RunXcrun(string sdkName, string[] arguments, string operation)
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

        using Process process = Process.Start(startInfo)
            ?? throw new IOException($"Could not start xcrun for {operation}.");
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                standardOutput.AppendLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data != null)
                standardError.AppendLine(eventArgs.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"xcrun timed out during {operation}.");
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string details = standardError.ToString().Trim();
            throw new IOException($"xcrun {operation} failed with exit code {process.ExitCode}: {details}");
        }
        return standardOutput.ToString();
    }

    private static Session CreateGlslEsSession()
    {
        SessionDescription sessionDescription = new()
        {
            Targets =
            [
                new TargetDescription
                {
                    Format = CompileTarget.Glsl,
                    Profile = GlobalSession.FindProfile(ResolveGlslEsProfile()),
                    CompilerOptionEntries = CreateSpirvBindingNamespaceOptions(),
                },
            ],
            DefaultMatrixLayoutMode = GetMatrixLayoutMode(),
        };
        return GlobalSession.CreateSession(sessionDescription);
    }

    internal static string ResolveGlslEsProfile()
    {
        if (s_glslEsProfile != null)
            return s_glslEsProfile;

        lock (s_compileLock)
        {
            if (s_glslEsProfile != null)
                return s_glslEsProfile;

            for (int i = 0; i < GlslEsProfileCandidates.Length; i++)
            {
                string name = GlslEsProfileCandidates[i];
                if (GlobalSession.FindProfile(name) != ProfileID.Unknown)
                {
                    s_glslEsProfile = name;
                    return name;
                }
            }

            s_glslEsProfile = GlslEsProfileCandidates[0];
            return s_glslEsProfile;
        }
    }

    private static CompilerOptionEntry[] CreateSpirvBindingNamespaceOptions() =>
    [
        CreateSpirvBindingNamespaceOption(SlangVulkanResourceKind.ConstantBuffer, 0),
        CreateSpirvBindingNamespaceOption(SlangVulkanResourceKind.ShaderResource, 64),
        CreateSpirvBindingNamespaceOption(SlangVulkanResourceKind.Sampler, 128),
        CreateSpirvBindingNamespaceOption(SlangVulkanResourceKind.UnorderedAccess, 192),
    ];

    private static CompilerOptionEntry CreateSpirvBindingNamespaceOption(
        SlangVulkanResourceKind kind,
        int bindingBase) => new()
    {
        Name = CompilerOptionName.VulkanBindShiftAll,
        Value = new CompilerOptionValue
        {
            Kind = CompilerOptionValueKind.Int,
            IntValue0 = (int)kind,
            IntValue1 = bindingBase,
        },
    };

    private static SlangStageCompilation CompileLogicalStage(
        Session logicalSession,
        string source,
        string moduleName,
        string sourcePath,
        string entryPointName,
        SlangShaderStage stage)
    {
        try
        {
            using Module logicalModule = logicalSession.LoadModuleFromSourceString(
                $"{moduleName}_logical",
                $"logical/{sourcePath}",
                source,
                out _);
            using EntryPoint logicalEntryPoint = logicalModule.FindAndCheckEntryPoint(entryPointName, stage, out _);
            using ComponentType logicalProgram = logicalSession.CreateCompositeComponentType(
                [logicalModule, logicalEntryPoint],
                out _);
            ShaderStageMask stageMask = stage switch
            {
                SlangShaderStage.Vertex => ShaderStageMask.Vertex,
                SlangShaderStage.Fragment => ShaderStageMask.Fragment,
                SlangShaderStage.Compute => ShaderStageMask.Compute,
                _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported Slang shader stage."),
            };
            SlangStageReflection logicalReflection = RuntimeSlangReflection.ReflectLogical(
                logicalProgram,
                TargetIndex,
                stageMask);
            byte[] bytecode = logicalProgram.GetEntryPointCode(0, TargetIndex, out _).ToArray();
            return new SlangStageCompilation(bytecode, logicalReflection);
        }
        catch (CompilationException ex)
        {
            string stageName = stage switch
            {
                SlangShaderStage.Vertex => "vertex",
                SlangShaderStage.Fragment => "fragment",
                SlangShaderStage.Compute => "compute",
                _ => stage.ToString().ToLowerInvariant(),
            };
            string diagnostics = string.IsNullOrWhiteSpace(ex.Diagnostics.Message)
                ? ex.Message
                : ex.Diagnostics.Message.Trim();
            if (Environment.GetEnvironmentVariable("XENGINE_DUMP_FAILED_SLANG") is string dumpDir && dumpDir.Length > 0)
            {
                System.IO.Directory.CreateDirectory(dumpDir);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dumpDir, $"failed_{stageName}_{sourcePath.Replace('/', '_')}"),
                    source);
            }
            throw new SlangStageCompilationException(
                $"Slang {stageName} compile failed for '{sourcePath}': {diagnostics}",
                ex);
        }
    }

    private static SlangProgramCompilation CompilePhysicalProgram(
        Session physicalSession,
        string vertexSource,
        string fragmentSource,
        int compileId,
        string vertexEntryPointName,
        string fragmentEntryPointName,
        SlangStageReflection logicalVertex,
        SlangStageReflection logicalFragment,
        bool metalPerKindSlots)
    {
        string vertexSourcePath = $"xengine_compile_{compileId}/{VertexSourcePath}";
        string fragmentSourcePath = $"xengine_compile_{compileId}/{FragmentSourcePath}";

        try
        {
            using Module vertexModule = physicalSession.LoadModuleFromSourceString(
                $"xengine_vertex_{compileId}_physical",
                $"physical/{vertexSourcePath}",
                vertexSource,
                out _);
            using EntryPoint vertexEntryPoint = vertexModule.FindAndCheckEntryPoint(
                vertexEntryPointName,
                SlangShaderStage.Vertex,
                out _);
            using Module fragmentModule = physicalSession.LoadModuleFromSourceString(
                $"xengine_fragment_{compileId}_physical",
                $"physical/{fragmentSourcePath}",
                fragmentSource,
                out _);
            using EntryPoint fragmentEntryPoint = fragmentModule.FindAndCheckEntryPoint(
                fragmentEntryPointName,
                SlangShaderStage.Fragment,
                out _);
            using ComponentType physicalProgram = physicalSession.CreateCompositeComponentType(
                [vertexModule, vertexEntryPoint, fragmentModule, fragmentEntryPoint],
                out _);

            byte[] vertexBytecode = physicalProgram.GetEntryPointCode(0, TargetIndex, out _).ToArray();
            byte[] fragmentBytecode = physicalProgram.GetEntryPointCode(1, TargetIndex, out _).ToArray();
            (SlangStageReflection vertexReflection, SlangStageReflection fragmentReflection) =
                RuntimeSlangReflection.AttachPhysicalLayouts(
                    logicalVertex,
                    logicalFragment,
                    physicalProgram,
                    TargetIndex,
                    metalPerKindSlots);
            return new SlangProgramCompilation(
                vertexBytecode,
                fragmentBytecode,
                vertexReflection,
                fragmentReflection);
        }
        catch (CompilationException ex)
        {
            string diagnostics = string.IsNullOrWhiteSpace(ex.Diagnostics.Message)
                ? ex.Message
                : ex.Diagnostics.Message.Trim();
            if (Environment.GetEnvironmentVariable("XENGINE_DUMP_FAILED_SLANG") is string programDumpDir && programDumpDir.Length > 0)
            {
                System.IO.Directory.CreateDirectory(programDumpDir);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(programDumpDir, $"failed_program_{compileId}_vertex.slang"),
                    vertexSource);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(programDumpDir, $"failed_program_{compileId}_fragment.slang"),
                    fragmentSource);
            }
            throw new SlangStageCompilationException(
                $"Slang program compile failed for '{vertexSourcePath}' and '{fragmentSourcePath}': {diagnostics}",
                ex);
        }
    }

    private static SlangStageCompilation CompilePhysicalCompute(
        Session physicalSession,
        string computeSource,
        int compileId,
        string computeEntryPointName,
        bool metalPerKindSlots,
        ShaderBindingLayout logicalLayout)
    {
        string computeSourcePath = $"xengine_compile_{compileId}/{ComputeSourcePath}";
        try
        {
            using Module computeModule = physicalSession.LoadModuleFromSourceString(
                $"xengine_compute_{compileId}_physical",
                $"physical/{computeSourcePath}",
                computeSource,
                out _);
            using EntryPoint computeEntryPoint = computeModule.FindAndCheckEntryPoint(
                computeEntryPointName,
                SlangShaderStage.Compute,
                out _);
            using ComponentType physicalProgram = physicalSession.CreateCompositeComponentType(
                [computeModule, computeEntryPoint],
                out _);
            byte[] bytecode = physicalProgram.GetEntryPointCode(0, TargetIndex, out _).ToArray();
            SlangStageReflection reflection = metalPerKindSlots
                ? new SlangStageReflection(
                    RuntimeSlangReflection.AttachPhysicalLayout(
                        logicalLayout,
                        physicalProgram,
                        TargetIndex,
                        ShaderStageMask.Compute,
                        metalPerKindSlots: true),
                    new ShaderVertexInputLayout())
                : RuntimeSlangReflection.ReflectLogical(
                    physicalProgram,
                    TargetIndex,
                    ShaderStageMask.Compute);
            return new SlangStageCompilation(bytecode, reflection);
        }
        catch (CompilationException ex)
        {
            string diagnostics = string.IsNullOrWhiteSpace(ex.Diagnostics.Message)
                ? ex.Message
                : ex.Diagnostics.Message.Trim();
            throw new SlangStageCompilationException(
                $"Slang compute compile failed for '{computeSourcePath}': {diagnostics}",
                ex);
        }
    }

    private static string CreatePreprocessorPreamble(
        IReadOnlyDictionary<string, bool>? keywords,
        IReadOnlyList<string>? extraDefines)
    {
        int capacity = (keywords?.Count ?? 0) + (extraDefines?.Count ?? 0);
        if (capacity == 0)
            return string.Empty;

        var names = new HashSet<string>(StringComparer.Ordinal);
        var preamble = new System.Text.StringBuilder(capacity * 24);

        if (keywords != null)
        {
            foreach ((string name, bool enabled) in keywords)
            {
                if (enabled && names.Add(name))
                    preamble.Append("#define ").Append(name).Append(" 1\n");
            }
        }

        if (extraDefines != null)
        {
            for (int i = 0; i < extraDefines.Count; i++)
            {
                string name = extraDefines[i];
                if (!string.IsNullOrWhiteSpace(name) && names.Add(name))
                    preamble.Append("#define ").Append(name).Append(" 1\n");
            }
        }

        return preamble.ToString();
    }

    private sealed class SlangStageCompilationException(string message, Exception innerException)
        : Exception(message, innerException);

    private readonly record struct SlangStageCompilation(
        byte[] Bytecode,
        SlangStageReflection Reflection);

    private readonly record struct SlangProgramCompilation(
        byte[] VertexBytecode,
        byte[] FragmentBytecode,
        SlangStageReflection VertexReflection,
        SlangStageReflection FragmentReflection);

    private enum SlangVulkanResourceKind
    {
        UnorderedAccess,
        Sampler,
        ShaderResource,
        ConstantBuffer,
    }
}
