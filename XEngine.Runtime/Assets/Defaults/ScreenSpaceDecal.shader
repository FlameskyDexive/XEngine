Shader "Default/ScreenSpaceDecal"

Variants
{
}

Properties
{
    _MainTex ("Texture", Texture2D) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _Opacity ("Opacity", Float) = 1.0
    _DecalRenderingLayers ("Rendering Layers", Int) = 255
}

Pass "ScreenSpaceDecal"
{
    Tags { "RenderOrder" = "Transparent" }
    Blend Alpha
    Cull Front
    ZWrite Off
    ZTest Greater

    GLSLPROGRAM

    Vertex
    {
        #include "XEngineCG"
        #include "VertexAttributes"

        void main()
        {
            gl_Position = TransformClip(vertexPosition);
        }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;
        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraNormalsTexture;
        uniform vec4 _MainColor;
        uniform float _Opacity;
        uniform int _DecalRenderingLayers;

        layout(location = 0) out vec4 fragColor;

        void main()
        {
            vec2 uv = gl_FragCoord.xy / _ScreenParams.xy;
            float rawDepth = texture(_CameraDepthTexture, uv).r;
            if (rawDepth >= 1.0)
                discard;

            vec4 clip = vec4(uv * 2.0 - 1.0, rawDepth * 2.0 - 1.0, 1.0);
            vec4 world = XENGINE_MATRIX_I_VP * clip;
            world.xyz /= world.w;

            vec3 local = (xengine_WorldToObject * vec4(world.xyz, 1.0)).xyz;
            if (any(greaterThan(abs(local), vec3(0.5))))
                discard;

            int objectLayers = int(texture(_CameraNormalsTexture, uv).a * 255.0 + 0.5);
            if ((_DecalRenderingLayers & objectLayers) == 0)
                discard;

            vec2 decalUv = local.xz + 0.5;
            vec4 albedo = texture(_MainTex, decalUv) * _MainColor;
            albedo.a *= _Opacity;
            if (albedo.a < 0.001)
                discard;
            fragColor = albedo;
        }
    }

    ENDGLSL

    SLANGPROGRAM
    Vertex
    {
        #include "XEngineCG"

        cbuffer ScreenSpaceDecalPS : register(b2)
        {
            int _DecalRenderingLayers;
            float _Opacity;
            float _NormalFade;
            float _DecalPadding;
            float4 _MainColor;
        };

        struct VSInput
        {
            float3 vertexPosition : POSITION;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
        };

        VSOutput main(VSInput input)
        {
            VSOutput o;
            o.position = TransformClip(input.vertexPosition);
            return o;
        }
    }

    Fragment
    {
        #include "XEngineCG"

        cbuffer ScreenSpaceDecalPS : register(b2)
        {
            int _DecalRenderingLayers;
            float _Opacity;
            float _NormalFade;
            float _DecalPadding;
            float4 _MainColor;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _CameraDepthTexture : register(t1);
        SamplerState _CameraDepthSampler : register(s1);
        Texture2D _CameraNormalsTexture : register(t2);
        SamplerState _CameraNormalsSampler : register(s2);

        struct PSInput
        {
            float4 position : SV_Position;
        };

        float4 main(PSInput input) : SV_Target
        {
            float2 uv = input.position.xy / _ScreenParams.xy;
            float rawDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, uv).r;
            if (rawDepth >= 1.0)
                discard;

            float4 clip = float4(uv * 2.0 - 1.0, rawDepth * 2.0 - 1.0, 1.0);
            float4 world = mul(XENGINE_MATRIX_I_VP, clip);
            world.xyz /= world.w;

            float3 local = mul(xengine_WorldToObject, float4(world.xyz, 1.0)).xyz;
            if (any(abs(local) > 0.5))
                discard;

            int objectLayers = (int)(_CameraNormalsTexture.Sample(_CameraNormalsSampler, uv).a * 255.0 + 0.5);
            if ((_DecalRenderingLayers & objectLayers) == 0)
                discard;

            float2 decalUv = local.xz + 0.5;
            float4 albedo = _MainTex.Sample(_MainTexSampler, decalUv) * _MainColor;
            albedo.a *= _Opacity;
            albedo.a *= 1.0 + (_NormalFade + _DecalPadding) * 0.0;
            if (albedo.a < 0.001)
                discard;
            return albedo;
        }
    }
    ENDSLANG
}
