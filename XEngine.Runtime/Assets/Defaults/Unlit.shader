Shader "Default/Unlit"

Variants
{
}

Properties
{
    _MainTex ("Texture", Texture2D) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _Tiling ("Tiling", Vector2) = (1.0, 1.0)
    _Offset ("Offset", Vector2) = (0.0, 0.0)
}

Pass "Unlit"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull Back

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

			uniform sampler2D _MainTex;
			uniform vec4 _MainColor;

			void main()
			{
				vec4 albedo = texture(_MainTex, texCoord0) * vColor * _MainColor;
				vec3 baseColor = gammaToLinearSpace(albedo.rgb);
				baseColor = ApplyFog(baseColor, worldPos);
				fragColor = vec4(baseColor, albedo.a);
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
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				o.position = TransformClip(input.vertexPosition);
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
				o.worldPos = TransformPosition(input.vertexPosition);
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
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
			};

			float4 main(PSInput input) : SV_Target
			{
				float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
				float3 baseColor = gammaToLinearSpace(albedo.rgb);
				baseColor = ApplyFog(baseColor, input.worldPos);
				return float4(baseColor, albedo.a);
			}
		}
	ENDSLANG
}

Pass "UnlitPrepass"
{
    Tags { "LightMode" = "Prepass" }
    Cull Back
    ZWrite On

	GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec3 vNormal;
			out vec4 vCurrClipNJ;
			out vec4 vPrevClip;

			void main()
			{
				gl_Position = TransformClip(vertexPosition); // jittered, for raster + depth
				vNormal = TransformDirection(vertexNormal);

				// Jitter-free current + previous clip positions for motion vectors.
				vec4 worldPos = GetModelMatrix() * vec4(vertexPosition, 1.0);
				vCurrClipNJ = XENGINE_MATRIX_VP_NONJITTERED * worldPos;
				vec4 prevWorldPos = XENGINE_MATRIX_M_PREVIOUS * vec4(vertexPosition, 1.0);
				vPrevClip = XENGINE_MATRIX_VP_PREVIOUS * prevWorldPos;
			}
		}

		Fragment
		{
            #include "XEngineCG"

			layout (location = 0) out vec4 normalOut;
			layout (location = 1) out vec4 motionRM;
			in vec3 vNormal;
			in vec4 vCurrClipNJ;
			in vec4 vPrevClip;

			void main()
			{
				normalOut = EncodeViewNormal(normalize(vNormal));

				// Motion vectors (jitter-free). Unlit has no PBR material -> roughness/metallic 0.
				vec2 currNDC = (vCurrClipNJ.xy / vCurrClipNJ.w) * 0.5 + 0.5;
				vec2 prevNDC = (vPrevClip.xy / vPrevClip.w) * 0.5 + 0.5;
				motionRM = vec4(currNDC - prevNDC, 0.0, 0.0);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float3 vertexNormal : NORMAL;
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float3 vNormal : TEXCOORD0;
				float4 vCurrClipNJ : TEXCOORD1;
				float4 vPrevClip : TEXCOORD2;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				o.position = TransformClip(input.vertexPosition);
				o.vNormal = TransformDirection(input.vertexNormal);
				float4 worldPos = mul(GetModelMatrix(), float4(input.vertexPosition, 1.0));
				o.vCurrClipNJ = mul(XENGINE_MATRIX_VP_NONJITTERED, worldPos);
				float4 prevWorldPos = mul(XENGINE_MATRIX_M_PREVIOUS, float4(input.vertexPosition, 1.0));
				o.vPrevClip = mul(XENGINE_MATRIX_VP_PREVIOUS, prevWorldPos);
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"

			struct PSInput
			{
				float4 position : SV_Position;
				float3 vNormal : TEXCOORD0;
				float4 vCurrClipNJ : TEXCOORD1;
				float4 vPrevClip : TEXCOORD2;
			};

			struct PSOutput
			{
				float4 normalOut : SV_Target0;
				float4 motionRM : SV_Target1;
			};

			PSOutput main(PSInput input)
			{
				PSOutput o;
				o.normalOut = EncodeViewNormal(normalize(input.vNormal));
				float2 currNDC = (input.vCurrClipNJ.xy / input.vCurrClipNJ.w) * 0.5 + 0.5;
				float2 prevNDC = (input.vPrevClip.xy / input.vPrevClip.w) * 0.5 + 0.5;
				o.motionRM = float4(currNDC - prevNDC, 0.0, 0.0);
				return o;
			}
		}
	ENDSLANG
}
