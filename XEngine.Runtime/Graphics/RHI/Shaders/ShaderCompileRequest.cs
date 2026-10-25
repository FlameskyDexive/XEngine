// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>
/// Platform environment used when compiling backend-specific shader bytecode. Most backends
/// use <see cref="Default"/>; Metal distinguishes desktop macOS, iOS device, and iOS Simulator
/// libraries because MetalLib files are not interchangeable across those platforms.
/// </summary>
public enum ShaderTargetEnvironment
{
    /// <summary>Use the compiler's default environment for the selected backend.</summary>
    Default = 0,

    /// <summary>Compile a Metal library for macOS.</summary>
    MacOS = 1,

    /// <summary>Compile a Metal library for iOS devices.</summary>
    IOS = 2,

    /// <summary>Compile a Metal library for iOS Simulator.</summary>
    IOSSimulator = 3,
}

/// <summary>Input to <see cref="IShaderCompiler.Compile"/>.</summary>
public sealed class ShaderCompileRequest
{
    public required GraphicsBackend TargetBackend { get; init; }
    public ShaderTargetEnvironment TargetEnvironment { get; init; } = ShaderTargetEnvironment.Default;
    public required ShaderLanguage Language { get; init; }

    /// <summary>Vertex-stage source (GLSL or HLSL text).</summary>
    public required string VertexSource { get; init; }

    /// <summary>Fragment / pixel-stage source (GLSL or HLSL text).</summary>
    public required string FragmentSource { get; init; }

    /// <summary>Optional geometry-stage source.</summary>
    public string? GeometrySource { get; init; }

    /// <summary>Optional compute-stage source. Compute-only requests may leave vertex/fragment empty.</summary>
    public string? ComputeSource { get; init; }

    /// <summary>Vertex entry point name (HLSL / SPIR-V). Defaults to <c>main</c>.</summary>
    public string VertexEntryPoint { get; init; } = "main";

    /// <summary>Fragment entry point name. Defaults to <c>main</c>.</summary>
    public string FragmentEntryPoint { get; init; } = "main";

    /// <summary>Compute entry point name. Defaults to <c>main</c>.</summary>
    public string ComputeEntryPoint { get; init; } = "main";

    /// <summary>Preprocessor keywords / defines (name → enabled).</summary>
    public IReadOnlyDictionary<string, bool>? Keywords { get; init; }

    /// <summary>Extra <c>#define</c> names injected before stage sources.</summary>
    public IReadOnlyList<string>? ExtraDefines { get; init; }

    /// <summary>Optional asset identity used only for compiler selection diagnostics.</summary>
    public string? ShaderName { get; init; }

    /// <summary>Optional pass identity used only for compiler selection diagnostics.</summary>
    public string? PassName { get; init; }

    /// <summary>Optional normalized variant key used only for compiler selection diagnostics.</summary>
    public string? VariantKey { get; init; }
}
