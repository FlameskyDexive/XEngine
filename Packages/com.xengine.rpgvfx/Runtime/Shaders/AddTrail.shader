// Ported from Hovl Studio "AddTrail" (alpha-blended beam/trail gradient; the laser scripts drive _MainTexture_ST/_Noise_ST tiling at runtime).
Shader "Hovl/AddTrail"
Variants
{
    Variant("GPU_INSTANCING")
    Variant("PARTICLE_MESH")
}
Properties
{
    _MainTexture ("MainTexture", Texture2D) = "white"
    _MainTexture_ST ("MainTexture ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Noise ("Noise", Texture2D) = "white"
    _Noise_ST ("Noise ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _SpeedMainTexUVNoiseZW ("Speed MainTex U/V + Noise Z/W", Vector4) = (0.0, 0.0, 0.0, 0.0)
    _StartColor ("StartColor", Color) = (1.0, 0.0, 0.0, 1.0)
    _EndColor ("EndColor", Color) = (1.0, 1.0, 0.0, 1.0)
    _Colorpower ("Color power", Float) = 1.0
    _Colorrange ("Color range", Float) = 1.0
    _Emission ("Emission", Float) = 2.0
    _Usedark ("Use dark", Float) = 1.0
    _Mask ("Mask", Float) = 0.0
    _Maskpower ("Mask power", Float) = 10.0
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

        uniform sampler2D _MainTexture;
        uniform sampler2D _Noise;
        uniform sampler2D _CameraDepthTexture;
        uniform vec4 _MainTexture_ST;
        uniform vec4 _Noise_ST;
        uniform vec4 _SpeedMainTexUVNoiseZW;
        uniform vec4 _StartColor;
        uniform vec4 _EndColor;
        uniform float _Colorpower;
        uniform float _Colorrange;
        uniform float _Emission;
        uniform float _Usedark;
        uniform float _Mask;
        uniform float _Maskpower;
        uniform float _Usedepth;
        uniform float _Depthpower;

		void main()
		{
            float u = hovlUv.x;
            vec4 gradient = mix(_StartColor, _EndColor, clamp(pow(u * _Colorrange, _Colorpower), 0.0, 1.0));

            vec2 uvMain = hovlUv * _MainTexture_ST.xy + _MainTexture_ST.zw;
            vec2 mainUv = HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.xy, uvMain + hovlLife);
            vec4 mainTex = texture(_MainTexture, mainUv);

            vec2 uvNoise = hovlUv * _Noise_ST.xy + _Noise_ST.zw;
            vec2 noiseUv = HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.zw, uvNoise);
            float headFade = clamp(pow(1.0 - u, 0.8) * 1.0, 0.2, 0.6);
            vec4 dissolve = clamp(texture(_Noise, noiseUv) + headFade - u, 0.0, 1.0);
            float v = hovlUv.y;
            float shape = clamp((1.0 - u) * (1.0 - v) * v * 6.0
                * mix(1.0, u * _Maskpower, _Mask), 0.0, 1.0);

            float fade = HovlSoftFade(hovlEyeDepth,
                texture(_CameraDepthTexture, gl_FragCoord.xy / _ScreenParams.xy).r,
                _Depthpower, _Usedepth);
            vec4 coverage = hovlColor.a * fade * mainTex * dissolve * shape;
            vec3 rgb = (gradient * hovlColor * _Emission * mix(vec4(1.0), coverage, _Usedark)).rgb;
            fragColor = HovlToScreen(rgb, coverage.r);
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

        cbuffer HovlTrailMaterial : register(b2)
        {
            float4 _MainTexture_ST;
            float4 _Noise_ST;
            float4 _SpeedMainTexUVNoiseZW;
            float4 _StartColor;
            float4 _EndColor;
            float _Colorpower;
            float _Colorrange;
            float _Emission;
            float _Usedark;
            float _Mask;
            float _Maskpower;
            float _Usedepth;
            float _Depthpower;
            float2 _HovlPadding;
        };

        Texture2D _MainTexture : register(t0);
        SamplerState _MainTextureSampler : register(s0);
        Texture2D _Noise : register(t1);
        SamplerState _NoiseSampler : register(s1);
        Texture2D _CameraDepthTexture : register(t5);
        SamplerState _CameraDepthSampler : register(s5);

		float4 main(HovlVSOutput input) : SV_Target
		{
            float u = input.uv.x;
            float4 gradient = lerp(_StartColor, _EndColor,
                saturate(pow(u * _Colorrange, _Colorpower)));

            float2 uvMain = input.uv * _MainTexture_ST.xy + _MainTexture_ST.zw;
            float2 mainUv = HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.xy, uvMain + input.life);
            float4 mainTex = _MainTexture.Sample(_MainTextureSampler, mainUv);

            float2 uvNoise = input.uv * _Noise_ST.xy + _Noise_ST.zw;
            float2 noiseUv = HovlPanner(_Time.y, _SpeedMainTexUVNoiseZW.zw, uvNoise);
            float headFade = clamp(pow(1.0 - u, 0.8), 0.2, 0.6);
            float4 dissolve = saturate(_Noise.Sample(_NoiseSampler, noiseUv) + headFade - u);
            float v = input.uv.y;
            float shape = saturate((1.0 - u) * (1.0 - v) * v * 6.0
                * lerp(1.0, u * _Maskpower, _Mask));

            float2 screenUV = input.clipPos.xy / max(_ScreenParams.xy, 1.0.xx);
            float fade = HovlSoftFade(input.eyeDepth,
                _CameraDepthTexture.Sample(_CameraDepthSampler, screenUV).r,
                _Depthpower, _Usedepth);
            float4 coverage = input.color.a * fade * mainTex * dissolve * shape;
            float3 rgb = (gradient * input.color * _Emission * lerp(1.0.xxxx, coverage, _Usedark)).rgb;
            return HovlToScreen(rgb, coverage.r);
		}
	}
	ENDSLANG
}
