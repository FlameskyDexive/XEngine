Shader "GpuAnimation/Unlit"

Variants
{
    Variant("GPU_INSTANCING")
    Variant("HAS_UV")
    Variant("HAS_NORMALS")
}

Properties
{
    _MainTex ("Albedo", Texture2D) = "white"
}

Pass "GpuAnimUnlit"
{
    Tags { "RenderOrder" = "Opaque" }

    Cull Off
    ZWrite On
    Blend Off

	GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;
			out vec4 vColor;

			uniform sampler2D _PosTex;
			uniform sampler2D _ClipTex;

			void main()
			{
				// Vertex animation texture sampling: UV2.y carries the raw vertex index,
				// CustomData = (clipIndex, phase, speed, -). Two nearest texels of the clip's
				// row segment are lerped manually (RGBA32F is not filterable on WebGPU).
				int vertexIndex = int(vertexTexCoord1.y + 0.5);
				vec4 animPos;
			#ifdef GPU_INSTANCING
				int clip = int(instanceCustomData.x + 0.5);
				float phase = instanceCustomData.y;
				float speed = max(instanceCustomData.z, 0.0001);
				vec4 clipInfo = texelFetch(_ClipTex, ivec2(0, clip), 0);
				int start = int(clipInfo.x + 0.5);
				int count = int(clipInfo.y + 0.5);
				float len = max(clipInfo.z, 0.0001);
				float f = fract(_Time.y / len * speed + phase) * float(count);
				int i0 = int(f);
				float blend = f - float(i0);
				int i1 = i0 + 1; if (i1 >= count) i1 = 0;
				vec3 p0 = texelFetch(_PosTex, ivec2(vertexIndex, start + i0), 0).xyz;
				vec3 p1 = texelFetch(_PosTex, ivec2(vertexIndex, start + i1), 0).xyz;
				animPos = vec4(mix(p0, p1, blend), 1.0);
			#else
				animPos = vec4(vertexPosition, 1.0);
			#endif

				texCoord0 = vertexTexCoord0;
				vColor = GetInstanceColor();
				gl_Position = GetMVPMatrix() * animPos;
			}
		}

		Fragment
		{
            #include "XEngineCG"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec4 vColor;

			uniform sampler2D _MainTex;

			void main()
			{
				vec4 albedo = texture(_MainTex, texCoord0) * vColor;
				fragColor = vec4(gammaToLinearSpace(albedo.rgb), albedo.a);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			Texture2D _PosTex : register(t1);
			Texture2D _ClipTex : register(t2);

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float2 vertexTexCoord0 : TEXCOORD0;
				float2 vertexTexCoord1 : TEXCOORD1;
				float4 vertexColor : COLOR0;
#ifdef GPU_INSTANCING
				float4 instanceModelRow0 : TEXCOORD8;
				float4 instanceModelRow1 : TEXCOORD9;
				float4 instanceModelRow2 : TEXCOORD10;
				float4 instanceModelRow3 : TEXCOORD11;
				float4 instanceColor : TEXCOORD12;
				float4 instanceCustomData : TEXCOORD13;
#endif
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float4 vColor : COLOR0;
			};

			VSOutput main(VSInput input)
			{
			#ifdef GPU_INSTANCING
				float4x4 model = transpose(float4x4(input.instanceModelRow0, input.instanceModelRow1, input.instanceModelRow2, input.instanceModelRow3));
				int vertexIndex = (int)(input.vertexTexCoord1.y + 0.5);
				int clip = (int)(input.instanceCustomData.x + 0.5);
				float phase = input.instanceCustomData.y;
				float speed = max(input.instanceCustomData.z, 0.0001);
				float4 clipInfo = _ClipTex.Load(int3(0, clip, 0));
				int start = (int)(clipInfo.x + 0.5);
				int count = (int)(clipInfo.y + 0.5);
				float len = max(clipInfo.z, 0.0001);
				float f = frac(_Time.y / len * speed + phase) * (float)count;
				int i0 = (int)f;
				float blend = f - (float)i0;
				int i1 = i0 + 1; if (i1 >= count) i1 = 0;
				float3 p0 = _PosTex.Load(int3(vertexIndex, start + i0, 0)).xyz;
				float3 p1 = _PosTex.Load(int3(vertexIndex, start + i1, 0)).xyz;
				float3 animPos = lerp(p0, p1, blend);
				VSOutput o;
				o.position = mul(XENGINE_MATRIX_VP, mul(model, float4(animPos, 1.0)));
				o.texCoord0 = input.vertexTexCoord0;
				o.vColor = input.vertexColor * input.instanceColor;
				return o;
			#else
				VSOutput o;
				o.position = mul(XENGINE_MATRIX_VP, mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)));
				o.texCoord0 = input.vertexTexCoord0;
				o.vColor = input.vertexColor;
				return o;
			#endif
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
				float4 vColor : COLOR0;
			};

			float4 main(PSInput input) : SV_Target
			{
				float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor;
				return float4(gammaToLinearSpace(albedo.rgb), albedo.a);
			}
		}
	ENDSLANG
}

Pass "GpuAnimShadow"
{
    Tags { "LightMode" = "ShadowCaster" }

    Cull Off
    ZWrite On
    Blend Off

	GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			uniform sampler2D _PosTex;
			uniform sampler2D _ClipTex;

			void main()
			{
				int vertexIndex = int(vertexTexCoord1.y + 0.5);
				vec4 animPos;
			#ifdef GPU_INSTANCING
				int clip = int(instanceCustomData.x + 0.5);
				float phase = instanceCustomData.y;
				float speed = max(instanceCustomData.z, 0.0001);
				vec4 clipInfo = texelFetch(_ClipTex, ivec2(0, clip), 0);
				int start = int(clipInfo.x + 0.5);
				int count = int(clipInfo.y + 0.5);
				float len = max(clipInfo.z, 0.0001);
				float f = fract(_Time.y / len * speed + phase) * float(count);
				int i0 = int(f);
				float blend = f - float(i0);
				int i1 = i0 + 1; if (i1 >= count) i1 = 0;
				vec3 p0 = texelFetch(_PosTex, ivec2(vertexIndex, start + i0), 0).xyz;
				vec3 p1 = texelFetch(_PosTex, ivec2(vertexIndex, start + i1), 0).xyz;
				animPos = vec4(mix(p0, p1, blend), 1.0);
			#else
				animPos = vec4(vertexPosition, 1.0);
			#endif
				gl_Position = GetMVPMatrix() * animPos;
			}
		}

		Fragment
		{
            #include "XEngineCG"

			void main()
			{
				gl_FragDepth = gl_FragCoord.z;
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			Texture2D _PosTex : register(t1);
			Texture2D _ClipTex : register(t2);

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float2 vertexTexCoord1 : TEXCOORD1;
#ifdef GPU_INSTANCING
				float4 instanceModelRow0 : TEXCOORD8;
				float4 instanceModelRow1 : TEXCOORD9;
				float4 instanceModelRow2 : TEXCOORD10;
				float4 instanceModelRow3 : TEXCOORD11;
				float4 instanceColor : TEXCOORD12;
				float4 instanceCustomData : TEXCOORD13;
#endif
			};

			struct VSOutput
			{
				float4 position : SV_Position;
			};

			VSOutput main(VSInput input)
			{
			#ifdef GPU_INSTANCING
				float4x4 model = transpose(float4x4(input.instanceModelRow0, input.instanceModelRow1, input.instanceModelRow2, input.instanceModelRow3));
				int vertexIndex = (int)(input.vertexTexCoord1.y + 0.5);
				int clip = (int)(input.instanceCustomData.x + 0.5);
				float phase = input.instanceCustomData.y;
				float speed = max(input.instanceCustomData.z, 0.0001);
				float4 clipInfo = _ClipTex.Load(int3(0, clip, 0));
				int start = (int)(clipInfo.x + 0.5);
				int count = (int)(clipInfo.y + 0.5);
				float len = max(clipInfo.z, 0.0001);
				float f = frac(_Time.y / len * speed + phase) * (float)count;
				int i0 = (int)f;
				float blend = f - (float)i0;
				int i1 = i0 + 1; if (i1 >= count) i1 = 0;
				float3 p0 = _PosTex.Load(int3(vertexIndex, start + i0, 0)).xyz;
				float3 p1 = _PosTex.Load(int3(vertexIndex, start + i1, 0)).xyz;
				float3 animPos = lerp(p0, p1, blend);
				VSOutput o;
				o.position = mul(XENGINE_MATRIX_VP, mul(model, float4(animPos, 1.0)));
				return o;
			#else
				VSOutput o;
				o.position = mul(XENGINE_MATRIX_VP, mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)));
				return o;
			#endif
			}
		}

		Fragment
		{
			#include "XEngineCG"

			struct PSInput
			{
				float4 position : SV_Position;
			};

			void main(PSInput input)
			{
			}
		}
	ENDSLANG
}

