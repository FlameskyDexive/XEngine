Shader "Default/Line"

Variants
{
}

Properties
{
    _MainTex ("Texture", Texture2D) = "white"
    _StartColor ("Start Color", Color) = (1.0, 1.0, 1.0, 1.0)
    _EndColor ("End Color", Color) = (1.0, 1.0, 1.0, 1.0)
}

Pass "Line"
{
    Tags { "RenderOrder" = "Transparent" }
    Cull Off
    Blend Alpha

	GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;
			out vec3 worldPos;
			out vec4 currentPos;
			out vec4 previousPos;
			out float fogCoord;
			out vec4 vColor;

			void main()
			{
				gl_Position = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
				fogCoord = gl_Position.z;
				currentPos = gl_Position;
				texCoord0 = vertexTexCoord0;

				vec4 prevWorldPos = XENGINE_MATRIX_M_PREVIOUS * vec4(vertexPosition, 1.0);
				previousPos = XENGINE_MATRIX_VP_PREVIOUS * prevWorldPos;

				worldPos = (XENGINE_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
				vColor = vertexColor;
			}
		}

		Fragment
		{
            #include "XEngineCG"

			layout (location = 0) out vec4 gAlbedo;
			layout (location = 1) out vec4 gMotionVector;
			layout (location = 2) out vec4 gNormal;
			layout (location = 3) out vec4 gSurface;

			in vec2 texCoord0;
			in vec3 worldPos;
			in vec4 currentPos;
			in vec4 previousPos;
			in float fogCoord;
			in vec4 vColor;

			uniform sampler2D _MainTex;

			void main()
			{
				vec2 curNDC = (currentPos.xy / currentPos.w) - _CameraJitter;
				vec2 prevNDC = (previousPos.xy / previousPos.w) - _CameraPreviousJitter;
			    gMotionVector = vec4((curNDC - prevNDC) * 0.5, 0.0, 1.0);

				vec4 albedo = texture(_MainTex, texCoord0) * vColor;

				// Lines don't have meaningful normals in billboarded mode
                gNormal = vec4(0.0, 0.0, 1.0, 1.0);

				// Unlit surface properties
				gSurface = vec4(1.0, 0.0, 0.0, 1.0);

				vec3 baseColor = albedo.rgb;
				baseColor.rgb = gammaToLinearSpace(baseColor.rgb);

				gAlbedo = vec4(baseColor, albedo.a);
				gAlbedo.rgb = ApplyFog(fogCoord, gAlbedo.rgb);
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
				float2 vertexTexCoord0 : TEXCOORD0;
				float4 vertexColor : COLOR;
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 currentPos : TEXCOORD2;
				float4 previousPos : TEXCOORD3;
				float4 vColor : COLOR0;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				float4 clipPos = mul(XENGINE_MATRIX_VP, mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)));
				o.position = clipPos;
				o.currentPos = clipPos;
				o.texCoord0 = input.vertexTexCoord0;
				float4 prevWorldPos = mul(XENGINE_MATRIX_M_PREVIOUS, float4(input.vertexPosition, 1.0));
				o.previousPos = mul(XENGINE_MATRIX_VP_PREVIOUS, prevWorldPos);
				o.worldPos = mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)).xyz;
				o.vColor = input.vertexColor;
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 currentPos : TEXCOORD2;
				float4 previousPos : TEXCOORD3;
				float4 vColor : COLOR0;
			};

			struct PSOutput
			{
				float4 gAlbedo : SV_Target0;
				float4 gMotionVector : SV_Target1;
				float4 gNormal : SV_Target2;
				float4 gSurface : SV_Target3;
			};

			PSOutput main(PSInput input)
			{
				PSOutput o;
				float2 curNDC = (input.currentPos.xy / input.currentPos.w) - _CameraJitter;
				float2 prevNDC = (input.previousPos.xy / input.previousPos.w) - _CameraPreviousJitter;
				o.gMotionVector = float4((curNDC - prevNDC) * 0.5, 0.0, 1.0);

				float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor;
				o.gNormal = float4(0.0, 0.0, 1.0, 1.0);
				o.gSurface = float4(1.0, 0.0, 0.0, 1.0);

				float3 baseColor = gammaToLinearSpace(albedo.rgb);
				baseColor = ApplyFog(baseColor, input.worldPos);
				o.gAlbedo = float4(baseColor, albedo.a);
				return o;
			}
		}
	ENDSLANG
}
