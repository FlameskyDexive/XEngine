// Ported from Hovl Studio "DissolveNoise" (alpha blend; scrolling noise tint + dissolve threshold keyed by normalized lifetime with edge color).
Shader "Hovl/DissolveNoise"
Variants
{
    Variant("GPU_INSTANCING")
    Variant("PARTICLE_MESH")
}
Properties
{
    _MainTex ("MainTex", Texture2D) = "white"
    _MainTex_ST ("MainTex ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _TextureNoise ("Texture Noise", Texture2D) = "white"
    _TextureNoise_ST ("Texture Noise ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Dissolvenoise ("Dissolve noise", Texture2D) = "white"
    _Dissolvenoise_ST ("Dissolve noise ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _NoisespeedXYEmissonZPowerW ("Noise speed XY / Emission Z / Power W", Vector4) = (0.5, 0.0, 2.0, 1.0)
    _DissolvespeedXY ("Dissolve speed XY", Vector4) = (0.0, 0.0, 0.0, 0.0)
    _Maincolor ("Main color", Color) = (0.76, 0.85, 0.94, 1.0)
    _Noisecolor ("Noise color", Color) = (0.25, 0.30, 0.36, 1.0)
    _Dissolvecolor ("Dissolve color", Color) = (1.0, 1.0, 1.0, 1.0)
    _Opacity ("Opacity", Float) = 1.0
    _Usedepth ("Use depth?", Float) = 0.0
    _InvFade ("Soft Particles Factor", Float) = 1.0
}

Pass "Particle"
{
    ColorMask RGB
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
        uniform sampler2D _TextureNoise;
        uniform sampler2D _Dissolvenoise;
        uniform sampler2D _CameraDepthTexture;
        uniform vec4 _MainTex_ST;
        uniform vec4 _TextureNoise_ST;
        uniform vec4 _Dissolvenoise_ST;
        uniform vec4 _NoisespeedXYEmissonZPowerW;
        uniform vec4 _DissolvespeedXY;
        uniform vec4 _Maincolor;
        uniform vec4 _Noisecolor;
        uniform vec4 _Dissolvecolor;
        uniform float _Opacity;
        uniform float _Usedepth;
        uniform float _InvFade;

		void main()
		{
            vec2 uv0 = hovlUv;
            float noisePower = _NoisespeedXYEmissonZPowerW.w;
            vec2 noisePanner = HovlPanner(_Time.y, _NoisespeedXYEmissonZPowerW.xy,
                uv0 * _TextureNoise_ST.xy + _TextureNoise_ST.zw);
            vec4 tint = clamp(pow(texture(_TextureNoise, noisePanner), vec4(noisePower)) * noisePower, 0.0, 1.0);
            vec3 rgb = mix(_Maincolor.rgb, _Noisecolor.rgb, tint.rgb);

            vec2 dissolvePanner = HovlPanner(_Time.y, _DissolvespeedXY.xy,
                uv0 * _Dissolvenoise_ST.xy + _Dissolvenoise_ST.zw);
            float dissolve = texture(_Dissolvenoise, dissolvePanner).r;
            float clip = dissolve - hovlLife;
            if (clip < 0.0)
                discard;
            float edge = smoothstep(0.0, 0.2, clip);
            rgb = mix(_Dissolvecolor.rgb, rgb, edge);

            float fade = HovlSoftFade(hovlEyeDepth,
                texture(_CameraDepthTexture, gl_FragCoord.xy / _ScreenParams.xy).r,
                _InvFade, _Usedepth);
            float a = _Maincolor.a * _Opacity * hovlColor.a * fade;
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

        cbuffer HovlDissolveMaterial : register(b2)
        {
            float4 _MainTex_ST;
            float4 _TextureNoise_ST;
            float4 _Dissolvenoise_ST;
            float4 _NoisespeedXYEmissonZPowerW;
            float4 _DissolvespeedXY;
            float4 _Maincolor;
            float4 _Noisecolor;
            float4 _Dissolvecolor;
            float _Opacity;
            float _Usedepth;
            float _InvFade;
            float _HovlPad;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _TextureNoise : register(t1);
        SamplerState _TextureNoiseSampler : register(s1);
        Texture2D _Dissolvenoise : register(t2);
        SamplerState _DissolvenoiseSampler : register(s2);
        Texture2D _CameraDepthTexture : register(t5);
        SamplerState _CameraDepthSampler : register(s5);

		float4 main(HovlVSOutput input) : SV_Target
		{
        float2 uv0 = input.uv;
        float noisePower = _NoisespeedXYEmissonZPowerW.w;
        float2 noisePanner = HovlPanner(_Time.y, _NoisespeedXYEmissonZPowerW.xy,
            uv0 * _TextureNoise_ST.xy + _TextureNoise_ST.zw);
        float4 tint = saturate(pow(_TextureNoise.Sample(_TextureNoiseSampler, noisePanner), noisePower) * noisePower);
        float3 rgb = lerp(_Maincolor.rgb, _Noisecolor.rgb, tint.rgb);

        float2 dissolvePanner = HovlPanner(_Time.y, _DissolvespeedXY.xy,
            uv0 * _Dissolvenoise_ST.xy + _Dissolvenoise_ST.zw);
        float dissolve = _Dissolvenoise.Sample(_DissolvenoiseSampler, dissolvePanner).r;
        float clip = dissolve - input.life;
        if (clip < 0.0)
            discard;
        float edge = smoothstep(0.0, 0.2, clip);
        rgb = lerp(_Dissolvecolor.rgb, rgb, edge);

        float2 sUV = input.clipPos.xy / max(_ScreenParams.xy, 1.0.xx);
        float fade = HovlSoftFade(input.eyeDepth,
            _CameraDepthTexture.Sample(_CameraDepthSampler, sUV).r, _InvFade, _Usedepth);
        float a = _Maincolor.a * _Opacity * input.color.a * fade;
        return HovlToScreen(rgb, a);
		}
	}
	ENDSLANG
}
