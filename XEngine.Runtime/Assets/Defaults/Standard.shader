Shader "Default/Standard"

Variants
{
    Variant("DIRECTIONAL_ONLY")
    Variant("GPU_INSTANCING", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS")
    Variant("GPU_INSTANCING", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS")
    Variant("GPU_INSTANCING", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_UV2")
    Variant("GPU_INSTANCING", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS", "HAS_UV2")
    // Directional-only light state crosses the shadow-pass instancing keyword set: the shadow
    // pass adds GPU_INSTANCING while the material carries DIRECTIONAL_ONLY from the light
    // state, so the union of both must exist for every mesh-keyword combination.
    Variant("DIRECTIONAL_ONLY", "GPU_INSTANCING")
    Variant("DIRECTIONAL_ONLY", "GPU_INSTANCING", "HAS_TANGENTS")
    Variant("DIRECTIONAL_ONLY", "GPU_INSTANCING", "HAS_UV2")
    Variant("DIRECTIONAL_ONLY", "GPU_INSTANCING", "HAS_TANGENTS", "HAS_UV2")
    Variant("DIRECTIONAL_ONLY", "GPU_INSTANCING", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS")
    Variant("DIRECTIONAL_ONLY", "GPU_INSTANCING", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS")
    Variant("DIRECTIONAL_ONLY", "GPU_INSTANCING", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_UV2")
    Variant("DIRECTIONAL_ONLY", "GPU_INSTANCING", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS", "HAS_UV2")
    Variant("DIRECTIONAL_ONLY", "HAS_TANGENTS")
    Variant("DIRECTIONAL_ONLY", "HAS_TANGENTS", "HAS_UV2")
    Variant("DIRECTIONAL_ONLY", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS")
    Variant("DIRECTIONAL_ONLY", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS", "HAS_UV2")
    Variant("GPU_INSTANCING")
    Variant("GPU_INSTANCING", "HAS_TANGENTS")
    Variant("GPU_INSTANCING", "HAS_TANGENTS", "HAS_UV2")
    Variant("HAS_TANGENTS")
    Variant("HAS_UV2")
    Variant("SKINNED")
    Variant("HAS_BONEINDICES")
    Variant("HAS_BONEWEIGHTS")
    Variant("HAS_TANGENTS", "HAS_UV2")
    Variant("SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS")
    Variant("SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS")
    Variant("SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_UV2")
    Variant("SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS", "HAS_UV2")
    // Blend shapes: morph meshes in practice are skinned characters (facial shapes on a bone
    // rig) or morph-only meshes. GPU_INSTANCING combos are deliberately absent — the instanced
    // batch path rejects morph meshes at runtime (per-instance weights unsupported).
    Variant("BLENDSHAPES", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS")
    Variant("BLENDSHAPES", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS")
    Variant("BLENDSHAPES", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_UV2")
    Variant("BLENDSHAPES", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS", "HAS_UV2")
    Variant("BLENDSHAPES")
    Variant("BLENDSHAPES", "HAS_TANGENTS")
    // Directional-only light state unions with the shadow pass keyword set (comment above): the
    // same morph combos must exist under DIRECTIONAL_ONLY for shadow casters.
    Variant("DIRECTIONAL_ONLY", "BLENDSHAPES", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS")
    Variant("DIRECTIONAL_ONLY", "BLENDSHAPES", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS")
    Variant("DIRECTIONAL_ONLY", "BLENDSHAPES", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_UV2")
    Variant("DIRECTIONAL_ONLY", "BLENDSHAPES", "SKINNED", "HAS_BONEINDICES", "HAS_BONEWEIGHTS", "HAS_TANGENTS", "HAS_UV2")
}

Properties
{
    _MainTex ("Albedo", Texture2D) = "grid"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _Tiling ("Tiling", Vector2) = (1.0, 1.0)
    _Offset ("Offset", Vector2) = (0.0, 0.0)

    _NormalTex ("Normal", Texture2D) = "normal"

    _SurfaceTex ("Surface (AO, Roughness, Metallicness)", Texture2D) = "surface"

    _EmissionTex ("Emission", Texture2D) = "emission"
    _EmissionIntensity ("Emission Intensity", Float) = 1.0

    _AlphaCutoff ("Alpha Cutoff", Float) = 0.5

    _ParallaxMap ("Height Map (G)", Texture2D) = "black"
    _Parallax ("Height Scale", Float) = 0.0
    _ParallaxSteps ("POM Steps", Int) = 16

    _TranslucencyMap ("Translucency (B) Occlusion (G)", Texture2D) = "white"
    _TranslucencyStrength ("Translucency Strength", Float) = 0.0
    _ScatteringPower ("Scattering Power", Float) = 0.0
    _ScatteringDistortion ("Scattering Distortion", Float) = 0.5
    _ScatteringScale ("Scattering Scale", Float) = 1.0
}

Pass "Standard"
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
			out vec2 vLightmapUV2;

			uniform vec2 _Tiling;
			uniform vec2 _Offset;

			void main()
			{
				gl_Position = TransformClip(vertexPosition);
				texCoord0 = vertexTexCoord0 * _Tiling + _Offset;
				vLightmapUV2 = vertexTexCoord1; // raw UV2; scale/offset applied in the fragment
				worldPos = TransformPosition(vertexPosition);
				vColor = GetInstanceColor();
				vNormal = TransformDirection(GetMorphedNormal(vertexNormal));
#ifdef HAS_TANGENTS
				vTangent = TransformDirection(GetMorphedTangent(vertexTangent.xyz));
				vBitangent = cross(vTangent, vNormal) * vertexTangent.w;
				// Guard against degenerate tangent frames (parallel normal/tangent)
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
            // Remove this define to fall back to the pure BVH path.
            #define XENGINE_CLUSTERED_LIGHTS
            #include "StandardSurface"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec3 worldPos;
			in vec4 vColor;
			in vec3 vNormal;
			in vec3 vTangent;
			in vec3 vBitangent;
			in vec2 vLightmapUV2;

			uniform sampler2D _MainTex;
			uniform sampler2D _NormalTex;
			uniform sampler2D _SurfaceTex;
			uniform sampler2D _EmissionTex;
			uniform float _EmissionIntensity;
			uniform vec4 _MainColor;
			uniform float _AlphaCutoff;

			uniform sampler2D _ParallaxMap;
			uniform float _Parallax;
			uniform int _ParallaxSteps;

			uniform sampler2D _TranslucencyMap;
			uniform float _TranslucencyStrength;
			uniform float _ScatteringPower;
			uniform float _ScatteringDistortion;
			uniform float _ScatteringScale;

			void main()
			{
				vec4 result = StandardSurface(texCoord0, worldPos, vColor,
				    vNormal, vTangent, vBitangent,
				    _MainTex, _NormalTex, _SurfaceTex, _EmissionTex,
				    _EmissionIntensity, _MainColor,
				    _ParallaxMap, _Parallax, _ParallaxSteps,
				    _TranslucencyMap, _TranslucencyStrength,
				    _ScatteringPower, _ScatteringDistortion, _ScatteringScale,
				    vLightmapUV2);

				// Alpha cutout discard below threshold, output fully opaque
				if (result.a < _AlphaCutoff)
				    discard;

				fragColor = vec4(result.rgb, 1.0);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
            #define XENGINE_STANDARD_INSTANCE_VERTEX
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
                XENGINE_INSTANCE_ATTRIBUTES
				float3 vertexPosition : POSITION;
				uint vertexId : SV_VertexID;
				float2 vertexTexCoord0 : TEXCOORD0;
				// Gated: meshes without UV2/tangents would leave these shader inputs without a
				// matching vertex attribute (VUID-07904 UB — the driver feeds garbage into the
				// whole vertex stage, shredding skinned meshes rendered via default Standard,
				// e.g. the Inspector FBX preview).
#ifdef HAS_UV2
				float2 vertexTexCoord1 : TEXCOORD1;
#endif
				// The input layout omits optional mesh streams. Supply the same upward normal
				// fallback as VertexAttributes.hlsl when a mesh has no authored normals.
#ifdef HAS_NORMALS
				float3 vertexNormal : NORMAL;
#endif
#ifdef HAS_TANGENTS
				float4 vertexTangent : TANGENT;
#endif
#ifdef HAS_BONEINDICES
				float4 vertexBoneIndices : BLENDINDICES;
#endif
#ifdef HAS_BONEWEIGHTS
				float4 vertexBoneWeights : BLENDWEIGHT;
#endif
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
				float2 vLightmapUV2 : TEXCOORD5;
			};

			VSOutput main(VSInput input)
			{
                XENGINE_SETUP_INSTANCE(input);
				VSOutput o;
				float3 sourceNormal = float3(0.0, 1.0, 0.0);
#ifdef HAS_NORMALS
				sourceNormal = input.vertexNormal;
#endif
#if defined(SKINNED) && defined(HAS_BONEINDICES) && defined(HAS_BONEWEIGHTS)
				// Morph the rest-pose vertex BEFORE skinning (same order as the GLSL path).
				float3 morphedPos = GetMorphedPosition(input.vertexPosition, input.vertexId);
				float3 skinnedPos = XSkinPosition(morphedPos, input.vertexBoneIndices, input.vertexBoneWeights).xyz;
				float3 skinnedN = XSkinNormal(GetMorphedNormal(sourceNormal, input.vertexId), input.vertexBoneIndices, input.vertexBoneWeights);
				o.position = TransformClip(skinnedPos);
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
#ifdef HAS_UV2
				o.vLightmapUV2 = input.vertexTexCoord1;
#else
				o.vLightmapUV2 = float2(0.0, 0.0);
#endif
				o.worldPos = TransformPosition(skinnedPos);
				o.vColor = GetInstanceColor();
				o.vNormal = TransformDirection(skinnedN);
#ifdef HAS_TANGENTS
				float3 skinnedT = XSkinNormal(GetMorphedTangent(input.vertexTangent.xyz, input.vertexId), input.vertexBoneIndices, input.vertexBoneWeights);
				o.vTangent = TransformDirection(skinnedT);
				o.vBitangent = cross(o.vTangent, o.vNormal) * input.vertexTangent.w;
#else
				o.vTangent = float3(1.0, 0.0, 0.0);
				o.vBitangent = float3(0.0, 1.0, 0.0);
#endif
				return o;
#else
				o.position = TransformClip(GetMorphedPosition(input.vertexPosition, input.vertexId));
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
#ifdef HAS_UV2
				o.vLightmapUV2 = input.vertexTexCoord1;
#else
				o.vLightmapUV2 = float2(0.0, 0.0);
#endif
				o.worldPos = TransformPosition(input.vertexPosition);
				o.vColor = GetInstanceColor();
				o.vNormal = TransformDirection(GetMorphedNormal(sourceNormal, input.vertexId));
#ifdef HAS_TANGENTS
				o.vTangent = TransformDirection(GetMorphedTangent(input.vertexTangent.xyz, input.vertexId));
				o.vBitangent = cross(o.vTangent, o.vNormal) * input.vertexTangent.w;
#else
				o.vTangent = float3(1.0, 0.0, 0.0);
				o.vBitangent = float3(0.0, 1.0, 0.0);
#endif
				return o;
#endif
			}
		}

		Fragment
		{
			// Clustered light lists (P3.2): cluster lookup replaces the per-fragment BVH walk.
			// Remove this define to fall back to the pure BVH path.
			#define XENGINE_CLUSTERED_LIGHTS
			#include "XEngineCG"
			#include "ShaderLibrary/LightCookie"
			#include "ShaderLibrary/GiProbes"
			#include "StandardLighting"
			#include "ShaderLibrary/ImageBasedLighting"

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
			Texture2D _ParallaxMap : register(t9);
			SamplerState _ParallaxMapSampler : register(s9);
			Texture2D _TranslucencyMap : register(t10);
			SamplerState _TranslucencyMapSampler : register(s10);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
				float3 vNormal : TEXCOORD2;
				float3 vTangent : TEXCOORD3;
				float3 vBitangent : TEXCOORD4;
				float2 vLightmapUV2 : TEXCOORD5;
			};

			float4 main(PSInput input) : SV_Target
			{
				// Parallax Occlusion Mapping: ray-march the height field in tangent space and offset
				// every subsequent texture fetch to the resolved UV. Matches the GLSL StandardSurface
				// path (only applied when HAS_TANGENTS-equivalent tangent frame is valid + _Parallax>0).
				float2 finalUV = input.texCoord0;
				float3 normal = normalize(input.vNormal);
				float3 tangent = normalize(input.vTangent);
				float3 bitangent = normalize(input.vBitangent);
				if (_Parallax > 0.0 && _ParallaxSteps > 0)
				{
					float3x3 tbn = float3x3(tangent, bitangent, normal);
					float3 viewDirWorld = _WorldSpaceCameraPos - input.worldPos;
					float3 viewDirTS = normalize(mul(transpose(tbn), viewDirWorld));
					finalUV = StandardParallaxOcclusionMapping(_ParallaxMap, _ParallaxMapSampler,
						input.texCoord0, viewDirTS, _Parallax, _ParallaxSteps);
				}

				float4 albedo = _MainTex.Sample(_MainTexSampler, finalUV) * input.vColor * _MainColor;
				if (albedo.a < _AlphaCutoff)
					discard;
				float3 tangentNormal = _NormalTex.Sample(_NormalTexSampler, finalUV).xyz * 2.0 - 1.0;
				normal = normalize(tangentNormal.x * tangent + tangentNormal.y * bitangent + tangentNormal.z * normal);
				float4 surface = _SurfaceTex.Sample(_SurfaceTexSampler, finalUV);
				float ao = saturate(1.0 - surface.r);
				float roughness = max(surface.g, 0.045);
				float metallic = saturate(surface.b);
				// Translucency map: G = extra occlusion (folded into AO), B = thickness. Matches the
				// GLSL StandardSurface convention; _TranslucencyStrength scales the per-pixel thickness.
				float4 transOcc = _TranslucencyMap.Sample(_TranslucencyMapSampler, finalUV);
				ao *= transOcc.g;
				float translucency = transOcc.b * _TranslucencyStrength;
				float3 viewDirection = normalize(_WorldSpaceCameraPos - input.worldPos);
				float3 emission = _EmissionTex.Sample(_EmissionTexSampler, finalUV).rgb * _EmissionIntensity;
				float3 baseColor = gammaToLinearSpace(albedo.rgb);
				// Baked GI / ambient (StandardCalculateGI falls through to StandardCalculateAmbient
				// when _GIMode == 0). The diffuse/specular ambient split mirrors the GLSL
				// StandardSurface path so metals pick up indirect specular via the ambient Fresnel.
				float3 ambientLight = StandardCalculateGI(input.worldPos, normal, input.vLightmapUV2, finalUV) * ao;
				float3 diffuseColor = baseColor * (1.0 - metallic);
				float3 ambientDiffuse = ambientLight * diffuseColor;
				float3 F0 = lerp(0.04.xxx, baseColor, metallic);
				float NdotV = max(dot(normal, viewDirection), 0.0);
				float3 F = StandardAmbientFresnel(NdotV, F0, roughness);
				float specOcclusion = 1.0 - roughness * roughness;
				float3 ambientSpecular = MixIndirectSpecular(
					ambientLight * F * lerp(specOcclusion, 1.0, 0.25),
					input.worldPos, normal, viewDirection, F0, roughness);
				float3 color = ambientDiffuse + ambientSpecular;
#ifdef DIRECTIONAL_ONLY
                color += StandardEvaluateDirectional(input.worldPos, normal, viewDirection, input.position.xy,
                    baseColor, metallic, roughness, ao,
                    translucency, _ScatteringPower, _ScatteringDistortion, _ScatteringScale);
#else
				color += StandardCalculateLighting(input.worldPos, normal, viewDirection, input.position.xy,
					baseColor, metallic, roughness, ao,
					translucency, _ScatteringPower, _ScatteringDistortion, _ScatteringScale);
#endif
				color += emission;
				color = StandardApplyFog(color, input.worldPos);
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
				vNormal = TransformDirection(GetMorphedNormal(vertexNormal));
#ifdef HAS_TANGENTS
				vTangent = TransformDirection(GetMorphedTangent(vertexTangent.xyz));
				vBitangent = cross(vTangent, vNormal) * vertexTangent.w;
				if (dot(vBitangent, vBitangent) < 0.000001) {
					vTangent = abs(vNormal.y) < 0.999 ? normalize(cross(vNormal, vec3(0,1,0))) : normalize(cross(vNormal, vec3(1,0,0)));
					vBitangent = cross(vTangent, vNormal) * vertexTangent.w;
				}
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
			uniform vec4 _MainColor;
			uniform float _AlphaCutoff;

			void main()
			{
				// Alpha cutoff for cutout mode
				if (_AlphaCutoff > 0.0)
				{
				    float alpha = texture(_MainTex, texCoord0).a * _MainColor.a;
				    if (alpha < _AlphaCutoff) discard;
				}

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
            #define XENGINE_STANDARD_INSTANCE_VERTEX
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
                XENGINE_INSTANCE_ATTRIBUTES
				float3 vertexPosition : POSITION;
				uint vertexId : SV_VertexID;
				float2 vertexTexCoord0 : TEXCOORD0;
#ifdef HAS_NORMALS
				float3 vertexNormal : NORMAL;
#endif
#ifdef HAS_TANGENTS
				float4 vertexTangent : TANGENT;
#endif
#ifdef HAS_BONEINDICES
				float4 vertexBoneIndices : BLENDINDICES;
				float4 vertexBoneWeights : BLENDWEIGHT;
#endif
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
                XENGINE_SETUP_INSTANCE(input);
				VSOutput o;
				float3 sourceNormal = float3(0.0, 1.0, 0.0);
#ifdef HAS_NORMALS
				sourceNormal = input.vertexNormal;
#endif
				// Morph the rest-pose vertex BEFORE skinning (same order as the GLSL path).
				float3 morphedPos = GetMorphedPosition(input.vertexPosition, input.vertexId);
#if defined(SKINNED) && defined(HAS_BONEINDICES)
				o.position = TransformClipSkinned(morphedPos, input.vertexBoneIndices, input.vertexBoneWeights);
#else
				o.position = TransformClip(morphedPos);
#endif
				o.vNormal = TransformDirection(GetMorphedNormal(sourceNormal, input.vertexId));
				// Gated: tangent-less variants have no vertexTangent input (member not found at
				// compile time; VUID-07904 UB at draw time if declared without a vertex stream).
#ifdef HAS_TANGENTS
				o.vTangent = TransformDirection(GetMorphedTangent(input.vertexTangent.xyz, input.vertexId));
				o.vBitangent = cross(o.vTangent, o.vNormal) * input.vertexTangent.w;
#else
				o.vTangent = float3(1.0, 0.0, 0.0);
				o.vBitangent = float3(0.0, 1.0, 0.0);
#endif
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
				float4 worldPos = mul(GetModelMatrix(), float4(morphedPos, 1.0));
				o.vCurrClipNJ = mul(XENGINE_MATRIX_VP_NONJITTERED, worldPos);
				float4 prevWorldPos = mul(XENGINE_MATRIX_M_PREVIOUS, float4(morphedPos, 1.0));
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

Pass "GBuffer"
{
    Tags { "LightMode" = "GBuffer" }
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
			out vec4 vColor;

			uniform vec2 _Tiling;
			uniform vec2 _Offset;

			void main()
			{
				gl_Position = TransformClip(vertexPosition);
				vNormal = TransformDirection(GetMorphedNormal(vertexNormal));
#ifdef HAS_TANGENTS
				vTangent = TransformDirection(GetMorphedTangent(vertexTangent.xyz));
				vBitangent = cross(vTangent, vNormal) * vertexTangent.w;
				if (dot(vBitangent, vBitangent) < 0.000001) {
					vTangent = abs(vNormal.y) < 0.999 ? normalize(cross(vNormal, vec3(0,1,0))) : normalize(cross(vNormal, vec3(1,0,0)));
					vBitangent = cross(vTangent, vNormal) * vertexTangent.w;
				}
#endif
				texCoord0 = vertexTexCoord0 * _Tiling + _Offset;
				vColor = GetInstanceColor();

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
			layout (location = 2) out vec4 albedoAO;
			layout (location = 3) out vec4 emissionOut;

			in vec3 vNormal;
			in vec3 vTangent;
			in vec3 vBitangent;
			in vec2 texCoord0;
			in vec4 vCurrClipNJ;
			in vec4 vPrevClip;
			in vec4 vColor;

			uniform sampler2D _MainTex;
			uniform sampler2D _NormalTex;
			uniform sampler2D _SurfaceTex;
			uniform sampler2D _EmissionTex;
			uniform vec4 _MainColor;
			uniform float _AlphaCutoff;
			uniform float _EmissionIntensity;

			void main()
			{
				if (_AlphaCutoff > 0.0)
				{
				    float alpha = texture(_MainTex, texCoord0).a * _MainColor.a * vColor.a;
				    if (alpha < _AlphaCutoff) discard;
				}

                vec3 worldNormal = ApplyNormalMap(_NormalTex, texCoord0, vNormal, vTangent, vBitangent);
				normalOut = EncodeViewNormal(worldNormal);

				vec2 currNDC = (vCurrClipNJ.xy / vCurrClipNJ.w) * 0.5 + 0.5;
				vec2 prevNDC = (vPrevClip.xy / vPrevClip.w) * 0.5 + 0.5;
				vec4 surface = texture(_SurfaceTex, texCoord0);
				motionRM = vec4(currNDC - prevNDC, surface.g, surface.b);

				vec3 albedo = gammaToLinearSpace(texture(_MainTex, texCoord0).rgb * vColor.rgb * _MainColor.rgb);
				float ao = 1.0 - surface.r;
				albedoAO = vec4(albedo, ao);
				emissionOut = vec4(texture(_EmissionTex, texCoord0).rgb * _EmissionIntensity, 0.0);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
            #define XENGINE_STANDARD_INSTANCE_VERTEX
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
                XENGINE_INSTANCE_ATTRIBUTES
				float3 vertexPosition : POSITION;
				uint vertexId : SV_VertexID;
				float2 vertexTexCoord0 : TEXCOORD0;
#ifdef HAS_NORMALS
				float3 vertexNormal : NORMAL;
#endif
#ifdef HAS_TANGENTS
				float4 vertexTangent : TANGENT;
#endif
#ifdef HAS_BONEINDICES
				float4 vertexBoneIndices : BLENDINDICES;
				float4 vertexBoneWeights : BLENDWEIGHT;
#endif
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
				float4 vColor : COLOR0;
			};

			VSOutput main(VSInput input)
			{
                XENGINE_SETUP_INSTANCE(input);
				VSOutput o;
				float3 sourceNormal = float3(0.0, 1.0, 0.0);
#ifdef HAS_NORMALS
				sourceNormal = input.vertexNormal;
#endif
				// Morph the rest-pose vertex BEFORE skinning (same order as the GLSL path).
				float3 morphedPos = GetMorphedPosition(input.vertexPosition, input.vertexId);
#if defined(SKINNED) && defined(HAS_BONEINDICES)
				o.position = TransformClipSkinned(morphedPos, input.vertexBoneIndices, input.vertexBoneWeights);
#else
				o.position = TransformClip(morphedPos);
#endif
				o.vNormal = TransformDirection(GetMorphedNormal(sourceNormal, input.vertexId));
				// Gated: tangent-less variants have no vertexTangent input (member not found at
				// compile time; VUID-07904 UB at draw time if declared without a vertex stream).
#ifdef HAS_TANGENTS
				o.vTangent = TransformDirection(GetMorphedTangent(input.vertexTangent.xyz, input.vertexId));
				o.vBitangent = cross(o.vTangent, o.vNormal) * input.vertexTangent.w;
#else
				o.vTangent = float3(1.0, 0.0, 0.0);
				o.vBitangent = float3(0.0, 1.0, 0.0);
#endif
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
				o.vColor = GetInstanceColor();
				float4 worldPos = mul(GetModelMatrix(), float4(morphedPos, 1.0));
				o.vCurrClipNJ = mul(XENGINE_MATRIX_VP_NONJITTERED, worldPos);
				float4 prevWorldPos = mul(XENGINE_MATRIX_M_PREVIOUS, float4(morphedPos, 1.0));
				o.vPrevClip = mul(XENGINE_MATRIX_VP_PREVIOUS, prevWorldPos);
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
				float3 vNormal : TEXCOORD0;
				float3 vTangent : TEXCOORD1;
				float3 vBitangent : TEXCOORD2;
				float2 texCoord0 : TEXCOORD3;
				float4 vCurrClipNJ : TEXCOORD4;
				float4 vPrevClip : TEXCOORD5;
				float4 vColor : COLOR0;
			};

			struct PSOutput
			{
				float4 normalOut : SV_Target0;
				float4 motionRM : SV_Target1;
				float4 albedoAO : SV_Target2;
				float4 emissionOut : SV_Target3;
			};

			PSOutput main(PSInput input)
			{
				PSOutput o;
				float4 albedoSample = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
				if (_AlphaCutoff > 0.0 && albedoSample.a < _AlphaCutoff)
					discard;
				float3 tangent = normalize(input.vTangent);
				float3 bitangent = normalize(input.vBitangent);
				float3 normal = normalize(input.vNormal);
				float3 tangentNormal = _NormalTex.Sample(_NormalTexSampler, input.texCoord0).xyz * 2.0 - 1.0;
				normal = normalize(tangentNormal.x * tangent + tangentNormal.y * bitangent + tangentNormal.z * normal);
				o.normalOut = EncodeViewNormal(normal);
				float2 currNDC = (input.vCurrClipNJ.xy / input.vCurrClipNJ.w) * 0.5 + 0.5;
				float2 prevNDC = (input.vPrevClip.xy / input.vPrevClip.w) * 0.5 + 0.5;
				float4 surface = _SurfaceTex.Sample(_SurfaceTexSampler, input.texCoord0);
				o.motionRM = float4(currNDC - prevNDC, surface.g, surface.b);
				o.albedoAO = float4(gammaToLinearSpace(albedoSample.rgb), saturate(1.0 - surface.r));
				o.emissionOut = float4(_EmissionTex.Sample(_EmissionTexSampler, input.texCoord0).rgb * _EmissionIntensity, 0.0);
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
				// Realtime-GI albedo capture for the forward path: matches the deferred GBuffer
				// albedoAO attachment (rgb = linear albedo, a = AO) so SSGI compositing is
				// identical on both rendering paths.
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
            #define XENGINE_STANDARD_INSTANCE_VERTEX
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
                XENGINE_INSTANCE_ATTRIBUTES
				float3 vertexPosition : POSITION;
				uint vertexId : SV_VertexID;
				float2 vertexTexCoord0 : TEXCOORD0;
#ifdef HAS_BONEINDICES
				float4 vertexBoneIndices : BLENDINDICES;
				float4 vertexBoneWeights : BLENDWEIGHT;
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
                XENGINE_SETUP_INSTANCE(input);
				VSOutput o;
				float3 morphedPos = GetMorphedPosition(input.vertexPosition, input.vertexId);
#if defined(SKINNED) && defined(HAS_BONEINDICES)
				o.position = TransformClipSkinned(morphedPos, input.vertexBoneIndices, input.vertexBoneWeights);
#else
				o.position = TransformClip(morphedPos);
#endif
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

Pass "StandardShadow"
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
			uniform vec4 _MainColor;
			uniform float _AlphaCutoff;

			void main()
			{
				if (_AlphaCutoff > 0.0)
				{
				    float alpha = texture(_MainTex, texCoord0).a * _MainColor.a;
				    if (alpha < _AlphaCutoff) discard;
				}
                gl_FragDepth = gl_FragCoord.z;
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
            #define XENGINE_STANDARD_INSTANCE_VERTEX
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
                XENGINE_INSTANCE_ATTRIBUTES
				float3 vertexPosition : POSITION;
				uint vertexId : SV_VertexID;
				float2 vertexTexCoord0 : TEXCOORD0;
#ifdef HAS_BONEINDICES
				float4 vertexBoneIndices : BLENDINDICES;
				float4 vertexBoneWeights : BLENDWEIGHT;
#endif
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
			};

			VSOutput main(VSInput input)
			{
                XENGINE_SETUP_INSTANCE(input);
				VSOutput o;
				float3 morphedPos = GetMorphedPosition(input.vertexPosition, input.vertexId);
#if defined(SKINNED) && defined(HAS_BONEINDICES)
				o.position = TransformClipSkinned(morphedPos, input.vertexBoneIndices, input.vertexBoneWeights);
#else
				o.position = TransformClip(morphedPos);
#endif
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
