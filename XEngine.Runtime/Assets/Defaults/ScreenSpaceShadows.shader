Shader "Default/ScreenSpaceShadows"

Variants
{
}

Properties
{
    _Steps ("Steps", Int) = 8
    _Thickness ("Thickness", Float) = 0.03
    _RayLength ("Ray Length", Float) = 0.25
}

Pass "ScreenSpaceShadows"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Off
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;

        void main()
        {
            gl_Position = vec4(vertexPosition, 1.0);
            TexCoords = vertexTexCoord;
        }
    }

    Fragment
    {
        #include "ShaderLibrary/ShaderConfig"
        #include "XEngineCG"

        uniform sampler2D _CameraDepthTexture;
        uniform vec3 _DirectionalLightDirection;
        uniform int _DirectionalLightEnabled;
        uniform int _Steps;
        uniform float _Thickness;
        uniform float _RayLength;

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        void main()
        {
            if (_DirectionalLightEnabled == 0)
            {
                fragColor = vec4(1.0);
                return;
            }

            float rawDepth = texture(_CameraDepthTexture, TexCoords).r;
            if (rawDepth >= 1.0)
            {
                fragColor = vec4(1.0);
                return;
            }

            vec3 viewPos = getViewPos(TexCoords, rawDepth);
            vec3 viewLight = normalize((XENGINE_MATRIX_V * vec4(_DirectionalLightDirection, 0.0)).xyz);
            int steps = max(_Steps, 1);
            float lit = 1.0;

            for (int i = 1; i <= MAX_SOFT_SHADOW_TAPS; i++)
            {
                if (i > steps)
                    break;
                float t = (float(i) / float(steps)) * _RayLength;
                vec3 sampleView = viewPos + viewLight * t;
                vec3 screen = getScreenFromViewPos(sampleView);
                if (screen.x < 0.0 || screen.x > 1.0 || screen.y < 0.0 || screen.y > 1.0)
                    break;
                float sceneDepth = texture(_CameraDepthTexture, screen.xy).r;
                if (sceneDepth >= 1.0)
                    continue;
                vec3 sceneView = getViewPos(screen.xy, sceneDepth);
                float delta = sceneView.z - sampleView.z;
                if (delta > 0.0 && delta < _Thickness)
                {
                    lit = 0.0;
                    break;
                }
            }

            fragColor = vec4(lit, lit, lit, 1.0);
        }
    }

    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        struct VSInput
        {
            float3 vertexPosition : POSITION;
            float2 vertexTexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.TexCoords = input.vertexTexCoord;
            output.position = float4(input.vertexPosition, 1.0);
            return output;
        }
    }

    Fragment
    {
        #include "XEngineCG"
        #include "ShaderLibrary/ShaderConfig"

        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        cbuffer ScreenSpaceShadowsPS : register(b2)
        {
            int _Steps;
            float _Thickness;
            float _RayLength;
            int _DirectionalLightEnabled;
            float4 _DirectionalLightDirection;
        };

        Texture2D _CameraDepthTexture : register(t0);
        SamplerState _CameraDepthSampler : register(s0);

        float3 ViewPosition(float2 uv, float depth)
        {
            float4 clip = float4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
            float4 view = mul(xengine_MatIP, clip);
            return view.xyz / view.w;
        }

        float2 ScreenFromView(float3 viewPos)
        {
            float4 clip = mul(xengine_MatP, float4(viewPos, 1.0));
            float3 ndc = clip.xyz / clip.w;
            return ndc.xy * 0.5 + 0.5;
        }

        float4 main(PSInput input) : SV_Target
        {
            if (_DirectionalLightEnabled == 0)
                return 1.0.xxxx;

            float rawDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r;
            if (rawDepth >= 1.0)
                return 1.0.xxxx;

            float3 viewPos = ViewPosition(input.TexCoords, rawDepth);
            float3 viewLight = normalize(mul((float3x3)XENGINE_MATRIX_V, _DirectionalLightDirection.xyz));
            int steps = max(_Steps, 1);
            float lit = 1.0;

            [loop]
            for (int i = 1; i <= MAX_SOFT_SHADOW_TAPS; i++)
            {
                if (i > steps)
                    break;
                float t = (float(i) / float(steps)) * _RayLength;
                float3 sampleView = viewPos + viewLight * t;
                float2 screen = ScreenFromView(sampleView);
                if (screen.x < 0.0 || screen.x > 1.0 || screen.y < 0.0 || screen.y > 1.0)
                    break;
                float sceneDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, screen).r;
                if (sceneDepth >= 1.0)
                    continue;
                float3 sceneView = ViewPosition(screen, sceneDepth);
                float delta = sceneView.z - sampleView.z;
                if (delta > 0.0 && delta < _Thickness)
                {
                    lit = 0.0;
                    break;
                }
            }

            return float4(lit, lit, lit, 1.0);
        }
    }

    ENDSLANG
}
