Shader "Default/Grass"

Variants
{
    Variant("GPU_INSTANCING")
}

Properties
{
    _MainTex ("Grass Texture", Texture2D) = "white"
    _AlphaCutoff ("Alpha Cutoff", Float) = 0.5
    _WindStrength ("Wind Strength", Float) = 0.3
    _WindSpeed ("Wind Speed", Float) = 1.5
    _Billboard ("Billboard", Float) = 1.0
    _AlignToNormal ("Align To Normal", Float) = 0.0
    _Translucency ("Translucency", Float) = 15.0
    _ScatterPower ("Scattering Power", Float) = 0.0
    _ScatterDistortion ("Scattering Distortion", Float) = 0.5
    _ScatterScale ("Scattering Scale", Float) = 1.0
    _GrassDistance ("Grass Max Distance", Float) = 150.0
    _GrassFadeStart ("Grass Fade Start (world units)", Float) = 90.0
}

Pass "Grass"
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
            out vec3 worldPos;
            out vec3 vNormal;

            uniform float _WindStrength;
            uniform float _WindSpeed;
            uniform float _Billboard;
            uniform float _AlignToNormal;
            uniform float _GrassDistance;
            uniform float _GrassFadeStart;
            uniform vec3 _TerrainUp;
            uniform sampler2D _Heightmap;
            uniform float _TerrainSize;
            uniform float _TerrainHeight;
            uniform mat4 _TerrainWorldToLocal;
            uniform mat4 _TerrainLocalToWorld;

			void main()
			{
#ifdef GPU_INSTANCING
                // Instance matrix is in terrain-local space; transform to world
                mat4 terrainToWorld = _TerrainLocalToWorld;

                // Extract grass blade position (terrain-local) and scale
                vec3 localPosition = instanceModelRow3.xyz;
                vec3 bladePosition = (terrainToWorld * vec4(localPosition, 1.0)).xyz;
                float scaleX = length(instanceModelRow0.xyz); // width
                float scaleY = length(instanceModelRow1.xyz); // height

                // Distance fade shrink blades to zero between _GrassFadeStart and _GrassDistance
                // so patches that the CPU-side culler is about to drop don't pop out suddenly.
                // Horizontal camera distance matches the CPU's XZ-plane patch cull.
                vec3 camFlat = vec3(_WorldSpaceCameraPos.x, bladePosition.y, _WorldSpaceCameraPos.z);
                float camDist = length(bladePosition - camFlat);
                float fadeRange = max(0.0001, _GrassDistance - _GrassFadeStart);
                float fade = 1.0 - clamp((camDist - _GrassFadeStart) / fadeRange, 0.0, 1.0);
                scaleX *= fade;
                scaleY *= fade;

                // Compute terrain surface normal from heightmap for blade orientation.
                vec2 terrainUV = localPosition.xz / _TerrainSize;
                vec2 hmSize2 = vec2(textureSize(_Heightmap, 0));
                float hmSize = hmSize2.x;
                float vertStep = hmSize > 1.0 ? (1.0 / (hmSize - 1.0)) : 0.001;

                vec2 baseUV = terrainUV * (hmSize2 - 1.0) / hmSize2 + 0.5 / hmSize2;
                vec2 stepUV = vec2(vertStep * (hmSize - 1.0) / hmSize, 0.0);
                vec2 stepVV = vec2(0.0, vertStep * (hmSize - 1.0) / hmSize);
                float hR = texture(_Heightmap, baseUV + stepUV).r * _TerrainHeight;
                float hL = texture(_Heightmap, baseUV - stepUV).r * _TerrainHeight;
                float hU = texture(_Heightmap, baseUV + stepVV).r * _TerrainHeight;
                float hD = texture(_Heightmap, baseUV - stepVV).r * _TerrainHeight;

                float wStep = vertStep * _TerrainSize;
                vec3 localNormal = normalize(vec3(-(hR - hL) / (wStep * 2.0), 1.0, -(hU - hD) / (wStep * 2.0)));
                vec3 terrainNormal = normalize((terrainToWorld * vec4(localNormal, 0.0)).xyz);

                // Up direction for blade orientation: terrain normal if AlignToNormal, else terrain's Y axis
                vec3 up = (_AlignToNormal > 0.5) ? terrainNormal : _TerrainUp;

                vec3 quadRight;
                vec3 localOffset;
                if (_Billboard > 0.5)
                {
                    // Cylindrical billboard around terrain up axis
                    vec3 cameraRight = vec3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[1][0], XENGINE_MATRIX_V[2][0]);
                    // Project camera right perpendicular to up
                    cameraRight = normalize(cameraRight - up * dot(cameraRight, up));
                    quadRight = cameraRight;
                    localOffset = cameraRight * vertexPosition.x * scaleX
                                 + up * vertexPosition.y * scaleY;
                }
                else
                {
                    // Non-billboard: transform instance orientation from terrain-local to world
                    vec3 right = normalize((terrainToWorld * vec4(normalize(instanceModelRow0.xyz), 0.0)).xyz);
                    // Re-orthogonalize right to be perpendicular to up
                    right = normalize(right - up * dot(right, up));
                    quadRight = right;
                    localOffset = right * vertexPosition.x * scaleX
                                + up * vertexPosition.y * scaleY;
                }

                // Quad face normal: perpendicular to the plane defined by right and up
                vec3 quadNormal = normalize(cross(up, quadRight));

                // Wind sway - only affects top vertices (y > 0)
                float windPhase = instanceCustomData.x;
                float bendFactor = instanceCustomData.y;
                float windAmount = max(0.0, vertexPosition.y);
                float windBase = sin(_Time.y * _WindSpeed + bladePosition.x * 0.7 + bladePosition.z * 0.4 + windPhase);
                float windTurb = sin(_Time.y * _WindSpeed * 2.73 + bladePosition.z * 1.3 + windPhase * 1.7) * 0.35;
                float wind = (windBase + windTurb) * _WindStrength * bendFactor;
                localOffset.x += wind * windAmount;
                localOffset.z += wind * windAmount * 0.3;

                vec3 worldPosition = bladePosition + localOffset;
                worldPosition += up * 0.01 * scaleY; // Minimal offset to reduce ground clipping
                worldPos = worldPosition;
                vNormal = quadNormal;
                vColor = instanceColor;

                gl_Position = XENGINE_MATRIX_VP * vec4(worldPosition, 1.0);
                texCoord0 = vertexTexCoord0;
#else
                gl_Position = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
                texCoord0 = vertexTexCoord0;
                worldPos = (XENGINE_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
                vNormal = vec3(0.0, 1.0, 0.0);
                vColor = vec4(1.0);
#endif
			}
		}

		Fragment
		{
            #include "XEngineCG"
            #include "Lighting"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec4 vColor;
            in vec3 worldPos;
            in vec3 vNormal;

            uniform sampler2D _MainTex;
            uniform float _AlphaCutoff;
            uniform float _Translucency;
            uniform float _ScatterPower;
            uniform float _ScatterDistortion;
            uniform float _ScatterScale;

			void main()
			{
                vec4 texColor = texture(_MainTex, texCoord0);
                vec4 finalColor = texColor * vColor;

                if (finalColor.a < _AlphaCutoff)
                    discard;

                vec3 baseColor = gammaToLinearSpace(finalColor.rgb);
                // Flip normal for back faces since grass is double-sided
                vec3 normal = normalize(vNormal) * (gl_FrontFacing ? 1.0 : -1.0);

                // Unified PBR + translucency in a single light loop
                vec3 viewDir = normalize(_WorldSpaceCameraPos.xyz - worldPos);
                vec3 lighting = CalculateForwardLighting(worldPos, normal, viewDir,
                                                         baseColor, 0.0, 0.9, 1.0,
                                                         _Translucency, _ScatterPower,
                                                         _ScatterDistortion, _ScatterScale);
                vec3 ambient = CalculateAmbient(normal) * baseColor * _AmbientStrength;

                vec3 color = ambient + lighting;
                color = ApplyFog(color, worldPos);

				fragColor = vec4(color, 1.0);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer GrassMaterial : register(b2)
			{
				float4x4 _TerrainWorldToLocal;
				float4x4 _TerrainLocalToWorld;
				float3 _TerrainUp;
				float _TerrainSize;
				float _TerrainHeight;
				float _AlphaCutoff;
				float _WindStrength;
				float _WindSpeed;
				float _Billboard;
				float _AlignToNormal;
				float _Translucency;
				float _ScatterPower;
				float _ScatterDistortion;
				float _ScatterScale;
				float _GrassDistance;
				float _GrassFadeStart;
			};

			Texture2D _Heightmap : register(t1);
			SamplerState _HeightmapSampler : register(s1);

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float2 vertexTexCoord0 : TEXCOORD0;
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
				float3 worldPos : TEXCOORD1;
				float3 vNormal : TEXCOORD2;
			};

			float3 SampleTerrainNormal(float3 localPosition)
			{
				uint width, height;
				_Heightmap.GetDimensions(width, height);
				float2 size = max(float2(width, height), 1.0.xx);
				float2 terrainUV = localPosition.xz / max(abs(_TerrainSize), 0.0001);
				float vertStep = width > 1 ? 1.0 / (width - 1.0) : 0.001;
				float2 baseUV = terrainUV * (size - 1.0) / size + 0.5 / size;
				float2 stepU = float2(vertStep * (width - 1.0) / size.x, 0.0);
				float2 stepV = float2(0.0, vertStep * (height - 1.0) / size.y);
				float hR = _Heightmap.SampleLevel(_HeightmapSampler, baseUV + stepU, 0).r * _TerrainHeight;
				float hL = _Heightmap.SampleLevel(_HeightmapSampler, baseUV - stepU, 0).r * _TerrainHeight;
				float hU = _Heightmap.SampleLevel(_HeightmapSampler, baseUV + stepV, 0).r * _TerrainHeight;
				float hD = _Heightmap.SampleLevel(_HeightmapSampler, baseUV - stepV, 0).r * _TerrainHeight;
				float worldStep = max(vertStep * abs(_TerrainSize), 0.0001);
				float3 localNormal = normalize(float3(-(hR - hL) / (worldStep * 2.0), 1.0, -(hU - hD) / (worldStep * 2.0)));
				return normalize(mul((float3x3)_TerrainLocalToWorld, localNormal));
			}

			VSOutput main(VSInput input)
			{
				VSOutput o;
#ifdef GPU_INSTANCING
				float3 localPosition = input.instanceModelRow3.xyz;
				float3 bladePosition = mul(_TerrainLocalToWorld, float4(localPosition, 1.0)).xyz;
				float scaleX = length(input.instanceModelRow0.xyz);
				float scaleY = length(input.instanceModelRow1.xyz);
				float3 cameraFlat = float3(_WorldSpaceCameraPos.x, bladePosition.y, _WorldSpaceCameraPos.z);
				float fadeRange = max(0.0001, _GrassDistance - _GrassFadeStart);
				float fade = 1.0 - saturate((length(bladePosition - cameraFlat) - _GrassFadeStart) / fadeRange);
				scaleX *= fade;
				scaleY *= fade;

				float3 terrainNormal = SampleTerrainNormal(localPosition);
				float3 up = normalize(_AlignToNormal > 0.5 ? terrainNormal : _TerrainUp);
				float3 quadRight;
				if (_Billboard > 0.5)
				{
					float3 cameraRight = float3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[0][1], XENGINE_MATRIX_V[0][2]);
					quadRight = normalize(cameraRight - up * dot(cameraRight, up));
				}
				else
				{
					float3 right = normalize(mul((float3x3)_TerrainLocalToWorld, normalize(input.instanceModelRow0.xyz)));
					quadRight = normalize(right - up * dot(right, up));
				}

				float3 localOffset = quadRight * input.vertexPosition.x * scaleX + up * input.vertexPosition.y * scaleY;
				float windAmount = max(0.0, input.vertexPosition.y);
				float windBase = sin(_Time.y * _WindSpeed + bladePosition.x * 0.7 + bladePosition.z * 0.4 + input.instanceCustomData.x);
				float windTurb = sin(_Time.y * _WindSpeed * 2.73 + bladePosition.z * 1.3 + input.instanceCustomData.x * 1.7) * 0.35;
				float wind = (windBase + windTurb) * _WindStrength * input.instanceCustomData.y;
				localOffset.x += wind * windAmount;
				localOffset.z += wind * windAmount * 0.3;
				float3 worldPosition = bladePosition + localOffset + up * (0.01 * scaleY);
				o.position = mul(XENGINE_MATRIX_VP, float4(worldPosition, 1.0));
				o.worldPos = worldPosition;
				o.vNormal = normalize(cross(up, quadRight));
				o.vColor = input.instanceColor;
#else
				o.position = TransformClip(input.vertexPosition);
				o.worldPos = TransformPosition(input.vertexPosition);
				o.vNormal = float3(0.0, 1.0, 0.0);
				o.vColor = 1.0.xxxx;
#endif
				o.texCoord0 = input.vertexTexCoord0;
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"
			#include "StandardLighting"

			cbuffer GrassMaterial : register(b2)
			{
				float4x4 _TerrainWorldToLocal;
				float4x4 _TerrainLocalToWorld;
				float3 _TerrainUp;
				float _TerrainSize;
				float _TerrainHeight;
				float _AlphaCutoff;
				float _WindStrength;
				float _WindSpeed;
				float _Billboard;
				float _AlignToNormal;
				float _Translucency;
				float _ScatterPower;
				float _ScatterDistortion;
				float _ScatterScale;
				float _GrassDistance;
				float _GrassFadeStart;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float4 vColor : COLOR0;
				float3 worldPos : TEXCOORD1;
				float3 vNormal : TEXCOORD2;
			};

			float4 main(PSInput input, bool frontFace : SV_IsFrontFace) : SV_Target
			{
				float4 finalColor = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor;
				if (finalColor.a < _AlphaCutoff)
					discard;
				float3 baseColor = gammaToLinearSpace(finalColor.rgb);
				float3 normal = normalize(input.vNormal) * (frontFace ? 1.0 : -1.0);
				float3 viewDirection = normalize(_WorldSpaceCameraPos - input.worldPos);
				float3 color = StandardCalculateAmbient(normal) * baseColor;
				color += StandardCalculateLighting(input.worldPos, normal, viewDirection, input.position.xy,
					baseColor, 0.0, 0.9, 1.0,
					_Translucency, _ScatterPower, _ScatterDistortion, _ScatterScale);
				return float4(StandardApplyFog(color, input.worldPos), 1.0);
			}
		}
	ENDSLANG
}

Pass "GrassPrepass"
{
    Tags { "LightMode" = "Prepass" }
    Cull Off
    ZWrite On

	GLSLPROGRAM

		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

            out vec3 vNormal;
            out vec2 texCoord0;

            uniform float _WindStrength;
            uniform float _WindSpeed;
            uniform float _Billboard;
            uniform float _AlignToNormal;
            uniform float _GrassDistance;
            uniform float _GrassFadeStart;
            uniform vec3 _TerrainUp;
            uniform sampler2D _Heightmap;
            uniform float _TerrainSize;
            uniform float _TerrainHeight;
            uniform mat4 _TerrainWorldToLocal;
            uniform mat4 _TerrainLocalToWorld;

			void main()
			{
#ifdef GPU_INSTANCING
                mat4 terrainToWorld = _TerrainLocalToWorld;
                vec3 localPosition = instanceModelRow3.xyz;
                vec3 bladePosition = (terrainToWorld * vec4(localPosition, 1.0)).xyz;
                float scaleX = length(instanceModelRow0.xyz);
                float scaleY = length(instanceModelRow1.xyz);

                // Must match the main Grass pass fade so DepthNormals geometry stays consistent.
                vec3 camFlat = vec3(_WorldSpaceCameraPos.x, bladePosition.y, _WorldSpaceCameraPos.z);
                float camDist = length(bladePosition - camFlat);
                float fadeRange = max(0.0001, _GrassDistance - _GrassFadeStart);
                float fade = 1.0 - clamp((camDist - _GrassFadeStart) / fadeRange, 0.0, 1.0);
                scaleX *= fade;
                scaleY *= fade;

                vec2 terrainUV = localPosition.xz / _TerrainSize;
                vec2 hmSize2 = vec2(textureSize(_Heightmap, 0));
                float hmSize = hmSize2.x;
                float vertStep = hmSize > 1.0 ? (1.0 / (hmSize - 1.0)) : 0.001;

                vec2 baseUV = terrainUV * (hmSize2 - 1.0) / hmSize2 + 0.5 / hmSize2;
                vec2 stepUV = vec2(vertStep * (hmSize - 1.0) / hmSize, 0.0);
                vec2 stepVV = vec2(0.0, vertStep * (hmSize - 1.0) / hmSize);
                float hR = texture(_Heightmap, baseUV + stepUV).r * _TerrainHeight;
                float hL = texture(_Heightmap, baseUV - stepUV).r * _TerrainHeight;
                float hU = texture(_Heightmap, baseUV + stepVV).r * _TerrainHeight;
                float hD = texture(_Heightmap, baseUV - stepVV).r * _TerrainHeight;

                float wStep = vertStep * _TerrainSize;
                vec3 localNormal = normalize(vec3(-(hR - hL) / (wStep * 2.0), 1.0, -(hU - hD) / (wStep * 2.0)));
                vec3 terrainNormal = normalize((terrainToWorld * vec4(localNormal, 0.0)).xyz);

                vec3 up = (_AlignToNormal > 0.5) ? terrainNormal : _TerrainUp;
                vec3 quadRight;
                vec3 localOffset;
                if (_Billboard > 0.5) {
                    vec3 cameraRight = vec3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[1][0], XENGINE_MATRIX_V[2][0]);
                    cameraRight = normalize(cameraRight - up * dot(cameraRight, up));
                    quadRight = cameraRight;
                    localOffset = cameraRight * vertexPosition.x * scaleX + up * vertexPosition.y * scaleY;
                } else {
                    vec3 right = normalize((terrainToWorld * vec4(normalize(instanceModelRow0.xyz), 0.0)).xyz);
                    right = normalize(right - up * dot(right, up));
                    quadRight = right;
                    localOffset = right * vertexPosition.x * scaleX + up * vertexPosition.y * scaleY;
                }

                float windPhase = instanceCustomData.x;
                float bendFactor = instanceCustomData.y;
                float windAmount = max(0.0, vertexPosition.y);
                float windBase = sin(_Time.y * _WindSpeed + bladePosition.x * 0.7 + bladePosition.z * 0.4 + windPhase);
                float windTurb = sin(_Time.y * _WindSpeed * 2.73 + bladePosition.z * 1.3 + windPhase * 1.7) * 0.35;
                float wind = (windBase + windTurb) * _WindStrength * bendFactor;
                localOffset.x += wind * windAmount;
                localOffset.z += wind * windAmount * 0.3;

                vec3 worldPosition = bladePosition + localOffset;
                worldPosition += up * 0.01 * scaleY; // Must match main Grass pass offset
                vNormal = normalize(cross(up, quadRight));
                gl_Position = XENGINE_MATRIX_VP * vec4(worldPosition, 1.0);
                texCoord0 = vertexTexCoord0;
#else
                gl_Position = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
                vNormal = vec3(0.0, 1.0, 0.0);
                texCoord0 = vertexTexCoord0;
#endif
			}
		}

		Fragment
		{
            #include "XEngineCG"

			layout (location = 0) out vec4 normalOut;
			layout (location = 1) out vec4 motionRM;
            in vec3 vNormal;
            in vec2 texCoord0;

            uniform sampler2D _MainTex;
            uniform float _AlphaCutoff;

			void main()
			{
                vec4 texColor = texture(_MainTex, texCoord0);
                if (texColor.a < _AlphaCutoff)
                    discard;

                vec3 n = normalize(vNormal) * (gl_FrontFacing ? 1.0 : -1.0);
                normalOut = EncodeViewNormal(n);

                // Grass is procedurally wind-animated with no stable previous position, so motion
                // stays zero (it was absent from the motion buffer before). Diffuse: roughness 1, metallic 0.
                motionRM = vec4(0.0, 0.0, 1.0, 0.0);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer GrassMaterial : register(b2)
			{
				float4x4 _TerrainWorldToLocal;
				float4x4 _TerrainLocalToWorld;
				float3 _TerrainUp;
				float _TerrainSize;
				float _TerrainHeight;
				float _AlphaCutoff;
				float _WindStrength;
				float _WindSpeed;
				float _Billboard;
				float _AlignToNormal;
				float _Translucency;
				float _ScatterPower;
				float _ScatterDistortion;
				float _ScatterScale;
				float _GrassDistance;
				float _GrassFadeStart;
			};

			Texture2D _Heightmap : register(t1);
			SamplerState _HeightmapSampler : register(s1);

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float2 vertexTexCoord0 : TEXCOORD0;
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
				float3 vNormal : TEXCOORD0;
				float2 texCoord0 : TEXCOORD1;
			};

			float3 SampleTerrainNormal(float3 localPosition)
			{
				uint width, height;
				_Heightmap.GetDimensions(width, height);
				float2 size = max(float2(width, height), 1.0.xx);
				float2 terrainUV = localPosition.xz / max(abs(_TerrainSize), 0.0001);
				float vertStep = width > 1 ? 1.0 / (width - 1.0) : 0.001;
				float2 baseUV = terrainUV * (size - 1.0) / size + 0.5 / size;
				float2 stepU = float2(vertStep * (width - 1.0) / size.x, 0.0);
				float2 stepV = float2(0.0, vertStep * (height - 1.0) / size.y);
				float hR = _Heightmap.SampleLevel(_HeightmapSampler, baseUV + stepU, 0).r * _TerrainHeight;
				float hL = _Heightmap.SampleLevel(_HeightmapSampler, baseUV - stepU, 0).r * _TerrainHeight;
				float hU = _Heightmap.SampleLevel(_HeightmapSampler, baseUV + stepV, 0).r * _TerrainHeight;
				float hD = _Heightmap.SampleLevel(_HeightmapSampler, baseUV - stepV, 0).r * _TerrainHeight;
				float worldStep = max(vertStep * abs(_TerrainSize), 0.0001);
				float3 localNormal = normalize(float3(-(hR - hL) / (worldStep * 2.0), 1.0, -(hU - hD) / (worldStep * 2.0)));
				return normalize(mul((float3x3)_TerrainLocalToWorld, localNormal));
			}

			VSOutput main(VSInput input)
			{
				VSOutput o;
#ifdef GPU_INSTANCING
				float3 localPosition = input.instanceModelRow3.xyz;
				float3 bladePosition = mul(_TerrainLocalToWorld, float4(localPosition, 1.0)).xyz;
				float scaleX = length(input.instanceModelRow0.xyz);
				float scaleY = length(input.instanceModelRow1.xyz);
				float3 cameraFlat = float3(_WorldSpaceCameraPos.x, bladePosition.y, _WorldSpaceCameraPos.z);
				float fadeRange = max(0.0001, _GrassDistance - _GrassFadeStart);
				float fade = 1.0 - saturate((length(bladePosition - cameraFlat) - _GrassFadeStart) / fadeRange);
				scaleX *= fade;
				scaleY *= fade;
				float3 up = normalize(_AlignToNormal > 0.5 ? SampleTerrainNormal(localPosition) : _TerrainUp);
				float3 quadRight;
				if (_Billboard > 0.5)
				{
					float3 cameraRight = float3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[0][1], XENGINE_MATRIX_V[0][2]);
					quadRight = normalize(cameraRight - up * dot(cameraRight, up));
				}
				else
				{
					float3 right = normalize(mul((float3x3)_TerrainLocalToWorld, normalize(input.instanceModelRow0.xyz)));
					quadRight = normalize(right - up * dot(right, up));
				}
				float3 localOffset = quadRight * input.vertexPosition.x * scaleX + up * input.vertexPosition.y * scaleY;
				float windAmount = max(0.0, input.vertexPosition.y);
				float windBase = sin(_Time.y * _WindSpeed + bladePosition.x * 0.7 + bladePosition.z * 0.4 + input.instanceCustomData.x);
				float windTurb = sin(_Time.y * _WindSpeed * 2.73 + bladePosition.z * 1.3 + input.instanceCustomData.x * 1.7) * 0.35;
				float wind = (windBase + windTurb) * _WindStrength * input.instanceCustomData.y;
				localOffset.x += wind * windAmount;
				localOffset.z += wind * windAmount * 0.3;
				float3 worldPosition = bladePosition + localOffset + up * (0.01 * scaleY);
				o.position = mul(XENGINE_MATRIX_VP, float4(worldPosition, 1.0));
				o.vNormal = normalize(cross(up, quadRight));
#else
				o.position = TransformClip(input.vertexPosition);
				o.vNormal = float3(0.0, 1.0, 0.0);
#endif
				o.texCoord0 = input.vertexTexCoord0;
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"

			cbuffer GrassMaterial : register(b2)
			{
				float4x4 _TerrainWorldToLocal;
				float4x4 _TerrainLocalToWorld;
				float3 _TerrainUp;
				float _TerrainSize;
				float _TerrainHeight;
				float _AlphaCutoff;
				float _WindStrength;
				float _WindSpeed;
				float _Billboard;
				float _AlignToNormal;
				float _Translucency;
				float _ScatterPower;
				float _ScatterDistortion;
				float _ScatterScale;
				float _GrassDistance;
				float _GrassFadeStart;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);

			struct PSInput
			{
				float4 position : SV_Position;
				float3 vNormal : TEXCOORD0;
				float2 texCoord0 : TEXCOORD1;
			};

			struct PSOutput
			{
				float4 normalOut : SV_Target0;
				float4 motionRM : SV_Target1;
			};

			PSOutput main(PSInput input, bool frontFace : SV_IsFrontFace)
			{
				if (_MainTex.Sample(_MainTexSampler, input.texCoord0).a < _AlphaCutoff)
					discard;
				PSOutput o;
				float3 normal = normalize(input.vNormal) * (frontFace ? 1.0 : -1.0);
				o.normalOut = EncodeViewNormal(normal);
				o.motionRM = float4(0.0, 0.0, 1.0, 0.0);
				return o;
			}
		}
	ENDSLANG
}
