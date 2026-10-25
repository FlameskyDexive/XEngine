Shader "Default/StandardTransparent"

Variants
{
    Variant("HAS_TANGENTS")
    Variant("BLENDSHAPES")
    Variant("BLENDSHAPES", "HAS_TANGENTS")
}

Properties
{
    _MainTex ("Albedo", Texture2D) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _Tiling ("Tiling", Vector2) = (1.0, 1.0)
    _Offset ("Offset", Vector2) = (0.0, 0.0)

    _NormalTex ("Normal", Texture2D) = "normal"

    _SurfaceTex ("Surface (AO, Roughness, Metallicness)", Texture2D) = "surface"

    _EmissionTex ("Emission", Texture2D) = "emission"
    _EmissionIntensity ("Emission Intensity", Float) = 1.0

    _TranslucencyMap ("Translucency (B) Occlusion (G)", Texture2D) = "white"
    _TranslucencyStrength ("Translucency Strength", Float) = 0.0
    _ScatteringPower ("Scattering Power", Float) = 0.0
    _ScatteringDistortion ("Scattering Distortion", Float) = 0.5
    _ScatteringScale ("Scattering Scale", Float) = 1.0
}

Pass "StandardTransparent"
{
    Tags { "RenderOrder" = "Transparent" }
    Blend Alpha
    ZWrite Off
    Cull Back

	GLSLPROGRAM

		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;
			out vec3 worldPos;
			out vec4 vColor;
			out vec3 vNormal;
			out vec3 vTangent;
			out vec3 vBitangent;

			uniform vec2 _Tiling;
			uniform vec2 _Offset;

			void main()
			{
				gl_Position = TransformClip(vertexPosition);
				texCoord0 = vertexTexCoord0 * _Tiling + _Offset;
				worldPos = TransformPosition(vertexPosition);
				vColor = GetInstanceColor();
				vNormal = TransformDirection(vertexNormal);
#ifdef HAS_TANGENTS
				vTangent = TransformDirection(vertexTangent.xyz);
				vBitangent = cross(vTangent, vNormal) * vertexTangent.w;
#endif
			}
		}

		Fragment
		{
            // Clustered light lists (P3.2): cluster lookup replaces the per-fragment BVH walk.
            #define XENGINE_CLUSTERED_LIGHTS
            #include "StandardSurface"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec3 worldPos;
			in vec4 vColor;
			in vec3 vNormal;
			in vec3 vTangent;
			in vec3 vBitangent;

			uniform sampler2D _MainTex;
			uniform sampler2D _NormalTex;
			uniform sampler2D _SurfaceTex;
			uniform sampler2D _EmissionTex;
			uniform float _EmissionIntensity;
			uniform vec4 _MainColor;

			uniform sampler2D _TranslucencyMap;
			uniform float _TranslucencyStrength;
			uniform float _ScatteringPower;
			uniform float _ScatteringDistortion;
			uniform float _ScatteringScale;

			void main()
			{
				fragColor = StandardSurface(texCoord0, worldPos, vColor,
				    vNormal, vTangent, vBitangent,
				    _MainTex, _NormalTex, _SurfaceTex, _EmissionTex,
				    _EmissionIntensity, _MainColor,
				    _MainTex, 0.0, 0,
				    _TranslucencyMap, _TranslucencyStrength,
				    _ScatteringPower, _ScatteringDistortion, _ScatteringScale,
				    vec2(0.0)); // transparent objects use realtime ambient / SH, not baked lightmaps
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer StandardMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _EmissionIntensity;
				float _AlphaCutoff;
				float _Parallax;
				int _ParallaxSteps;
				float _TranslucencyStrength;
				float _ScatteringPower;
				float _ScatteringDistortion;
				float _ScatteringScale;
			};

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				uint vertexId : SV_VertexID;
				float2 vertexTexCoord0 : TEXCOORD0;
				float3 vertexNormal : NORMAL;
				float4 vertexTangent : TANGENT;
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
				float3 vNormal : TEXCOORD2;
				float3 vTangent : TEXCOORD3;
				float3 vBitangent : TEXCOORD4;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				o.position = TransformClip(GetMorphedPosition(input.vertexPosition, input.vertexId));
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
				o.worldPos = TransformPosition(input.vertexPosition);
				o.vColor = GetInstanceColor();
				o.vNormal = TransformDirection(GetMorphedNormal(input.vertexNormal, input.vertexId));
				o.vTangent = TransformDirection(GetMorphedTangent(input.vertexTangent.xyz, input.vertexId));
				o.vBitangent = cross(o.vTangent, o.vNormal) * input.vertexTangent.w;
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"

			cbuffer StandardMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _EmissionIntensity;
				float _AlphaCutoff;
				float _Parallax;
				int _ParallaxSteps;
				float _TranslucencyStrength;
				float _ScatteringPower;
				float _ScatteringDistortion;
				float _ScatteringScale;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);
			Texture2D _NormalTex : register(t1);
			SamplerState _NormalTexSampler : register(s1);
			Texture2D _SurfaceTex : register(t2);
			SamplerState _SurfaceTexSampler : register(s2);
			Texture2D _EmissionTex : register(t3);
			SamplerState _EmissionTexSampler : register(s3);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
				float3 vNormal : TEXCOORD2;
				float3 vTangent : TEXCOORD3;
				float3 vBitangent : TEXCOORD4;
			};

			float4 main(PSInput input) : SV_Target
			{
				float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
				if (_AlphaCutoff > 0.0 && albedo.a < _AlphaCutoff)
					discard;
				float3 emission = _EmissionTex.Sample(_EmissionTexSampler, input.texCoord0).rgb * _EmissionIntensity;
				float3 color = gammaToLinearSpace(albedo.rgb) + emission;
				color = ApplyFog(color, input.worldPos);
				return float4(color, albedo.a);
			}
		}
	ENDSLANG
}
