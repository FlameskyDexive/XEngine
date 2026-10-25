Shader "Default/SpriteLit"

Variants
{
}

Properties
{
    _MainTex ("Sprite Texture", Texture2D) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _Tiling ("Tiling", Vector2) = (1.0, 1.0)
    _Offset ("Offset", Vector2) = (0.0, 0.0)
}

Pass "SpriteLit"
{
    Tags { "RenderOrder" = "Transparent" }
    Blend Alpha
    ZWrite Off
    Cull Off

	GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;
			out vec4 vColor;

			uniform vec2 _Tiling;
			uniform vec2 _Offset;

			void main()
			{
				gl_Position = TransformClip(vertexPosition);
				texCoord0 = vertexTexCoord0 * _Tiling + _Offset;
				vColor = GetInstanceColor();
			}
		}

		Fragment
		{
            #include "XEngineCG"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec4 vColor;

			uniform sampler2D _MainTex;
			uniform sampler2D _LightTexture2D;
			uniform vec4 _MainColor;

			void main()
			{
				vec4 albedo = texture(_MainTex, texCoord0) * vColor * _MainColor;
				vec3 baseColor = gammaToLinearSpace(albedo.rgb);
				vec2 screenUv = gl_FragCoord.xy / _ScreenParams.xy;
				vec3 light = texture(_LightTexture2D, screenUv).rgb;
				fragColor = vec4(baseColor * light, albedo.a);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer UnlitMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
			};

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float2 vertexTexCoord0 : TEXCOORD0;
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float4 vColor : COLOR0;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				o.position = TransformClip(input.vertexPosition);
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
				o.vColor = GetInstanceColor();
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"

			cbuffer UnlitMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);
			Texture2D _LightTexture2D : register(t1);
			SamplerState _LightTexture2DSampler : register(s1);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float4 vColor : COLOR0;
			};

			float4 main(PSInput input) : SV_Target
			{
				float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
				float3 baseColor = gammaToLinearSpace(albedo.rgb);
				float2 screenUv = input.position.xy / _ScreenParams.xy;
				float3 light = _LightTexture2D.Sample(_LightTexture2DSampler, screenUv).rgb;
				return float4(baseColor * light, albedo.a);
			}
		}
	ENDSLANG
}
