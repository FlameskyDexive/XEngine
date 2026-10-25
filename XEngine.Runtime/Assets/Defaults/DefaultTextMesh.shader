Shader "Default/DefaultTextMesh"

Variants
{
}

Properties
{
    _MainTex ("SDF Atlas", Texture2D) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _Tiling ("Tiling", Vector2) = (1.0, 1.0)
    _Offset ("Offset", Vector2) = (0.0, 0.0)
}

// World-space SDF text (TextMeshComponent). Same distance-field coverage as Default/DefaultText,
// but tagged Transparent so the scene pipeline draws it, and depth-aware (default ZTest) so it is
// occluded by nearer geometry. For an always-on-top nameplate, copy this and add `ZTest Off`.
Pass "DefaultTextMesh"
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
			out vec3 worldPos;
			out vec4 vColor;

			uniform vec2 _Tiling;
			uniform vec2 _Offset;

			void main()
			{
				gl_Position = TransformClip(vertexPosition);
				texCoord0 = vertexTexCoord0 * _Tiling + _Offset;
				worldPos = TransformPosition(vertexPosition);
				vColor = GetInstanceColor();
			}
		}

		Fragment
		{
            #include "XEngineCG"
            #include "Lighting"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec3 worldPos;
			in vec4 vColor;

			uniform sampler2D _MainTex;   // single-channel SDF replicated across RGB(A)
			uniform vec4 _MainColor;

			// Reconstruct sharp, resolution-independent coverage from the distance field.
			const float sdfPxRange = 4.0;
			float sdfScreenPxRange(vec2 uv) {
				vec2 unitRange = vec2(sdfPxRange) / vec2(textureSize(_MainTex, 0));
				vec2 screenTexSize = vec2(1.0) / fwidth(uv);
				return max(0.5 * dot(unitRange, screenTexSize), 1.0);
			}

			void main()
			{
				float sd = texture(_MainTex, texCoord0).r;
				float screenPxDistance = sdfScreenPxRange(texCoord0) * (sd - 0.5);
				float coverage = clamp(screenPxDistance + 0.5, 0.0, 1.0);
				fragColor = vColor * _MainColor * coverage;
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

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float4 vColor : COLOR0;
			};

			static const float sdfPxRange = 4.0;
			float sdfScreenPxRange(float2 uv)
			{
				uint width;
				uint height;
				_MainTex.GetDimensions(width, height);
				float2 unitRange = float2(sdfPxRange, sdfPxRange) / float2(width, height);
				float2 uvWidth = abs(ddx(uv)) + abs(ddy(uv));
				float2 screenTexSize = float2(1.0, 1.0) / uvWidth;
				return max(0.5 * dot(unitRange, screenTexSize), 1.0);
			}

			float4 main(PSInput input) : SV_Target
			{
				float sd = _MainTex.Sample(_MainTexSampler, input.texCoord0).r;
				float screenPxDistance = sdfScreenPxRange(input.texCoord0) * (sd - 0.5);
				float coverage = saturate(screenPxDistance + 0.5);
				return input.vColor * _MainColor * coverage;
			}
		}
	ENDSLANG
}
