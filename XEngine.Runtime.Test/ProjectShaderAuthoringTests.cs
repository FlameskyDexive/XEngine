// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// N4 acceptance: the "Assets/Create/Shader" template is dual-source (GLSLPROGRAM + SLANGPROGRAM),
/// so a newly authored project shader parses with both an OpenGL program and a modern-backend
/// (Slang) program. This guards the parse-level contract; the offline Slang compile itself is
/// exercised by the shader compiler tests.
/// </summary>
public sealed class ProjectShaderAuthoringTests
{
    private const string DualSourceShader = """
Shader "Custom/NewShader"
Properties
{
    _MainTex ("Albedo", Texture2D) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
}
Pass "Default"
{
    GLSLPROGRAM
        Vertex
        {
            void main()
            {
                gl_Position = vec4(0.0, 0.0, 0.0, 1.0);
            }
        }
        Fragment
        {
            layout (location = 0) out vec4 fragColor;
            void main()
            {
                fragColor = vec4(1.0, 1.0, 1.0, 1.0);
            }
        }
    ENDGLSL
    SLANGPROGRAM
        Vertex
        {
            cbuffer CustomMaterial : register(b2)
            {
                float4 _MainColor;
            };
            struct VSInput { float3 vertexPosition : POSITION; };
            float4 main(VSInput input) : SV_Position
            {
                return float4(input.vertexPosition, 1.0);
            }
        }
        Fragment
        {
            cbuffer CustomMaterial : register(b2)
            {
                float4 _MainColor;
            };
            float4 main(float4 position : SV_Position) : SV_Target
            {
                return _MainColor;
            }
        }
    ENDSLANG
}
""";

    [Fact]
    public void DualSourceTemplateParsesWithBothPrograms()
    {
        bool parsed = ShaderParser.ParseShader("Assets/Custom/NewShader.shader", DualSourceShader, out Shader? shader);

        Assert.True(parsed);
        Assert.NotNull(shader);
        Assert.Equal("Custom/NewShader", shader!.Name);

        var pass = shader.GetPass(0);
        Assert.True(pass.HasGlsl, "template must retain an OpenGL (GLSLPROGRAM) program");
        Assert.True(pass.HasHlsl, "template must declare a modern-backend (SLANGPROGRAM) program");
        Assert.True(pass.HasSlang, "modern program must be Slang-sourced");
    }
}
