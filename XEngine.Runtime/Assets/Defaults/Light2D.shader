Shader "Default/Light2D"

Variants
{
}

Properties
{
    _MainTex ("Cookie", Texture2D) = "white"
    _MainColor ("Color", Color) = (1.0, 1.0, 1.0, 1.0)
    _Tiling ("Inner Outer", Vector2) = (0.25, 3.0)
    _Offset ("Type Shadow", Vector2) = (1.0, 0.0)
}

Pass "Light2D"
{
    Tags { "RenderOrder" = "Transparent" }
    Blend Additive
    ZTest Off
    ZWrite Off
    Cull Off

	GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"

			layout (location = 0) in vec3 vertexPosition;
			layout (location = 1) in vec2 vertexTexCoord;

			uniform vec2 _Offset;

			out vec2 texCoord0;
			out vec3 worldPos;

			void main()
			{
				texCoord0 = vertexTexCoord;
				worldPos = TransformPosition(vertexPosition);
				if (_Offset.x < 0.5)
					gl_Position = vec4(vertexPosition.xy, 0.0, 1.0);
				else
					gl_Position = TransformClip(vertexPosition);
			}
		}

		Fragment
		{
            #include "XEngineCG"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec3 worldPos;

			uniform sampler2D _MainTex;
			uniform sampler2D _ShadowMask2D;
			uniform vec4 _MainColor;
			uniform vec2 _Tiling;
			uniform vec2 _Offset;

			void main()
			{
				float lightType = _Offset.x;
				vec3 color = _MainColor.rgb;
				if (lightType >= 0.5)
				{
					vec2 origin = (XENGINE_MATRIX_M * vec4(0.0, 0.0, 0.0, 1.0)).xy;
					float dist = length(worldPos.xy - origin);
					float inner = _Tiling.x;
					float outer = max(_Tiling.y, inner + 1e-4);
					float t = 1.0;
					if (dist >= outer)
						t = 0.0;
					else if (dist > inner)
					{
						float u = (dist - inner) / (outer - inner);
						t = 1.0 - u;
						t = t * t * (3.0 - 2.0 * t);
						t = pow(t, max(_MainColor.a, 1.0));
					}
					color *= t;
					if (lightType >= 1.5)
						color *= texture(_MainTex, texCoord0).rgb;
				}

				vec2 screenUv = gl_FragCoord.xy / _ScreenParams.xy;
				float shadow = 1.0 - _Offset.y * (1.0 - texture(_ShadowMask2D, screenUv).r);
				fragColor = vec4(color * shadow, 1.0);
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
				float2 vertexTexCoord : TEXCOORD0;
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				o.texCoord0 = input.vertexTexCoord;
				o.worldPos = TransformPosition(input.vertexPosition);
				if (_Offset.x < 0.5)
					o.position = float4(input.vertexPosition.xy, 0.0, 1.0);
				else
					o.position = TransformClip(input.vertexPosition);
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
			Texture2D _ShadowMask2D : register(t1);
			SamplerState _ShadowMask2DSampler : register(s1);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
			};

			float4 main(PSInput input) : SV_Target
			{
				float lightType = _Offset.x;
				float3 color = _MainColor.rgb;
				if (lightType >= 0.5)
				{
					float2 origin = mul(XENGINE_MATRIX_M, float4(0.0, 0.0, 0.0, 1.0)).xy;
					float dist = length(input.worldPos.xy - origin);
					float inner = _Tiling.x;
					float outer = max(_Tiling.y, inner + 1e-4);
					float t = 1.0;
					if (dist >= outer)
						t = 0.0;
					else if (dist > inner)
					{
						float u = (dist - inner) / (outer - inner);
						t = 1.0 - u;
						t = t * t * (3.0 - 2.0 * t);
						t = pow(t, max(_MainColor.a, 1.0));
					}
					color *= t;
					if (lightType >= 1.5)
						color *= _MainTex.Sample(_MainTexSampler, input.texCoord0).rgb;
				}

				float2 screenUv = input.position.xy / _ScreenParams.xy;
				float shadow = 1.0 - _Offset.y * (1.0 - _ShadowMask2D.Sample(_ShadowMask2DSampler, screenUv).r);
				return float4(color * shadow, 1.0);
			}
		}
	ENDSLANG
}
