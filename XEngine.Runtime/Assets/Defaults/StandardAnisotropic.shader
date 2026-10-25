Shader "Default/StandardAnisotropic"

Variants
{
    Variant("HAS_TANGENTS")
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

    _AlphaCutoff ("Alpha Cutoff", Float) = 0.5

    _Anisotropy ("Anisotropy", Float) = 0.5
    _AnisoDirectionMap ("Anisotropy Direction (RG)", Texture2D) = "normal"
}

Pass "StandardAniso"
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
				if (dot(vBitangent, vBitangent) < 0.000001) {
					vTangent = abs(vNormal.y) < 0.999 ? normalize(cross(vNormal, vec3(0,1,0))) : normalize(cross(vNormal, vec3(1,0,0)));
					vBitangent = cross(vTangent, vNormal) * vertexTangent.w;
				}
#endif
			}
		}

		Fragment
		{
            // Clustered light lists (P3.2): cluster lookup replaces the per-fragment BVH walk.
            #define XENGINE_CLUSTERED_LIGHTS
            #include "XEngineCG"
            #include "Lighting"
            #include "ShaderLibrary/ImageBasedLighting"

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
			uniform float _AlphaCutoff;

			uniform float _Anisotropy;
			uniform sampler2D _AnisoDirectionMap;

			void main()
			{
				// Albedo
				vec4 albedo = texture(_MainTex, texCoord0) * vColor * _MainColor;
				vec3 baseColor = gammaToLinearSpace(albedo.rgb);

				// Alpha cutout
				if (albedo.a < _AlphaCutoff)
				    discard;

				// Normal mapping
				vec3 worldNormal = ApplyNormalMap(_NormalTex, texCoord0, vNormal, vTangent, vBitangent);

				// Surface: R = AO, G = Roughness, B = Metallic
				vec4 surface = texture(_SurfaceTex, texCoord0);
				float ao = 1.0 - surface.r;
				float roughness = surface.g;
				float metallic = surface.b;

				// Tangent frame for anisotropic lighting
				vec3 T = normalize(vTangent);
				vec3 B = normalize(vBitangent);
				vec3 N = normalize(worldNormal);

				// Optionally rotate tangent direction by aniso direction map
				// RG encodes direction in tangent plane, default (0.5, 0.5) = use mesh tangent as-is
				vec2 anisoDir = texture(_AnisoDirectionMap, texCoord0).rg * 2.0 - 1.0;
				float anisoDirLen = length(anisoDir);
				vec3 anisoTangent, anisoBitangent;
				if (anisoDirLen > 0.01)
				{
				    anisoDir /= anisoDirLen;
				    anisoTangent = normalize(T * anisoDir.x + B * anisoDir.y);
				    anisoBitangent = normalize(cross(N, anisoTangent));
				}
				else
				{
				    // No direction map or neutral use mesh tangent directly
				    anisoTangent = T;
				    anisoBitangent = B;
				}

				// Emission
				vec3 emission = texture(_EmissionTex, texCoord0).rgb * _EmissionIntensity;

				// Anisotropic PBR lighting
				vec3 viewDir = normalize(_WorldSpaceCameraPos.xyz - worldPos);
				vec3 lighting = CalculateForwardLightingAniso(worldPos, N, viewDir,
				    anisoTangent, anisoBitangent,
				    baseColor, metallic, roughness, _Anisotropy, ao);

				// Ambient + fog (energy conserved for metals)
				vec3 ambientLight = CalculateAmbient(N) * ao * _AmbientStrength;
				vec3 diffuseColor = baseColor * (1.0 - metallic);
				vec3 ambientDiffuse = ambientLight * diffuseColor;

				vec3 F0 = mix(vec3(0.04), baseColor, metallic);
				float NdotV = max(dot(N, viewDir), 0.0);
				vec3 F = FresnelSchlickRoughness(NdotV, F0, roughness);
				float specOcclusion = 1.0 - roughness * roughness;
				vec3 ambientSpecular = MixIndirectSpecular(
					ambientLight * F * mix(specOcclusion, 1.0, 0.25),
					worldPos, N, viewDir, F0, roughness);

				vec3 ambient = ambientDiffuse + ambientSpecular;
				vec3 color = ApplyFog(ambient + lighting + emission, worldPos);

				fragColor = vec4(color, 1.0);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer AnisotropicMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _EmissionIntensity;
				float _AlphaCutoff;
				float _Anisotropy;
				float _AnisotropicMaterialPadding;
			};

			struct VSInput
			{
				float3 vertexPosition : POSITION;
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
				o.position = TransformClip(input.vertexPosition);
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
				o.worldPos = TransformPosition(input.vertexPosition);
				o.vColor = GetInstanceColor();
				o.vNormal = TransformDirection(input.vertexNormal);
				o.vTangent = TransformDirection(input.vertexTangent.xyz);
				o.vBitangent = cross(o.vTangent, o.vNormal) * input.vertexTangent.w;
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"

			cbuffer AnisotropicMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _EmissionIntensity;
				float _AlphaCutoff;
				float _Anisotropy;
				float _AnisotropicMaterialPadding;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);
			Texture2D _NormalTex : register(t1);
			SamplerState _NormalTexSampler : register(s1);
			Texture2D _SurfaceTex : register(t2);
			SamplerState _SurfaceTexSampler : register(s2);
			Texture2D _EmissionTex : register(t3);
			SamplerState _EmissionTexSampler : register(s3);
			Texture2D _AnisoDirectionMap : register(t4);
			SamplerState _AnisoDirectionMapSampler : register(s4);

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
				if (albedo.a < _AlphaCutoff)
					discard;
				float3 emission = _EmissionTex.Sample(_EmissionTexSampler, input.texCoord0).rgb * _EmissionIntensity;
				float3 color = gammaToLinearSpace(albedo.rgb) + emission;
				color = ApplyFog(color, input.worldPos);
				return float4(color, 1.0);
			}
		}
	ENDSLANG
}

Pass "Prepass"
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
			out vec3 vTangent;
			out vec3 vBitangent;
			out vec2 texCoord0;
			out vec4 vCurrClipNJ;
			out vec4 vPrevClip;

			uniform vec2 _Tiling;
			uniform vec2 _Offset;

			void main()
			{
				gl_Position = TransformClip(vertexPosition); // jittered, for raster + depth
				vNormal = TransformDirection(vertexNormal);
#ifdef HAS_TANGENTS
				vTangent = TransformDirection(vertexTangent.xyz);
				vBitangent = cross(vTangent, vNormal) * vertexTangent.w;
#endif
				texCoord0 = vertexTexCoord0 * _Tiling + _Offset;

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
			in vec3 vTangent;
			in vec3 vBitangent;
			in vec2 texCoord0;
			in vec4 vCurrClipNJ;
			in vec4 vPrevClip;

			uniform sampler2D _NormalTex;
			uniform sampler2D _MainTex;
			uniform sampler2D _SurfaceTex;
			uniform float _AlphaCutoff;

			void main()
			{
				if (texture(_MainTex, texCoord0).a < _AlphaCutoff)
				    discard;

                vec3 worldNormal = ApplyNormalMap(_NormalTex, texCoord0, vNormal, vTangent, vBitangent);
				normalOut = EncodeViewNormal(worldNormal);

				// Motion vectors (jitter-free) + packed roughness/metallic (_SurfaceTex G/B).
				vec2 currNDC = (vCurrClipNJ.xy / vCurrClipNJ.w) * 0.5 + 0.5;
				vec2 prevNDC = (vPrevClip.xy / vPrevClip.w) * 0.5 + 0.5;
				vec4 surface = texture(_SurfaceTex, texCoord0);
				motionRM = vec4(currNDC - prevNDC, surface.g, surface.b);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer PrepassMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _AlphaCutoff;
			};

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float2 vertexTexCoord0 : TEXCOORD0;
				float3 vertexNormal : NORMAL;
				float4 vertexTangent : TANGENT;
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float3 vNormal : TEXCOORD0;
				float3 vTangent : TEXCOORD1;
				float3 vBitangent : TEXCOORD2;
				float2 texCoord0 : TEXCOORD3;
				float4 vCurrClipNJ : TEXCOORD4;
				float4 vPrevClip : TEXCOORD5;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				o.position = TransformClip(input.vertexPosition);
				o.vNormal = TransformDirection(input.vertexNormal);
				o.vTangent = TransformDirection(input.vertexTangent.xyz);
				o.vBitangent = cross(o.vTangent, o.vNormal) * input.vertexTangent.w;
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
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

			cbuffer PrepassMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _AlphaCutoff;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);
			Texture2D _NormalTex : register(t1);
			SamplerState _NormalTexSampler : register(s1);
			Texture2D _SurfaceTex : register(t2);
			SamplerState _SurfaceTexSampler : register(s2);

			struct PSInput
			{
				float4 position : SV_Position;
				float3 vNormal : TEXCOORD0;
				float3 vTangent : TEXCOORD1;
				float3 vBitangent : TEXCOORD2;
				float2 texCoord0 : TEXCOORD3;
				float4 vCurrClipNJ : TEXCOORD4;
				float4 vPrevClip : TEXCOORD5;
			};

			struct PSOutput
			{
				float4 normalOut : SV_Target0;
				float4 motionRM : SV_Target1;
			};

			PSOutput main(PSInput input)
			{
				PSOutput o;
				if (_AlphaCutoff > 0.0)
				{
					float alpha = _MainTex.Sample(_MainTexSampler, input.texCoord0).a * _MainColor.a;
					if (alpha < _AlphaCutoff)
						discard;
				}
				o.normalOut = EncodeViewNormal(normalize(input.vNormal));
				float2 currNDC = (input.vCurrClipNJ.xy / input.vCurrClipNJ.w) * 0.5 + 0.5;
				float2 prevNDC = (input.vPrevClip.xy / input.vPrevClip.w) * 0.5 + 0.5;
				float4 surface = _SurfaceTex.Sample(_SurfaceTexSampler, input.texCoord0);
				o.motionRM = float4(currNDC - prevNDC, surface.g, surface.b);
				return o;
			}
		}
	ENDSLANG
}

Pass "GiAlbedo"
{
    Tags { "LightMode" = "GiAlbedo" }
    Cull Back
    ZWrite On

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

			layout (location = 0) out vec4 albedoOut;

			in vec2 texCoord0;
			in vec4 vColor;

			uniform sampler2D _MainTex;
			uniform sampler2D _SurfaceTex;
			uniform vec4 _MainColor;
			uniform float _AlphaCutoff;

			void main()
			{
				// Realtime-GI albedo capture (forward path): rgb = linear albedo, a = AO.
				vec4 mainTex = texture(_MainTex, texCoord0) * vColor * _MainColor;
				if (_AlphaCutoff > 0.0 && mainTex.a < _AlphaCutoff)
					discard;
				vec4 surface = texture(_SurfaceTex, texCoord0);
				albedoOut = vec4(gammaToLinearSpace(mainTex.rgb), saturate(1.0 - surface.r));
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer GiAlbedoMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _AlphaCutoff;
				float3 _GiAlbedoMaterialPadding;
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

			cbuffer GiAlbedoMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _AlphaCutoff;
				float3 _GiAlbedoMaterialPadding;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);
			Texture2D _SurfaceTex : register(t1);
			SamplerState _SurfaceTexSampler : register(s1);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float4 vColor : COLOR0;
			};

			float4 main(PSInput input) : SV_Target
			{
				float4 mainTex = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
				if (_AlphaCutoff > 0.0 && mainTex.a < _AlphaCutoff)
					discard;
				float4 surface = _SurfaceTex.Sample(_SurfaceTexSampler, input.texCoord0);
				return float4(gammaToLinearSpace(mainTex.rgb), saturate(1.0 - surface.r));
			}
		}
	ENDSLANG
}

Pass "ShadowCaster"
{
    Tags { "LightMode" = "ShadowCaster" }
    Cull Back

	GLSLPROGRAM

		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;

			uniform vec2 _Tiling;
			uniform vec2 _Offset;

			void main()
			{
				gl_Position = TransformClip(vertexPosition);
				texCoord0 = vertexTexCoord0 * _Tiling + _Offset;
			}
		}

		Fragment
		{
            #include "XEngineCG"

			in vec2 texCoord0;
			uniform sampler2D _MainTex;
			uniform float _AlphaCutoff;

			void main()
			{
				if (texture(_MainTex, texCoord0).a < _AlphaCutoff)
				    discard;
                gl_FragDepth = gl_FragCoord.z;
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer ShadowMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _AlphaCutoff;
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
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				o.position = TransformClip(input.vertexPosition);
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
				return o;
			}
		}

		Fragment
		{
			cbuffer ShadowMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float _AlphaCutoff;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
			};

			void main(PSInput input)
			{
				if (_AlphaCutoff > 0.0)
				{
					float alpha = _MainTex.Sample(_MainTexSampler, input.texCoord0).a * _MainColor.a;
					if (alpha < _AlphaCutoff)
						discard;
				}
			}
		}
	ENDSLANG
}
