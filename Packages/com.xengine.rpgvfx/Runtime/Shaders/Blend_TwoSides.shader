// Ported from Hovl Studio "Blend_TwoSides" (alpha blend). Screen-radial falloff stands in for the source's view-normal fresnel on billboards (documented simplification; mesh path keeps the proxy too).
Shader "Hovl/Blend_TwoSides"
Variants
{
    Variant("GPU_INSTANCING")
    Variant("PARTICLE_MESH")
}
Properties
{
    _MainTex ("Main Tex", Texture2D) = "white"
    _MainTex_ST ("Main Tex ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Mask ("Mask", Texture2D) = "white"
    _Mask_ST ("Mask ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Noise ("Noise", Texture2D) = "white"
    _Noise_ST ("Noise ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _SpeedMainTexUVNoiseZW ("Speed MainTex U/V + Noise Z/W", Vector4) = (0.0, 0.0, 0.0, 0.0)
    _Emission ("Emission", Float) = 2.0
    _UseFresnel ("Use Fresnel?", Float) = 1.0
    _Fresnel ("Fresnel", Float) = 1.0
    _FresnelEmission ("Fresnel Emission", Float) = 1.0
    _FresnelColor ("Fresnel Color", Color) = (0.36, 0.09, 0.09, 1.0)
    _FrontFacesColor ("Front Faces Color", Color) = (0.0, 0.23, 1.0, 1.0)
    _BackFacesColor ("Back Faces Color", Color) = (1.0, 0.0, 0.0, 1.0)
    _Usedepth ("Use depth?", Float) = 0.0
    _Depthpower ("Depth power", Float) = 1.0
}

Pass "Particle"
{
    Tags { "RenderOrder" = "Transparent" }
    Cull Off
    ZWrite Off
    Blend Alpha

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
        uniform sampler2D _Mask;
        uniform sampler2D _Noise;
        uniform sampler2D _CameraDepthTexture;
        uniform vec4 _MainTex_ST;
        uniform vec4 _Mask_ST;
        uniform vec4 _Noise_ST;
        uniform vec4 _SpeedMainTexUVNoiseZW;
        uniform vec4 _FresnelColor;
        uniform vec4 _FrontFacesColor;
        uniform vec4 _BackFacesColor;
        uniform float _Emission;
        uniform float _UseFresnel;
        uniform float _Fresnel;
        uniform float _FresnelEmission;
        uniform float _Usedepth;
        uniform float _Depthpower;

		void main()
		{
            float radial = length(hovlUv - vec2(0.5)) * 2.0;
            float fresnel = pow(clamp(1.0 - radial, 0.0, 1.0), _Fresnel);

            vec2 uvMain = hovlUv * _MainTex_ST.xy + _MainTex_ST.zw;
            vec4 mainTex = texture(_MainTex, HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.xy, uvMain));
            vec2 uvNoise = hovlUv * _Noise_ST.xy + _Noise_ST.zw;
            vec4 noise = texture(_Noise, HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.zw, uvNoise));
            vec4 mask = texture(_Mask, hovlUv * _Mask_ST.xy + _Mask_ST.zw);

            vec4 baseColor = mix(_FrontFacesColor, _BackFacesColor, step(0.5, hovlRandom));
            vec4 col = baseColor;
            if (_UseFresnel > 0.5) {
                col = baseColor * (1.0 - fresnel) + _FresnelEmission * _FresnelColor * fresnel;
            }
            col *= mainTex * noise * hovlColor * _Emission;
            col.a *= mask.a;

            float fade = HovlSoftFade(hovlEyeDepth,
                texture(_CameraDepthTexture, gl_FragCoord.xy / _ScreenParams.xy).r,
                _Depthpower, _Usedepth);
            col.a *= fade;

            fragColor = HovlToScreen(col.rgb, col.a);
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

        cbuffer HovlTwoSidesMaterial : register(b2)
        {
            float4 _MainTex_ST;
            float4 _Mask_ST;
            float4 _Noise_ST;
            float4 _SpeedMainTexUVNoiseZW;
            float4 _FresnelColor;
            float4 _FrontFacesColor;
            float4 _BackFacesColor;
            float _Emission;
            float _UseFresnel;
            float _Fresnel;
            float _FresnelEmission;
            float _Usedepth;
            float _Depthpower;
            float3 _HovlPad;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _Mask : register(t1);
        SamplerState _MaskSampler : register(s1);
        Texture2D _Noise : register(t2);
        SamplerState _NoiseSampler : register(s2);
        Texture2D _CameraDepthTexture : register(t5);
        SamplerState _CameraDepthSampler : register(s5);

		float4 main(HovlVSOutput input) : SV_Target
		{
            float radial = length(input.uv - float2(0.5, 0.5)) * 2.0;
            float fresnel = pow(saturate(1.0 - radial), _Fresnel);

            float2 uvMain = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
            float4 mainTex = _MainTex.Sample(_MainTexSampler, HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.xy, uvMain));
            float2 uvNoise = input.uv * _Noise_ST.xy + _Noise_ST.zw;
            float4 noise = _Noise.Sample(_NoiseSampler, HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.zw, uvNoise));
            float4 mask = _Mask.Sample(_MaskSampler, input.uv * _Mask_ST.xy + _Mask_ST.zw);

            float4 baseColor = lerp(_FrontFacesColor, _BackFacesColor, step(0.5, input.random));
            float4 col = baseColor;
            if (_UseFresnel > 0.5) {
                col = baseColor * (1.0 - fresnel) + _FresnelEmission * _FresnelColor * fresnel;
            }
            col *= mainTex * noise * input.color * _Emission;
            col.a *= mask.a;

            float2 screenUV = input.clipPos.xy / max(_ScreenParams.xy, 1.0.xx);
            float fade = HovlSoftFade(input.eyeDepth,
                _CameraDepthTexture.Sample(_CameraDepthSampler, screenUV).r,
                _Depthpower, _Usedepth);
            col.a *= fade;

            return HovlToScreen(col.rgb, col.a);
		}
	}
	ENDSLANG
}
