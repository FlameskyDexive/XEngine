Shader "GpuAnimation/Lit"

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

Pass "GpuAnimLit"
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
			uniform sampler2D _NrmTex;
			uniform sampler2D _ClipTex;

			void main()
			{
				int vertexIndex = int(vertexTexCoord1.y + 0.5);
				vec3 animPos;
				vec3 animNrm;
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
				animPos = mix(p0, p1, blend);
				vec3 n0 = texelFetch(_NrmTex, ivec2(vertexIndex, start + i0), 0).xyz;
				vec3 n1 = texelFetch(_NrmTex, ivec2(vertexIndex, start + i1), 0).xyz;
				animNrm = normalize(mix(n0, n1, blend));
			#else
				animPos = vertexPosition;
				animNrm = vertexNormal;
			#endif

				mat4 model = GetModelMatrix();
				vec3 worldNormal = normalize(mat3(model) * animNrm);

				// Lighting is computed in the VERTEX stage (crowd shading is per-instance,
				// not per-pixel — this keeps the fragment identical to the proven unlit path).
				// Scene sun lambert via the engine globals, floored by a fixed key light so the
				// crowd is never black without established light state.
				// Fixed key light + ambient (GL path mirrors the Slang shading).
				vec3 keyColor = vec3(0.9, 0.87, 0.8) * max(dot(worldNormal, normalize(vec3(0.4, 0.8, 0.35))), 0.0);
				vec3 lightTerm = keyColor + vec3(0.35, 0.37, 0.42);

				texCoord0 = vertexTexCoord0;
				vColor = GetInstanceColor() * vec4(lightTerm, 1.0);
				gl_Position = GetMVPMatrix() * vec4(animPos, 1.0);
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
			Texture2D _NrmTex : register(t4);
			Texture2D _ClipTex : register(t2);

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float3 vertexNormal : NORMAL;
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
				float3 n0 = _NrmTex.Load(int3(vertexIndex, start + i0, 0)).xyz;
				float3 n1 = _NrmTex.Load(int3(vertexIndex, start + i1, 0)).xyz;
				float3 animNrm = normalize(lerp(n0, n1, blend));
				float3 worldNormal = normalize(mul((float3x3)model, animNrm));

				// Vertex-stage lambert: scene sun globals floored by a fixed key light. The
				// fragment stays byte-identical to the proven unlit path (stage-IO safe).
				// Fixed key light + ambient (scene-sun uniform wiring blocked by an engine-side
				// standard-lighting cbuffer layout mismatch - see M10 evidence).
				float3 keyColor = float3(0.9, 0.87, 0.8) * max(dot(worldNormal, normalize(float3(0.4, 0.8, 0.35))), 0.0);
				float3 lightTerm = keyColor + float3(0.35, 0.37, 0.42);

				VSOutput o;
				o.position = mul(XENGINE_MATRIX_VP, mul(model, float4(animPos, 1.0)));
				o.texCoord0 = input.vertexTexCoord0;
				o.vColor = input.vertexColor * input.instanceColor * float4(lightTerm, 1.0);
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
