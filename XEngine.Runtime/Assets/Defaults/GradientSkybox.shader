Shader "Default/GradientSkybox"

Variants
{
}

Properties
{
    _TopColor ("Top Color", Color) = (0.4, 0.6, 0.9, 1.0)
    _BottomColor ("Bottom Color", Color) = (0.8, 0.8, 0.7, 1.0)
    _Exponent ("Exponent", Float) = 1.0
}

Pass "GradientSkybox"
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

                vDirection = normalize(vertexPosition);
            }
        }

        Fragment
        {
            #include "XEngineCG"

            layout (location = 0) out vec4 fragColor;

            in vec3 vDirection;

            uniform vec4 _TopColor;
            uniform vec4 _BottomColor;
            uniform float _Exponent;

            void main()
            {
                float t = pow(clamp(vDirection.y * 0.5 + 0.5, 0.0, 1.0), _Exponent);
                vec3 color = mix(_BottomColor.rgb, _TopColor.rgb, t);
                fragColor = vec4(color, 1.0);
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
                VSOutput o;
                o.position = TransformSkyboxClip(input.vertexPosition);
                o.vDirection = normalize(input.vertexPosition);
                return o;
            }
        }

        Fragment
        {
            cbuffer GradientPS : register(b2)
            {
                float4 _TopColor;
                float4 _BottomColor;
                float _Exponent;
            };

            struct PSInput
            {
                float4 position : SV_Position;
                float3 vDirection : TEXCOORD0;
            };

            float4 main(PSInput input) : SV_Target
            {
                float t = pow(saturate(input.vDirection.y * 0.5 + 0.5), _Exponent);
                float3 color = lerp(_BottomColor.rgb, _TopColor.rgb, t);
                return float4(color, 1.0);
            }
        }
    ENDSLANG
}
