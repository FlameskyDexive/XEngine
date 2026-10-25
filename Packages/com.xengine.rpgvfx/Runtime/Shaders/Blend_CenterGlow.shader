// Ported from Hovl Studio "Blend_CenterGlow" (alpha blend).
Shader "Hovl/Blend_CenterGlow"
Variants
{
    Variant("GPU_INSTANCING")
    Variant("PARTICLE_MESH")
}
Properties
{
    _MainTex ("MainTex", Texture2D) = "white"
    _Noise ("Noise", Texture2D) = "white"
    _Flow ("Flow", Texture2D) = "white"
    _Mask ("Mask", Texture2D) = "white"
    _MainTex_ST ("MainTex ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Noise_ST ("Noise ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Flow_ST ("Flow ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Mask_ST ("Mask ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _SpeedMainTexUVNoiseZW ("Speed MainTex U/V + Noise Z/W", Vector4) = (0.0, 0.0, 0.0, 0.0)
    _DistortionSpeedXYPowerZ ("Distortion Speed XY Power Z", Vector4) = (0.0, 0.0, 0.0, 0.0)
    _Emission ("Emission", Float) = 2.0
    _Color ("Color", Color) = (0.5, 0.5, 0.5, 1.0)
    _Usecenterglow ("Use center glow?", Float) = 0.0
    _Usedepth ("Use depth?", Float) = 0.0
    _Usecustomrandom ("Use custom random?", Float) = 0.0
    _Depthpower ("Depth power", Float) = 1.0
    _Opacity ("Opacity", Float) = 1.0
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
        uniform sampler2D _Noise;
        uniform sampler2D _Flow;
        uniform sampler2D _Mask;
        uniform sampler2D _CameraDepthTexture;
        uniform vec4 _MainTex_ST;
        uniform vec4 _Noise_ST;
        uniform vec4 _Flow_ST;
        uniform vec4 _Mask_ST;
        uniform vec4 _SpeedMainTexUVNoiseZW;
        uniform vec4 _DistortionSpeedXYPowerZ;
        uniform vec4 _Color;
        uniform float _Emission;
        uniform float _Usecenterglow;
        uniform float _Usedepth;
        uniform float _Usecustomrandom;
        uniform float _Depthpower;
        uniform float _Opacity;

		void main()
		{
            vec2 uv0 = hovlUv;
            vec2 uvMain = uv0 * _MainTex_ST.xy + _MainTex_ST.zw;
            vec2 pannerMain = HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.xy, uvMain);
            vec2 uvFlow = uv0 * _Flow_ST.xy + _Flow_ST.zw;
            vec2 pannerFlow = HovlPanner(_Time.y, _DistortionSpeedXYPowerZ.xy, uvFlow);
            vec4 mask = texture(_Mask, uv0 * _Mask_ST.xy + _Mask_ST.zw);
            vec4 flow = texture(_Flow, pannerFlow);
            vec4 mainTex = texture(_MainTex, pannerMain - (flow * mask).rg * _DistortionSpeedXYPowerZ.z);

            float ur = 0.0;
            vec2 pannerNoise = HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.zw,
                uv0 * _Noise_ST.xy + _Noise_ST.zw + ur);
            vec4 noise = texture(_Noise, pannerNoise);

            vec4 base = mainTex * noise * _Color * hovlColor;

            float invAge = 1.0 - hovlLife;
            vec4 glow = mask - vec4(invAge);
            glow = clamp(mask * glow, 0.0, 1.0);
            vec4 col = vec4(mix(base.rgb, base.rgb * glow.rgb, _Usecenterglow) * _Emission,
                base.a * _Opacity);

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

        cbuffer HovlCenterGlowMaterial : register(b2)
        {
            float4 _MainTex_ST;
            float4 _Noise_ST;
            float4 _Flow_ST;
            float4 _Mask_ST;
            float4 _SpeedMainTexUVNoiseZW;
            float4 _DistortionSpeedXYPowerZ;
            float4 _Color;
            float _Emission;
            float _Usecenterglow;
            float _Usedepth;
            float _Usecustomrandom;
            float _Depthpower;
            float _Opacity;
            float4 _HovlPadding;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _Noise : register(t1);
        SamplerState _NoiseSampler : register(s1);
        Texture2D _Flow : register(t2);
        SamplerState _FlowSampler : register(s2);
        Texture2D _Mask : register(t3);
        SamplerState _MaskSampler : register(s3);
        Texture2D _CameraDepthTexture : register(t5);
        SamplerState _CameraDepthSampler : register(s5);

		float4 main(HovlVSOutput input) : SV_Target
		{
            float2 uv0 = input.uv;
            float2 uvMain = uv0 * _MainTex_ST.xy + _MainTex_ST.zw;
            float2 pannerMain = HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.xy, uvMain);
            float2 uvFlow = uv0 * _Flow_ST.xy + _Flow_ST.zw;
            float2 pannerFlow = HovlPanner(_Time.y, _DistortionSpeedXYPowerZ.xy, uvFlow);
            float4 mask = _Mask.Sample(_MaskSampler, uv0 * _Mask_ST.xy + _Mask_ST.zw);
            float4 flow = _Flow.Sample(_FlowSampler, pannerFlow);
            float4 mainTex = _MainTex.Sample(_MainTexSampler,
                pannerMain - (flow * mask).rg * _DistortionSpeedXYPowerZ.z);

            float ur = 0.0;
            float2 pannerNoise = HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.zw,
                uv0 * _Noise_ST.xy + _Noise_ST.zw + ur);
            float4 noise = _Noise.Sample(_NoiseSampler, pannerNoise);

            float4 base = mainTex * noise * _Color * input.color;

            float invAge = 1.0 - input.life;
            float4 glow = mask - float4(invAge, invAge, invAge, invAge);
            glow = saturate(mask * glow);
            float4 col = float4(lerp(base.rgb, base.rgb * glow.rgb, _Usecenterglow) * _Emission,
                base.a * _Opacity);

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
