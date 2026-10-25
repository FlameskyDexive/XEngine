// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>Source language for a shader compile request.</summary>
public enum ShaderLanguage
{
    Glsl = 0,
    Hlsl = 1,
}

/// <summary>Bytecode / IR format produced by a compiler for a given backend.</summary>
public enum ShaderBytecodeFormat
{
    /// <summary>OpenGL: GLSL source strings (not true bytecode).</summary>
    GlslSource = 0,

    /// <summary>Vulkan: SPIR-V module bytes.</summary>
    SpirV = 1,

    /// <summary>Direct3D 12: DXIL container bytes.</summary>
    Dxil = 2,

    /// <summary>
    /// WebGPU: UTF-8 WGSL source text. WebGPU accepts no binary shader form, so unlike SPIR-V and
    /// DXIL this artifact stays human-readable - it is still shipped as a prebuilt artifact because
    /// the device side never runs a compiler.
    /// </summary>
    Wgsl = 3,

    /// <summary>
    /// Metal: <c>metallib</c> bytes from Slang <c>CompileTarget.MetalLib</c>. Produced on a macOS
    /// build host with Xcode; the device never compiles shader source.
    /// </summary>
    MetalLib = 4,

    /// <summary>
    /// WebGL2: UTF-8 GLSL ES 3.00 source text. Like WGSL this stays human-readable and is still
    /// shipped as a prebuilt artifact because the device never runs Slang.
    /// </summary>
    GlslEs300 = 5,
}

/// <summary>
/// The single source of truth for which bytecode format a backend's shader artifacts carry.
/// </summary>
/// <remarks>
/// Producers (compilers), the artifact constructor, and the artifact reader all have to agree, so
/// the mapping lives here rather than being repeated as a ternary at each site.
/// </remarks>
public static class ShaderBytecodeFormats
{
    /// <summary>
    /// Whether a backend consumes prebuilt shader artifacts at all. OpenGL compiles GLSL source at
    /// runtime and the Null device draws nothing, so neither participates.
    /// </summary>
    public static bool IsArtifactBackend(GraphicsBackend backend) =>
        backend is GraphicsBackend.Vulkan
            or GraphicsBackend.Direct3D12
            or GraphicsBackend.WebGPU
            or GraphicsBackend.Metal
            or GraphicsBackend.WebGL2;

    /// <summary>The bytecode format a backend's artifacts must carry.</summary>
    public static ShaderBytecodeFormat GetExpected(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.Vulkan => ShaderBytecodeFormat.SpirV,
        GraphicsBackend.Direct3D12 => ShaderBytecodeFormat.Dxil,
        GraphicsBackend.WebGPU => ShaderBytecodeFormat.Wgsl,
        GraphicsBackend.Metal => ShaderBytecodeFormat.MetalLib,
        GraphicsBackend.WebGL2 => ShaderBytecodeFormat.GlslEs300,
        _ => throw new ArgumentOutOfRangeException(
            nameof(backend), backend, "Shader artifacts require a modern backend."),
    };
}
