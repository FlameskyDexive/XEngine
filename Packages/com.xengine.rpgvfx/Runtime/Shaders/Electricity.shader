// Ported from Hovl Studio "Electricity" (additive; two opposed panned noise samples multiplied, remapped into a mask lookup with radial power boost).
Shader "Hovl/Electricity"
Variants
{
    Variant("GPU_INSTANCING")
    Variant("PARTICLE_MESH")
}
Properties
{
    _MainTexture ("Main Texture", Texture2D) = "white"
    _MainTexture_ST ("Main Texture ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Mask ("Mask", Texture2D) = "white"
    _Mask_ST ("Mask ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Color ("Color", Color) = (0.5, 0.5, 0.5, 1.0)
    _Emission ("Emission", Float) = 6.0
    _RemapXYFresnelZW ("Remap XY/Fresnel ZW", Vector4) = (-10.0, 10.0, 2.0, 2.0)
    _Speed ("Speed", Vector4) = (0.19, 0.23, -0.2, -0.05)
    _Dissolveamount ("Dissolve amount", Float) = 0.33
    _Opacity ("Opacity", Float) = 1.0
    _Usedepth ("Use depth?", Float) = 0.0
    _Depth ("Depth", Float) = 0.15
}

Pass "Particle"
{
    ColorMask RGB
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

        uniform sampler2D _MainTexture;
        uniform sampler2D _Mask;
        uniform sampler2D _CameraDepthTexture;
        uniform vec4 _MainTexture_ST;
        uniform vec4 _Mask_ST;
        uniform vec4 _Color;
        uniform vec4 _RemapXYFresnelZW;
        uniform vec4 _Speed;
        uniform float _Emission;
        uniform float _Dissolveamount;
        uniform float _Opacity;
        uniform float _Usedepth;
        uniform float _Depth;

		void main()
		{
            vec2 uv0 = hovlUv * _MainTexture_ST.xy + _MainTexture_ST.zw;
            float n1 = texture(_MainTexture, HovlPanner(_Time.y, _Speed.xy, uv0)).r;
            float n2 = texture(_MainTexture, HovlPanner(_Time.y, _Speed.zw, uv0)).r;
            float bolt = n1 * n2;
            float remapped = clamp(_RemapXYFresnelZW.x + bolt * (_RemapXYFresnelZW.y - _RemapXYFresnelZW.x), 0.0, 1.0);
            float arc = 1.0 - remapped;
            float mask = texture(_Mask, vec2(arc, 0.5)).r;
            float radial = 1.0 - length(hovlUv - vec2(0.5)) * 2.0;
            float power = pow(clamp(radial, 0.0, 1.0), _RemapXYFresnelZW.z) * _RemapXYFresnelZW.w;

            vec3 rgb = _Color.rgb * mask * power * hovlColor.rgb * _Emission;
            float a = _Opacity * hovlColor.a;

            float fade = HovlSoftFade(hovlEyeDepth,
                texture(_CameraDepthTexture, gl_FragCoord.xy / _ScreenParams.xy).r,
                _Depth, _Usedepth);
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

        cbuffer HovlElectricityMaterial : register(b2)
        {
            float4 _MainTexture_ST;
            float4 _Mask_ST;
            float4 _Color;
            float4 _RemapXYFresnelZW;
            float4 _Speed;
            float _Emission;
            float _Dissolveamount;
            float _Opacity;
            float _Usedepth;
            float _Depth;
            float3 _HovlPad;
        };

        Texture2D _MainTexture : register(t0);
        SamplerState _MainTextureSampler : register(s0);
        Texture2D _Mask : register(t1);
        SamplerState _MaskSampler : register(s1);
        Texture2D _CameraDepthTexture : register(t5);
        SamplerState _CameraDepthSampler : register(s5);

		float4 main(HovlVSOutput input) : SV_Target
		{
        float2 uv0 = input.uv * _MainTexture_ST.xy + _MainTexture_ST.zw;
        float n1 = _MainTexture.Sample(_MainTextureSampler, HovlPanner(_Time.y, _Speed.xy, uv0)).r;
        float n2 = _MainTexture.Sample(_MainTextureSampler, HovlPanner(_Time.y, _Speed.zw, uv0)).r;
        float bolt = n1 * n2;
        float remapped = saturate(_RemapXYFresnelZW.x + bolt * (_RemapXYFresnelZW.y - _RemapXYFresnelZW.x));
        float arc = 1.0 - remapped;
        float mask = _Mask.Sample(_MaskSampler, float2(arc, 0.5)).r;
        float radial = 1.0 - length(input.uv - float2(0.5, 0.5)) * 2.0;
        float power = pow(saturate(radial), _RemapXYFresnelZW.z) * _RemapXYFresnelZW.w;

        float3 rgb = _Color.rgb * mask * power * input.color.rgb * _Emission;
        float a = _Opacity * input.color.a;

        float2 sUV = input.clipPos.xy / max(_ScreenParams.xy, 1.0.xx);
        float fade = HovlSoftFade(input.eyeDepth,
            _CameraDepthTexture.Sample(_CameraDepthSampler, sUV).r, _Depth, _Usedepth);
        a *= fade;
        return HovlToScreen(rgb, a);
		}
	}
	ENDSLANG
}
