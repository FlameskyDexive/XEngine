Shader "Spine/SkeletonAdditive"

Variants
{
    Variant("HAS_UV", "HAS_COLORS")
}

Properties
{
    _MainTex ("Skeleton Texture", Texture2D) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
}

Pass "SkeletonAdditive"
{
    Tags { "RenderOrder" = "Transparent" }
    Blend Additive
    ZWrite Off
    Cull Off

	GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;
			out vec4 vColor;

			void main()
			{
				gl_Position = TransformClip(vertexPosition);
				texCoord0 = vertexTexCoord0;
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
			uniform vec4 _MainColor;

			void main()
			{
				vec4 albedo = texture(_MainTex, texCoord0) * vColor * _MainColor;
				fragColor = vec4(gammaToLinearSpace(albedo.rgb), albedo.a);
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
				float4 vertexColor : COLOR0;
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
				o.texCoord0 = input.vertexTexCoord0;
				o.vColor = input.vertexColor;
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

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float4 vColor : COLOR0;
			};

			float4 main(PSInput input) : SV_Target
			{
				float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
				return float4(gammaToLinearSpace(albedo.rgb), albedo.a);
			}
		}
	ENDSLANG
}
