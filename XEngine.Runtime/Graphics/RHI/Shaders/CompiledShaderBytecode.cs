// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>
/// Holds compiled HLSL bytecode (SPIR-V or DXIL) plus optional reflection for
/// Vulkan / Direct3D12 backends. OpenGL continues to use <see cref="GraphicsProgram"/>.
/// </summary>
public sealed class CompiledShaderBytecode
{
    public ShaderLanguage Language { get; }
    public ShaderBytecodeFormat Format { get; }
    public byte[] VertexBytecode { get; }
    public byte[] FragmentBytecode { get; }
    public byte[] ComputeBytecode { get; }
    public ShaderBindingLayout? BindingLayout { get; }
    public ShaderVertexInputLayout? VertexInputLayout { get; }

    public bool HasGraphics => VertexBytecode.Length > 0 && FragmentBytecode.Length > 0;
    public bool HasCompute => ComputeBytecode.Length > 0;

    public CompiledShaderBytecode(
        ShaderLanguage language,
        ShaderBytecodeFormat format,
        byte[] vertexBytecode,
        byte[] fragmentBytecode,
        ShaderBindingLayout? bindingLayout = null,
        ShaderVertexInputLayout? vertexInputLayout = null,
        byte[]? computeBytecode = null)
    {
        Language = language;
        Format = format;
        VertexBytecode = vertexBytecode ?? throw new ArgumentNullException(nameof(vertexBytecode));
        FragmentBytecode = fragmentBytecode ?? throw new ArgumentNullException(nameof(fragmentBytecode));
        ComputeBytecode = computeBytecode ?? [];
        BindingLayout = bindingLayout;
        VertexInputLayout = vertexInputLayout;
    }
}
