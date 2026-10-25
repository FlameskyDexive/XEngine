// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Canonical include names under <c>Assets/Defaults</c> for forward shaders and the Shader Graph
/// compiler (M24). The parser appends <c>.glsl</c> or <c>.hlsl</c> from the program block.
/// </summary>
public static class ShaderLibrary
{
    public const string Config = "ShaderLibrary/ShaderConfig";
    public const string Core = "ShaderLibrary/Core";
    public const string Common = "XEngineCG";
    public const string VertexAttributes = "VertexAttributes";
    public const string Pbr = "PBR";
    public const string Shadow = "Shadow";
    public const string LightBvh = "LightBVH";
    public const string Lighting = "Lighting";
    public const string StandardLighting = "StandardLighting";
    public const string StandardShadowSampling = "StandardShadowSampling";
    public const string ImageBasedLighting = "ShaderLibrary/ImageBasedLighting";
    public const string LightCookie = "ShaderLibrary/LightCookie";
    public const string RenderingLayers = "ShaderLibrary/RenderingLayers";

    /// <summary>GLSL forward-lit stack (Standard / Terrain / Grass OpenGL path).</summary>
    public static readonly string[] ForwardLitGlslIncludes =
    [
        Config,
        Common,
        VertexAttributes,
        Pbr,
        Shadow,
        LightBvh,
        Lighting,
        ImageBasedLighting,
        LightCookie,
        RenderingLayers,
    ];

    /// <summary>Slang/HLSL forward-lit stack (Vulkan / D3D12 Standard path).</summary>
    public static readonly string[] ForwardLitHlslIncludes =
    [
        Config,
        Common,
        VertexAttributes,
        StandardShadowSampling,
        StandardLighting,
        ImageBasedLighting,
        LightCookie,
        RenderingLayers,
    ];
}
