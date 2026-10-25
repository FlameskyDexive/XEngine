// Ported from Hovl Studio "LightGlow" (additive emissive panned main+noise).
Shader "Hovl/LightGlow"
Variants
{
    Variant("GPU_INSTANCING")
    Variant("PARTICLE_MESH")
}
Properties
{
    _MainTex ("MainTex", Texture2D) = "white"
    _MainTex_ST ("MainTex ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Noise ("Noise", Texture2D) = "white"
    _Noise_ST ("Noise ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _SpeedMainTexUVNoiseZW ("Speed MainTex U/V + Noise Z/W", Vector4) = (0.0, 0.0, 0.0, 0.0)
    _Color ("Color", Color) = (1.0, 1.0, 1.0, 1.0)
    _Emission ("Emission", Float) = 2.0
}

Pass "Particle"
{
    Tags { "RenderOrder" = "Transparent" }
    Cull Off
    ZWrite Off
    Blend Additive

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
        uniform sampler2D _Noise;
        uniform vec4 _MainTex_ST;
        uniform vec4 _Noise_ST;
        uniform vec4 _SpeedMainTexUVNoiseZW;
        uniform vec4 _Color;
        uniform float _Emission;

		void main()
		{
            vec4 mainTex = texture(_MainTex, HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.xy,
                hovlUv * _MainTex_ST.xy + _MainTex_ST.zw));
            vec4 noise = texture(_Noise, HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.zw,
                hovlUv * _Noise_ST.xy + _Noise_ST.zw));
            vec3 rgb = (mainTex * noise * _Color * hovlColor).rgb * _Emission;
            fragColor = HovlToScreen(rgb, hovlColor.a);
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

        cbuffer HovlGlowMaterial : register(b2)
        {
            float4 _MainTex_ST;
            float4 _Noise_ST;
            float4 _SpeedMainTexUVNoiseZW;
            float4 _Color;
            float _Emission;
            float3 _HovlPad;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _Noise : register(t1);
        SamplerState _NoiseSampler : register(s1);

		float4 main(HovlVSOutput input) : SV_Target
		{
        float4 mainTex = _MainTex.Sample(_MainTexSampler, HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.xy,
            input.uv * _MainTex_ST.xy + _MainTex_ST.zw));
        float4 noise = _Noise.Sample(_NoiseSampler, HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.zw,
            input.uv * _Noise_ST.xy + _Noise_ST.zw));
        float3 rgb = (mainTex * noise * _Color * input.color).rgb * _Emission;
        return HovlToScreen(rgb, input.color.a);
		}
	}
	ENDSLANG
}
