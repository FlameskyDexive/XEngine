// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>Output of <see cref="IShaderCompiler.Compile"/>.</summary>
public sealed class ShaderCompileResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    public ShaderCompilerKind CompilerKind { get; internal set; }

    public ShaderCompilerKind? FallbackFrom { get; internal set; }

    public string? FallbackReason { get; internal set; }

    public string? Diagnostics { get; internal set; }

    public ShaderBytecodeFormat Format { get; init; }

    /// <summary>OpenGL path: GLSL vertex source with version / defines injected.</summary>
    public string? GlslVertexSource { get; init; }

    /// <summary>OpenGL path: GLSL fragment source with version / defines injected.</summary>
    public string? GlslFragmentSource { get; init; }

    /// <summary>OpenGL path: optional geometry source.</summary>
    public string? GlslGeometrySource { get; init; }

    /// <summary>Vulkan / D3D12: vertex-stage bytecode (SPIR-V or DXIL).</summary>
    public byte[]? VertexBytecode { get; init; }

    /// <summary>Vulkan / D3D12: fragment-stage bytecode.</summary>
    public byte[]? FragmentBytecode { get; init; }

    /// <summary>Vulkan / D3D12 / Metal / WebGPU: compute-stage bytecode.</summary>
    public byte[]? ComputeBytecode { get; init; }

    /// <summary>Optional reflection layout parsed from the compiler output.</summary>
    public ShaderBindingLayout? BindingLayout { get; init; }

    /// <summary>Vertex semantic locations used by Vulkan pipeline input state.</summary>
    public ShaderVertexInputLayout? VertexInputLayout { get; init; }

    public static ShaderCompileResult Fail(string error) => new()
    {
        Success = false,
        ErrorMessage = error ?? throw new ArgumentNullException(nameof(error)),
    };
}
