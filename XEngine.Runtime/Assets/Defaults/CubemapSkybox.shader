Shader "Default/CubemapSkybox"

Variants
{
}

Properties
{
    _CubeRight ("Right (+X)", Texture2D) = "white"
    _CubeLeft ("Left (-X)", Texture2D) = "white"
    _CubeTop ("Top (+Y)", Texture2D) = "white"
    _CubeBottom ("Bottom (-Y)", Texture2D) = "white"
    _CubeFront ("Front (+Z)", Texture2D) = "white"
    _CubeBack ("Back (-Z)", Texture2D) = "white"
    _Tint ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _Exposure ("Exposure", Float) = 1.0
}

Pass "CubemapSkybox"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull None
    ZWrite Off
    ZTest Off

    GLSLPROGRAM

        Vertex
        {
            #include "XEngineCG"
            #include "VertexAttributes"

            out vec3 vDirection;

            void main()
            {
                gl_Position = TransformSkyboxClip(vertexPosition);

                vDirection = vertexPosition;
            }
        }

        Fragment
        {
            #include "XEngineCG"

            layout (location = 0) out vec4 fragColor;

            in vec3 vDirection;

            uniform sampler2D _CubeRight;
            uniform sampler2D _CubeLeft;
            uniform sampler2D _CubeTop;
            uniform sampler2D _CubeBottom;
            uniform sampler2D _CubeFront;
            uniform sampler2D _CubeBack;
            uniform vec4 _Tint;
            uniform float _Exposure;

            // Sample a cubemap face from 6 separate 2D textures
            vec4 sampleCubemap(vec3 dir)
            {
                vec3 absDir = abs(dir);
                vec2 uv;
                vec4 color;

                if (absDir.x >= absDir.y && absDir.x >= absDir.z)
                {
                    // X dominant
                    if (dir.x > 0.0)
                    {
                        uv = vec2(-dir.z, -dir.y) / absDir.x * 0.5 + 0.5;
                        color = texture(_CubeRight, uv);
                    }
                    else
                    {
                        uv = vec2(dir.z, -dir.y) / absDir.x * 0.5 + 0.5;
                        color = texture(_CubeLeft, uv);
                    }
                }
                else if (absDir.y >= absDir.x && absDir.y >= absDir.z)
                {
                    // Y dominant
                    if (dir.y > 0.0)
                    {
                        uv = vec2(dir.x, dir.z) / absDir.y * 0.5 + 0.5;
                        color = texture(_CubeTop, uv);
                    }
                    else
                    {
                        uv = vec2(dir.x, -dir.z) / absDir.y * 0.5 + 0.5;
                        color = texture(_CubeBottom, uv);
                    }
                }
                else
                {
                    // Z dominant
                    if (dir.z > 0.0)
                    {
                        uv = vec2(dir.x, -dir.y) / absDir.z * 0.5 + 0.5;
                        color = texture(_CubeFront, uv);
                    }
                    else
                    {
                        uv = vec2(-dir.x, -dir.y) / absDir.z * 0.5 + 0.5;
                        color = texture(_CubeBack, uv);
                    }
                }

                return color;
            }

            void main()
            {
                vec3 dir = normalize(vDirection);
                vec4 color = sampleCubemap(dir);
                color.rgb *= _Tint.rgb * _Exposure;

                fragColor = vec4(color.rgb, 1.0);
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include "XEngineCG"

            struct VSInput
            {
                float3 vertexPosition : POSITION;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float3 vDirection : TEXCOORD0;
            };

            VSOutput main(VSInput input)
            {
                VSOutput output;
                output.position = TransformSkyboxClip(input.vertexPosition);
                output.vDirection = input.vertexPosition;
                return output;
            }
        }

        Fragment
        {
            cbuffer CubemapSkyboxPS : register(b2)
            {
                float4 _Tint;
                float _Exposure;
                float3 _CubemapSkyboxPadding;
            };

            Texture2D _CubeRight : register(t0);
            SamplerState _CubeRightSampler : register(s0);
            Texture2D _CubeLeft : register(t1);
            SamplerState _CubeLeftSampler : register(s1);
            Texture2D _CubeTop : register(t2);
            SamplerState _CubeTopSampler : register(s2);
            Texture2D _CubeBottom : register(t3);
            SamplerState _CubeBottomSampler : register(s3);
            Texture2D _CubeFront : register(t4);
            SamplerState _CubeFrontSampler : register(s4);
            Texture2D _CubeBack : register(t5);
            SamplerState _CubeBackSampler : register(s5);

            struct PSInput
            {
                float4 position : SV_Position;
                float3 vDirection : TEXCOORD0;
            };

            float4 SampleCubemap(float3 direction)
            {
                float3 absoluteDirection = abs(direction);
                float2 uv;
                if (absoluteDirection.x >= absoluteDirection.y && absoluteDirection.x >= absoluteDirection.z)
                {
                    if (direction.x > 0.0)
                    {
                        uv = float2(-direction.z, -direction.y) / absoluteDirection.x * 0.5 + 0.5;
                        return _CubeRight.Sample(_CubeRightSampler, uv);
                    }
                    uv = float2(direction.z, -direction.y) / absoluteDirection.x * 0.5 + 0.5;
                    return _CubeLeft.Sample(_CubeLeftSampler, uv);
                }
                if (absoluteDirection.y >= absoluteDirection.x && absoluteDirection.y >= absoluteDirection.z)
                {
                    if (direction.y > 0.0)
                    {
                        uv = float2(direction.x, direction.z) / absoluteDirection.y * 0.5 + 0.5;
                        return _CubeTop.Sample(_CubeTopSampler, uv);
                    }
                    uv = float2(direction.x, -direction.z) / absoluteDirection.y * 0.5 + 0.5;
                    return _CubeBottom.Sample(_CubeBottomSampler, uv);
                }
                if (direction.z > 0.0)
                {
                    uv = float2(direction.x, -direction.y) / absoluteDirection.z * 0.5 + 0.5;
                    return _CubeFront.Sample(_CubeFrontSampler, uv);
                }
                uv = float2(-direction.x, -direction.y) / absoluteDirection.z * 0.5 + 0.5;
                return _CubeBack.Sample(_CubeBackSampler, uv);
            }

            float4 main(PSInput input) : SV_Target
            {
                float3 color = SampleCubemap(normalize(input.vDirection)).rgb;
                color *= _Tint.rgb * _Exposure;
                return float4(color, 1.0);
            }
        }
    ENDSLANG
}
