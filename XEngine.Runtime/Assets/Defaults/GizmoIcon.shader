Shader "Default/GizmoIcon"

Variants
{
}

Properties
{
    _MainTex ("Icon", Texture2D) = "white"
    _IconColor ("Color", Vector4) = (1.0, 1.0, 1.0, 1.0)
    _IconCenter ("Center", Vector3) = (0.0, 0.0, 0.0)
    _IconScale ("Scale", Float) = 1.0
}

Pass "GizmoIcon"
{
    Tags { "RenderOrder" = "Transparent" }

    Cull Off
    Blend Alpha
    ZWrite Off
    ZTest Always

    GLSLPROGRAM
    Vertex
    {
        #include "XEngineCG"
        #include "VertexAttributes"

        out vec2 vUV;

        uniform vec3 _IconCenter;
        uniform float _IconScale;

        void main()
        {
            // World-space billboard: 1 meter * _IconScale, always facing camera
            float halfSize = _IconScale * 0.5;

            // Camera right/up from the view matrix
            vec3 camRight = vec3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[1][0], XENGINE_MATRIX_V[2][0]);
            vec3 camUp    = vec3(XENGINE_MATRIX_V[0][1], XENGINE_MATRIX_V[1][1], XENGINE_MATRIX_V[2][1]);

            // vertexPosition is -1..1 from fullscreen quad
            vec3 worldPos = _IconCenter
                + camRight * (vertexPosition.x * halfSize)
                + camUp    * (vertexPosition.y * halfSize);

            gl_Position = XENGINE_MATRIX_VP * vec4(worldPos, 1.0);
            vUV = vertexPosition.xy * 0.5 + 0.5;
        }
    }
    Fragment
    {
        #include "XEngineCG"

        in vec2 vUV;
        layout (location = 0) out vec4 finalColor;

        uniform sampler2D _MainTex;
        uniform vec4 _IconColor;
        uniform sampler2D _CameraDepthTexture;

        void main()
        {
            vec4 texColor = texture(_MainTex, vUV);
            vec4 color = texColor * _IconColor;

            if (color.a < 0.01) discard;

            // Depth-based dimming (same as gizmo shader)
            vec2 screenUV = gl_FragCoord.xy / _ScreenParams.xy;
            float sceneDepth = texture(_CameraDepthTexture, screenUV).r;
            float fragmentDepth = gl_FragCoord.z;
            float occluded = step(sceneDepth, fragmentDepth - 0.00001);
            if (occluded > 0.5)
            {
                color.rgb *= 0.5;
                color.a *= 0.3;
            }

            finalColor = color;
        }
    }
    ENDGLSL

    SLANGPROGRAM
    Vertex
    {
        #include "XEngineCG"

        cbuffer GizmoIconMaterial : register(b2)
        {
            float4 _IconColor;
            float3 _IconCenter;
            float _IconScale;
        };

        struct VSInput
        {
            float3 vertexPosition : POSITION;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 uv : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput output;
            float halfSize = _IconScale * 0.5;
            float3 cameraRight = float3(XENGINE_MATRIX_V._m00, XENGINE_MATRIX_V._m10, XENGINE_MATRIX_V._m20);
            float3 cameraUp = float3(XENGINE_MATRIX_V._m01, XENGINE_MATRIX_V._m11, XENGINE_MATRIX_V._m21);
            float3 worldPosition = _IconCenter
                + cameraRight * (input.vertexPosition.x * halfSize)
                + cameraUp * (input.vertexPosition.y * halfSize);
            output.position = mul(XENGINE_MATRIX_VP, float4(worldPosition, 1.0));
            output.uv = input.vertexPosition.xy * 0.5 + 0.5;
            return output;
        }
    }
    Fragment
    {
        #include "XEngineCG"

        cbuffer GizmoIconMaterial : register(b2)
        {
            float4 _IconColor;
            float3 _IconCenter;
            float _IconScale;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _CameraDepthTexture : register(t1);
        SamplerState _CameraDepthSampler : register(s1);

        struct PSInput
        {
            float4 position : SV_Position;
            float2 uv : TEXCOORD0;
        };

        float4 main(PSInput input) : SV_Target
        {
            float4 color = _MainTex.Sample(_MainTexSampler, input.uv) * _IconColor;
            if (color.a < 0.01)
                discard;

            float2 screenUV = input.position.xy / _ScreenParams.xy;
            float sceneDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, screenUV).r;
            float occluded = step(sceneDepth, input.position.z - 0.00001);
            if (occluded > 0.5)
            {
                color.rgb *= 0.5;
                color.a *= 0.3;
            }
            return color;
        }
    }
    ENDSLANG
}
