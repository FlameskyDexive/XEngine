// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>
/// Backend-specific shader compiler. OpenGL injects GLSL preamble; Vulkan/D3D12
/// invoke DXC to produce SPIR-V or DXIL.
/// </summary>
public interface IShaderCompiler
{
    /// <summary>Compile stage sources for the request's target backend.</summary>
    ShaderCompileResult Compile(ShaderCompileRequest request);
}
