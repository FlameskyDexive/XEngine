// Ported from Hovl Studio "SmoothSmoke" (alpha blend, soft-particle smoke).
Shader "Hovl/SmoothSmoke"
Variants
{
    Variant("GPU_INSTANCING")
    Variant("PARTICLE_MESH")
}
Properties
{
    _MainTex ("MainTex", Texture2D) = "white"
    _MainTex_ST ("MainTex ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Color ("Color", Color) = (1.0, 1.0, 1.0, 1.0)
    _Emission ("Emission", Float) = 2.0
    _Useblack ("Use black", Float) = 0.0
    _Usedepth ("Use depth?", Float) = 0.0
    _Depthpower ("Depth power", Float) = 1.0
}

Pass "Particle"
{
    Tags { "RenderOrder" = "Transparent" }
    Cull Off
    ZWrite Off
    Blend Alpha
    ColorMask RGB

	GLSLPROGRAM
	Vertex
	{
		#define HOVL_STAGE_VERTEX
		#include "HovlCore"

		void main()
		{
			vec4 clipPos;
			HovlBillboardVS(vertexPosition, vertexTexCoord0, clipPos);
			gl_Position = clipPos;
		}
	}
	Fragment
	{
		#define HOVL_STAGE_FRAGMENT
		#include "HovlCore"

		layout (location = 0) out vec4 fragColor;

        uniform sampler2D _MainTex;
        uniform sampler2D _CameraDepthTexture;
        uniform vec4 _MainTex_ST;
        uniform vec4 _Color;
        uniform float _Emission;
        uniform float _Useblack;
        uniform float _Usedepth;
        uniform float _Depthpower;

		void main()
		{
            // Unity's Custom1.xy stream controls the dissolve window in UV0.zw.
            vec2 uvMain = hovlUv * _MainTex_ST.xy + _MainTex_ST.zw;
            float alpha = texture(_MainTex, uvMain).a;
            float s = smoothstep(hovlLife, hovlLife + max(hovlRandom, 0.00001), alpha);
            float mask = clamp(s, 0.0, 1.0);
            vec3 rgb = (_Color * mix(1.0, mask, _Useblack) * _Emission * hovlColor).rgb;
            float a = _Color.a * mask * hovlColor.a;

            float fade = HovlSoftFade(hovlEyeDepth,
                texture(_CameraDepthTexture, gl_FragCoord.xy / _ScreenParams.xy).r,
                _Depthpower, _Usedepth);
            a *= fade;

            fragColor = HovlToScreen(rgb, a);
		}
	}
	ENDGLSL

	SLANGPROGRAM
	Vertex
	{
		#define HOVL_STAGE_VERTEX
		#include "HovlCore"

		HovlVSOutput main(HovlVSInput input)
		{
			HovlVSOutput o;
			HovlBillboardVS(input, o);
			return o;
		}
	}
	Fragment
	{
		#define HOVL_STAGE_FRAGMENT
		#include "HovlCore"

        cbuffer HovlSmokeMaterial : register(b2)
        {
            float4 _MainTex_ST;
            float4 _Color;
            float _Emission;
            float _Useblack;
            float _Usedepth;
            float _Depthpower;
            float4 _HovlPadding;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _CameraDepthTexture : register(t5);
        SamplerState _CameraDepthSampler : register(s5);

		float4 main(HovlVSOutput input) : SV_Target
		{
            // Unity's Custom1.xy stream controls the dissolve window in UV0.zw.
            float alpha = _MainTex.Sample(_MainTexSampler, input.uv * _MainTex_ST.xy + _MainTex_ST.zw).a;
            float s = smoothstep(input.life, input.life + max(input.random, 0.00001), alpha);
            float mask = saturate(s);
            float3 rgb = (_Color * lerp(1.0, mask, _Useblack) * _Emission * input.color).rgb;
            float a = _Color.a * mask * input.color.a;

            float2 screenUV = input.clipPos.xy / max(_ScreenParams.xy, 1.0.xx);
            float fade = HovlSoftFade(input.eyeDepth,
                _CameraDepthTexture.Sample(_CameraDepthSampler, screenUV).r,
                _Depthpower, _Usedepth);
            a *= fade;

            return HovlToScreen(rgb, a);
		}
	}
	ENDSLANG
}
