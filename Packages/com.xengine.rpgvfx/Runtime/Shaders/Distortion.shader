// Ported from Hovl Studio "Distortion" (heat haze: engine GrabTexture + normal-map distortion; Refraction.shader precedent).
Shader "Hovl/Distortion"
Variants
{
    Variant("GPU_INSTANCING")
    Variant("PARTICLE_MESH")
}
Properties
{
    _NormalMap ("Normal Map", Texture2D) = "normal"
    _NormalMap_ST ("Normal Map ST", Vector4) = (1.0, 1.0, 0.0, 0.0)
    _Distortionpower ("Distortion power", Float) = 0.05
    _InvFade ("Soft Particles Factor", Float) = 1.0
    _Usedepth ("Use depth?", Float) = 0.0
}

Pass "Particle"
{
    GrabTexture "_GrabTexture"
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

        uniform sampler2D _NormalMap;
        uniform sampler2D _GrabTexture;
        uniform sampler2D _CameraDepthTexture;
        uniform vec4 _NormalMap_ST;
        uniform float _Distortionpower;
        uniform float _InvFade;
        uniform float _Usedepth;

		void main()
		{
            vec3 n = texture(_NormalMap, hovlUv * _NormalMap_ST.xy + _NormalMap_ST.zw).rgb * 2.0 - 1.0;
            float fade = HovlSoftFade(hovlEyeDepth,
                texture(_CameraDepthTexture, gl_FragCoord.xy / _ScreenParams.xy).r,
                1.0 / max(_InvFade, 0.001), _Usedepth);
            vec2 texelSize = 1.0 / vec2(textureSize(_GrabTexture, 0));
            // Fragment coordinates use the render target's actual origin. Rebuilding UV
            // from clip.xy omits Vulkan/D3D's viewport Y flip and mirrors the grabbed image.
            vec2 screenUV = gl_FragCoord.xy * texelSize;
            // Unity scales pixel displacement by inverse camera distance, then divides
            // the projected grab coordinates by w. Particle alpha changes displacement.
            vec2 offset = n.rg * texelSize * _Distortionpower * hovlColor.a * fade
                * (hovlScreen.z / hovlScreen.w);
            vec4 col = texture(_GrabTexture, clamp(screenUV + offset, vec2(0.0), vec2(1.0)));
            float edge = (abs(n.r) + abs(n.g) * 30.0) - 0.03;
            float a = clamp(col.a * edge, 0.0, 1.0);
            fragColor = HovlToScreen(col.rgb, a);
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

        cbuffer HovlDistortMaterial : register(b2)
        {
            float4 _NormalMap_ST;
            float _Distortionpower;
            float _InvFade;
            float _Usedepth;
            float _HovlPad;
        };

        Texture2D _NormalMap : register(t0);
        SamplerState _NormalMapSampler : register(s0);
        Texture2D _GrabTexture : register(t4);
        SamplerState _GrabTextureSampler : register(s4);
        Texture2D _CameraDepthTexture : register(t5);
        SamplerState _CameraDepthSampler : register(s5);

		float4 main(HovlVSOutput input) : SV_Target
		{
        float3 n = _NormalMap.Sample(_NormalMapSampler, input.uv * _NormalMap_ST.xy + _NormalMap_ST.zw).rgb * 2.0 - 1.0;
        float2 sUV = input.clipPos.xy / max(_ScreenParams.xy, 1.0.xx);
        float fade = HovlSoftFade(input.eyeDepth,
            _CameraDepthTexture.Sample(_CameraDepthSampler, sUV).r, 1.0 / max(_InvFade, 0.001), _Usedepth);
        uint grabWidth, grabHeight;
        _GrabTexture.GetDimensions(grabWidth, grabHeight);
        float2 texelSize = 1.0 / float2(grabWidth, grabHeight);
        float2 screenUV = input.clipPos.xy * texelSize;
        float2 offset = n.rg * texelSize * _Distortionpower * input.color.a * fade
            * (input.screen.z / input.screen.w);
        float4 col = _GrabTexture.Sample(_GrabTextureSampler, saturate(screenUV + offset));
        float edge = (abs(n.r) + abs(n.g) * 30.0) - 0.03;
        float a = saturate(col.a * edge);
        return HovlToScreen(col.rgb, a);
		}
	}
	ENDSLANG
}
